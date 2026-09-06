// The command line, which is the thing most people will actually use.
//
// FIRST CLASS, NOT AN AFTERTHOUGHT. Some services are driven from a terminal by
// nature: a password recovery run is a command line with flags, not a form. So
// submitting a job from a shell has to be as direct as running the tool would
// have been, and everything after a bare `--` is handed to the service verbatim:
//
//     idlegpu submit hashcat -- -m 22000 hash.hc22000 rockyou.txt
//
// becomes the body {"argv":["-m","22000","hash.hc22000","rockyou.txt"]}, which the
// hashcat controller turns into a hashcat.exe invocation. The runner never learns
// what any of those flags mean.
//
// A client that wants to send structured parameters instead uses --body or
// --body-file, and curl works against the same endpoints, because the framing is
// real HTTP/1.1 rather than a private protocol.
//
// CONFIGURATION COMES FROM THE SAME worker.ini THE AGENT READS, so on the runner's
// own machine there is nothing to configure: the CLI finds the port, reads the
// api key file, and derives the fingerprint from the certificate sitting next to
// it. For a remote runner the four things that change are flags: --host, --port,
// --fingerprint and --key-file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace IdleGpu
{
    public static class Cli
    {
        public static int Run(string[] args, Config cfg)
        {
            var pos = new List<string>();
            var passthrough = new List<string>();
            string host = null, fingerprint = null, key = null, keyFile = null;
            string body = null, bodyFile = null, idem = null, outFile = null, artefact = null;
            int port = 0, intervalMs = 1000, timeoutSeconds = 0;
            bool raw = false, wait = false;

            bool afterDashDash = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (afterDashDash) { passthrough.Add(a); continue; }
                if (a == "--") { afterDashDash = true; continue; }
                if (a == "--json") { raw = true; continue; }
                if (a == "--wait") { wait = true; continue; }
                if (a == "--host" && i + 1 < args.Length) { host = args[++i]; continue; }
                if (a == "--port" && i + 1 < args.Length) { port = int.Parse(args[++i], CultureInfo.InvariantCulture); continue; }
                if (a == "--fingerprint" && i + 1 < args.Length) { fingerprint = args[++i]; continue; }
                if (a == "--key" && i + 1 < args.Length) { key = args[++i]; continue; }
                if (a == "--key-file" && i + 1 < args.Length) { keyFile = args[++i]; continue; }
                if (a == "--body" && i + 1 < args.Length) { body = args[++i]; continue; }
                if (a == "--body-file" && i + 1 < args.Length) { bodyFile = args[++i]; continue; }
                if (a == "--idempotency-key" && i + 1 < args.Length) { idem = args[++i]; continue; }
                if ((a == "-o" || a == "--out") && i + 1 < args.Length) { outFile = args[++i]; continue; }
                if (a == "--artefact" && i + 1 < args.Length) { artefact = args[++i]; continue; }
                if (a == "--interval" && i + 1 < args.Length) { intervalMs = (int)(double.Parse(args[++i], CultureInfo.InvariantCulture) * 1000); continue; }
                if (a == "--timeout" && i + 1 < args.Length) { timeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); continue; }
                if (a == "--config" && i + 1 < args.Length) { i++; continue; }   // already consumed
                if (a.StartsWith("-")) { Console.Error.WriteLine("unknown option: " + a); return 2; }
                pos.Add(a);
            }

            if (pos.Count == 0) { Usage(); return 2; }
            string verb = pos[0].ToLowerInvariant();

            // `fingerprint` is the one subcommand that does not talk to a runner:
            // it reads the certificate off disk, which is how the operator gets the
            // value every other machine has to pin.
            if (verb == "fingerprint")
            {
                if (!File.Exists(cfg.CertPath))
                {
                    Console.Error.WriteLine("no certificate at " + cfg.CertPath +
                        "; start the agent once and it will mint one");
                    return 1;
                }
                string pin = LocalPin(cfg);
                Console.WriteLine(pin);
                Console.Error.WriteLine("grouped: " + Certs.PinGrouped(pin));
                return 0;
            }

            // `service` is the opt-in surface, and it is deliberately LOCAL: it
            // reads worker.ini, walks the install directories and runs provisioning
            // scripts, all without a running agent and without touching the network.
            //
            // Two reasons it is not an API call. It has to work before the agent is
            // running (you install a service so that the agent has something to
            // run), and starting a multi-gigabyte download is not a thing anything
            // reachable over a socket should be able to do to somebody's gaming PC.
            // The listener reports these states; only a person changes them.
            if (verb == "service") return Service(pos, cfg, raw);

            var client = new RunnerClient();
            // NEVER cfg.Bind. 0.0.0.0 is where a listener ACCEPTS, not an address
            // anything can dial, and using it here made `idlegpu health` fail on
            // exactly the machines that had been opened to a LAN: "IPv4 address
            // 0.0.0.0 ... cannot be used as a target address". A client on this
            // machine wants loopback whatever the listener is bound to; only an
            // explicit ClientHost or --host names somewhere else.
            client.Host = host != null ? host
                : (!string.IsNullOrEmpty(cfg.ClientHost) ? cfg.ClientHost
                   : (cfg.BindIsLoopback() || cfg.Bind == "0.0.0.0" || cfg.Bind == "::"
                      ? "127.0.0.1" : cfg.Bind));
            client.Port = port > 0 ? port : (cfg.ClientPort > 0 ? cfg.ClientPort : cfg.Port);
            client.Pin = fingerprint != null ? fingerprint
                : (!string.IsNullOrEmpty(cfg.CertFingerprint) ? cfg.CertFingerprint : LocalPin(cfg));
            client.ApiKey = key != null ? key : ReadKey(keyFile, cfg);
            if (timeoutSeconds > 0) client.TimeoutMs = timeoutSeconds * 1000;

            try
            {
                switch (verb)
                {
                    case "status": return Status(client, raw);
                    case "services": return Services(client, raw);
                    case "health": return Simple(client, "GET", "/healthz", raw);
                    case "submit": return Submit(client, pos, passthrough, body, bodyFile, idem,
                                                 wait, intervalMs, outFile, artefact, raw);
                    case "jobs": return Job(client, pos, raw);
                    case "job": return Job(client, pos, raw);
                    case "watch": return Watch(client, pos, intervalMs, raw);
                    case "result": return Result(client, pos, outFile, artefact);
                    case "cancel": return Cancel(client, pos, raw);
                    case "mode": return SetMode(client, pos, raw);
                    case "asset": return Asset(client, pos, raw);
                    case "routes": return Simple(client, "GET", "/", raw);
                    default:
                        Console.Error.WriteLine("unknown command: " + verb);
                        Usage();
                        return 2;
                }
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("could not reach the runner at " + client.Host + ":" +
                    client.Port.ToString(CultureInfo.InvariantCulture) + " - " + ex.Message);
                return 1;
            }
        }

        /// The logon helper: report what only this session can see, once a second.
        ///
        /// Runs in the signed-in user's session, where GetForegroundWindow and
        /// GetLastInputInfo answer for the right desktop and Steam's running app
        /// id is in the reachable registry hive. Everything else -- the GPU, the
        /// counters, process scanning, the anti-cheat service -- the agent reads
        /// perfectly well for itself from session 0, so none of it is sent.
        ///
        /// It holds no policy and makes no decisions. It posts facts to loopback
        /// and the agent decides, which keeps one copy of the policy rather than
        /// two that can disagree.
        ///
        /// A failed post is not fatal and not retried hard: the agent expires a
        /// report after a few seconds and falls back to refusing while somebody
        /// is signed in, which is the safe answer. A helper that cannot reach the
        /// agent must not spin, and must never be the reason a game stutters.
        public static int Presence(Config cfg, int seconds)
        {
            var client = new RunnerClient();
            client.Host = "127.0.0.1";
            client.Port = cfg.ClientPort > 0 ? cfg.ClientPort : cfg.Port;
            client.Pin = !string.IsNullOrEmpty(cfg.CertFingerprint)
                ? cfg.CertFingerprint : LocalPin(cfg);
            client.ApiKey = ReadKey(null, cfg);
            client.TimeoutMs = 4000;

            // IT MUST BE IN THE CONSOLE SESSION, AND IT CHECKS EVERY TICK.
            //
            // This helper exists to supply the signals session 0 cannot read. A
            // copy of it running ANYWHERE ELSE -- started over SSH, from a
            // scheduled task, from a second desktop session -- reads its own
            // session and would report that nobody has touched the machine for
            // ten minutes while somebody is mid-match. That is not a degraded
            // report, it is the exact lie the whole design exists to prevent,
            // and it would arrive stamped as authoritative.
            //
            // Checked in the loop rather than once at startup because sessions
            // change underneath a process: fast user switching moves the console
            // elsewhere and this helper must go quiet the moment it does.
            DateTime until = seconds > 0
                ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
            int failures = 0;
            bool warned = false;
            while (DateTime.UtcNow < until)
            {
                SessionSignals ss = Win.Read();
                if (!ss.RunningInConsoleSession)
                {
                    if (!warned)
                    {
                        Console.Error.WriteLine(
                            "not reporting: this helper is in session "
                            + ss.OwnSessionId.ToString(CultureInfo.InvariantCulture)
                            + " and the console is session "
                            + ss.ConsoleSessionId.ToString(CultureInfo.InvariantCulture)
                            + ". It can only see the session it runs in, so anything"
                            + " it said about the user would be false. Start it at"
                            + " logon, from the Startup folder.");
                        warned = true;
                    }
                    Thread.Sleep(5000);
                    continue;
                }
                warned = false;
                LauncherSignals ls = Launchers.Read(cfg.GameProcessNames,
                                                    cfg.AntiCheatServices);
                var q = new StringBuilder("/v1/presence?");
                q.Append("input_idle_s=").Append(ss.InputIdleSeconds.ToString(CultureInfo.InvariantCulture));
                q.Append("&locked=").Append(ss.Locked ? "1" : "0");
                q.Append("&fullscreen=").Append(ss.ForegroundIsFullScreen ? "1" : "0");
                q.Append("&fg_process=").Append(Uri.EscapeDataString(ss.ForegroundProcess ?? ""));
                if (ls != null)
                {
                    q.Append("&steam_appid=").Append(ls.SteamRunningAppId.ToString(CultureInfo.InvariantCulture));
                    q.Append("&steam_appname=").Append(Uri.EscapeDataString(ls.SteamRunningAppName ?? ""));
                }
                try
                {
                    Response r = client.Send("POST", q.ToString(), new byte[0], null, null);
                    failures = r.Status == 200 ? 0 : failures + 1;
                }
                catch (Exception) { failures++; }

                // The agent is not up, or not up yet. Back off to once every five
                // seconds rather than hammering a socket that is not listening;
                // the first success returns to one second.
                Thread.Sleep(failures > 3 ? 5000 : 1000);
            }
            return 0;
        }

        static string LocalPin(Config cfg)
        {
            try
            {
                if (!File.Exists(cfg.CertPath)) return "";
                // Read the public certificate only. Loading it without the private
                // key means no CNG container is created, so running the CLI does
                // not litter the profile the way starting the server does.
                var c = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    File.ReadAllBytes(cfg.CertPath), (string)null,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.DefaultKeySet);
                string pin = Certs.Pin(c);
                c.Reset();
                return pin;
            }
            catch (Exception) { return ""; }
        }

        static string ReadKey(string keyFile, Config cfg)
        {
            try
            {
                if (!string.IsNullOrEmpty(keyFile) && File.Exists(keyFile))
                    return File.ReadAllText(keyFile).Trim();
            }
            catch (Exception) { }
            return cfg.EffectiveApiKey();
        }

        // -- subcommands ---------------------------------------------------------

        static int Simple(RunnerClient c, string method, string target, bool raw)
        {
            Response r = c.Send(method, target, null, null, null);
            Console.WriteLine(r.Text.TrimEnd());
            return r.Ok ? 0 : 1;
        }

        static int Status(RunnerClient c, bool raw)
        {
            Response r = c.Send("GET", "/v1/status", null, null, null);
            if (!r.Ok || raw) { Console.WriteLine(r.Text.TrimEnd()); return r.Ok ? 0 : 1; }
            string j = r.Text;
            Console.WriteLine("state       {0}", Json.PeekString(j, "state"));
            Console.WriteLine("mode        {0}", Json.PeekString(j, "mode"));
            Console.WriteLine("reason      {0}", Json.PeekString(j, "reason"));
            Console.WriteLine("running     {0}", Json.PeekString(j, "running_service") ?? "nothing");
            Console.WriteLine();
            Console.WriteLine("(--json for the whole document, including the GPU sample)");
            return 0;
        }

        static int Services(RunnerClient c, bool raw)
        {
            Response r = c.Send("GET", "/v1/services", null, null, null);
            Console.WriteLine(r.Text.TrimEnd());
            return r.Ok ? 0 : 1;
        }

        static int Submit(RunnerClient c, List<string> pos, List<string> passthrough,
                          string body, string bodyFile, string idem, bool wait, int intervalMs,
                          string outFile, string artefact, bool raw)
        {
            if (pos.Count < 2) { Console.Error.WriteLine("usage: idlegpu submit <service> [--body JSON | --body-file F | -- ARGS...]"); return 2; }
            string service = pos[1];
            string payload;
            if (!string.IsNullOrEmpty(bodyFile)) payload = File.ReadAllText(bodyFile).Trim();
            else if (!string.IsNullOrEmpty(body)) payload = body.Trim();
            else if (passthrough.Count > 0)
            {
                // Everything after `--`, verbatim, as an argument vector. This is
                // the whole reason the CLI is first class: for a service that is a
                // command line tool, the job IS a command line.
                var sb = new StringBuilder("{\"argv\":[");
                for (int i = 0; i < passthrough.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(Json.Esc(passthrough[i]));
                }
                sb.Append("]}");
                payload = sb.ToString();
            }
            else payload = "{}";

            // Checked HERE as well as on the server, because the client is where
            // the shell that mangled it can be named. A 400 from across a network
            // saying "malformed JSON" sends somebody looking at the runner; this
            // says the argument never left their own machine intact.
            if (!Json.LooksLikeObject(payload))
            {
                Console.Error.WriteLine("that is not a well-formed JSON object:");
                Console.Error.WriteLine("  " + payload);
                Console.Error.WriteLine();
                Console.Error.WriteLine("PowerShell strips double quotes when it passes an argument to a");
                Console.Error.WriteLine("native program, which is almost always the cause. Either:");
                Console.Error.WriteLine("  idlegpu submit " + service + " --body-file job.json");
                Console.Error.WriteLine("  idlegpu submit " + service + " --body '{\"text\":\"hello\"}'");
                return 2;
            }

            var extra = new List<string>();
            if (!string.IsNullOrEmpty(idem)) extra.Add("Idempotency-Key: " + idem);
            Response r = c.Send("POST", "/v1/services/" + service + "/jobs",
                new UTF8Encoding(false).GetBytes(payload), "application/json", extra.ToArray());
            if (!r.Ok) { Console.Error.WriteLine(r.Text.TrimEnd()); return 1; }
            string jobId = Json.PeekString(r.Text, "job_id");
            if (raw) Console.WriteLine(r.Text.TrimEnd());
            else Console.WriteLine(jobId);
            if (!wait) return 0;
            int rc = PollUntilDone(c, service, jobId, intervalMs, raw);
            if (rc == 0 && !string.IsNullOrEmpty(outFile))
                return Fetch(c, service, jobId, outFile, artefact);
            return rc;
        }

        static int Job(RunnerClient c, List<string> pos, bool raw)
        {
            if (pos.Count < 3) { Console.Error.WriteLine("usage: idlegpu job <service> <job-id>"); return 2; }
            Response r = c.Send("GET", "/v1/services/" + pos[1] + "/jobs/" + pos[2], null, null, null);
            Console.WriteLine(r.Text.TrimEnd());
            return r.Ok ? 0 : 1;
        }

        /// Poll, rather than hold a streaming connection open.
        ///
        /// Server-sent progress is deliberately not implemented: a long lived
        /// response would occupy one of the listener's bounded connection slots for
        /// the whole life of a job, which is the wrong thing to spend them on when
        /// polling a cheap endpoint answers the same question.
        static int Watch(RunnerClient c, List<string> pos, int intervalMs, bool raw)
        {
            if (pos.Count < 3) { Console.Error.WriteLine("usage: idlegpu watch <service> <job-id>"); return 2; }
            return PollUntilDone(c, pos[1], pos[2], intervalMs, raw);
        }

        static int PollUntilDone(RunnerClient c, string service, string jobId, int intervalMs, bool raw)
        {
            string last = "";
            while (true)
            {
                Response r = c.Send("GET", "/v1/services/" + service + "/jobs/" + jobId, null, null, null);
                if (!r.Ok) { Console.Error.WriteLine(r.Text.TrimEnd()); return 1; }
                string st = Json.PeekString(r.Text, "status") ?? "unknown";
                if (st != last)
                {
                    Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + st);
                    last = st;
                }
                if (st == "done") { if (raw) Console.WriteLine(r.Text.TrimEnd()); return 0; }
                if (st == "failed" || st == "cancelled") { Console.WriteLine(r.Text.TrimEnd()); return 1; }
                Thread.Sleep(intervalMs < 200 ? 200 : intervalMs);
            }
        }

        static int Result(RunnerClient c, List<string> pos, string outFile, string artefact)
        {
            if (pos.Count < 3) { Console.Error.WriteLine("usage: idlegpu result <service> <job-id> -o FILE"); return 2; }
            if (string.IsNullOrEmpty(outFile)) { Console.Error.WriteLine("-o FILE is required"); return 2; }
            return Fetch(c, pos[1], pos[2], outFile, artefact);
        }

        static int Fetch(RunnerClient c, string service, string jobId, string outFile, string artefact)
        {
            string target = "/v1/services/" + service + "/jobs/" + jobId + "/result";
            if (!string.IsNullOrEmpty(artefact)) target += "?artefact=" + artefact;
            Response r = c.Download(target, outFile);
            if (!r.Ok) { Console.Error.WriteLine(r.Text.TrimEnd()); return 1; }
            string bytes;
            r.Headers.TryGetValue("X-Saved-Bytes", out bytes);
            Console.Error.WriteLine("wrote " + outFile + " (" + (bytes ?? "?") + " bytes)");
            return 0;
        }

        static int Cancel(RunnerClient c, List<string> pos, bool raw)
        {
            if (pos.Count < 3) { Console.Error.WriteLine("usage: idlegpu cancel <service> <job-id>"); return 2; }
            Response r = c.Send("DELETE", "/v1/services/" + pos[1] + "/jobs/" + pos[2], null, null, null);
            Console.WriteLine(r.Text.TrimEnd());
            return r.Ok ? 0 : 1;
        }

        static int SetMode(RunnerClient c, List<string> pos, bool raw)
        {
            if (pos.Count < 2) { Console.Error.WriteLine("usage: idlegpu mode Auto|AlwaysOn|Off"); return 2; }
            byte[] b = new UTF8Encoding(false).GetBytes(
                Json.Obj(Json.P("mode", Json.Esc(pos[1]))));
            Response r = c.Send("POST", "/v1/mode", b, "application/json", null);
            Console.WriteLine(r.Text.TrimEnd());
            return r.Ok ? 0 : 1;
        }

        static int Asset(RunnerClient c, List<string> pos, bool raw)
        {
            if (pos.Count >= 3 && pos[1].ToLowerInvariant() == "put")
            {
                byte[] data = File.ReadAllBytes(pos[2]);
                string digest = JobStore.Sha256Hex(data);
                // Ask before uploading. A reference clip crosses the wire exactly
                // once however many jobs use it, which is the entire point of
                // addressing it by content.
                Response head = c.Send("HEAD", "/v1/assets/" + digest, null, null, null);
                if (head.Status == 200)
                {
                    Console.WriteLine(digest);
                    Console.Error.WriteLine("already held by the runner; nothing uploaded");
                    return 0;
                }
                Response r = c.Send("POST", "/v1/assets", data, "application/octet-stream", null);
                if (!r.Ok) { Console.Error.WriteLine(r.Text.TrimEnd()); return 1; }
                Console.WriteLine(Json.PeekString(r.Text, "sha256"));
                return 0;
            }
            if (pos.Count >= 3 && pos[1].ToLowerInvariant() == "has")
            {
                Response r = c.Send("HEAD", "/v1/assets/" + pos[2], null, null, null);
                Console.WriteLine(r.Status == 200 ? "present" : "absent");
                return r.Status == 200 ? 0 : 1;
            }
            Console.Error.WriteLine("usage: idlegpu asset put <file> | idlegpu asset has <sha256>");
            return 2;
        }

        // -- the opt-in surface ---------------------------------------------------

        static int Service(List<string> pos, Config cfg, bool raw)
        {
            string what = pos.Count > 1 ? pos[1].ToLowerInvariant() : "list";
            if (what == "list" || what == "cost") return ServiceList(cfg, what == "cost", raw);

            if (pos.Count < 3)
            {
                Console.Error.WriteLine("usage: idlegpu service " + what + " <id>");
                return 2;
            }
            string id = pos[2];
            ServiceDef d = cfg.Service(id);
            if (d == null)
            {
                // Naming what IS known is the whole difference between a usable
                // error and a support question. A typo and an uninstalled service
                // produce the same symptom otherwise.
                Console.Error.WriteLine("worker.ini has no [service." + id + "] section.");
                Console.Error.WriteLine("known services: " + (cfg.Services.Count == 0
                    ? "(none; see worker.ini.example)"
                    : string.Join(", ", Ids(cfg))));
                return 2;
            }

            switch (what)
            {
                case "install": return ServiceInstall(d, cfg);
                case "enable": return ServiceEnable(d, cfg, true);
                case "disable": return ServiceEnable(d, cfg, false);
                case "remove": return ServiceRemove(d, cfg);
                default:
                    Console.Error.WriteLine("unknown: idlegpu service " + what);
                    Console.Error.WriteLine("try: list, cost, install, enable, disable, remove");
                    return 2;
            }
        }

        static string[] Ids(Config cfg)
        {
            var outp = new List<string>();
            foreach (ServiceDef s in cfg.Services) outp.Add(s.Id);
            return outp.ToArray();
        }

        static int ServiceList(Config cfg, bool measureDisk, bool raw)
        {
            if (cfg.Services.Count == 0)
            {
                Console.WriteLine("no services are configured.");
                Console.WriteLine("worker.ini.example ships sections for the ones this build knows about;");
                Console.WriteLine("copy the one you want into worker.ini and run: idlegpu service install <id>");
                return 0;
            }

            if (raw)
            {
                var sb = new StringBuilder("{\"services\":[");
                for (int i = 0; i < cfg.Services.Count; i++)
                {
                    ServiceDef d = cfg.Services[i];
                    if (i > 0) sb.Append(",");
                    sb.Append(Install.Json(d, Install.Status(d, measureDisk), false, 0, null,
                        d.YieldGraceSeconds > 0 ? d.YieldGraceSeconds : cfg.YieldGraceSeconds));
                }
                sb.Append("]}");
                Console.WriteLine(sb.ToString());
                return 0;
            }

            long total = 0;
            Console.WriteLine("id                state         disk        what it costs to install");
            Console.WriteLine("----------------- ------------- ----------- ------------------------");
            foreach (ServiceDef d in cfg.Services)
            {
                ServiceStatus st = Install.Status(d, measureDisk);
                if (st.DiskBytes > 0) total += st.DiskBytes;
                // The three states as one word each, because the point of keeping
                // them apart is that a person can see at a glance which of them is
                // theirs to change.
                string state = !st.Installed ? "known" : (st.Enabled ? "ready" : "installed");
                Console.WriteLine(
                    Pad(d.Id, 17) + " " + Pad(state, 13) + " " +
                    Pad(st.DiskBytes < 0 ? "-" : Install.Human(st.DiskBytes), 11) + " " +
                    (st.Installed ? "(installed)"
                     : (string.IsNullOrEmpty(d.SizeHint) ? "nothing to fetch" : d.SizeHint)));
            }
            Console.WriteLine();
            if (measureDisk)
            {
                Console.WriteLine("services on disk: " + Install.Human(total) +
                    "  under " + cfg.RuntimeRoot);
                Console.WriteLine("everything else this program uses: " +
                    Install.Human(Install.DirectoryBytes(cfg.DataDir) - total));
            }
            else
            {
                Console.WriteLine("known     = this build has a section for it; nothing downloaded, nothing running");
                Console.WriteLine("installed = provisioned, real disk committed, but not opted in");
                Console.WriteLine("ready     = installed and enabled; the scheduler will run it when the GPU is free");
                Console.WriteLine();
                Console.WriteLine("`idlegpu service cost` measures what each one is actually using.");
            }
            return 0;
        }

        static string Pad(string s, int n)
        {
            if (s == null) s = "";
            return s.Length >= n ? s : s + new string(' ', n - s.Length);
        }

        static int ServiceInstall(ServiceDef d, Config cfg)
        {
            if (Install.IsInstalled(d))
            {
                Console.WriteLine(d.Id + " is already installed (" +
                    Install.Human(Install.DiskBytes(d)) + " in " + d.InstallDir + ")");
            }
            else
            {
                int rc = Install.RunProvision(d, cfg, cfg.ScriptsRoot, Console.Error.WriteLine);
                if (rc != 0) return rc;
                Console.WriteLine();
                Console.WriteLine(d.Id + " installed: " + Install.Human(Install.DiskBytes(d)) +
                    " in " + d.InstallDir);
            }
            // Installing IS opting in. Making somebody type two commands to get one
            // outcome they already asked for is ceremony; `service disable` is right
            // there for the case where they want it fetched but not running.
            return ServiceEnable(d, cfg, true);
        }

        static int ServiceEnable(ServiceDef d, Config cfg, bool on)
        {
            if (on && !Install.IsInstalled(d))
            {
                Console.Error.WriteLine(d.Id + " is not installed, so enabling it would do nothing.");
                Console.Error.WriteLine("run: idlegpu service install " + d.Id);
                if (!string.IsNullOrEmpty(d.SizeHint))
                    Console.Error.WriteLine("that will download " + d.SizeHint);
                return 1;
            }
            string err;
            if (!Install.SetEnabled(cfg.ConfigPath, d.Id, on, out err))
            {
                Console.Error.WriteLine("could not write " + cfg.ConfigPath + ": " + err);
                return 1;
            }
            d.Enabled = on;
            Console.WriteLine(d.Id + " is now " + (on ? "enabled" : "disabled") + " in " + cfg.ConfigPath);
            // The agent reads worker.ini once, at start, so a mode written here is a
            // mode that takes effect next time. Saying so beats leaving somebody to
            // wonder why `idlegpu services` still disagrees with what they just did.
            Console.WriteLine("restart the agent for this to take effect.");
            return 0;
        }

        static int ServiceRemove(ServiceDef d, Config cfg)
        {
            long had = Install.DiskBytes(d);
            if (had == 0 && !Install.IsInstalled(d))
            {
                Console.WriteLine(d.Id + " has nothing installed; nothing to reclaim.");
                return 0;
            }
            string err;
            if (!Install.Remove(d, out err))
            {
                // Almost always a file the running controller still has open. Saying
                // which fix applies is cheaper than making somebody guess.
                Console.Error.WriteLine("could not remove " + d.InstallDir + ": " + err);
                Console.Error.WriteLine("if the agent is running, stop it first: a controller holds files open.");
                return 1;
            }
            Console.WriteLine("removed " + d.InstallDir + ", reclaiming " + Install.Human(had));
            string e2;
            Install.SetEnabled(cfg.ConfigPath, d.Id, false, out e2);
            Console.WriteLine(d.Id + " is now disabled; its [service." + d.Id + "] section is still in worker.ini,");
            Console.WriteLine("so `idlegpu service install " + d.Id + "` puts it back.");
            return 0;
        }

        public static void Usage()
        {
            Console.WriteLine("idlegpu - lend a gaming PC's GPU to whatever you like, and give it straight back");
            Console.WriteLine();
            Console.WriteLine("agent:");
            Console.WriteLine("  idlegpu --tray                     the agent with a tray icon (default)");
            Console.WriteLine("  idlegpu --serve                    the agent headless, with the listener");
            Console.WriteLine("  idlegpu --once                     one JSON snapshot of the signals, then exit");
            Console.WriteLine("  idlegpu --watch [seconds]          one line per second, then exit");
            Console.WriteLine("  idlegpu --calibrate [csv] [mins]   record every signal to a CSV, then exit");
            Console.WriteLine("  idlegpu --config <path>            settings file (default worker.ini beside the exe)");
            Console.WriteLine();
            Console.WriteLine("services (local; nothing is downloaded until you ask):");
            Console.WriteLine("  idlegpu service list               known / installed / ready, and what each would cost");
            Console.WriteLine("  idlegpu service cost               measure what each one is using on this disk");
            Console.WriteLine("  idlegpu service install <id>       fetch it into the contained directory, then enable it");
            Console.WriteLine("  idlegpu service disable <id>       stop running it; keep it on disk");
            Console.WriteLine("  idlegpu service remove <id>        delete it and reclaim the disk");
            Console.WriteLine();
            Console.WriteLine("client:");
            Console.WriteLine("  idlegpu status                     what the runner thinks is going on");
            Console.WriteLine("  idlegpu services                   what it can do, from each controller's manifest");
            Console.WriteLine("  idlegpu submit <svc> [--body JSON | --body-file F | -- ARGS...]");
            Console.WriteLine("                                     [--idempotency-key K] [--wait] [-o FILE]");
            Console.WriteLine("  idlegpu job <svc> <job>            one job's status");
            Console.WriteLine("  idlegpu watch <svc> <job>          poll until it finishes");
            Console.WriteLine("  idlegpu result <svc> <job> -o F    stream an artefact out [--artefact NAME]");
            Console.WriteLine("  idlegpu cancel <svc> <job>         withdraw it, or ask the controller to stop");
            Console.WriteLine("  idlegpu mode Auto|AlwaysOn|Off     change the mode");
            Console.WriteLine("  idlegpu asset put <file>           store a blob, print its sha256");
            Console.WriteLine("  idlegpu fingerprint                the certificate digest every client pins");
            Console.WriteLine();
            Console.WriteLine("connecting to another machine:");
            Console.WriteLine("  --host H --port N --fingerprint SHA256 --key-file F");
            Console.WriteLine();
            Console.WriteLine("curl works too. The framing is real HTTP/1.1:");
            Console.WriteLine("  curl --cacert server.pem -H \"Authorization: Bearer $KEY\" https://host:47600/v1/status");
        }
    }
}
