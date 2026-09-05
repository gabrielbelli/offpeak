// The queue, which is a directory, and the content addressed asset store.
//
// THE WHOLE INTER PROCESS PROTOCOL IS IN THIS FILE AND IT IS FILENAMES. There is
// no socket between the agent and a service controller, no JSON parser in the
// agent, and no shared library a controller has to link. A controller is any
// program that can list a directory, rename a file and read one line from stdin.
//
//   <QueueDir>/pending/<job>.json      the agent wrote a lease. Work to do.
//   <QueueDir>/working/<job>.json      the controller claimed it, by rename.
//   <QueueDir>/cancel/<job>            the agent asks for this job to stop.
//   <QueueDir>/done/<job>.done.json    the controller finished it.
//   <QueueDir>/done/<job>.failed.json  the controller could not.
//   <QueueDir>/done/<job>.cancelled.json
//   <QueueDir>/done/<job>.<anything>   an artefact. Bytes. The agent never opens it.
//   <QueueDir>/service.json            the manifest, written by the controller.
//
// WHY THE STATUS IS IN THE FILE NAME AND NOT IN THE FILE. The agent would
// otherwise need a JSON parser to answer "is this job done", and there is no JSON
// parser available to a csc.exe build without dragging in System.Web.Extensions.
// A hand written one on the request path of a network listener is a bad trade for
// a question that a directory listing already answers. So the agent globs, and
// the terminal record's CONTENTS stay entirely the controller's business: the
// agent passes them through to the client verbatim and never looks inside.
//
// The same rule holds in the other direction. A submitted job body is spliced
// into the lease unparsed. What a job MEANS is the service's business, which is
// what makes adding Stable Diffusion or Hashcat a matter of writing a controller
// rather than changing this file.
//
// PATH SAFETY. Job ids are minted here, never chosen by a client, and every id
// that arrives from the network is checked against Safe() before it is allowed
// near Path.Combine. An artefact name from a query string gets the same
// treatment. tests/Tests.cs has a case named after each of those, because a
// listener that will accept "../../worker.ini" is a file server for the whole
// profile.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IdleGpu
{
    public enum JobState { Unknown, Queued, Running, Cancelling, Done, Failed, Cancelled }

    public class JobView
    {
        public string Id = "";
        public string Service = "";
        public JobState State = JobState.Unknown;
        public List<string> Artefacts = new List<string>();
        public string Record;          // the controller's terminal record, verbatim, or null
    }

    public class JobStore
    {
        public readonly string ServiceId;
        public readonly string Root;
        readonly string _pending, _working, _done, _cancel, _index;

        public JobStore(string serviceId, string root)
        {
            ServiceId = serviceId;
            Root = root;
            _pending = Path.Combine(root, "pending");
            _working = Path.Combine(root, "working");
            _done = Path.Combine(root, "done");
            _cancel = Path.Combine(root, "cancel");
            _index = Path.Combine(root, "index");
        }

        public string PendingDir { get { return _pending; } }
        public string WorkingDir { get { return _working; } }
        public string DoneDir { get { return _done; } }
        public string CancelDir { get { return _cancel; } }
        public string ManifestPath { get { return Path.Combine(Root, "service.json"); } }

        public void EnsureDirs()
        {
            foreach (string d in new string[] { Root, _pending, _working, _done, _cancel, _index })
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
        }

        /// A name that may be joined onto a path. Deliberately strict: this is the
        /// only thing standing between a URL and the user's profile.
        public static bool Safe(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length > maxLen) return false;
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') continue;
                if (c >= 'A' && c <= 'Z') continue;
                if (c >= '0' && c <= '9') continue;
                if (c == '-' || c == '_' || c == '.') continue;
                return false;
            }
            // "." and ".." pass the character check and must not pass this one.
            if (s == "." || s == "..") return false;
            if (s.StartsWith(".")) return false;
            return !IsReservedDeviceName(s);
        }

        /// The MS-DOS device names, which Windows still resolves ANYWHERE in the
        /// file system and with any extension: "done\\con.wav" opens the console,
        /// not a file, and "nul.json" is a bit bucket that swallows a write and
        /// returns success. So a job or artefact called con, nul, aux, prn, com3 or
        /// lpt1 is not a traversal but it is still a name that does not mean what
        /// the caller and this program both think it means.
        ///
        /// The characters after the first dot do not save you, which is why this
        /// tests the stem rather than the whole string.
        static bool IsReservedDeviceName(string s)
        {
            int dot = s.IndexOf('.');
            string stem = (dot < 0 ? s : s.Substring(0, dot)).ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                stem[3] >= '1' && stem[3] <= '9') return true;
            return false;
        }

        public static bool SafeJobId(string s) { return Safe(s, 64); }
        public static bool SafeServiceId(string s) { return Safe(s, 64); }

        /// A new job id. Minted here so no client ever supplies one.
        public static string NewJobId()
        {
            return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }

        // -- submission ----------------------------------------------------------

        /// The job id a live idempotency key already claimed, or null.
        ///
        /// WHY THE KEY IS HASHED BEFORE IT BECOMES A FILE NAME. The key is client
        /// text. Hashing it means no client controlled string is ever a path
        /// component, and it also means a key can be any length and any alphabet
        /// without this code caring. The first client of this runner sends its own
        /// job uuid as the key, which is exactly the intended use: a retry after a
        /// yield must resume the same job, not speak the same sentence twice.
        public string LookupIdempotent(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string p = Path.Combine(_index, Sha256Hex(new UTF8Encoding(false).GetBytes(key)) + ".txt");
            try
            {
                if (!File.Exists(p)) return null;
                string id = File.ReadAllText(p).Trim();
                return SafeJobId(id) ? id : null;
            }
            catch (Exception) { return null; }
        }

        void RecordIdempotent(string key, string jobId)
        {
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                File.WriteAllText(
                    Path.Combine(_index, Sha256Hex(new UTF8Encoding(false).GetBytes(key)) + ".txt"),
                    jobId, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// Write one lease into pending/. This is the entire cost of a submission
        /// on the listener thread: build a string, write a file, rename it. The
        /// policy loop never learns that it happened.
        ///
        /// Written to a temp name and renamed, because a controller polling
        /// pending/ must never see a half written lease. Rename within a directory
        /// is atomic enough on NTFS for a reader that only globs *.json.
        public string Submit(string jobId, string paramsJson, string idempotencyKey, string assetsDir)
        {
            EnsureDirs();
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append(Json.P("job_id", Json.Esc(jobId))).Append(",");
            sb.Append(Json.P("service", Json.Esc(ServiceId))).Append(",");
            sb.Append(Json.P("submitted_at", Json.Esc(
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)))).Append(",");
            // Where content addressed inputs live. A controller resolves a sha256
            // it was given in params to assets_dir/<sha256>. The agent puts the
            // path in the lease rather than making every controller reimplement
            // the layout, and rather than making the client send bytes twice.
            sb.Append(Json.P("assets_dir", Json.Esc(assetsDir))).Append(",");
            sb.Append("\"params\":").Append(paramsJson);
            sb.Append("}\n");

            string tmp = Path.Combine(_pending, jobId + ".json.tmp");
            string fin = Path.Combine(_pending, jobId + ".json");
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(fin)) File.Delete(fin);
            File.Move(tmp, fin);
            RecordIdempotent(idempotencyKey, jobId);
            return jobId;
        }

        // -- reading -------------------------------------------------------------

        /// Put leases whose controller died back where the scheduler can see them.
        ///
        /// THE DEADLOCK THIS BREAKS, measured on spring on a real GPU yield and
        /// not obvious from either side alone.
        ///
        /// A controller killed mid-unit leaves its lease in working/. The shared
        /// controller library requeues orphans when it STARTS, which is the right
        /// place for it to happen and is useless here, because the scheduler only
        /// starts a controller for a service with work in pending/ - and the only
        /// lease there is is the orphan, in working/. So: the job never resumes,
        /// the scheduler sees an idle service for ever, and the client polls a
        /// job that is queued and never runs. Each half is individually correct.
        ///
        /// The agent is the one that knows both facts (the lease is there, the
        /// controller is not), so the agent is where this belongs. Called only
        /// from the scheduler thread, never from the policy loop: it enumerates
        /// directories, which is a syscall per service, and nothing that costs a
        /// syscall may sit on the path that gives somebody their GPU back.
        ///
        /// Safe to call only when the controller is known NOT to be running,
        /// which is the caller's contract; otherwise it would snatch a lease out
        /// from under a controller that is working on it.
        public int RequeueOrphans()
        {
            int moved = 0;
            try
            {
                foreach (string f in Directory.GetFiles(_working, "*.json"))
                {
                    string name = Path.GetFileName(f);
                    if (name.EndsWith(".part", StringComparison.Ordinal)) continue;
                    string id = Path.GetFileNameWithoutExtension(name);
                    // A job that reached a terminal record is finished; its lease
                    // is litter rather than work, so it is removed and not rerun.
                    if (File.Exists(Path.Combine(_done, id + ".done.json")) ||
                        File.Exists(Path.Combine(_done, id + ".failed.json")) ||
                        File.Exists(Path.Combine(_done, id + ".cancelled.json")))
                    {
                        try { File.Delete(f); } catch (Exception) { }
                        continue;
                    }
                    string back = Path.Combine(_pending, name);
                    try
                    {
                        if (File.Exists(back)) File.Delete(f);
                        else { File.Move(f, back); moved++; }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return moved;
        }

        public int QueuedCount()
        {
            try { return Directory.GetFiles(_pending, "*.json").Length; }
            catch (Exception) { return 0; }
        }

        public JobView Look(string jobId, int maxRecordBytes)
        {
            return Look(jobId, maxRecordBytes, true);
        }

        /// `controllerRunning` is what turns an orphaned lease back into queued.
        ///
        /// THE DEFECT THIS PREVENTS, measured on spring during a real GPU yield.
        /// A speech controller mid-generate() cannot check the yield flag - the
        /// model has no interruption point inside it - so the job object kills it,
        /// which is the designed path and reclaimed 4013 MiB of VRAM in 2.46 s.
        /// But a killed process cannot tidy up after itself, so its lease is left
        /// in working/ and this method read that as Running. Nothing was running.
        ///
        /// A client polling that job sees "running" for ever, on a machine whose
        /// owner is playing a game and whose GPU will not be free for a minute and
        /// a half. The truth is "queued": the controller library requeues orphans
        /// the next time it starts, so the job really will resume, and queued is
        /// what a caller needs to see in order to keep waiting rather than give up
        /// or, worse, submit it again.
        ///
        /// A lease in working/ WITH a live controller is genuinely running, which
        /// is why this is a parameter rather than an assumption either way.
        public JobView Look(string jobId, int maxRecordBytes, bool controllerRunning)
        {
            var v = new JobView();
            v.Id = jobId;
            v.Service = ServiceId;
            if (!SafeJobId(jobId)) return v;

            string[] terminal = new string[] { "done", "failed", "cancelled" };
            foreach (string t in terminal)
            {
                string p = Path.Combine(_done, jobId + "." + t + ".json");
                if (!File.Exists(p)) continue;
                v.State = t == "done" ? JobState.Done
                        : t == "failed" ? JobState.Failed : JobState.Cancelled;
                v.Record = ReadJsonBlob(p, maxRecordBytes);
                break;
            }

            try
            {
                foreach (string f in Directory.GetFiles(_done, jobId + ".*"))
                {
                    string name = Path.GetFileName(f);
                    if (name.EndsWith(".done.json", StringComparison.Ordinal) ||
                        name.EndsWith(".failed.json", StringComparison.Ordinal) ||
                        name.EndsWith(".cancelled.json", StringComparison.Ordinal)) continue;
                    v.Artefacts.Add(name);
                }
                v.Artefacts.Sort(StringComparer.Ordinal);
            }
            catch (Exception) { }

            if (v.State != JobState.Unknown) return v;
            if (File.Exists(Path.Combine(_working, jobId + ".json")))
            {
                if (!controllerRunning)
                {
                    // An orphaned lease. See the note on this overload: the
                    // controller was killed mid-unit and will requeue this the
                    // next time it starts. Reporting it as running would leave a
                    // client watching a process that does not exist.
                    v.State = JobState.Queued;
                    return v;
                }
                v.State = File.Exists(Path.Combine(_cancel, jobId))
                    ? JobState.Cancelling : JobState.Running;
                return v;
            }
            if (File.Exists(Path.Combine(_pending, jobId + ".json"))) v.State = JobState.Queued;
            return v;
        }

        /// The full path of one artefact, or null if the name is unsafe or absent.
        public string ArtefactPath(string jobId, string name)
        {
            if (!SafeJobId(jobId) || !Safe(name, 200)) return null;
            if (!name.StartsWith(jobId + ".", StringComparison.Ordinal)) return null;
            string p = Path.Combine(_done, name);
            return File.Exists(p) ? p : null;
        }

        /// Cancel. Pending work is withdrawn here and now; running work is asked,
        /// because only the controller knows whether it can stop.
        public bool Cancel(string jobId)
        {
            if (!SafeJobId(jobId)) return false;
            string lease = Path.Combine(_pending, jobId + ".json");
            if (File.Exists(lease))
            {
                try
                {
                    File.Delete(lease);
                    File.WriteAllText(Path.Combine(_done, jobId + ".cancelled.json"),
                        Json.Obj(Json.P("job_id", Json.Esc(jobId)),
                                 Json.P("status", Json.Esc("cancelled")),
                                 Json.P("cancelled_at", Json.Esc(
                                     DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))),
                                 Json.P("cancelled_before_start", "true")) + "\n",
                        new UTF8Encoding(false));
                    return true;
                }
                catch (Exception) { return false; }
            }
            if (File.Exists(Path.Combine(_working, jobId + ".json")))
            {
                try
                {
                    EnsureDirs();
                    File.WriteAllText(Path.Combine(_cancel, jobId), "", new UTF8Encoding(false));
                    return true;
                }
                catch (Exception) { return false; }
            }
            return false;
        }

        /// The manifest the controller published about itself, verbatim, or null.
        public string Manifest(int maxBytes) { return ReadJsonBlob(ManifestPath, maxBytes); }

        /// Read a file that should contain one JSON object and hand it back
        /// untouched, or null.
        ///
        /// The only inspection is the cheapest possible sanity check: it must
        /// start with '{' and end with '}' and be under a cap. That is not
        /// validation and is not claimed to be. It exists so that a controller
        /// writing a truncated or half flushed file cannot make this agent emit a
        /// broken response body, which would look like an agent bug rather than a
        /// controller bug. A controller that writes syntactically valid nonsense
        /// gets its nonsense passed through, which is correct: the agent is not
        /// the author of that document.
        public static string ReadJsonBlob(string path, int maxBytes)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var fi = new FileInfo(path);
                if (fi.Length == 0 || fi.Length > maxBytes) return null;
                string t = File.ReadAllText(path, new UTF8Encoding(false)).Trim();
                if (t.Length < 2 || t[0] != '{' || t[t.Length - 1] != '}') return null;
                return t;
            }
            catch (Exception) { return null; }
        }

        public static string StateName(JobState s)
        {
            switch (s)
            {
                case JobState.Queued: return "queued";
                case JobState.Running: return "running";
                case JobState.Cancelling: return "cancelling";
                case JobState.Done: return "done";
                case JobState.Failed: return "failed";
                case JobState.Cancelled: return "cancelled";
                default: return "unknown";
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(data);
                var sb = new StringBuilder(d.Length * 2);
                foreach (byte b in d) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /// Inputs that would otherwise cross the wire once per job.
    ///
    /// The first client of this runner sends a voice reference clip that lives in
    /// a directory on a NAS. The remote machine has no such directory and never
    /// will, so the BYTES have to travel. They travel once: the client hashes the
    /// clip, asks HEAD /v1/assets/<sha256>, and uploads only on a miss.
    ///
    /// The digest is the identity and any name is just a label, which is also what
    /// keeps one person's cloned voice names out of a shipped open source
    /// repository.
    public class AssetStore
    {
        public readonly string Dir;
        public AssetStore(string dir) { Dir = dir; }

        public void EnsureDir() { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); }

        public static bool SafeDigest(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        public string PathOf(string digest)
        {
            return SafeDigest(digest) ? Path.Combine(Dir, digest) : null;
        }

        public bool Has(string digest)
        {
            string p = PathOf(digest);
            return p != null && File.Exists(p);
        }

        public long SizeOf(string digest)
        {
            string p = PathOf(digest);
            try { return p != null && File.Exists(p) ? new FileInfo(p).Length : -1; }
            catch (Exception) { return -1; }
        }

        /// Store bytes under their own digest. Already present is a no-op, which
        /// is the point of content addressing: an upload that races another upload
        /// of the same clip cannot corrupt anything.
        public string Put(byte[] data)
        {
            EnsureDir();
            string digest = JobStore.Sha256Hex(data);
            string fin = Path.Combine(Dir, digest);
            if (File.Exists(fin)) return digest;
            string tmp = fin + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, data);
            try
            {
                if (File.Exists(fin)) File.Delete(tmp);
                else File.Move(tmp, fin);
            }
            catch (Exception)
            {
                try { File.Delete(tmp); } catch (Exception) { }
                if (!File.Exists(fin)) throw;
            }
            return digest;
        }
    }
}
