// What the policy must decide, asserted against recorded samples.
//
// These tests drive the SAME Policy.Evaluate the tray runs. There is no second
// implementation and no mock: the only difference is that the snapshots come from
// a CSV instead of from nvidia-smi and the Windows performance counters. That is
// the whole reason Model.cs was split out of Signals.cs - this binary links
// Model, Config, Policy, Replay, Jobs, Http and Install and touches no Windows
// API, no GPU, no registry and no network, so it runs on any machine that can
// compile C#. Nothing here opens a socket: the HTTP tests feed a MemoryStream to
// the same parser the listener feeds a TLS stream to.
//
// Every test is named after the mistake it stops, because a test named after the
// function it calls tells you nothing when it goes red at two in the morning.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace IdleGpu
{
    static class Tests
    {
        static int _failed, _passed;
        static string _fixtures;

        static void Main(string[] args)
        {
            _fixtures = args.Length > 0 ? args[0]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");

            IdleDesktopEventuallyRunsJobs();
            IdleDesktopNeverLooksBusy();
            Session0RefusesForEverAndSaysWhyItCannotTell();
            Session0WithNobodySignedInIsAllowedToWork();
            Session0WithALockedDesktopIsAllowedToWork();
            SteamVetoesBeforeTheGpuMoves();
            PausedGameIsCaughtByVramAlone();
            VideoPlaybackDoesNotYield();
            Tier2NeedsExactlyThreeConsecutiveSamples();
            ValorantIsCaughtByVgcNotVgk();
            DeadSmiIsBlockedNotBusy();
            CooldownIsNinetySecondsNotEightyNine();
            AltTabOutOfAGameNeverBecomesAvailable();
            OurOwnJobIsNotEvidenceOfAUser();
            WithoutTheOwnPidExemptionItWouldOscillate();
            LockedScreenIsNotAFullScreenGame();
            EveryProcessOnTheRealIdleDesktopIsHarmless();
            ThresholdsClearTheMeasuredIdleCeiling();
            EveryControllerIsExemptOrTheAgentYieldsToItself();

            AServiceIsKnownBeforeItIsInstalled();
            EnablingWithoutInstallingIsNotReady();
            NotInstalledIsNeverReportedAsGpuBusy();
            DisablingReclaimsNothingButRemovingDoes();
            EveryCacheIsPointedInsideTheServiceDirectory();
            EnabledIsWrittenIntoTheRightSectionOnly();
            TheShippedExampleWorksWithoutBeingEdited();

            AJobIdFromTheNetworkCannotEscapeTheQueueDirectory();
            AnArtefactNameFromAQueryStringCannotEscapeEither();
            AChunkedBodyIsRefusedRatherThanMisread();
            ExpectContinueIsRefusedRatherThanHung();
            AFloodOfHeadersIsCappedNotBuffered();
            TheRequestLineIsCappedBeforeItIsSplit();
            APercentEncodedTraversalIsStillATraversal();
            AMalformedBodyIsRefusedAtSubmitNotInsideTheController();
            AKilledControllersLeaseReadsAsQueuedNotRunning();
            AnOrphanedLeaseIsRescuedRatherThanWaitingForItself();

            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed", _passed, _failed);
            Environment.Exit(_failed == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------ helpers ---

        static List<Snapshot> Load(string name)
        {
            return Replay.Load(Path.Combine(_fixtures, name));
        }

        /// Replay a fixture and return the state after every row.
        static List<WorkerState> Run(string name, out Policy p, int ownJobPid)
        {
            p = new Policy(new Config());
            var states = new List<WorkerState>();
            foreach (Snapshot s in Load(name))
            {
                if (ownJobPid > 0) s.OwnJobPids.Add(ownJobPid);
                p.Evaluate(s);
                states.Add(p.State);
            }
            return states;
        }

        static List<WorkerState> Run(string name, out Policy p) { return Run(name, out p, -1); }

        static void Ok(bool cond, string what)
        {
            if (cond) { _passed++; Console.WriteLine("  pass  " + what); }
            else { _failed++; Console.WriteLine("  FAIL  " + what); }
        }

        static void Case(string name) { Console.WriteLine(); Console.WriteLine(name); }

        static int FirstIndexOf(List<WorkerState> st, WorkerState want)
        {
            for (int i = 0; i < st.Count; i++) if (st[i] == want) return i;
            return -1;
        }

        // -------------------------------------------------------------- tests ---

        /// The baseline claim: on a real idle desktop the worker eventually works.
        /// If this fails the whole project is pointless, because the machine never
        /// offers the GPU at all.
        static void IdleDesktopEventuallyRunsJobs()
        {
            Case("a genuinely idle desktop ends up available");
            Policy p;
            List<WorkerState> st = Run("idle_desktop.csv", out p);
            Ok(st[st.Count - 1] == WorkerState.Available,
                "150 s of measured idle ends Available (ended " +
                Policy.Describe(st[st.Count - 1]) + ")");
            Ok(p.Last.Reasons.Count == 0, "and gives no reason to yield");
            Ok(p.CanRun(Mode.Auto), "so Auto will take a job");
        }

        /// The measured idle baseline must not trip a single tier-2 vote. This is
        /// the test that fails if somebody "tidies up" UtilGpuBusyPct to 5, which
        /// looks reasonable and is unreachable: 150 of 150 measured samples read 5
        /// or 6 per cent on an idle desktop.
        static void IdleDesktopNeverLooksBusy()
        {
            Case("measured idle never reads as load");
            Policy p;
            List<WorkerState> st = Run("idle_desktop.csv", out p);
            Ok(FirstIndexOf(st, WorkerState.Busy) < 0,
                "no sample in the 150-sample baseline is Busy");
        }

        /// Fail-closed, and the reason has to name the actual problem. A worker
        /// that says "busy" when it means "I am blind" sends the user looking for
        /// a game that is not running.
        static void Session0RefusesForEverAndSaysWhyItCannotTell()
        {
            Case("an agent in session 0 refuses to run, for ever");
            Policy p;
            List<WorkerState> st = Run("session0_blocked.csv", out p);
            Ok(st.TrueForAll(delegate(WorkerState s) { return s == WorkerState.Blocked; }),
                "every one of 150 samples is Blocked");
            Ok(!p.CanRun(Mode.Auto), "Auto will not run");
            Ok(p.Last.ReasonText.Contains("session 0") && p.Last.ReasonText.Contains("cannot observe"),
                "and the reason names the session, not a game: " + p.Last.ReasonText);
        }

        /// THE DEFECT THIS PREVENTS: refusing to work when there is nobody to
        /// yield to. Being unable to observe a user was treated as an absolute
        /// veto, which conflated "somebody is there and I cannot see them" with
        /// "nobody is there". The second is the safest moment this program will
        /// ever get, and it is most of a gaming PC's uptime -- and it is the
        /// state a service lives in from boot until somebody signs in.
        static void Session0WithNobodySignedInIsAllowedToWork()
        {
            Case("session 0 with nobody signed in is free to work");
            Policy p;
            List<WorkerState> st = Run("session0_nobody_signed_in.csv", out p);
            Ok(st[st.Count - 1] == WorkerState.Available,
                "the last sample is Available, not Blocked");
            Ok(p.CanRun(Mode.Auto), "Auto will run");
            Ok(!p.Last.Blind, "and it is not reported as blind: there is nothing to be blind to");
            Ok(p.Last.ReasonText.Contains("nobody is signed in"),
                "the reason says why it is allowed: " + p.Last.ReasonText);
        }

        /// A locked desktop was already documented as the safest possible time to
        /// run. Being in session 0 does not change that, and treating it as blind
        /// threw the case away.
        static void Session0WithALockedDesktopIsAllowedToWork()
        {
            Case("session 0 with the desktop locked is free to work");
            Policy p;
            List<WorkerState> st = Run("session0_locked.csv", out p);
            Ok(st[st.Count - 1] == WorkerState.Available, "the last sample is Available");
            Ok(p.CanRun(Mode.Auto), "Auto will run");
            Ok(p.Last.ReasonText.Contains("locked"),
                "the reason names the lock: " + p.Last.ReasonText);
        }

        /// The single most important latency claim in the design. Steam writes
        /// RunningAppID before the game renders its first frame, so the veto must
        /// fire on a sample whose GPU columns are still at the idle values.
        static void SteamVetoesBeforeTheGpuMoves()
        {
            Case("Steam vetoes on the first sample, before the GPU moves");
            List<Snapshot> rows = Load("steam_launch.csv");
            var p = new Policy(new Config());
            int firstBusy = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                p.Evaluate(rows[i]);
                if (p.State == WorkerState.Busy && firstBusy < 0) firstBusy = i;
            }
            Ok(firstBusy == 100, "Busy on row 100, the row the appid appears (got " + firstBusy + ")");
            Ok(rows[100].Gpu.PState == "P5" && rows[100].Gpu.ClockMemMhz == 810,
                "and that row's GPU is still idle: P5 at 810 MHz");
            Ok(rows[100].Gpu.UtilGpu <= 6, "and utilisation is still 5-6%, so no load signal could have fired");
        }

        /// The case the naive policy gets wrong in the expensive direction, and the
        /// reason the OS counters are read at all: nvidia-smi reports used_memory
        /// as [N/A] per process on this machine, so this is invisible to it.
        static void PausedGameIsCaughtByVramAlone()
        {
            Case("a paused game holding VRAM is caught with no other signal");
            List<Snapshot> rows = Load("paused_game_vram.csv");
            var p = new Policy(new Config());
            int firstBusy = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                p.Evaluate(rows[i]);
                if (p.State == WorkerState.Busy && firstBusy < 0) firstBusy = i;
            }
            Ok(firstBusy == 100, "Busy on row 100 (got " + firstBusy + ")");
            Ok(rows[100].Launchers.SteamRunningAppId == 0, "with no Steam appid at all");
            Ok(rows[100].Gpu.PState == "P5", "and the GPU at idle P5, drawing nothing");
            Ok(p.Last.ReasonText.Contains("TheFinals") && p.Last.ReasonText.Contains("MiB"),
                "and the reason names the process and its VRAM: " + p.Last.ReasonText);
        }

        /// Deliberate: a desktop is nearly always playing something, and a worker
        /// that yields to a YouTube tab is a worker that never runs.
        static void VideoPlaybackDoesNotYield()
        {
            Case("video playback does not take the GPU away");
            Policy p;
            List<WorkerState> st = Run("video_playback.csv", out p);
            Ok(FirstIndexOf(st, WorkerState.Busy) < 0, "no sample is Busy despite the decoder at 41%");
            Ok(st[st.Count - 1] == WorkerState.Available, "and it ends Available");
        }

        /// Off-by-one on the confirmation window is the classic way this goes
        /// wrong: two samples is jumpy, four is a second of the user's frames.
        static void Tier2NeedsExactlyThreeConsecutiveSamples()
        {
            Case("a tier-2 load vote needs exactly three consecutive samples");
            Policy p;
            List<WorkerState> st = Run("tier2_load_only.csv", out p);
            int firstBusy = FirstIndexOf(st, WorkerState.Busy);
            Ok(firstBusy == 62, "load starts at row 60, Busy at row 62 (got " + firstBusy + ")");
        }

        /// vgk is Running/System at all times on this machine and is therefore
        /// useless. vgc is Stopped/Manual and starts with the game. Both measured.
        static void ValorantIsCaughtByVgcNotVgk()
        {
            Case("Valorant is caught by the vgc service");
            Policy p;
            List<WorkerState> st = Run("valorant_vgc.csv", out p);
            int firstBusy = FirstIndexOf(st, WorkerState.Busy);
            Ok(firstBusy == 80, "Busy on the row vgc starts (got " + firstBusy + ")");
            Ok(p.Last.ReasonText.Contains("vgc"), "and says so: " + p.Last.ReasonText);
        }

        /// Blind is not the same as busy, and the distinction is what tells the
        /// user whether to go looking for a problem.
        static void DeadSmiIsBlockedNotBusy()
        {
            Case("a dead nvidia-smi stream blocks rather than blaming a game");
            Policy p;
            List<WorkerState> st = Run("smi_dead.csv", out p);
            Ok(st.TrueForAll(delegate(WorkerState s) { return s == WorkerState.Blocked; }),
                "every sample is Blocked, never Busy");
            Ok(!p.CanRun(Mode.Auto), "and Auto will not run");
        }

        /// The asymmetry is the safety property: instant to yield, slow to return.
        /// A test that only checked "eventually available" would pass with a
        /// one-second cooldown, which is the setting that costs somebody a match.
        static void CooldownIsNinetySecondsNotEightyNine()
        {
            Case("returning to available takes the full 90 s of continuous clear");
            var cfg = new Config();
            Policy p;
            List<WorkerState> st = Run("cooldown_90s.csv", out p);
            // Rows 0-29 carry the Steam appid; row 29 is the last busy sample.
            Ok(st[29] == WorkerState.Busy, "row 29 is the last Busy row");
            Ok(st[30] == WorkerState.Draining, "row 30 is Draining, not Available");
            int firstAvail = FirstIndexOf(st, WorkerState.Available);
            Ok(firstAvail == 29 + cfg.ClearCooldownSeconds,
                "Available at row " + (29 + cfg.ClearCooldownSeconds) + " exactly (got " + firstAvail + ")");
            Ok(st[firstAvail - 1] == WorkerState.Draining, "and the row before it is still Draining");
        }

        /// Alt-tabbing to a browser mid-match drops the full-screen flag and lets
        /// the GPU fall back towards idle. The game is still there. This is the
        /// fixture that fails if somebody rebuilds the policy on utilisation.
        static void AltTabOutOfAGameNeverBecomesAvailable()
        {
            Case("alt-tabbing out of a running game never frees the GPU");
            Policy p;
            List<WorkerState> st = Run("alt_tab_midgame.csv", out p);
            Ok(FirstIndexOf(st, WorkerState.Available) < 0,
                "no sample in 120 s is Available");
            Ok(st.TrueForAll(delegate(WorkerState s) { return s == WorkerState.Busy; }),
                "all 120 are Busy, including the 60 after the GPU fell back to idle");
        }

        /// Without this the policy oscillates: allocate, see our own VRAM, yield,
        /// free it, see nothing, allocate. For ever.
        static void OurOwnJobIsNotEvidenceOfAUser()
        {
            Case("our own job's 3.2 GiB is not a user");
            Policy p;
            List<WorkerState> st = Run("own_job_vram.csv", out p, 31337);
            Ok(st[st.Count - 1] == WorkerState.Available,
                "with our job at pid 31337 the card is still ours");
        }

        /// The other half of the same test. If the exemption silently stopped
        /// working, the test above would keep passing on a policy that had simply
        /// stopped reading VRAM at all.
        static void WithoutTheOwnPidExemptionItWouldOscillate()
        {
            Case("the same 3.2 GiB held by anything else is a veto");
            Policy p;
            List<WorkerState> st = Run("own_job_vram.csv", out p, -1);
            Ok(st.TrueForAll(delegate(WorkerState s) { return s == WorkerState.Busy; }),
                "every sample is Busy when the pid is not ours");
        }

        /// A locked machine is the safest possible moment to run: the user is
        /// demonstrably not there. The full-screen veto must not fire behind the
        /// lock screen, which is itself a full-screen window.
        static void LockedScreenIsNotAFullScreenGame()
        {
            Case("a locked screen is not a full-screen game");
            Policy p;
            List<WorkerState> st = Run("locked_fullscreen.csv", out p);
            Ok(st[st.Count - 1] == WorkerState.Available,
                "LogonUI full-screen behind a lock does not veto");
        }

        /// The allowlist and the 512 MiB trip point, against the real measured
        /// process list rather than against a story about it. Fifteen processes
        /// were holding GPU memory on spring's idle desktop; not one of them may
        /// read as a game.
        static void EveryProcessOnTheRealIdleDesktopIsHarmless()
        {
            Case("no process on the real idle desktop trips the VRAM veto");
            var cfg = new Config();
            var p = new Policy(cfg);
            var s = new Snapshot();
            s.At = DateTime.UtcNow;

            string path = Path.Combine(_fixtures, "_recorded_vram_spring.csv");
            string[] lines = File.ReadAllLines(path);
            double biggest = 0; string biggestName = "";
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = Replay.SplitCsv(lines[i]);
                if (f.Length < 3) continue;
                var u = new ProcessGpuUse();
                u.Pid = int.Parse(f[0], CultureInfo.InvariantCulture);
                u.Name = f[1];
                u.DedicatedMiB = double.Parse(f[2], CultureInfo.InvariantCulture);
                s.GpuProcesses.Add(u);
                if (u.DedicatedMiB > biggest) { biggest = u.DedicatedMiB; biggestName = u.Name; }
            }

            Ok(s.GpuProcesses.Count == 15, "read all 15 measured processes");
            List<ProcessGpuUse> foreign = p.ForeignVram(s);
            Ok(foreign.Count == 0, "none of them is foreign; got " +
                (foreign.Count == 0 ? "none" : foreign[0].Name));
            Ok(biggest < cfg.ForeignVramBusyMiB,
                "the largest measured consumer (" + biggestName + " at " +
                biggest.ToString("N1", CultureInfo.InvariantCulture) +
                " MiB) is under the " + cfg.ForeignVramBusyMiB.ToString("N0", CultureInfo.InvariantCulture) +
                " MiB trip point");
        }

        /// Guards the defaults themselves against a well-meaning edit. Each of
        /// these is a margin over a measured idle ceiling, and the measurement is
        /// in tests/fixtures/_recorded_idle_spring.csv where anybody can check it.
        static void ThresholdsClearTheMeasuredIdleCeiling()
        {
            Case("every threshold clears the measured idle ceiling");
            var cfg = new Config();
            List<Snapshot> idle = Load("idle_desktop.csv");

            int maxUtil = idle.Max(delegate(Snapshot s) { return s.Gpu.UtilGpu; });
            double maxPower = idle.Max(delegate(Snapshot s) { return s.Gpu.PowerWatts; });
            int maxClk = idle.Max(delegate(Snapshot s) { return s.Gpu.ClockMemMhz; });
            double max3d = idle.Max(delegate(Snapshot s) { return s.Util3d; });

            Ok(cfg.UtilGpuBusyPct > maxUtil,
                "utilisation trip " + cfg.UtilGpuBusyPct + "% clears measured idle max " + maxUtil + "%");
            Ok(cfg.PowerBusyWatts > maxPower,
                "power trip " + cfg.PowerBusyWatts + " W clears measured idle max " +
                maxPower.ToString("N2", CultureInfo.InvariantCulture) + " W");
            Ok(cfg.MemClockBusyMhz > maxClk,
                "memory clock trip " + cfg.MemClockBusyMhz + " MHz clears measured idle " + maxClk + " MHz");
            Ok(cfg.Util3dBusyPct > max3d,
                "3D engine trip " + cfg.Util3dBusyPct + "% clears measured idle " +
                max3d.ToString("N2", CultureInfo.InvariantCulture) + "%");
            Ok(Array.IndexOf(cfg.IdlePStates, "P5") >= 0,
                "P5 is treated as idle, which is what all 150 measured samples were");
        }

        // ------------------------------------------- many controllers, one GPU ---

        /// THE DEFECT THIS PREVENTS, and it is the one the multi-service change
        /// introduces. Snapshot.OwnJobPid used to be a single int, because there
        /// used to be a single service. With more than one controller alive during
        /// a handoff (one draining, the next starting) the second one is not in the
        /// exemption, its VRAM reads as a stranger's, and the policy yields the GPU
        /// to itself: allocate, see our own memory, yield, free it, see nothing,
        /// allocate. For ever, at 1 Hz, on somebody's gaming PC.
        static void EveryControllerIsExemptOrTheAgentYieldsToItself()
        {
            Case("two controllers of ours are both exempt");
            var cfg = new Config();
            var p = new Policy(cfg);

            var s = new Snapshot();
            s.At = DateTime.UtcNow;
            s.GpuProcesses.Add(Proc(31337, "python", 3210.0));
            s.GpuProcesses.Add(Proc(31338, "hashcat", 2048.0));

            s.OwnJobPids.Add(31337);
            Ok(p.ForeignVram(s).Count == 1,
                "with only the first controller exempt the second reads as a user");

            s.OwnJobPids.Add(31338);
            Ok(p.ForeignVram(s).Count == 0,
                "with both exempt neither reads as a user");

            // The exemption must be by pid and nothing else. A controller and a
            // game can both be called python.exe, and exempting by name would hand
            // a trivially available disguise to anything that wanted the GPU held.
            var t = new Snapshot();
            t.At = DateTime.UtcNow;
            t.GpuProcesses.Add(Proc(999, "python", 3210.0));
            t.OwnJobPids.Add(31337);
            Ok(p.ForeignVram(t).Count == 1,
                "a different pid with the same process name is still foreign");
        }

        static ProcessGpuUse Proc(int pid, string name, double mib)
        {
            var u = new ProcessGpuUse();
            u.Pid = pid; u.Name = name; u.DedicatedMiB = mib;
            return u;
        }

        // ------------------------------------------------------ opting in --------

        static Config Cfg(string dataDir)
        {
            var c = new Config();
            c.DataDir = dataDir;
            c.LogPath = ""; c.StatePath = ""; c.AssetsDir = ""; c.QueueRoot = "";
            c.CertPath = ""; c.CertKeyLedgerPath = ""; c.ProvisionStatusPath = "";
            c.RuntimeRoot = ""; c.ScriptsRoot = "";
            return c;
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "idlegpu-t-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static ServiceDef Svc(Config c, string id, string provision)
        {
            var d = new ServiceDef();
            d.Id = id;
            d.Command = Path.Combine(c.DataDir, "runtime", id, "venv", "python.exe");
            d.Provision = provision;
            d.SizeHint = "about 6.3 GB";
            c.Services.Add(d);
            return d;
        }

        /// THE DEFECT THIS PREVENTS: a base install that quietly downloads six
        /// gigabytes. A [service.chatterbox] section shipped in the example file
        /// must leave the machine exactly as it found it until somebody asks.
        static void AServiceIsKnownBeforeItIsInstalled()
        {
            Case("a service section is known, not installed");
            string root = TempDir();
            try
            {
                Config c = Cfg(root);
                ServiceDef d = Svc(c, "chatterbox", "services\\chatterbox\\provision.ps1");
                c.Resolve();

                Ok(!d.Enabled, "Enabled defaults to false, so a shipped section opts nobody in");
                ServiceStatus st = Install.Status(d, true);
                Ok(!st.Installed, "no ready marker means not installed");
                Ok(st.NotReadyReason == "not_installed", "the reason says which of the two it is");
                Ok(st.DiskBytes == 0, "and it is costing nothing: " + Install.Human(st.DiskBytes));
                Ok(!Directory.Exists(d.InstallDir),
                    "merely knowing about a service does not even create its directory");
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS: an interrupted 6 GB download that leaves the
        /// service looking installed. The marker is the provisioning script's LAST
        /// act, so half a download reads as none.
        static void EnablingWithoutInstallingIsNotReady()
        {
            Case("enabled but unprovisioned is not ready");
            string root = TempDir();
            try
            {
                Config c = Cfg(root);
                ServiceDef d = Svc(c, "chatterbox", "services\\chatterbox\\provision.ps1");
                c.Resolve();
                d.Enabled = true;

                // Half a download: gigabytes on disk, no marker.
                Directory.CreateDirectory(Path.Combine(d.InstallDir, "venv"));
                File.WriteAllText(d.Command, "not really python");

                ServiceStatus st = Install.Status(d, true);
                Ok(!st.Installed, "bytes on disk without the marker are not an install");
                Ok(st.NotReadyReason == "not_installed", "and it still says not_installed");

                File.WriteAllText(d.ReadyMarker, "chatterbox");
                st = Install.Status(d, true);
                Ok(st.Installed && st.Enabled, "the marker is what makes it installed");
                Ok(st.NotReadyReason == null, "installed and enabled has no reason not to run");
                Ok(st.DiskBytes > 0, "and now it costs something: " + Install.Human(st.DiskBytes));
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS, and it is the one the API exists to avoid: a
        /// client that cannot tell "this runner has no speech installed" from
        /// "somebody is gaming" retries for ever against a machine that was never
        /// going to say yes. One of those is a five minute fix by the owner. The
        /// other is nobody's to fix.
        static void NotInstalledIsNeverReportedAsGpuBusy()
        {
            Case("not installed and gpu busy are different answers");
            string root = TempDir();
            try
            {
                Config c = Cfg(root);
                ServiceDef d = Svc(c, "chatterbox", "services\\chatterbox\\provision.ps1");
                c.Resolve();
                d.Enabled = true;

                string json = Install.Json(d, Install.Status(d, false), false, 0, null, 2);
                Ok(json.Contains("\"known\":true"), "known is true");
                Ok(json.Contains("\"installed\":false"), "installed is false");
                Ok(json.Contains("\"ready\":false"), "ready is false");
                Ok(json.Contains("\"not_ready_reason\":\"not_installed\""),
                    "and the reason names the service, not the GPU");
                Ok(!json.Contains("gpu"),
                    "no per-service block mentions the GPU at all; that is reported once, for the machine");
                Ok(json.Contains("\"size_hint\":\"about 6.3 GB\""),
                    "and the cost is published before anything is fetched");
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS: `disable` that deletes six gigabytes somebody
        /// wanted back tomorrow, or `remove` that leaves them on the disk. They are
        /// different verbs because they are different intentions.
        static void DisablingReclaimsNothingButRemovingDoes()
        {
            Case("disable keeps the disk, remove reclaims it");
            string root = TempDir();
            try
            {
                Config c = Cfg(root);
                ServiceDef d = Svc(c, "chatterbox", "services\\chatterbox\\provision.ps1");
                c.Resolve();
                Directory.CreateDirectory(d.InstallDir);
                File.WriteAllBytes(Path.Combine(d.InstallDir, "weights.bin"), new byte[4096]);
                File.WriteAllText(d.ReadyMarker, "x");

                long before = Install.DiskBytes(d);
                Ok(before >= 4096, "measured " + Install.Human(before) + " on disk");

                d.Enabled = false;
                Ok(Install.DiskBytes(d) == before, "disabling reclaims nothing");
                Ok(Install.IsInstalled(d), "and leaves it installed");

                string err;
                Ok(Install.Remove(d, out err), "remove succeeds: " + (err ?? "no error"));
                Ok(Install.DiskBytes(d) == 0, "and reclaims all of it");
                Ok(!Install.IsInstalled(d), "after which it is known again, not installed");
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS: "delete the directory to uninstall" quietly
        /// becoming false. torch writes weights to %USERPROFILE%\.cache\huggingface
        /// by default, pip caches wheels in %LOCALAPPDATA%, and an unpacked wheel
        /// goes through %TEMP%. Every one of those is gigabytes, and every one of
        /// them is outside the contained directory unless it is pointed inside it.
        static void EveryCacheIsPointedInsideTheServiceDirectory()
        {
            Case("every cache variable points inside the contained directory");
            string root = TempDir();
            try
            {
                Config c = Cfg(root);
                ServiceDef d = Svc(c, "chatterbox", "services\\chatterbox\\provision.ps1");
                c.Resolve();

                Dictionary<string, string> env = Install.ContainedEnvironment(d, c);
                string[] mustBeContained = {
                    "HF_HOME", "HUGGINGFACE_HUB_CACHE", "TORCH_HOME", "TRANSFORMERS_CACHE",
                    "XDG_CACHE_HOME", "PIP_CACHE_DIR", "UV_CACHE_DIR",
                    "UV_PYTHON_INSTALL_DIR", "UV_PROJECT_ENVIRONMENT",
                    // MEASURED on spring: without UV_PYTHON_BIN_DIR, uv wrote a
                    // shim to %USERPROFILE%\.local\bin that no uninstall removes.
                    "UV_PYTHON_BIN_DIR", "UV_TOOL_DIR", "UV_TOOL_BIN_DIR",
                    // MEASURED: HUGGINGFACE_HUB_CACHE is the deprecated name and
                    // diffusers reads HF_HUB_CACHE, so with only the old one set it
                    // wrote version_diffusers_cache.txt into %USERPROFILE%\.cache.
                    "HF_HUB_CACHE", "HF_ASSETS_CACHE", "DIFFUSERS_CACHE",
                    "TMP", "TEMP"
                };
                foreach (string k in mustBeContained)
                {
                    string v;
                    bool have = env.TryGetValue(k, out v);
                    Ok(have && v.StartsWith(d.InstallDir, StringComparison.OrdinalIgnoreCase),
                        k + " is inside the service directory (" + (have ? v : "UNSET") + ")");
                }
                Ok(d.InstallDir.StartsWith(c.DataDir, StringComparison.OrdinalIgnoreCase),
                    "and the service directory is inside the contained directory");
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS: `idlegpu service enable a` turning on service
        /// b, because the writer matched the first Enabled line in the file rather
        /// than the first one inside the right section. Both sections have one.
        static void EnabledIsWrittenIntoTheRightSectionOnly()
        {
            Case("enable writes into its own section");
            string root = TempDir();
            try
            {
                string ini = Path.Combine(root, "worker.ini");
                File.WriteAllText(ini, string.Join("\n", new string[] {
                    "StartMode = Auto",
                    "",
                    "[service.alpha]",
                    "Command = a.exe",
                    "Enabled = false",
                    "",
                    "[service.beta]",
                    "# beta has a comment the writer must not eat",
                    "Command = b.exe",
                    "Enabled = false",
                    ""
                }));

                string err;
                Ok(Install.SetEnabled(ini, "beta", true, out err), "wrote beta: " + (err ?? "ok"));
                Config c = Config.Load(ini);
                Ok(c.Service("beta").Enabled, "beta is enabled");
                Ok(!c.Service("alpha").Enabled, "alpha is untouched");
                Ok(File.ReadAllText(ini).Contains("# beta has a comment the writer must not eat"),
                    "and the human's comment survived");

                // A section with no Enabled line at all is the common case, because
                // false is the default and nobody writes a default down.
                File.WriteAllText(ini, "[service.gamma]\nCommand = g.exe\n");
                Ok(Install.SetEnabled(ini, "gamma", true, out err), "wrote gamma: " + (err ?? "ok"));
                Ok(Config.Load(ini).Service("gamma").Enabled, "gamma is enabled with no line to replace");

                Ok(!Install.SetEnabled(ini, "delta", true, out err),
                    "a service with no section is refused rather than invented");
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS: a worker.ini.example nobody can use unedited.
        /// Every path a service needs is under the user's own profile, so a literal
        /// path in the shipped example is THIS machine's path and wrong on every
        /// other one - which is the whole of rule 6 in one file. The placeholders
        /// are expanded from the config, so uncommenting a section is enough.
        static void TheShippedExampleWorksWithoutBeingEdited()
        {
            Case("the shipped example expands to real paths on any machine");
            string root = TempDir();
            try
            {
                string ini = Path.Combine(root, "worker.ini");
                File.WriteAllText(ini, string.Join("\n", new string[] {
                    "DataDir = " + root,
                    "[service.echo]",
                    "Enabled = true",
                    "Command = %RUNTIME%\\echo\\python\\python.exe",
                    "Arguments = \"%SCRIPTS%\\echo\\controller.py\" --queue \"%QUEUE%\"",
                    ""
                }));
                Config c = Config.Load(ini);
                ServiceDef d = c.Service("echo");

                Ok(d.Command.IndexOf('%') < 0, "no placeholder survives into Command: " + d.Command);
                Ok(d.Arguments.IndexOf('%') < 0, "nor into Arguments: " + d.Arguments);
                Ok(d.Command.StartsWith(c.RuntimeRoot, StringComparison.OrdinalIgnoreCase),
                    "Command lands under RuntimeRoot");
                Ok(d.Arguments.IndexOf(c.ScriptsRoot, StringComparison.OrdinalIgnoreCase) >= 0,
                    "the controller comes from ScriptsRoot");
                Ok(d.Arguments.IndexOf(d.QueueDir, StringComparison.OrdinalIgnoreCase) >= 0,
                    "and the queue is this service's own");
                Ok(!d.InstallDir.Equals(c.ScriptsRoot, StringComparison.OrdinalIgnoreCase),
                    "what a service downloads is not where its scripts live, so removing " +
                    "it cannot delete the provisioning script needed to reinstall it");
                Ok(d.WorkingDir == d.InstallDir,
                    "and a controller with no WorkingDir runs in its own directory, not in " +
                    "whatever directory the agent happened to be started from");
            }
            finally { Nuke(root); }
        }

        /// THE DEFECT THIS PREVENTS, found by driving the real CLI on the real
        /// machine rather than by reading the code. The submit handler checked only
        /// that the body started with { and ended with }, so a body that was not
        /// JSON at all was accepted, written into a lease, answered 202, and failed
        /// several minutes later inside the controller as "the lease could not be
        /// parsed" - by which time the client had gone and the error was nowhere
        /// near its cause. The cause was PowerShell, which strips double quotes
        /// when passing an argument to a native executable, so {"text":"hi"}
        /// arrives as {text:hi}. PowerShell is the shell on the machines this runs
        /// on, so that is the ordinary case rather than an exotic one.
        static void AMalformedBodyIsRefusedAtSubmitNotInsideTheController()
        {
            Case("a malformed body is refused at submit");

            // The exact string PowerShell produced on spring.
            Ok(!Json.LooksLikeObject("{text:the runner does not know,seconds:3}"),
                "the measured PowerShell-mangled body is refused");

            string[] bad = {
                "", "  ", "null", "[]", "[1,2]", "\"a string\"", "42",
                "{", "}", "{\"a\"}", "{\"a\":}", "{:1}", "{\"a\":1,}", "{'a':1}",
                "{\"a\":1}{\"b\":2}",          // two documents, not one
                "{\"a\":1} trailing",
                "{\"a\":01}",                  // a leading zero is not a JSON number
                "{\"a\":.5}", "{\"a\":1.}", "{\"a\":1e}",
                "{\"a\":\"unterminated}",
                "{\"a\":\"bad \\x escape\"}",
                "{\"a\":tru}",
            };
            foreach (string b in bad) Ok(!Json.LooksLikeObject(b), "refused: " + Show(b));

            string[] good = {
                "{}", "{ }", "  {\"a\":1}  ",
                "{\"text\":\"hello\",\"seconds\":3,\"units\":3}",
                "{\"a\":[1,2,{\"b\":null}],\"c\":{\"d\":true},\"e\":-1.5e10}",
                "{\"unicode\":\"\\\\u00e9\\\\n\\\\t\"}",
                "{\"argv\":[\"-m\",\"22000\",\"hash.hc22000\"]}",
            };
            foreach (string b in good) Ok(Json.LooksLikeObject(b), "accepted: " + Show(b));

            // A deeply nested body must be REFUSED, not crash the process. This
            // runs on a listener thread against bytes from whoever opened the
            // socket, and a stack overflow on .NET cannot be caught: it would take
            // the policy loop down with it, which is the one thread whose job is to
            // give somebody their GPU back.
            var deep = new StringBuilder();
            for (int i = 0; i < 5000; i++) deep.Append("{\"a\":");
            deep.Append("1");
            for (int i = 0; i < 5000; i++) deep.Append("}");
            Ok(!Json.LooksLikeObject(deep.ToString()),
                "5000 levels of nesting is refused rather than overflowing the stack");
        }

        /// THE DEFECT THIS PREVENTS, measured on spring during a real GPU yield.
        ///
        /// A speech controller in the middle of generate() cannot check the yield
        /// flag, because the model has no interruption point inside it. So the job
        /// object kills it - the designed path, and it reclaimed 4013 MiB of VRAM
        /// in 2.46 s. A killed process cannot tidy up, so its lease is left in
        /// working/, and the job endpoint read that as "running".
        ///
        /// Nothing was running. A client polling that job watches a process that
        /// does not exist, on a machine whose owner is now playing a game, and the
        /// honest answer is "queued": the controller library requeues orphans when
        /// it next starts, so the job really will resume, and queued is what tells
        /// a caller to keep waiting rather than give up or submit it a second time.
        static void AKilledControllersLeaseReadsAsQueuedNotRunning()
        {
            Case("a killed controller's lease reads as queued");
            string root = TempDir();
            try
            {
                var store = new JobStore("speech", Path.Combine(root, "q"));
                store.EnsureDirs();
                store.Submit("job1", "{\"segments\":[\"hello\"]}", null, null);

                Ok(store.Look("job1", 4096, false).State == JobState.Queued,
                    "a fresh lease in pending is queued");

                // The controller claims it, the way the shared library does: by
                // renaming into working/.
                File.Move(Path.Combine(store.PendingDir, "job1.json"),
                          Path.Combine(store.WorkingDir, "job1.json"));

                Ok(store.Look("job1", 4096, true).State == JobState.Running,
                    "with a live controller it is running");
                Ok(store.Look("job1", 4096, false).State == JobState.Queued,
                    "with no controller it is queued again, not running for ever");

                // And a job that actually finished stays finished, whatever the
                // controller is doing now. This is the half that would let the fix
                // above quietly turn every completed job back into queued.
                File.WriteAllText(Path.Combine(store.DoneDir, "job1.done.json"),
                                  "{\"status\":\"done\"}");
                Ok(store.Look("job1", 4096, false).State == JobState.Done,
                    "a terminal record still wins over an orphaned lease");
            }
            finally { Nuke(root); }
        }

        /// THE DEADLOCK THIS PREVENTS, measured on spring on a real GPU yield.
        ///
        /// A controller killed mid-unit leaves its lease in working/. The
        /// controller library requeues orphans when it starts - and the scheduler
        /// only starts a controller for a service that has work in PENDING. The
        /// only lease is the orphan, in working. Each half is correct on its own
        /// and together they wait for each other for ever: the job reports queued,
        /// the scheduler reports an idle service, and nothing ever moves again.
        static void AnOrphanedLeaseIsRescuedRatherThanWaitingForItself()
        {
            Case("an orphaned lease is rescued, not waited on");
            string root = TempDir();
            try
            {
                var store = new JobStore("speech", Path.Combine(root, "q"));
                store.EnsureDirs();
                store.Submit("job1", "{}", null, null);
                File.Move(Path.Combine(store.PendingDir, "job1.json"),
                          Path.Combine(store.WorkingDir, "job1.json"));

                Ok(store.QueuedCount() == 0,
                    "the scheduler sees nothing to do, which is the deadlock");
                Ok(store.RequeueOrphans() == 1, "one lease is rescued");
                Ok(store.QueuedCount() == 1,
                    "and now the scheduler will start the controller that resumes it");

                // A job that FINISHED leaves a lease behind too, if the controller
                // was killed between writing its record and tidying up. Rerunning
                // that would speak the same text a second time, which for a speech
                // service is the one outcome idempotency exists to prevent.
                store.Submit("job2", "{}", null, null);
                File.Move(Path.Combine(store.PendingDir, "job2.json"),
                          Path.Combine(store.WorkingDir, "job2.json"));
                File.WriteAllText(Path.Combine(store.DoneDir, "job2.done.json"), "{}");
                Ok(store.RequeueOrphans() == 0, "a finished job is not resurrected");
                Ok(!File.Exists(Path.Combine(store.WorkingDir, "job2.json")),
                    "and its stale lease is cleared away rather than left to be found again");
                Ok(store.Look("job2", 4096, false).State == JobState.Done,
                    "it stays done");
            }
            finally { Nuke(root); }
        }

        static void Nuke(string dir)
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        // ------------------------------------------- the network facing edges ----

        /// THE DEFECT THIS PREVENTS: a job id is the only client-chosen string that
        /// ever reaches Path.Combine. A listener that accepts "..\\..\\worker.ini"
        /// is a file server for the whole user profile, over TLS, with a bearer
        /// token that was only ever meant to authorise speech.
        static void AJobIdFromTheNetworkCannotEscapeTheQueueDirectory()
        {
            Case("a job id cannot escape the queue directory");
            string[] evil = {
                "..", "..\\..\\worker.ini", "../../worker.ini", "a/b", "a\\b",
                "C:\\Windows\\win.ini", "\\\\server\\share\\x", "a:b", "",
                // Reserved device names. Not traversals, but "done\\nul.wav" is a
                // write that succeeds and stores nothing, and "con" opens a console.
                "con", "CON", "nul.json", "aux", "prn", "com1", "LPT9",
                new string('a', 65), ".hidden", "a\0b"
            };
            foreach (string e in evil)
                Ok(!JobStore.SafeJobId(e), "refused: " + Show(e));
            foreach (string good in new string[] { "abc123", "a-b_c", "0123456789abcdef" })
                Ok(JobStore.SafeJobId(good), "accepted: " + good);
        }

        /// The same check on the other client-chosen string, which arrives in a
        /// query parameter rather than a path segment and is therefore easy to
        /// forget about.
        static void AnArtefactNameFromAQueryStringCannotEscapeEither()
        {
            Case("an artefact name cannot escape either");
            string root = TempDir();
            try
            {
                var store = new JobStore("svc", Path.Combine(root, "q"));
                store.EnsureDirs();
                File.WriteAllText(Path.Combine(root, "secret.txt"), "the api key");
                File.WriteAllText(Path.Combine(store.DoneDir, "job1.wav"), "audio");

                Ok(store.ArtefactPath("job1", "job1.wav") != null, "a real artefact resolves");
                Ok(store.ArtefactPath("job1", "..\\..\\secret.txt") == null, "a traversal does not");
                Ok(store.ArtefactPath("job1", "../../secret.txt") == null, "nor with forward slashes");
                Ok(store.ArtefactPath("job1", "C:\\Windows\\win.ini") == null, "nor an absolute path");
                Ok(store.ArtefactPath("job1", "other.wav") == null, "nor an artefact of another job");
            }
            finally { Nuke(root); }
        }

        static string Show(string s)
        {
            if (s == null) return "(null)";
            if (s.Length == 0) return "(empty)";
            if (s.Length > 24) return s.Substring(0, 21) + "...";
            return s.Replace("\0", "\\0");
        }

        static HttpRequest Parse(string raw, out int status)
        {
            status = 0;
            var lim = new HttpLimits();
            using (var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(raw)))
            {
                try { return Http.Read(new ByteReader(ms, 4096), lim); }
                catch (HttpError e) { status = e.Status; return null; }
            }
        }

        /// THE DEFECT THIS PREVENTS: a hand-written HTTP server that ignores
        /// Transfer-Encoding reads the chunk-size line as the body. Many clients
        /// send chunked for a streamed body without being asked. Refusing with 411
        /// is a server that says what it does not do; silently misreading is a
        /// server that writes a lease containing the text "1a\r\n".
        static void AChunkedBodyIsRefusedRatherThanMisread()
        {
            Case("a chunked body is refused, not misread");
            int status;
            HttpRequest q = Parse(
                "POST /v1/services/echo/jobs HTTP/1.1\r\nHost: x\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n", out status);
            Ok(q == null, "the request is rejected");
            // 501, not 411. 411 would mean "you forgot a Content-Length", which
            // invites the client to add one and try the same chunked body again.
            // 501 says the coding itself is not implemented, which is the truth.
            Ok(status == 501, "with 501 Not Implemented, not a silent misread; got " + status);
        }

        /// THE DEFECT THIS PREVENTS: curl sends Expect: 100-continue automatically
        /// for any body over 1 KB. A server that does not answer it leaves curl
        /// waiting out its full timeout, which reads to the user as "the runner is
        /// hung" rather than "this server does not do that".
        static void ExpectContinueIsRefusedRatherThanHung()
        {
            Case("expect: 100-continue is answered, not ignored");
            int status;
            HttpRequest q = Parse(
                "POST /v1/services/echo/jobs HTTP/1.1\r\nHost: x\r\n" +
                "Expect: 100-continue\r\nContent-Length: 2\r\n\r\n{}", out status);
            Ok(q == null, "the request is rejected");
            Ok(status == 417, "with 417 Expectation Failed, immediately; got " + status);
        }

        /// THE DEFECT THIS PREVENTS: one socket dribbling headers for ever is a
        /// slowloris, and this server has a bounded connection pool, so a handful
        /// of them is the whole listener. The cap has to bite during parsing rather
        /// than after it.
        static void AFloodOfHeadersIsCappedNotBuffered()
        {
            Case("a flood of headers is capped");
            var sb = new StringBuilder("GET /v1/status HTTP/1.1\r\n");
            for (int i = 0; i < 500; i++) sb.Append("X-Pad-").Append(i).Append(": x\r\n");
            sb.Append("\r\n");
            int status;
            HttpRequest q = Parse(sb.ToString(), out status);
            Ok(q == null, "the request is rejected");
            Ok(status == 431, "with 431 Request Header Fields Too Large; got " + status);
        }

        /// The same cap on the first line, which is read before any header count
        /// exists to check.
        static void TheRequestLineIsCappedBeforeItIsSplit()
        {
            Case("an enormous request line is capped");
            int status;
            HttpRequest q = Parse("GET /" + new string('a', 40000) + " HTTP/1.1\r\n\r\n", out status);
            Ok(q == null, "the request is rejected");
            Ok(status == 414, "with 414 URI Too Long, not 431, which would send the " +
                "client off to trim headers that were never the problem; got " + status);
        }

        /// THE DEFECT THIS PREVENTS: checking the path for ".." BEFORE percent
        /// decoding it, so that %2e%2e%2f walks straight past the check. The
        /// decoding happens in Http.Read, so the segments the router sees are the
        /// decoded ones and the id check runs on the real string.
        static void APercentEncodedTraversalIsStillATraversal()
        {
            Case("a percent-encoded traversal is decoded before it is checked");
            int status;
            HttpRequest q = Parse(
                "GET /v1/services/echo/jobs/%2e%2e%2f%2e%2e%2fworker.ini HTTP/1.1\r\n\r\n", out status);
            Ok(q != null, "it parses (rejecting it is the router's job, not the parser's)");
            if (q == null) return;
            string[] seg = q.Segments();
            Ok(q.Path.IndexOf("..") >= 0, "the parser decoded it, so the router can see the dots");
            string jobId = seg.Length > 4 ? seg[4] : "";
            Ok(!JobStore.SafeJobId(jobId), "and the id check refuses it: " + Show(jobId));
        }

    }
}
