// The routes, and the two independent checks in front of them.
//
// NOTHING IN THIS FILE MAY TOUCH THE POLICY LOOP. Every handler here runs on a
// thread pool thread that the listener handed the connection to, and the rules
// that keep the yield promise are visible in what each one does:
//
//   GET  /v1/status      returns the string FastLoop published on its last tick.
//                        No lock, no snapshot, no computation. A thousand pollers
//                        cost a thousand string copies and nothing else.
//   POST .../jobs        writes one file into a pending directory. FastLoop never
//                        enumerates job files, so a flood of submissions is
//                        invisible to it. The scheduler notices, on its own thread.
//   POST /v1/mode        the ONLY input that reaches the loop, and it goes through
//                        a bounded queue that FastLoop drains in O(1). Full means
//                        503, not growth.
//
// AUTHENTICATION IS TWO INDEPENDENT CHECKS, both before any lease is written.
//
//   TLS       a pinned self-signed fingerprint is the trust root. There is no
//             public certificate authority to appeal to and none is assumed.
//   Bearer    a token in an Authorization: Bearer header or an X-Api-Key header,
//             compared in constant time.
//
// Loopback MAY run tokenless, the way BOINC's GUI RPC does, because reaching
// 127.0.0.1 already means code execution on the machine and a token would only be
// protecting the user from themselves. Any non-loopback bind REQUIRES the token
// and an address allowlist, and Listener.Validate refuses to start without them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace IdleGpu
{
    public class ApiContext
    {
        public Agent Agent;
        public Config Config;
        public IPAddress Peer;
        public bool PeerIsLoopback;
    }

    public static class Api
    {
        public static void Handle(HttpRequest q, Stream outp, ApiContext ctx)
        {
            string[] seg = q.Segments();

            // Liveness, deliberately separate from status and deliberately free.
            // A supervisor poking "is this process alive" must not need a
            // credential, and must not be answered by something that could be slow.
            if (q.Path == "/healthz")
            {
                if (!ctx.PeerIsLoopback) { Http.WriteError(outp, 403, "healthz is loopback only"); return; }
                if (q.Method != "GET") { Http.WriteError(outp, 405, "GET only"); return; }
                Http.WriteText(outp, 200, "application/json", Json.Obj(
                    Json.P("ok", "true"),
                    Json.P("controller_running", ctx.Agent.JobRunning ? "true" : "false"),
                    Json.P("running_service", ctx.Agent.RunningService == null
                        ? "null" : Json.Esc(ctx.Agent.RunningService))) + "\n");
                return;
            }

            if (q.Path == "/")
            {
                if (q.Method != "GET") { Http.WriteError(outp, 405, "GET only"); return; }
                Http.WriteText(outp, 200, "application/json", Index());
                return;
            }

            if (seg.Length == 0 || seg[0] != "v1")
            {
                Http.WriteError(outp, 404, "no such route; GET / lists them");
                return;
            }

            // Everything past this point is credentialled, except status on
            // loopback, which the tray and a local poller read constantly.
            bool statusOnLoopback = ctx.PeerIsLoopback && seg.Length == 2 && seg[1] == "status";
            if (!statusOnLoopback && !Authorised(q, ctx))
            {
                Http.WriteError(outp, 401, "a bearer token is required; see ApiKeyFile in worker.ini");
                return;
            }

            if (seg.Length == 2 && seg[1] == "status")
            {
                if (q.Method != "GET") { Http.WriteError(outp, 405, "GET only"); return; }
                // The published string, verbatim. This is the whole handler, and
                // that is the point.
                Http.WriteText(outp, 200, "application/json", ctx.Agent.PublishedStatus + "\n");
                return;
            }

            if (seg.Length == 2 && seg[1] == "services")
            {
                if (q.Method != "GET") { Http.WriteError(outp, 405, "GET only"); return; }
                Http.WriteText(outp, 200, "application/json", ctx.Agent.ServicesJson() + "\n");
                return;
            }

            if (seg.Length == 2 && seg[1] == "mode") { Mode(q, outp, ctx); return; }

            if (seg.Length == 2 && seg[1] == "profile") { Profile(q, outp, ctx); return; }

            if (seg.Length == 2 && seg[1] == "limits") { Limits(q, outp, ctx); return; }

            if (seg.Length == 2 && seg[1] == "presence") { Presence(q, outp, ctx); return; }

            if (seg[1] == "assets") { Assets(q, seg, outp, ctx); return; }

            if (seg[1] == "services") { ServiceRoute(q, seg, outp, ctx); return; }

            Http.WriteError(outp, 404, "no such route; GET / lists them");
        }

        // -- services and jobs ---------------------------------------------------

        static void ServiceRoute(HttpRequest q, string[] seg, Stream outp, ApiContext ctx)
        {
            // /v1/services/{id}/jobs[/{job}[/result|/events]]
            if (seg.Length < 4 || seg[3] != "jobs")
            {
                Http.WriteError(outp, 404, "expected /v1/services/{id}/jobs");
                return;
            }
            string serviceId = seg[2];
            if (!JobStore.SafeServiceId(serviceId))
            {
                Http.WriteError(outp, 400, "the service id contains characters that are not allowed");
                return;
            }
            JobStore store = ctx.Agent.Queue(serviceId);
            if (store == null)
            {
                Http.WriteError(outp, 404, "no service with that id is registered; GET /v1/services lists them");
                return;
            }

            if (seg.Length == 4)
            {
                if (q.Method != "POST") { Http.WriteError(outp, 405, "POST to submit a job"); return; }
                Submit(q, outp, ctx, store);
                return;
            }

            string jobId = seg[4];
            if (!JobStore.SafeJobId(jobId))
            {
                // Named in a test: a job id is the only client supplied string that
                // ever reaches Path.Combine, so it is checked before it gets there.
                Http.WriteError(outp, 400, "the job id contains characters that are not allowed");
                return;
            }

            if (seg.Length == 5)
            {
                if (q.Method == "GET") { JobStatus(outp, ctx, store, jobId); return; }
                if (q.Method == "DELETE") { Cancel(outp, store, jobId); return; }
                Http.WriteError(outp, 405, "GET for status, DELETE to cancel");
                return;
            }

            if (seg.Length == 6 && seg[5] == "result")
            {
                if (q.Method != "GET") { Http.WriteError(outp, 405, "GET only"); return; }
                Result(q, outp, ctx, store, jobId);
                return;
            }

            if (seg.Length == 6 && seg[5] == "events")
            {
                // Named rather than silently missing, because a client that gets a
                // 404 here goes looking for a typo and a client that gets this goes
                // and polls instead.
                Http.WriteError(outp, 501,
                    "server-sent progress is not implemented; poll /v1/services/{id}/jobs/{job} instead");
                return;
            }

            Http.WriteError(outp, 404, "no such route; GET / lists them");
        }

        static void Submit(HttpRequest q, Stream outp, ApiContext ctx, JobStore store)
        {
            string body = q.BodyText().Trim();
            // The body is the SERVICE'S business and is stored unparsed. The only
            // check is that it is one JSON object, so that a lease file is always a
            // valid document for whoever reads it next. What is inside it is
            // between the client and the controller, which is exactly what lets a
            // new service be added without touching this file.
            if (!Json.LooksLikeObject(body))
            {
                // VALIDATED, NOT INTERPRETED. The agent confirms the grammar and
                // reads none of the keys, so adding a service still costs no change
                // here. What it refuses to do is accept a body it can see the
                // controller will choke on, return 202, and let the failure surface
                // minutes later as "the lease could not be parsed" with the client
                // long gone. Measured cause of exactly that: PowerShell strips the
                // double quotes when it passes an argument to a native executable.
                Http.WriteError(outp, 400,
                    "the body must be one well-formed JSON object; its CONTENTS are the service's " +
                    "business but its syntax is not. If your shell is PowerShell, it removes double " +
                    "quotes when calling a native program: use --body-file, or single-quote the " +
                    "whole argument and double the inner quotes.");
                return;
            }

            string key = q.Header("Idempotency-Key");
            if (key != null && key.Length > 512)
            {
                Http.WriteError(outp, 400, "Idempotency-Key is too long");
                return;
            }
            if (!string.IsNullOrEmpty(key))
            {
                string existing = store.LookupIdempotent(key);
                if (existing != null)
                {
                    // Never a second run. The first client of this runner retries
                    // after a yield, which is the NORMAL case rather than an error,
                    // and a retry that started a second generation would mean the
                    // same sentence spoken twice.
                    JobView have = store.Look(existing, ctx.Config.MaxPassthroughBytes, ctx.Agent.IsRunning(store.ServiceId));
                    Http.WriteText(outp, 200, "application/json",
                        JobJson(have, true) + "\n");
                    return;
                }
            }

            string id = JobStore.NewJobId();
            try
            {
                store.Submit(id, body, key, ctx.Config.AssetsDir);
            }
            catch (Exception ex)
            {
                ctx.Agent.LogLine("submit to " + store.ServiceId + " failed: " + ex.Message);
                Http.WriteError(outp, 500, "the lease could not be written; see worker.log");
                return;
            }
            Http.WriteText(outp, 202, "application/json", Json.Obj(
                Json.P("job_id", Json.Esc(id)),
                Json.P("service", Json.Esc(store.ServiceId)),
                Json.P("status", Json.Esc("queued")),
                Json.P("reused", "false")) + "\n");
        }

        static void JobStatus(Stream outp, ApiContext ctx, JobStore store, string jobId)
        {
            JobView v = store.Look(jobId, ctx.Config.MaxPassthroughBytes, ctx.Agent.IsRunning(store.ServiceId));
            if (v.State == JobState.Unknown)
            {
                Http.WriteError(outp, 404, "no such job");
                return;
            }
            Http.WriteText(outp, 200, "application/json", JobJson(v, false) + "\n");
        }

        static string JobJson(JobView v, bool reused)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append(Json.P("job_id", Json.Esc(v.Id))).Append(",");
            sb.Append(Json.P("service", Json.Esc(v.Service))).Append(",");
            sb.Append(Json.P("status", Json.Esc(JobStore.StateName(v.State)))).Append(",");
            sb.Append(Json.P("reused", reused ? "true" : "false")).Append(",");
            sb.Append("\"artefacts\":[");
            for (int i = 0; i < v.Artefacts.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Esc(v.Artefacts[i]));
            }
            sb.Append("],");
            // The controller's own terminal record, passed through untouched. The
            // agent has no opinion about what a finished job means.
            sb.Append("\"record\":").Append(v.Record == null ? "null" : v.Record);
            sb.Append("}");
            return sb.ToString();
        }

        static void Cancel(Stream outp, JobStore store, string jobId)
        {
            JobView before = store.Look(jobId, 1024, true);
            if (before.State == JobState.Unknown) { Http.WriteError(outp, 404, "no such job"); return; }
            bool acted = store.Cancel(jobId);
            Http.WriteText(outp, 200, "application/json", Json.Obj(
                Json.P("job_id", Json.Esc(jobId)),
                Json.P("was", Json.Esc(JobStore.StateName(before.State))),
                // Withdrawing queued work is immediate. Stopping running work is a
                // request: only the controller knows whether it can, and a
                // controller that ignores it finishes the job. Saying which of the
                // two happened is the honest answer.
                Json.P("cancelled_now", (acted && before.State == JobState.Queued) ? "true" : "false"),
                Json.P("requested", acted ? "true" : "false")) + "\n");
        }

        static void Result(HttpRequest q, Stream outp, ApiContext ctx, JobStore store, string jobId)
        {
            JobView v = store.Look(jobId, ctx.Config.MaxPassthroughBytes, ctx.Agent.IsRunning(store.ServiceId));
            if (v.State == JobState.Unknown) { Http.WriteError(outp, 404, "no such job"); return; }
            if (v.Artefacts.Count == 0)
            {
                Http.WriteError(outp, 409,
                    "that job has produced no artefact yet; its status is " + JobStore.StateName(v.State));
                return;
            }
            string want = q.Param("artefact");
            string name = want == null ? v.Artefacts[0] : want;
            string path = store.ArtefactPath(jobId, name);
            if (path == null) { Http.WriteError(outp, 404, "no such artefact for that job"); return; }
            try
            {
                var fi = new FileInfo(path);
                using (FileStream fs = File.OpenRead(path))
                {
                    // Streamed with a real Content-Length and never buffered whole:
                    // an image batch or a potfile can be larger than anything this
                    // agent should be allocating on a request thread.
                    Http.WriteFile(outp, ContentType(name), fs, fi.Length, new string[] {
                        "Content-Disposition: attachment; filename=\"" + name + "\"",
                        "X-IdleGpu-Artefact: " + name
                    });
                }
            }
            catch (Exception)
            {
                Http.WriteError(outp, 500, "the artefact could not be read");
            }
        }

        /// A guess from the extension, and no more than a guess. The agent has no
        /// opinion about artefact formats; this exists so a browser does not offer
        /// to download a JSON sidecar as an unknown blob.
        static string ContentType(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            if (e == ".json") return "application/json";
            if (e == ".txt" || e == ".log") return "text/plain; charset=utf-8";
            if (e == ".png") return "image/png";
            if (e == ".jpg" || e == ".jpeg") return "image/jpeg";
            if (e == ".wav") return "audio/wav";
            return "application/octet-stream";
        }

        // -- assets --------------------------------------------------------------

        static void Assets(HttpRequest q, string[] seg, Stream outp, ApiContext ctx)
        {
            var store = new AssetStore(ctx.Config.AssetsDir);

            if (seg.Length == 2)
            {
                if (q.Method != "POST") { Http.WriteError(outp, 405, "POST bytes to store them"); return; }
                if (q.Body.Length == 0) { Http.WriteError(outp, 400, "empty body"); return; }
                try
                {
                    string digest = store.Put(q.Body);
                    Http.WriteText(outp, 201, "application/json", Json.Obj(
                        Json.P("sha256", Json.Esc(digest)),
                        Json.P("bytes", Json.Num(q.Body.Length))) + "\n");
                }
                catch (Exception ex)
                {
                    ctx.Agent.LogLine("asset write failed: " + ex.Message);
                    Http.WriteError(outp, 500, "the asset could not be stored");
                }
                return;
            }

            if (seg.Length == 3)
            {
                string digest = seg[2].ToLowerInvariant();
                if (!AssetStore.SafeDigest(digest))
                {
                    Http.WriteError(outp, 400, "expected a lower case hex sha256");
                    return;
                }
                long size = store.SizeOf(digest);
                if (q.Method == "HEAD")
                {
                    // The whole point of this route: a client asks before it
                    // uploads, so a reference clip crosses the wire exactly once
                    // however many jobs use it.
                    if (size < 0) { Http.WriteHeadOnly(outp, 404, "application/octet-stream", 0, null); return; }
                    Http.WriteHeadOnly(outp, 200, "application/octet-stream", size, null);
                    return;
                }
                if (q.Method == "GET")
                {
                    if (size < 0) { Http.WriteError(outp, 404, "no such asset"); return; }
                    try
                    {
                        using (FileStream fs = File.OpenRead(store.PathOf(digest)))
                            Http.WriteFile(outp, "application/octet-stream", fs, size, null);
                    }
                    catch (Exception) { Http.WriteError(outp, 500, "the asset could not be read"); }
                    return;
                }
                Http.WriteError(outp, 405, "HEAD or GET");
                return;
            }

            Http.WriteError(outp, 404, "no such route; GET / lists them");
        }

        // -- presence --------------------------------------------------------------

        /// What the logon helper can see and this agent cannot.
        ///
        /// QUERY PARAMETERS, NOT A JSON BODY, and that is deliberate. This build
        /// has a JSON writer and a shallow field peek, no parser, and writing one
        /// to carry six scalars between two copies of the same program would be
        /// the wrong trade. The shape is fixed, small, and both ends ship
        /// together.
        ///
        /// It is authenticated like every other route. The helper runs as the
        /// signed-in user and reads the same key file, which is inside the
        /// install directory that user owns.
        static void Presence(HttpRequest q, Stream outp, ApiContext ctx)
        {
            if (q.Method != "POST") { Http.WriteError(outp, 405, "POST only"); return; }
            var r = new PresenceReport();
            r.InputIdleSeconds = ParseInt(q.Param("input_idle_s"), -1);
            r.Locked = ParseBool(q.Param("locked"));
            r.ForegroundIsFullScreen = ParseBool(q.Param("fullscreen"));
            r.ForegroundProcess = q.Param("fg_process");
            if (r.ForegroundProcess == null) r.ForegroundProcess = "";
            r.SteamRunningAppId = ParseInt(q.Param("steam_appid"), 0);
            r.SteamRunningAppName = q.Param("steam_appname");
            if (r.SteamRunningAppName == null) r.SteamRunningAppName = "";
            ctx.Agent.SetPresence(r);
            Http.WriteText(outp, 200, "application/json",
                "{\"ok\":true,\"ttl_seconds\":"
                + ((int)Agent.PresenceTtl.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                + "}\n");
        }

        static int ParseInt(string v, int fallback)
        {
            int n;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                ? n : fallback;
        }

        static bool ParseBool(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // -- mode ----------------------------------------------------------------

        static void Mode(HttpRequest q, Stream outp, ApiContext ctx)
        {
            if (q.Method != "POST") { Http.WriteError(outp, 405, "POST to change the mode"); return; }
            string want = q.Param("mode");
            if (string.IsNullOrEmpty(want)) want = Json.PeekString(q.BodyText(), "mode");
            if (string.IsNullOrEmpty(want)) want = q.BodyText().Trim();
            IdleGpu.Mode parsed;
            try { parsed = (IdleGpu.Mode)Enum.Parse(typeof(IdleGpu.Mode), want, true); }
            catch (Exception)
            {
                Http.WriteError(outp, 400, "mode must be Auto, AlwaysOn or Off");
                return;
            }
            var cmd = new Command();
            cmd.Kind = "mode";
            cmd.Value = parsed.ToString();
            if (!ctx.Agent.Submit(cmd))
            {
                // The queue is bounded on purpose. An unbounded queue drained once
                // per tick is a memory leak with a network interface on it.
                Http.WriteError(outp, 503, "the command queue is full; try again");
                return;
            }
            Http.WriteText(outp, 202, "application/json", Json.Obj(
                Json.P("accepted", Json.Esc(parsed.ToString())),
                // Accepted, not applied: the loop applies it at the top of its next
                // tick, which is how a network request stays off the yield path.
                Json.P("note", Json.Esc("applied at the top of the next policy tick"))) + "\n");
        }

        // -- posture and limits ---------------------------------------------------
        //
        // POLICY MUST BE EDITABLE WITHOUT A GUI. The settings window is the nice
        // way to see the whole matrix at once, and it needs a desktop session to
        // exist. An agent running as a boot task under SYSTEM has no desktop, and
        // the person who wants to change what their headless machine lends out
        // over SSH is exactly the person this program is for. So the same two
        // changes the window makes are a route and a CLI verb as well, and all
        // three go through Agent.DrainCommands so the policy thread stays the
        // single writer.

        static void Profile(HttpRequest q, Stream outp, ApiContext ctx)
        {
            if (q.Method != "POST") { Http.WriteError(outp, 405, "POST to change the posture"); return; }
            string want = q.Param("profile");
            if (string.IsNullOrEmpty(want)) want = Json.PeekString(q.BodyText(), "profile");
            if (string.IsNullOrEmpty(want)) want = q.BodyText().Trim();
            if (string.IsNullOrEmpty(want))
            {
                Http.WriteError(outp, 400, "profile must be one of: "
                    + string.Join(", ", Config.ProfileIds()));
                return;
            }
            if (ctx.Config.ResolveProfile(want) == null)
            {
                Http.WriteError(outp, 400, "no posture called '" + want + "'; known: "
                    + string.Join(", ", Config.ProfileIds()));
                return;
            }
            var cmd = new Command();
            cmd.Kind = "profile";
            cmd.Value = want;
            if (!ctx.Agent.Submit(cmd))
            {
                Http.WriteError(outp, 503, "the command queue is full; try again");
                return;
            }
            Http.WriteText(outp, 202, "application/json", Json.Obj(
                Json.P("accepted", Json.Esc(Config.NormaliseProfile(want))),
                Json.P("note", Json.Esc("applied at the top of the next policy tick, "
                    + "and it binds to a job that is already running"))) + "\n");
        }

        static void Limits(HttpRequest q, Stream outp, ApiContext ctx)
        {
            if (q.Method != "POST") { Http.WriteError(outp, 405, "POST to change one row"); return; }
            string body = q.BodyText();
            string state = q.Param("state");
            if (string.IsNullOrEmpty(state)) state = Json.PeekString(body, "state");
            if (string.IsNullOrEmpty(state))
            {
                Http.WriteError(outp, 400,
                    "state must be one of: nobodyhome, locked, idle, lightuse, busy");
                return;
            }
            // Built as the same `state field=value` line the CLI sends and the
            // worker.ini parser understands, so there is one syntax to learn and
            // one place it is interpreted.
            var spec = new StringBuilder(state.Trim().ToLowerInvariant());
            AddField(spec, q, body, "gpu");
            AddField(spec, q, body, "cpu_pct", "cpupct");
            AddField(spec, q, body, "priority");
            AddField(spec, q, body, "working_set_mib", "workingsetmib");
            AddField(spec, q, body, "min_free_mib", "minfreemib");
            AddField(spec, q, body, "admit");

            MachineState st; ResourceLimits row;
            if (!Config.ParseLimitsCommand(ctx.Config, spec.ToString(), out st, out row))
            {
                Http.WriteError(outp, 400,
                    "state must be one of: nobodyhome, locked, idle, lightuse, busy");
                return;
            }
            var cmd = new Command();
            cmd.Kind = "limits";
            cmd.Value = spec.ToString();
            if (!ctx.Agent.Submit(cmd))
            {
                Http.WriteError(outp, 503, "the command queue is full; try again");
                return;
            }
            Http.WriteText(outp, 202, "application/json", Json.Obj(
                Json.P("accepted", Json.Esc(spec.ToString())),
                // A row can be tightened on the way in, because the cooldown
                // ratchet assumes the ladder is monotone. Saying what was asked
                // for is not the same as saying what will be in force, so the
                // caller is told to read it back rather than assume.
                Json.P("note", Json.Esc("applied at the top of the next policy tick; "
                    + "read GET /v1/services to see what is in force, because a row "
                    + "more generous than the one above it is tightened"))) + "\n");
        }

        static void AddField(StringBuilder sb, HttpRequest q, string body, string name)
        {
            AddField(sb, q, body, name, name);
        }

        static void AddField(StringBuilder sb, HttpRequest q, string body, string name, string key)
        {
            string v = q.Param(name);
            if (string.IsNullOrEmpty(v)) v = Json.PeekString(body, name);
            if (string.IsNullOrEmpty(v)) return;
            // A value with a space in it would break the space-separated spec.
            // None of these fields has one; a bad value is dropped rather than
            // corrupting the fields after it.
            if (v.IndexOf(' ') >= 0) return;
            sb.Append(" ").Append(key).Append("=").Append(v);
        }

        // -- auth ----------------------------------------------------------------

        static bool Authorised(HttpRequest q, ApiContext ctx)
        {
            string key = ctx.Config.EffectiveApiKey();
            if (string.IsNullOrEmpty(key))
            {
                // No key configured. Only loopback gets in, and Listener.Validate
                // has already refused to start a non-loopback listener in this
                // state, so this is a second line rather than the only one.
                return ctx.PeerIsLoopback;
            }
            string sent = null;
            string auth = q.Header("Authorization");
            if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                sent = auth.Substring(7).Trim();
            if (sent == null) sent = q.Header("X-Api-Key");
            if (sent == null) return false;
            return FixedTimeEquals(sent, key);
        }

        /// Compare without leaking the position of the first difference through
        /// timing. Length is compared too, which does leak the length; that is the
        /// standard trade and it is why the token is a generated 256-bit value
        /// rather than a password somebody chose.
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);
            if (y.Length == 0) return x.Length == 0;
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < x.Length; i++)
                diff |= x[i] ^ y[i % y.Length];
            return diff == 0;
        }

        static string Index()
        {
            var routes = new List<string>();
            routes.Add("GET    /healthz                                  is the agent up (loopback, no token)");
            routes.Add("GET    /v1/status                                the published policy snapshot");
            routes.Add("GET    /v1/services                              registered services and their manifests");
            routes.Add("POST   /v1/services/{id}/jobs                    submit; body is service defined JSON");
            routes.Add("GET    /v1/services/{id}/jobs/{job}              job status");
            routes.Add("GET    /v1/services/{id}/jobs/{job}/result       stream an artefact (?artefact=NAME)");
            routes.Add("DELETE /v1/services/{id}/jobs/{job}              cancel");
            routes.Add("HEAD   /v1/assets/{sha256}                       do you already hold this blob");
            routes.Add("GET    /v1/assets/{sha256}                       fetch it back");
            routes.Add("POST   /v1/assets                                store bytes, returns {sha256}");
            routes.Add("POST   /v1/mode                                  Auto | AlwaysOn | Off");
            routes.Add("POST   /v1/profile                               generous | balanced | away | <saved id>");
            routes.Add("POST   /v1/limits                                one row: {state, cpu_pct, priority, ...}");
            var sb = new StringBuilder();
            sb.Append("{\"routes\":[");
            for (int i = 0; i < routes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Esc(routes[i]));
            }
            sb.Append("]}\n");
            return sb.ToString();
        }
    }
}
