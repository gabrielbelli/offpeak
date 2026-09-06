// Opting in: the three states, what a service costs, and reclaiming it.
//
// THE PROMISE THIS FILE KEEPS is that a base install stays small. The agent, the
// tray, the listener, the policy and the client are 426 KB on disk, and
// that is the whole of what somebody gets for saying yes to lending their idle
// GPU. Speech costs about six gigabytes; image generation will cost more; a
// person who never asked for either must never pay for either.
//
// So provisioning is part of the SERVICE contract, not the base install. A
// service declares what it needs (ServiceDef.Provision), where it goes
// (InstallDir), how it proves it finished (ReadyMarker) and roughly what it will
// cost before you commit (SizeHint). Nothing in the agent runs Provision. The
// only thing that ever does is a person typing `idlegpu service install <id>`.
//
// THREE STATES, NEVER CONFLATED. This is the distinction the API exists to make,
// and it matters because two of these are the owner's to fix and one is not:
//
//   KNOWN      this build has a [service.<id>] section for it. It costs nothing,
//              it has downloaded nothing, and it will not run. Fixable: install it.
//   INSTALLED  provisioning finished and left its ReadyMarker. Real disk is now
//              committed and `idlegpu service cost` will say how much.
//   READY      installed AND enabled AND registered with the agent, so the
//              scheduler will launch it the moment the policy allows.
//
// and separately, orthogonally, and NOT a property of the service at all:
//
//   RUNNABLE   ready AND the GPU policy currently permits work.
//
// A client asking "can you speak for me" gets a different answer to "no, speech
// is not installed on this runner" than to "no, somebody is playing a game". The
// first is a five minute fix by the machine's owner; the second is nobody's to
// fix and will clear on its own. Collapsing them into one boolean is how a client
// ends up retrying for ever against a runner that was never going to say yes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleGpu
{
    /// What is true about one service right now.
    public class ServiceStatus
    {
        public string Id = "";
        public bool Enabled;
        public bool Installed;

        /// True when the service needs nothing fetched at all, so Installed is
        /// true the moment the controller script is on disk. services/echo is the
        /// example: a controller that is one file has no provisioning step and
        /// pretending it does would make the simplest case the fiddliest.
        public bool NeedsNoProvisioning;

        /// -1 when not measured. Measuring is a recursive directory walk, so it is
        /// done on demand and never on a hot path.
        public long DiskBytes = -1;

        /// Why the service will not take a job, in one machine readable word, or
        /// null when it will. Deliberately about the SERVICE only: whether the GPU
        /// is free is a property of the machine and is reported separately, because
        /// conflating the two is the exact defect this file's header describes.
        public string NotReadyReason;
    }

    public static class Install
    {
        // -- state ---------------------------------------------------------------

        public static bool IsInstalled(ServiceDef s)
        {
            if (s == null) return false;
            if (NeedsNoProvisioning(s))
            {
                // Nothing to fetch, so "installed" means the controller exists.
                // Command may be a bare name resolved from PATH by the operating
                // system (py.exe, python.exe), in which case there is nothing here
                // to check and the honest answer is yes; a missing interpreter then
                // fails loudly at launch, which is a better error than this one.
                if (string.IsNullOrEmpty(s.Command)) return false;
                if (s.Command.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                    s.Command.IndexOf('/') < 0) return true;
                try { return File.Exists(s.Command); }
                catch (Exception) { return false; }
            }
            try { return !string.IsNullOrEmpty(s.ReadyMarker) && File.Exists(s.ReadyMarker); }
            catch (Exception) { return false; }
        }

        public static bool NeedsNoProvisioning(ServiceDef s)
        {
            return s == null || string.IsNullOrEmpty(s.Provision);
        }

        public static ServiceStatus Status(ServiceDef s, bool measureDisk)
        {
            var st = new ServiceStatus();
            st.Id = s.Id;
            st.Enabled = s.Enabled;
            st.NeedsNoProvisioning = NeedsNoProvisioning(s);
            st.Installed = IsInstalled(s);
            if (measureDisk) st.DiskBytes = DiskBytes(s);
            if (!st.Installed) st.NotReadyReason = "not_installed";
            else if (!st.Enabled) st.NotReadyReason = "not_enabled";
            else st.NotReadyReason = null;
            return st;
        }

        /// The honest cost of a service: what is actually on the disk, measured.
        ///
        /// Not a number from a README and not the sum of what a package index says
        /// the wheels weigh. A README's figure ages the moment a dependency does,
        /// and the figure people care about is the one their own drive is short.
        public static long DiskBytes(ServiceDef s)
        {
            if (s == null || string.IsNullOrEmpty(s.InstallDir)) return 0;
            return DirectoryBytes(s.InstallDir);
        }

        public static long DirectoryBytes(string dir)
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                var stack = new Stack<string>();
                stack.Push(dir);
                while (stack.Count > 0)
                {
                    string d = stack.Pop();
                    string[] files;
                    try { files = Directory.GetFiles(d); }
                    catch (Exception) { continue; }   // a directory we cannot read is not a reason to fail
                    foreach (string f in files)
                    {
                        try { total += new FileInfo(f).Length; }
                        catch (Exception) { }
                    }
                    try { foreach (string sub in Directory.GetDirectories(d)) stack.Push(sub); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return total;
        }

        /// Bytes as a person reads them. Deliberately powers of 1024 with the IEC
        /// names, because that is what Windows Explorer shows and a disagreement
        /// between this program and the file properties dialog would be read as a
        /// bug in this program.
        public static string Human(long bytes)
        {
            if (bytes < 0) return "unknown";
            string[] unit = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024.0 && i < unit.Length - 1) { v /= 1024.0; i++; }
            return v.ToString(i == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + unit[i];
        }

        // -- acting --------------------------------------------------------------

        /// Run a service's provisioning script and wait for it.
        ///
        /// DELIBERATELY NOT AN API ROUTE. Everything else a client can ask this
        /// runner to do is bounded and cheap: submit a lease, read a status string,
        /// stream a file. This one downloads gigabytes onto somebody's gaming PC
        /// and takes tens of minutes. A remote caller that can start it can fill a
        /// disk from across the LAN, and there is no version of that which is a
        /// good idea, so installing is something a person does at the keyboard (or
        /// over SSH, which is the same thing) and the network can only observe.
        ///
        /// The output is not captured. A twenty minute download with a progress bar
        /// is the one case where a person genuinely wants to watch, and swallowing
        /// it into a log they have to tail afterwards makes a slow thing feel broken.
        public static int RunProvision(ServiceDef s, Config c, string repoRoot, Action<string> say)
        {
            if (NeedsNoProvisioning(s)) { say("nothing to fetch for " + s.Id); return 0; }

            string script = ResolveScript(s.Provision, repoRoot);
            if (script == null)
            {
                say("provisioning script not found: " + s.Provision);
                say("looked under " + repoRoot + " and beside the executable");
                return 3;
            }

            try { Directory.CreateDirectory(s.InstallDir); }
            catch (Exception ex) { say("cannot create " + s.InstallDir + ": " + ex.Message); return 3; }

            // EVERY CACHE POINTED INSIDE THE SERVICE'S OWN DIRECTORY, set here and
            // not left to the script to remember. A provisioning script that forgets
            // one of these scatters gigabytes into %USERPROFILE%\.cache and
            // %LOCALAPPDATA%\pip, and the uninstall story ("delete the directory")
            // quietly stops being true. Setting them on the child's environment
            // rather than the machine's also keeps the promise that this program
            // makes no machine-wide or user-wide environment change: these variables
            // exist for the lifetime of the provisioning process and nowhere else.
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"" +
                            " -Root \"" + s.InstallDir + "\"" +
                            (string.IsNullOrEmpty(s.ProvisionArguments) ? "" : " " + s.ProvisionArguments);
            psi.WorkingDirectory = Directory.Exists(s.InstallDir) ? s.InstallDir : Path.GetDirectoryName(script);
            psi.UseShellExecute = false;
            foreach (KeyValuePair<string, string> kv in ContainedEnvironment(s, c))
                psi.EnvironmentVariables[kv.Key] = kv.Value;

            say("installing " + s.Id + " into " + s.InstallDir);
            if (!string.IsNullOrEmpty(s.SizeHint)) say("the service says this will cost: " + s.SizeHint);
            say("");

            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    say("");
                    say("provisioning failed with exit code " +
                        p.ExitCode.ToString(CultureInfo.InvariantCulture) +
                        "; " + s.Id + " is still not installed");
                    return p.ExitCode;
                }
            }

            if (!IsInstalled(s))
            {
                // The marker is the script's last act, so reaching here means the
                // script exited zero without finishing. Saying so is better than
                // reporting success and failing at first launch instead.
                say("provisioning exited cleanly but did not write " + s.ReadyMarker +
                    ", so " + s.Id + " is not installed");
                return 4;
            }
            return 0;
        }

        /// Find a provisioning script whether this is an installed copy or a git
        /// checkout.
        ///
        /// Installed, the scripts are at ScriptsRoot inside the contained directory.
        /// In a checkout, the exe is in dist\ and the scripts are in services\ one
        /// level up. Trying both means `build.ps1` then `dist\idlegpu.exe service
        /// install echo` works without an install step, which is what somebody
        /// evaluating this repository will type first.
        public static string ResolveScript(string relative, string scriptsRoot)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            if (Path.IsPathRooted(relative)) return File.Exists(relative) ? relative : null;

            var tries = new List<string>();
            if (!string.IsNullOrEmpty(scriptsRoot)) tries.Add(Path.Combine(scriptsRoot, relative));
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            tries.Add(Path.Combine(Path.Combine(exeDir, "services"), relative));
            try
            {
                string up = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(up)) tries.Add(Path.Combine(Path.Combine(up, "services"), relative));
            }
            catch (Exception) { }

            foreach (string t in tries)
            {
                try { if (File.Exists(t)) return t; }
                catch (Exception) { }
            }
            return null;
        }

        /// The environment every provisioning script and every controller inherits,
        /// with each cache a package manager might otherwise scatter pointed inside
        /// the service's own directory.
        ///
        /// The list is empirical: each entry is a place something in this stack was
        /// found to write by default. HF_HOME and TORCH_HOME are model weights,
        /// which is the big one (3.21 GB for Chatterbox alone). PIP_CACHE_DIR,
        /// UV_CACHE_DIR and XDG_CACHE_HOME are wheel caches. TMP and TEMP are here
        /// because a multi-gigabyte wheel is unpacked through the temp directory and
        /// an interrupted install leaves it there.
        public static Dictionary<string, string> ContainedEnvironment(ServiceDef s, Config c)
        {
            var e = new Dictionary<string, string>();
            string root = s.InstallDir;
            string cache = Path.Combine(root, "cache");
            e["IDLEGPU_SERVICE_ROOT"] = root;
            e["IDLEGPU_SERVICE_ID"] = s.Id;
            e["IDLEGPU_QUEUE_DIR"] = s.QueueDir;
            e["HF_HOME"] = Path.Combine(root, "models", "hf");
            e["HUGGINGFACE_HUB_CACHE"] = Path.Combine(root, "models", "hf", "hub");
            // HF_HUB_CACHE is the CURRENT name; HUGGINGFACE_HUB_CACHE above is the
            // deprecated one, and libraries are inconsistent about which they read.
            // Measured: with only the old name set, importing diffusers still wrote
            // %USERPROFILE%\.cache\huggingface\hub\version_diffusers_cache.txt.
            // One byte, and the principle is the whole point: "delete the folder to
            // uninstall" is either true or it is not.
            e["HF_HUB_CACHE"] = Path.Combine(root, "models", "hf", "hub");
            e["HF_ASSETS_CACHE"] = Path.Combine(root, "models", "hf", "assets");
            e["DIFFUSERS_CACHE"] = Path.Combine(root, "models", "hf", "hub");
            e["TORCH_HOME"] = Path.Combine(root, "models", "torch");
            e["TRANSFORMERS_CACHE"] = Path.Combine(root, "models", "hf", "transformers");
            e["XDG_CACHE_HOME"] = cache;
            // THE SPIN TRAP, and it is the difference between a cap that costs
            // throughput in proportion and one that destroys it.
            //
            // Torch's OpenMP runtime does not sleep a worker thread when a parallel
            // region ends: it BUSY-SPINS, waiting for the next one, for
            // KMP_BLOCKTIME milliseconds - 200 by default. On an unconstrained
            // machine that is a sensible trade, because a spin is cheaper than a
            // context switch and the next region is usually imminent.
            //
            // Under a HARD CAP it is the worst possible behaviour. The kernel
            // counts spinning as work, so the job spends its entire budget for the
            // scheduling interval doing nothing, is descheduled, and the real work
            // waits for the next interval. A ten per cent cap stops being a ten per
            // cent slowdown and becomes an arbitrary one.
            //
            // PASSIVE makes a finished worker block instead. Set on the child only,
            // like everything else here; nothing is written to the machine.
            e["OMP_WAIT_POLICY"] = "PASSIVE";
            e["KMP_BLOCKTIME"] = "0";
            e["PIP_CACHE_DIR"] = Path.Combine(cache, "pip");
            e["UV_CACHE_DIR"] = Path.Combine(cache, "uv");
            e["UV_PYTHON_INSTALL_DIR"] = Path.Combine(root, "python");
            e["UV_PROJECT_ENVIRONMENT"] = Path.Combine(root, "venv");
            // MEASURED LEAK, not a precaution. `uv python install 3.12` with only
            // UV_PYTHON_INSTALL_DIR set still wrote a 45 KB shim to
            // %USERPROFILE%\.local\bin\python3.12.exe and printed a note about
            // adding that directory to PATH. It is outside the contained directory,
            // it survives `service remove`, and "uninstall is delete the folder"
            // would have been quietly false because of it. Found by listing the
            // profile after an install rather than by reading the documentation.
            e["UV_PYTHON_BIN_DIR"] = Path.Combine(root, "bin");
            e["UV_TOOL_DIR"] = Path.Combine(root, "tools");
            e["UV_TOOL_BIN_DIR"] = Path.Combine(root, "bin");
            // uv reads a user-level config from %APPDATA%\uv\uv.toml. A stranger's
            // machine may have one that redirects a cache back out of here.
            e["UV_NO_CONFIG"] = "1";
            e["TMP"] = Path.Combine(cache, "tmp");
            e["TEMP"] = Path.Combine(cache, "tmp");
            // THE THIRD MEASURED LEAK, and the one no environment variable of its
            // own can close. spacy-pkuseg is a locked chatterbox dependency and it
            // resolves its model directory with expanduser("~/.pkuseg"), a path
            // built in code with no variable to override. Installing chatterbox
            // wrote 90.4 MB to %USERPROFILE%\.pkuseg, `service remove` left it, and
            // the README's "delete the folder, that is everything" was false
            // because of it.
            //
            // So the home directory ITSELF is redirected for the child. On Windows
            // CPython, expanduser("~") returns USERPROFILE, so this catches every
            // library that builds a dotfile path that way rather than only the one
            // that was caught. HOME is set alongside because the same libraries
            // check it first on other platforms and some check it here too.
            //
            // Safe because this environment is only ever handed to a service
            // subprocess whose caches, models, temp and interpreter are already
            // inside `root`. Nothing in it has business reading the real profile,
            // and anything that did would be reaching outside the contained
            // directory, which is the thing being prevented.
            e["USERPROFILE"] = root;
            e["HOME"] = root;
            try { Directory.CreateDirectory(Path.Combine(cache, "tmp")); }
            catch (Exception) { }
            return e;
        }

        /// Delete everything the service downloaded. This is the whole of removal:
        /// one directory, because everything a service fetches was pointed into it.
        public static bool Remove(ServiceDef s, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(s.InstallDir) || !Directory.Exists(s.InstallDir)) return true;
                Directory.Delete(s.InstallDir, true);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // -- persisting the choice ------------------------------------------------

        /// Write Enabled into the service's own section of worker.ini, creating the
        /// section if the file does not have one.
        ///
        /// Rewrites one line and leaves every other line, comment and blank alone.
        /// worker.ini is a file a person edits by hand and has written their own
        /// notes into; a settings writer that reformats those out of existence is a
        /// settings writer they stop using and go back to editing by hand, which
        /// puts the CLI and the file permanently out of step.
        public static bool SetEnabled(string configPath, string serviceId, bool enabled, out string error)
        {
            error = null;
            try
            {
                var lines = new List<string>();
                if (File.Exists(configPath)) lines.AddRange(File.ReadAllLines(configPath));

                string want = "[service." + serviceId + "]";
                int sectionAt = -1;
                for (int i = 0; i < lines.Count; i++)
                    if (string.Equals(lines[i].Trim(), want, StringComparison.OrdinalIgnoreCase)) { sectionAt = i; break; }

                if (sectionAt < 0)
                {
                    error = "worker.ini has no " + want + " section";
                    return false;
                }

                // Find the end of the section: the next section header, or the end.
                int end = lines.Count;
                for (int i = sectionAt + 1; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[") && t.EndsWith("]")) { end = i; break; }
                }

                bool replaced = false;
                for (int i = sectionAt + 1; i < end; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("#") || t.StartsWith(";")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(t.Substring(0, eq).Trim(), "Enabled", StringComparison.OrdinalIgnoreCase)) continue;
                    lines[i] = "Enabled              = " + (enabled ? "true" : "false");
                    replaced = true;
                    break;
                }
                if (!replaced)
                {
                    int at = end;
                    while (at > sectionAt + 1 && lines[at - 1].Trim().Length == 0) at--;
                    lines.Insert(at, "Enabled              = " + (enabled ? "true" : "false"));
                }

                File.WriteAllLines(configPath, lines.ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // -- reporting ------------------------------------------------------------

        /// The service block of GET /v1/services, and the only place the three
        /// states are turned into JSON. Kept here rather than in Api.cs so that the
        /// CLI's `service list` and the API cannot drift into disagreeing about what
        /// "installed" means.
        /// Without an agent there is nobody to ask whether the machine is free
        /// right now, so `available` is published as null rather than as false.
        /// `idlegpu service list` runs in a separate process from the agent and
        /// used to have no way to say "I do not know"; false would have read as
        /// "busy" to anything parsing it.
        public static string Json(ServiceDef d, ServiceStatus st, bool running, int queued,
                                  string manifest, int graceSeconds)
        {
            return Json(d, st, running, queued, manifest, graceSeconds,
                        d.WantsGpu ? "gpu" : "cpu", null, "");
        }

        public static string Json(ServiceDef d, ServiceStatus st, bool running, int queued,
                                  string manifest, int graceSeconds,
                                  string device, bool? available, string unavailableReason)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append(IdleGpu.Json.P("id", IdleGpu.Json.Esc(d.Id))).Append(",");
            sb.Append(IdleGpu.Json.P("description", IdleGpu.Json.Esc(d.Description))).Append(",");
            sb.Append(IdleGpu.Json.P("priority", IdleGpu.Json.Num(d.Priority))).Append(",");
            sb.Append(IdleGpu.Json.P("labels",
                "[" + Join(d.Labels) + "]")).Append(",");
            sb.Append(IdleGpu.Json.P("yield_grace_seconds", IdleGpu.Json.Num(graceSeconds))).Append(",");

            // The three states, spelled out separately and never merged. A client
            // that only understands one of them still gets a true answer from it.
            sb.Append(IdleGpu.Json.P("known", "true")).Append(",");
            sb.Append(IdleGpu.Json.P("installed", st.Installed ? "true" : "false")).Append(",");
            sb.Append(IdleGpu.Json.P("enabled", st.Enabled ? "true" : "false")).Append(",");
            sb.Append(IdleGpu.Json.P("ready", (st.Installed && st.Enabled) ? "true" : "false")).Append(",");
            sb.Append(IdleGpu.Json.P("not_ready_reason",
                st.NotReadyReason == null ? "null" : IdleGpu.Json.Esc(st.NotReadyReason))).Append(",");

            sb.Append(IdleGpu.Json.P("needs_provisioning", st.NeedsNoProvisioning ? "false" : "true")).Append(",");
            sb.Append(IdleGpu.Json.P("size_hint",
                string.IsNullOrEmpty(d.SizeHint) ? "null" : IdleGpu.Json.Esc(d.SizeHint))).Append(",");
            sb.Append(IdleGpu.Json.P("disk_bytes",
                st.DiskBytes < 0 ? "null" : st.DiskBytes.ToString(CultureInfo.InvariantCulture))).Append(",");

            sb.Append(IdleGpu.Json.P("running", running ? "true" : "false")).Append(",");
            sb.Append(IdleGpu.Json.P("queued", IdleGpu.Json.Num(queued))).Append(",");
            // WHICH RESOURCE, AND WILL IT RUN NOW. `device` is what this service
            // is after; `available` is this machine's answer for it, already
            // resolved against the right gate. A client should read `available`
            // and never try to work it out from gpu_available and a device name,
            // because that is the calculation this runner exists to do for them.
            sb.Append(IdleGpu.Json.P("device", IdleGpu.Json.Esc(device))).Append(",");
            sb.Append(IdleGpu.Json.P("available",
                available.HasValue ? (available.Value ? "true" : "false") : "null")).Append(",");
            sb.Append(IdleGpu.Json.P("unavailable_reason",
                string.IsNullOrEmpty(unavailableReason) ? "null" : IdleGpu.Json.Esc(unavailableReason))).Append(",");
            sb.Append("\"manifest\":").Append(manifest == null ? "null" : manifest);
            sb.Append("}");
            return sb.ToString();
        }

        static string Join(string[] items)
        {
            if (items == null || items.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(IdleGpu.Json.Esc(items[i]));
            }
            return sb.ToString();
        }
    }
}
