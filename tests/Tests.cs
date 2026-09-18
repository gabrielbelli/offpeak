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
        static string _fixtureRoot;

        static void Main(string[] args)
        {
            _fixtures = args.Length > 0 ? args[0]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
            _fixtureRoot = _fixtures;

            TheExampleFileAndTheBuiltInDefaultsAgree();
            AFixtureWithoutCpuColumnsStillGivesItsOldVerdict();
            AMemoryColumnThatIsAbsentIsNotZeroMemory();
            OurOwnCpuLoadIsSubtractedNotSuppressed();
            AnAdmissionRefusalIsQueuedNotFailed();

            IdleDesktopEventuallyRunsJobs();
            IdleDesktopNeverLooksBusy();
            Session0RefusesForEverAndSaysWhyItCannotTell();
            Session0WithNobodySignedInIsAllowedToWork();
            Session0WithALockedDesktopIsAllowedToWork();
            Session0SaysWhyItCanSeeTheUserRatherThanInventingALock();
            OurOwnJobIsNotSomebodyElseUsingTheGpu();
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

            NobodySignedInGetsTheWholeMachine();
            SigningInEndsUnlimitedOnTheVeryNextSample();
            AnUnmeasurableIdleTimeIsNeverIdle();
            ARowNobodyConfiguredTakesNothing();
            AZeroCpuCapMeansStopBecauseWindowsHasNoZeroCap();
            AJobBeingStoppedIsNotUncappedOnTheWayOut();
            AGridThatInvertsTheLadderIsRepairedNotObeyed();
            AProfileDoesNotDependOnWhereItSitsInTheFile();
            ASavedProfileComesBackWithItsName();
            AdmitIsSeparateFromTheCap();
            ThreadCountFollowsTheCapAndStopsAtPhysicalCores();
            OurOwnCpuIsNotSomebodyElseUsingTheMachine();
            AGameOnTheCardDoesNotStopTheProcessor();
            ACompileDoesNotStopTheCard();
            AnUnexplainedBusyGetsTheWholeBusyRow();
            TheCooldownRemembersWhichResourceWasContended();
            TheLadderTightensAtOnceAndLoosensOnTheCooldown();
            ACpuServiceDoesNotWaitOutAGpuCooldown();
            AnUnknownPriorityBecomesIdleNotNormal();
            LimitsSurviveARoundTripThroughWorkerIni();
            AnUnmeasurableFreeMemoryDoesNotStopEverything();
            AJobIsNotStartedWithNoMemoryHeadroom();
            TheShippedCpuCapsAreTheMeasuredOnes();
            ForeignCpuAloneIsNeverOneSampleWorthOfBusy();
            EveryTrayRungIsReachableAndSaysWhatItDoes();

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

        /// THE DEFECT THIS PREVENTS, and it made the runner useless: the agent
        /// yielding to its own job.
        ///
        /// Every tier 2 signal is a WHOLE-GPU reading with no owner attached, so
        /// the moment our controller puts a model on the card the memory clock
        /// goes to 6801 MHz and the performance state to P2 -- and tier 2 read
        /// that as somebody else at the machine. Measured on the first real job
        /// through this runner: started, saw the clocks it had itself caused,
        /// yielded, killed the job tree, cooled down ninety seconds, repeated.
        /// Four times, on a locked desktop with nobody near it, with the job
        /// sitting queued throughout.
        ///
        /// The VRAM rule already had this exemption. The load votes did not.
        static void OurOwnJobIsNotSomebodyElseUsingTheGpu()
        {
            Case("the agent does not yield to the load it created itself");
            Policy p;
            List<WorkerState> st = Run("own_job_is_the_load.csv", out p, 14416);
            Ok(st[st.Count - 1] == WorkerState.Available,
                "it stays Available while its own controller drives the card");
            Ok(p.CanRun(Mode.Auto), "so the job is allowed to keep running");
            Ok(!p.Last.ReasonText.Contains("memory clock"),
                "and the clocks it caused are not quoted back as a reason: "
                + p.Last.ReasonText);

            // The same samples with the load belonging to somebody ELSE must
            // still stop it, or this fix would have disabled tier 2 outright.
            Policy q;
            List<WorkerState> other = Run("own_job_is_the_load.csv", out q, -1);
            Ok(other[other.Count - 1] != WorkerState.Available,
                "identical load from an unknown process still stops it");
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
                // CHANGED DELIBERATELY when the runner started selling the
                // processor as well as the card. The old assertion was that a
                // per-service block never says the word "gpu", which was the
                // right rule while there was one resource and one answer: the
                // machine's availability belonged at the top of the document and
                // repeating it per service was how the two got conflated.
                //
                // With two resources a service must say WHICH ONE it wants, or
                // the client is left to guess from the manifest, and it must
                // carry its OWN availability, already resolved against the right
                // gate, or a CPU service reads as busy whenever a game is on the
                // card it never touches. The rule the old test protected is kept
                // and asserted below: the MACHINE-wide gpu_available is still
                // reported once, at the top, and never inside a service.
                Ok(json.Contains("\"device\":\"gpu\""),
                    "the block says which resource this service is after");
                Ok(json.Contains("\"available\":null"),
                    "and, with no agent to ask, says it does not know whether the machine is "
                    + "free rather than guessing false");
                Ok(!json.Contains("gpu_available"),
                    "but the machine-wide answer is still reported once, for the machine, "
                    + "and never repeated per service");
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

        /// THE DEFECT THIS PREVENTS, AND IT WAS LIVE IN THREE PLACES AT ONCE.
        ///
        /// The GPU column of the light-use row was shipped three ways and no two
        /// agreed. Config.DefaultLimits returned Gpu = false; worker.ini.example
        /// shipped `limits.lightuse.gpu = yes`; Policy.CanRunGpu's docstring
        /// promised the column ships as yes in every row but Busy so that
        /// installing the release changed nobody's behaviour. So a machine using
        /// the example file behaved differently from one using the built-in
        /// defaults, silently, and the documentation described neither.
        ///
        /// Nothing catches that by reading, because the three live in different
        /// files and each is defensible on its own. This does: the shipped example
        /// is loaded and compared against the shipped defaults, cell by cell.
        ///
        /// If this fails, ONE of the two is wrong and the fix is to decide which
        /// rather than to relax the assertion.
        static void TheExampleFileAndTheBuiltInDefaultsAgree()
        {
            Case("the shipped example and the built-in defaults are the same machine");
            string example = FindExample();
            if (example == null)
            {
                Ok(true, "worker.ini.example is not beside the tests; skipped");
                return;
            }
            string root = TempDir();
            try
            {
                // Copied so the load cannot touch the real file, and given a
                // DataDir so nothing resolves into the user's profile.
                string ini = Path.Combine(root, "worker.ini");
                var lines = new List<string>(File.ReadAllLines(example));
                lines.Insert(0, "DataDir = " + root);
                File.WriteAllLines(ini, lines.ToArray());

                Config c = Config.Load(ini);
                Ok(c.GridRepairs.Count == 0,
                    "the shipped example is monotone and needs no repair"
                    + (c.GridRepairs.Count > 0 ? ": " + c.GridRepairs[0] : ""));
                Ok(c.EffectiveProfile() == Config.ProfileBalanced,
                    "and it is exactly the balanced posture, not a custom one (got "
                    + c.EffectiveProfile() + ")");

                var built = new Config();
                foreach (MachineState st in Config.AllStates())
                {
                    ResourceLimits a = c.LimitsFor(st);
                    ResourceLimits b = built.LimitsFor(st);
                    Ok(a.SameAs(b), Config.StateLabel(st) + " matches: example says "
                        + a.Describe() + ", the built-in default says " + b.Describe());
                }
                Ok(c.IdleAfterSeconds == built.IdleAfterSeconds, "IdleAfterSeconds agrees");
                Ok(c.ForeignCpuBusyPct == built.ForeignCpuBusyPct, "ForeignCpuBusyPct agrees");

                // And the two speech services really are one on each device, so
                // the per-device scheduler gate has two groups to put them in.
                ServiceDef gpu = c.Service("chatterbox");
                ServiceDef cpu = c.Service("chatterbox-cpu");
                Ok(gpu != null && cpu != null, "the example declares both speech services");
                if (gpu != null && cpu != null)
                {
                    Ok(gpu.WantsGpu, "chatterbox waits for the card");
                    Ok(!cpu.WantsGpu, "chatterbox-cpu does not");
                    Ok(cpu.NeedsMemoryMib > 0,
                        "and the CPU one declares what it needs resident, for the admission check");
                    Ok(cpu.Arguments.IndexOf("--device cpu", StringComparison.Ordinal) >= 0,
                        "and names its device rather than letting auto resolve to cuda");
                }
            }
            finally { Nuke(root); }
        }

        /// The example lives beside the tests in the repository and nowhere near
        /// them in an install, so it is looked for rather than assumed.
        static string FindExample()
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            var tried = new List<string>();
            string dir = here;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                tried.Add(Path.Combine(dir, "worker.ini.example"));
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            // The fixtures directory is passed on the command line, so the
            // repository root is two levels above it whatever the build did.
            if (_fixtureRoot != null)
            {
                string up = Path.GetDirectoryName(_fixtureRoot.TrimEnd(Path.DirectorySeparatorChar));
                if (up != null)
                {
                    tried.Add(Path.Combine(up, "worker.ini.example"));
                    string up2 = Path.GetDirectoryName(up.TrimEnd(Path.DirectorySeparatorChar));
                    if (up2 != null) tried.Add(Path.Combine(up2, "worker.ini.example"));
                }
            }
            foreach (string p in tried) if (File.Exists(p)) return p;
            return null;
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

        // ----------------------------------------------- the limits matrix ---
        //
        // These drive the same Policy.Evaluate the tray runs. The snapshots are
        // built by hand rather than replayed, because the session and CPU fields
        // the ladder reads are not in the recorded GPU fixtures, and inventing a
        // CSV column for a signal nobody has recorded would be a fixture that
        // asserts nothing about the machine.

        static Snapshot Sn(bool hasUser, bool locked, bool inConsole, bool presenceFresh,
                           int idleSeconds, DateTime at)
        {
            var s = new Snapshot();
            s.At = at;
            s.GpuHealthy = true;
            s.Gpu = new GpuSample();
            s.Gpu.Valid = true;
            s.Gpu.PState = "P8";
            s.Gpu.ClockMemMhz = 405;
            s.Gpu.PowerWatts = 34.0;
            s.Gpu.UtilGpu = 5;
            s.Session = new SessionSignals();
            s.Session.HasConsoleUser = hasUser;
            s.Session.Locked = locked;
            s.Session.RunningInConsoleSession = inConsole;
            s.Session.PresenceFresh = presenceFresh;
            s.Session.InputIdleSeconds = idleSeconds;
            s.Session.ConsoleSessionId = 1;
            s.Session.OwnSessionId = inConsole ? 1u : 0u;
            s.Session.ConsoleUserName = hasUser ? "someone" : "";
            s.Launchers = new LauncherSignals();
            return s;
        }

        static DateTime T0 = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        /// THE DEFECT THIS PREVENTS: a verdict that reports a fact about the
        /// machine which is not true of the machine.
        ///
        /// The reason string said "the desktop is locked" whenever a console
        /// user existed, without ever consulting Locked. A boot agent is
        /// permanently outside the console session, so it published that on
        /// every verdict where somebody was signed in -- measured on spring
        /// 2026-09-09 beside its own JSON reading "locked": false, with the
        /// session idle for 930 s and correctly classified Idle.
        ///
        /// Nothing behaved wrongly, and that is what made it worth a test: the
        /// reason is the only thing an owner reads, and it said this agent
        /// works only while the screen is locked. A server watching that string
        /// would conclude the same.
        static void Session0SaysWhyItCanSeeTheUserRatherThanInventingALock()
        {
            Case("session 0 with the helper reporting does not claim a lock");
            var p = new Policy(new Config());
            // Signed in, NOT locked, outside the console session, and the logon
            // helper is fresh: exactly the deployed shape.
            Verdict v = p.Evaluate(Sn(true, false, false, true, 930, T0));
            Ok(!v.ReasonText.Contains("locked"),
                "the reason does not claim a lock that is not there: " + v.ReasonText);
            Ok(v.ReasonText.Contains("helper"),
                "it says the helper is what makes the user visible: " + v.ReasonText);
            Ok(!v.Blind, "and it is not blind: the helper is reporting");
            Ok(v.MachineState == MachineState.Idle,
                "930 s of no input is Idle (got " + v.MachineState + ")");

            // The genuinely locked case must keep saying so.
            Verdict locked = p.Evaluate(Sn(true, true, false, true, 5, T0.AddSeconds(1)));
            Ok(locked.ReasonText.Contains("locked"),
                "a real lock is still named: " + locked.ReasonText);

            // And nobody signed in must still say nobody.
            Verdict nobody = p.Evaluate(Sn(false, false, false, false, -1, T0.AddSeconds(2)));
            Ok(nobody.ReasonText.Contains("nobody is signed in"),
                "the sign-in screen is still named: " + nobody.ReasonText);
        }

        /// The owner's own stated default, and the one that makes the hard half of
        /// the problem go away: when nobody is at the machine there is nobody to
        /// disturb, so there is nothing to throttle.
        static void NobodySignedInGetsTheWholeMachine()
        {
            Case("nobody signed in means unlimited CPU, which is the chosen default");
            var p = new Policy(new Config());
            Verdict v = p.Evaluate(Sn(false, false, false, false, -1, T0));
            Ok(v.MachineState == MachineState.NobodyHome,
                "the sign-in screen classifies as nobody home (got " + v.MachineState + ")");
            Ok(v.Limits.CpuPct == 100, "and the CPU cap is 100, which is no cap at all");
            Ok(v.Limits.Gpu, "and the GPU is ours");
            Ok(Config.NormalisePriority(v.Limits.Priority) == "normal",
                "and it runs at normal priority, because there is nobody to be polite to");
        }

        /// UNLIMITED MUST END THE INSTANT SOMEBODY SIGNS IN. This is the same
        /// promise as any other yield and it is the case worth measuring: a job at
        /// full tilt when the owner appears. One sample, no confirmation window,
        /// no cooldown on the way DOWN.
        static void SigningInEndsUnlimitedOnTheVeryNextSample()
        {
            Case("signing in ends unlimited on the very next sample");
            var p = new Policy(new Config());
            Verdict before = p.Evaluate(Sn(false, false, false, false, -1, T0));
            Ok(before.Limits.CpuPct == 100, "flat out while nobody is signed in");
            // The next second: signed in, unlocked, typing.
            Verdict after = p.Evaluate(Sn(true, false, true, true, 0, T0.AddSeconds(1)));
            Ok(after.MachineState == MachineState.LightUse,
                "one sample later it is light use (got " + after.MachineState + ")");
            Ok(after.Limits.CpuPct == 10,
                "and the cap is already 10 per cent, in the same tick (got " +
                after.Limits.CpuPct + ")");
            Ok(Config.NormalisePriority(after.Limits.Priority) == "idle",
                "and the priority is already idle");
        }

        /// An idle time this agent cannot measure is not a measured idle time. A
        /// boot agent lives in session 0, where GetLastInputInfo answers for the
        /// CALLING session and reports a number that is a lie about the user -
        /// measured on spring at 620,953 ms with somebody sitting at the keyboard.
        /// Reading that as "idle for ten minutes" would hand the machine over to a
        /// job while its owner was using it.
        static void AnUnmeasurableIdleTimeIsNeverIdle()
        {
            Case("an idle time this agent cannot see is never Idle");
            var p = new Policy(new Config());
            // Signed in, unlocked, no helper reporting, and a session-0 read that
            // claims ten minutes of idle.
            Verdict v = p.Evaluate(Sn(true, false, false, false, 600, T0));
            Ok(v.Blind, "session 0 with somebody signed in and no helper is blind");
            Ok(v.MachineState == MachineState.Busy,
                "and blind classifies as busy, not idle (got " + v.MachineState + ")");
            Ok(v.Limits.CpuPct == 0, "so it takes nothing at all");

            // With the helper reporting the SAME idle time, it is genuinely idle.
            var q = new Policy(new Config());
            Verdict w = q.Evaluate(Sn(true, false, false, true, 600, T0));
            Ok(w.MachineState == MachineState.Idle,
                "the same 600 s reported by the logon helper IS idle (got " + w.MachineState + ")");
        }

        /// THE REGRESSION GUARD ON THE WHOLE FIXTURE-COMPATIBILITY ARGUMENT, and
        /// it was written before the columns were added rather than after.
        ///
        /// Replay.Load builds a header-to-index map from row 0 and Get() returns
        /// "" for a column that is not there, so ADDING names to the header cannot
        /// break a read. The danger is never the read; it is the DEFAULT'S
        /// MEANING. Every fixture in this directory was recorded before the
        /// processor was something this runner sold, and none of them says
        /// anything about it. If a missing cpu_total_pct read as an idle
        /// processor, that would be a measurement in the record that was never
        /// taken - and the next person to add a column would copy the pattern.
        ///
        /// So every existing fixture must still produce exactly the verdict it
        /// produced before, and this checks all of them rather than a chosen one.
        static void AFixtureWithoutCpuColumnsStillGivesItsOldVerdict()
        {
            Case("a fixture with no CPU columns still gives its old verdict");
            string[] files = Directory.GetFiles(_fixtures, "*.csv");
            Ok(files.Length > 0, "there are fixtures to check (" + files.Length + ")");
            int old = 0, withCpu = 0, checkedRows = 0;
            foreach (string f in files)
            {
                List<Snapshot> rows = Replay.Load(f);
                if (rows.Count == 0) continue;
                string name = Path.GetFileName(f);
                bool anyCpu = false, anyMem = false;
                foreach (Snapshot s in rows)
                {
                    if (s.Cpu != null && s.Cpu.Valid) anyCpu = true;
                    if (s.Memory != null && s.Memory.Valid) anyMem = true;
                }
                if (anyCpu) { withCpu++; Ok(anyMem, name + ": records both new columns or neither"); continue; }
                Ok(!anyMem, name + ": memory is unmeasured, not zero");
                old++;
                checkedRows += rows.Count;

                // And the policy really does skip the CPU tier on it, so a GPU-only
                // recording cannot acquire a reason about the processor that it
                // never recorded.
                var p = new Policy(new Config());
                foreach (Snapshot s in rows)
                {
                    Verdict v = p.Evaluate(s);
                    if (v.ReasonText.IndexOf("somebody else's work", StringComparison.Ordinal) >= 0)
                    {
                        Ok(false, name + " grew a CPU reason it never recorded");
                        return;
                    }
                }
            }
            Ok(old > 0, old + " fixtures predate the processor columns, over "
                + checkedRows + " recorded samples");
            Ok(withCpu > 0, withCpu + " newer fixtures do carry them, so both paths are exercised");
            Ok(true, "and no older fixture grew a CPU reason it never recorded");
        }

        /// THE SAME DEFECT THE GPU HAS, ON THE SIDE THAT CAN ACTUALLY FIX IT.
        ///
        /// A whole-machine load reading has no owner attached to it, so the moment
        /// our own controller starts, the agent reads the load IT created as
        /// somebody else at the machine and yields to itself. Measured on the GPU:
        /// four restarts in a row on a locked desktop with nobody near it. The GPU
        /// could only paper over that by SUPPRESSING its load votes while a job
        /// runs, because nvidia-smi reports [N/A] for per-process memory on this
        /// machine and there is no way to attribute utilisation to a process.
        ///
        /// The CPU does not have to make that trade. A job object accounts for its
        /// own processes exactly, so the load is SUBTRACTED rather than muted, and
        /// the signal keeps working WHILE a job runs - which is precisely when it
        /// matters, because that is when the owner comes back.
        ///
        /// Replayed from recorded columns rather than hand-built snapshots, so the
        /// arithmetic in Replay.Load is exercised too.
        static void OurOwnCpuLoadIsSubtractedNotSuppressed()
        {
            Case("our own CPU load is subtracted, not suppressed");

            // The machine at 92.7 per cent, and 92.4 of it is inside our job.
            var p = new Policy(new Config());
            Verdict v = null;
            List<Snapshot> ours = Replay.Load(Path.Combine(_fixtures, "cpu_own_job_is_the_load.csv"));
            Ok(ours.Count > 0, "the fixture loads (" + ours.Count + " samples)");
            foreach (Snapshot s in ours) v = p.Evaluate(s);
            Ok(ours[0].Cpu.MachinePct > 90, "the machine really is flat out");
            Ok(ours[0].Cpu.ForeignPct < 1,
                "and almost none of it is somebody else's (got " + ours[0].Cpu.ForeignPct + ")");
            Ok(v.MachineState != MachineState.Busy,
                "so twenty seconds of our own load never reads as a user (got " + v.MachineState + ")");
            Ok(!v.CpuContended, "and the processor is not marked contended");
            Ok(v.ReasonText.IndexOf("somebody else's work", StringComparison.Ordinal) < 0,
                "and no reason blames a person: " + v.ReasonText);

            // Somebody else's compile, on a card nobody is touching.
            var p2 = new Policy(new Config());
            Verdict v2 = null;
            foreach (Snapshot s in Replay.Load(Path.Combine(_fixtures, "cpu_busy_compile.csv")))
                v2 = p2.Evaluate(s);
            Ok(v2.MachineState == MachineState.Busy,
                "somebody else's compile does read as busy (got " + v2.MachineState + ")");
            Ok(v2.CpuContended, "with the processor contended");
            Ok(!v2.GpuContended, "and the card left alone");
            Ok(p2.CanRunGpu(Mode.Auto) || p2.State != WorkerState.Available,
                "so the GPU column is not closed by a compile");

            // A job of ours running while somebody else does something small. 94
            // per cent of the machine, but only 22 of it is theirs, which is under
            // the trip point - so the job carries on rather than yielding to a
            // number it is mostly responsible for itself.
            var p3 = new Policy(new Config());
            Verdict v3 = null;
            List<Snapshot> mixed = Replay.Load(Path.Combine(_fixtures, "cpu_mostly_ours.csv"));
            foreach (Snapshot s in mixed) v3 = p3.Evaluate(s);
            Ok(mixed[0].Cpu.MachinePct > 90, "the machine is flat out again");
            Ok(mixed[0].Cpu.ForeignPct > 20 && mixed[0].Cpu.ForeignPct < 25,
                "and 22 per cent of it is theirs (got " + mixed[0].Cpu.ForeignPct + ")");
            Ok(v3.MachineState != MachineState.Busy,
                "which is under the trip point, so the job carries on (got " + v3.MachineState + ")");
        }

        /// A REFUSAL IS A WAIT, NOT A FAILURE. A job the runner will not start yet
        /// stays queued and is tried again on the next tick; it is never failed
        /// back to the caller, because "not right now" is not "never" and the
        /// caller cannot tell the difference from a rejection.
        static void AnAdmissionRefusalIsQueuedNotFailed()
        {
            Case("a memory refusal is a wait, not a failure");
            List<Snapshot> rows = Replay.Load(Path.Combine(_fixtures, "memory_pressure.csv"));
            Ok(rows.Count > 0, "the fixture loads");
            Snapshot s = rows[0];
            Ok(s.Memory.Valid, "memory is measured");
            Ok(s.Memory.AvailableMib < 2048,
                "and the machine has almost nothing free (" + s.Memory.AvailableMib + " MiB)");

            var c = new Config();
            var p = new Policy(c);
            Verdict v = null;
            foreach (Snapshot one in rows) v = p.Evaluate(one);

            // The policy's own verdict is unaffected: memory is an admission
            // threshold and not a veto. Every idle Windows desktop has half its
            // memory in use, and a veto on that would never clear.
            Ok(v.MachineState != MachineState.Busy,
                "low memory is not itself evidence of a user (got " + v.MachineState + ")");
            Ok(p.CanRunCpu(Mode.Auto), "so a job already running is not stopped for it");

            // But a NEW one is not started.
            string why;
            Ok(!Policy.MemoryAllows(s.Memory.AvailableMib, s.Memory.Valid,
                                    v.Limits.MinFreeMib, 7168, out why),
                "while a new 7 GiB job is refused: " + why);
            Ok(why.Length > 0, "and the refusal says why, which is what makes it a wait");
        }

        /// AN ABSENT MEMORY COLUMN IS NOT ZERO MEMORY, and this is the one where
        /// getting the default wrong would have been catastrophic rather than
        /// merely dishonest.
        ///
        /// mem_avail_mib defaulting to 0 does not mean "we did not look", it means
        /// ZERO BYTES FREE. Policy.MemoryAllows would then have refused admission
        /// on every fixture recorded before the column existed: each would have
        /// kept its policy verdict, passed every existing test, and silently
        /// stopped being able to start a job.
        ///
        /// The rule is the opposite of the one tier 0 uses for the user, and both
        /// reasons belong in the docstrings because the next person to add a
        /// signal will pick one of them by pattern-matching. Blindness about the
        /// USER fails closed: being wrong costs somebody their match. Blindness
        /// about FREE MEMORY fails open: being wrong costs a runner that never
        /// starts anything, on a machine whose counter is simply unavailable.
        static void AMemoryColumnThatIsAbsentIsNotZeroMemory()
        {
            Case("an absent memory column is not zero memory");
            string why;

            // Absent. Fails OPEN.
            Ok(Policy.MemoryAllows(0, false, 6144, 5120, out why),
                "unmeasured memory admits the job rather than refusing for ever");
            Ok(why.Length == 0, "and says nothing, because there is nothing to report");

            // Present and genuinely low. Fails CLOSED, with a reason a person can read.
            Ok(!Policy.MemoryAllows(2048, true, 6144, 5120, out why),
                "2 GiB free against an 11 GiB reservation refuses");
            Ok(why.IndexOf("2,048", StringComparison.Ordinal) >= 0
               || why.IndexOf("2048", StringComparison.Ordinal) >= 0,
                "and names the number: " + why);

            // Present and ample.
            Ok(Policy.MemoryAllows(24000, true, 6144, 5120, out why), "24 GiB free admits it");

            // A row that asks for no headroom never refuses, whatever is free,
            // because MinFreeMib of zero means "start regardless" and not "start
            // when there is nothing left".
            Ok(Policy.MemoryAllows(1, true, 0, 5120, out why),
                "a row with no headroom rule starts regardless");

            // And the round trip through a real fixture-shaped file: present and
            // low really does refuse, so the column is wired up and not merely
            // parsed.
            string dir = Path.Combine(Path.GetTempPath(), "idlegpu-mem-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string csv = Path.Combine(dir, "low.csv");
                File.WriteAllLines(csv, new string[] {
                    "iso_time,gpu_healthy,console_user,locked,own_session,console_session,mem_total_mib,mem_avail_mib",
                    "2026-09-06T12:00:00.0000000Z,1,,0,1,1,32670,1500",
                });
                List<Snapshot> rows = Replay.Load(csv);
                Ok(rows.Count == 1, "the fixture loads");
                Ok(rows[0].Memory.Valid, "and its memory column is measured");
                Ok(rows[0].Memory.AvailableMib == 1500,
                    "with the number it recorded (got " + rows[0].Memory.AvailableMib + ")");
                Ok(!Policy.MemoryAllows(rows[0].Memory.AvailableMib, rows[0].Memory.Valid,
                                        6144, 5120, out why),
                    "and a job that needs headroom is not started on it");
            }
            finally { Nuke(dir); }
        }

        /// THE RATCHET ASSUMES SOMETHING NOTHING CHECKED.
        ///
        /// Policy.Evaluate holds the worst MachineState seen inside ninety seconds
        /// and then looks its row up, which takes for granted that a worse state
        /// has a smaller row. A grid where Busy is more generous than LightUse -
        /// one hand edit, one mistyped spinner, one saved custom posture - would
        /// invert the ratchet and hand the job MORE machine at the moment the owner
        /// sat down, which is the exact opposite of the only promise this program
        /// makes.
        ///
        /// REPAIRED, NOT REFUSED. Somebody who mistypes a number must not end up
        /// with an agent that will not start.
        static void AGridThatInvertsTheLadderIsRepairedNotObeyed()
        {
            Case("a grid that inverts the ladder is repaired, not obeyed");
            string dir = Path.Combine(Path.GetTempPath(), "idlegpu-mono-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string ini = Path.Combine(dir, "worker.ini");
                File.WriteAllLines(ini, new string[] {
                    "[limits]",
                    "limits.lightuse.cpupct = 90",     // looser than idle's 50
                    "limits.busy.cpupct = 100",        // looser than everything
                    "limits.busy.gpu = yes",
                    "limits.busy.admit = yes",
                });
                Config c = Config.Load(ini);
                Ok(c.LimitsFor(MachineState.LightUse).CpuPct
                   <= c.LimitsFor(MachineState.Idle).CpuPct,
                   "light use is no more generous than idle (got "
                   + c.LimitsFor(MachineState.LightUse).CpuPct + " against "
                   + c.LimitsFor(MachineState.Idle).CpuPct + ")");
                Ok(c.LimitsFor(MachineState.Busy).CpuPct
                   <= c.LimitsFor(MachineState.LightUse).CpuPct,
                   "and busy is no more generous than light use");
                Ok(c.GridRepairs.Count > 0, "and the repair is reported rather than silent");

                // Every row of every shipped profile is already monotone, so the
                // repair never fires on a machine nobody has edited.
                foreach (string id in Config.ProfileIds())
                {
                    var fresh = new Config();
                    fresh.ApplyProfile(id);
                    Ok(fresh.TightenGrid().Count == 0,
                        "the " + id + " profile is monotone as shipped");
                }
            }
            finally { Nuke(dir); }
        }

        /// A SEED IS NOT A MEASUREMENT AND A NAME IS NOT A NUMBER.
        ///
        /// A profile fills every cell; a limits.<state>.<field> line overrides one.
        /// If both were applied in the order they were read, the same file would
        /// mean two different things depending on whether the person happened to
        /// type the profile line above or below the override. So the profile is
        /// resolved first whatever the line order, and the effective posture reports
        /// as "custom" the moment an override differs from it.
        static void AProfileDoesNotDependOnWhereItSitsInTheFile()
        {
            Case("a profile does not depend on where it sits in the file");
            string dir = Path.Combine(Path.GetTempPath(), "idlegpu-prof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string above = Path.Combine(dir, "above.ini");
                File.WriteAllLines(above, new string[] {
                    "Profile = generous",
                    "limits.lightuse.cpupct = 15",
                });
                string below = Path.Combine(dir, "below.ini");
                File.WriteAllLines(below, new string[] {
                    "limits.lightuse.cpupct = 15",
                    "Profile = generous",
                });
                Config a = Config.Load(above);
                Config b = Config.Load(below);
                Ok(a.LimitsFor(MachineState.LightUse).CpuPct == 15,
                    "the override wins over the profile (got " + a.LimitsFor(MachineState.LightUse).CpuPct + ")");
                Ok(b.LimitsFor(MachineState.LightUse).CpuPct == 15,
                    "in either order (got " + b.LimitsFor(MachineState.LightUse).CpuPct + ")");
                Ok(a.LimitsFor(MachineState.Busy).CpuPct == b.LimitsFor(MachineState.Busy).CpuPct,
                    "and every other cell comes from the profile either way");
                Ok(a.LimitsFor(MachineState.Busy).CpuPct
                   == Config.ProfileLimits(Config.ProfileGenerous)[MachineState.Busy].CpuPct,
                    "which is the generous profile's busy row, not the balanced one");
                Ok(a.EffectiveProfile() == "custom",
                    "an edited cell reports as custom, not as generous");
                Ok(a.EffectiveProfileLabel().IndexOf("Generous", StringComparison.Ordinal) >= 0,
                    "and says which posture it is based on: " + a.EffectiveProfileLabel());

                // Unedited, the name and the numbers agree and nothing says custom.
                string clean = Path.Combine(dir, "clean.ini");
                File.WriteAllLines(clean, new string[] { "Profile = away" });
                Config d = Config.Load(clean);
                Ok(d.EffectiveProfile() == Config.ProfileAway,
                    "an untouched profile reports as itself (got " + d.EffectiveProfile() + ")");
                Ok(d.LimitsFor(MachineState.LightUse).CpuPct == 0,
                    "and 'only when I am away' really does mean nothing while somebody is here");
                Ok(d.LimitsFor(MachineState.Locked).CpuPct == 100,
                    "while still taking the whole machine when the desktop is locked");
                Ok(d.IdleAfterSeconds == 300 && d.ForeignCpuBusyPct == 30.0,
                    "and the profile carries its two tunings with it");
            }
            finally { Nuke(dir); }
        }

        /// WHAT A SAVED POSTURE IS FOR. Somebody who has priced their own machine
        /// wants that back in the tray beside the three shipped ones, not typed
        /// again. Same flat-key shape as [limits], because a second file format is
        /// a second thing to get wrong at 2am.
        static void ASavedProfileComesBackWithItsName()
        {
            Case("a saved posture comes back with its name");
            string dir = Path.Combine(Path.GetTempPath(), "idlegpu-saved-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string ini = Path.Combine(dir, "worker.ini");
                File.WriteAllLines(ini, new string[] {
                    "[profile.nightshift]",
                    "profile.nightshift.name = Night shift",
                    "profile.nightshift.lightuse.cpupct = 40",
                    "profile.nightshift.busy.cpupct = 20",
                    "Profile = nightshift",
                });
                Config c = Config.Load(ini);
                Ok(c.SavedProfiles.ContainsKey("nightshift"), "the saved posture is parsed");
                Ok(c.AnyProfileLabel("nightshift") == "Night shift",
                    "under the name it was given (got " + c.AnyProfileLabel("nightshift") + ")");
                Ok(c.LimitsFor(MachineState.LightUse).CpuPct == 40,
                    "and it is in force (got " + c.LimitsFor(MachineState.LightUse).CpuPct + ")");
                Ok(c.LimitsFor(MachineState.Busy).CpuPct == 20, "in every cell it names");
                Ok(c.LimitsFor(MachineState.NobodyHome).CpuPct == 100,
                    "and cells it does not name still describe a whole machine");
                Ok(c.EffectiveProfile() == "nightshift",
                    "and it reports as itself rather than as custom (got " + c.EffectiveProfile() + ")");
            }
            finally { Nuke(dir); }
        }

        /// KEEP THE WARM JOB, TAKE NO NEW ONES. A CPU yield throttles rather than
        /// killing, so a job that has already paid its 22 second model load and
        /// 4.7 GiB of allocation is worth holding at a trickle. Starting a NEW one
        /// on a machine somebody is using is not. MinFreeMib could only have said
        /// that with a number so large it would have been a lie about memory, which
        /// is exactly why Admit is a field of its own.
        static void AdmitIsSeparateFromTheCap()
        {
            Case("keeping a warm job is not the same as inviting a new one");
            var c = new Config();
            c.ApplyProfile(Config.ProfileGenerous);
            var p = new Policy(c);
            Snapshot s = Sn(true, false, true, true, 0, T0);
            s.Launchers.GameProcesses.Add("somegame.exe");
            // A game AND a compile: both resources contended, so the whole Busy row.
            for (int i = 0; i < 3; i++)
            {
                s = Sn(true, false, true, true, 0, T0.AddSeconds(i));
                s.Launchers.GameProcesses.Add("somegame.exe");
                s.Cpu = new CpuSample();
                s.Cpu.Valid = true;
                s.Cpu.MachinePct = 99; s.Cpu.OwnPct = 0; s.Cpu.ForeignPct = 99;
                p.Evaluate(s);
            }
            Ok(p.Last.MachineState == MachineState.Busy, "both resources are contended");
            Ok(p.Last.Limits.CpuPct == 10,
                "the generous profile keeps the warm job at ten per cent (got " + p.Last.Limits.CpuPct + ")");
            Ok(p.CanRunCpu(Mode.Auto), "so a running CPU job is throttled rather than stopped");
            Ok(!p.AdmitsNewWork(Mode.Auto), "but nothing new starts");
            Ok(!c.LimitsFor(MachineState.Busy).Admit, "because the busy row admits nothing");
            Ok(c.LimitsFor(MachineState.LightUse).Admit, "while light use still does");

            // Always-on is the owner saying "use my machine anyway". It overrides
            // admission the same way it overrides everything else.
            Ok(p.AdmitsNewWork(Mode.AlwaysOn), "always-on admits work regardless");
            Ok(!p.AdmitsNewWork(Mode.Off), "and off admits nothing regardless");
        }

        /// SIXTEEN THREADS INSIDE A TENTH OF A MACHINE IS THE WORST CONFIGURATION
        /// A HARD CAP CAN BE GIVEN. Once the job has spent its share of a
        /// scheduling interval no thread in it runs until the next one, so the
        /// threads take turns being descheduled and thrash the owner's cache on the
        /// way. The cap decides how much; this decides how thinly it is spread.
        ///
        /// AT ONE HUNDRED PER CENT THE ANSWER IS PHYSICAL CORES, not logical ones.
        /// The NAS thread sweep on this same model: 0.077 realtime at 2 threads,
        /// 0.230 at 8, 0.285 at 16 - per-thread efficiency halving from 2 to 16,
        /// which is the signature of work bound by single-thread latency. Two
        /// sibling threads on one core share the L1, the L2 and the front end, and
        /// this model's whole advantage is a 96 MiB L3 it walks every token.
        static void ThreadCountFollowsTheCapAndStopsAtPhysicalCores()
        {
            Case("the thread count follows the cap, and takes the machine at full");
            // MEASURED ON THE RUNNER, one model load, no cap, only the thread
            // count varying: 8 threads 0.249x, 12 threads 0.271x, 16 threads
            // 0.271x. The physical-core rule this replaces was inferred from a
            // sweep on a 2016 Xeon and gave 8 here, leaving 9% unclaimed on the
            // machine that actually runs the work. 16 costs nothing over 12, and
            // the hundred per cent row applies when nobody is signed in.
            Config.CpuThreadsAtFull = 0;
            Ok(Config.ThreadsFor(Config.Rung(0), 16, 8) == 16,
                "unlimited on 8 cores and 16 threads asks for all 16 (got "
                + Config.ThreadsFor(Config.Rung(0), 16, 8) + ")");
            Ok(Config.ThreadsFor(Config.Rung(1), 16, 8) == 8, "fifty per cent of sixteen is eight");
            Ok(Config.ThreadsFor(Config.Rung(2), 16, 8) == 2, "ten per cent of sixteen rounds to two");
            Ok(Config.ThreadsFor(Config.Rung(3), 16, 8) == 0,
                "and a row that means stop asks for nothing at all");

            // DETECTED, NEVER ASSUMED. A stranger's machine is not sixteen threads.
            Ok(Config.ThreadsFor(Config.Rung(0), 4, 4) == 4, "a four thread machine asks for four");

            // The override, for a chip that disagrees with the one measured.
            Config.CpuThreadsAtFull = 12;
            Ok(Config.ThreadsFor(Config.Rung(0), 16, 8) == 12,
                "CpuThreadsAtFull is honoured at full");
            Ok(Config.ThreadsFor(Config.Rung(0), 8, 4) == 8,
                "and never asks for more threads than the machine has");
            Config.CpuThreadsAtFull = 0;
            Ok(Config.ThreadsFor(Config.Rung(2), 4, 4) == 1,
                "and ten per cent of four is one, never zero, because zero threads is no job");

            // A machine whose topology cannot be read gets the behaviour it had
            // before physical cores were detected at all, rather than a guess.
            Ok(Config.ThreadsFor(Config.Rung(0), 16, 0) == 16,
                "an unreadable core count falls back to the logical one");
        }

        /// Missing configuration must never read as permission. A worker.ini with
        /// a typo in a state name used to be a state with no row, and a state with
        /// no row must take nothing rather than everything.
        static void ARowNobodyConfiguredTakesNothing()
        {
            Case("a state nobody configured takes nothing");
            var c = new Config();
            c.Limits.Remove(MachineState.LightUse);
            ResourceLimits r = c.LimitsFor(MachineState.LightUse);
            Ok(r.CpuPct == 0, "no row means no CPU");
            Ok(!r.Gpu, "no row means no GPU");
        }

        /// SetInformationJobObject returns INVALID_ARGS for a CpuRate of zero, so
        /// zero cannot mean "capped to nothing" and has to mean "do not run". If
        /// somebody ever makes 0 mean "unlimited" to tidy the UI up, this is the
        /// test that stops it.
        ///
        /// WHAT CHANGED IN THIS TEST AND WHY. It used to reach the zero row by
        /// starting a game, and asserted that a game therefore stopped CPU work.
        /// That is no longer true and was never wanted: a game contends the CARD,
        /// and the Busy row is now split by resource so an uncontended processor
        /// falls back to the LightUse row. See AGameOnTheCardDoesNotStopTheProcessor.
        /// The property this test is actually about - zero means stop - is asserted
        /// directly on the row and on the kernel arithmetic instead, which is
        /// stricter than going through a game to get at it.
        static void AZeroCpuCapMeansStopBecauseWindowsHasNoZeroCap()
        {
            Case("a CPU cap of zero means stop, not unlimited");
            var c = new Config();
            Ok(c.LimitsFor(MachineState.Busy).CpuPct == 0, "the busy row ships as zero");
            var p = new Policy(c);
            // Blind: the one state where nothing at all is on offer, and the only
            // way to reach a whole Busy row without naming a resource.
            Snapshot s = Sn(true, false, false, false, 0, T0);
            Verdict v = p.Evaluate(s);
            Ok(v.MachineState == MachineState.Busy, "blindness is the busy row");
            Ok(v.Limits.CpuPct == 0, "whose cap is zero");
            Ok(!p.CanRunCpu(Mode.Auto), "and zero means a CPU service may not run");
        }

        /// THE DEFECT: A JOB ON ITS WAY OUT USED TO BE UNCAPPED.
        ///
        /// The rate write was guarded by "CpuPct > 0 && CpuPct < 100", so a row of
        /// zero fell through with a zeroed struct - and a zeroed ControlFlags
        /// CLEARS the cap rather than setting it to nothing. Zero is the row a game
        /// produces. So at the exact moment somebody started a game, the job that
        /// was about to be stopped had its cap REMOVED and ran flat out for the
        /// whole of YieldGraceSeconds while Stop() waited for it to exit politely.
        ///
        /// CpuRate = 0 is rejected by the kernel with INVALID_ARGS, measured, so
        /// zero is not expressible and has to clamp to the smallest cap there is.
        static void AJobBeingStoppedIsNotUncappedOnTheWayOut()
        {
            Case("a job wound down to zero is clamped, not uncapped");
            uint flags, rate;
            CpuRate.For(0, out flags, out rate);
            Ok(flags == (CpuRate.Enable | CpuRate.HardCap),
                "zero still writes a hard cap, because ControlFlags 0 would REMOVE the cap");
            Ok(rate == 100, "clamped to one per cent, the smallest the kernel accepts (got " + rate + ")");

            CpuRate.For(10, out flags, out rate);
            Ok(flags == (CpuRate.Enable | CpuRate.HardCap), "ten per cent is a hard cap");
            Ok(rate == 1000, "in hundredths of one per cent of the whole machine (got " + rate + ")");

            CpuRate.For(100, out flags, out rate);
            Ok(flags == 0, "one hundred per cent clears rate control entirely rather than ceiling at it");
            Ok(rate == 0, "with nothing left in the struct");
        }

        /// THE DEFECT THE GPU HAS AND THE CPU MUST NOT. A whole-machine load
        /// reading has no owner attached to it, so the agent reads the load it
        /// created as somebody else's and yields to itself; measured on the GPU as
        /// four restarts in a row on a locked desktop with nobody near the machine.
        /// A job object accounts for its own processes exactly, so the CPU can
        /// SUBTRACT its own load rather than suppressing the whole signal.
        static void OurOwnCpuIsNotSomebodyElseUsingTheMachine()
        {
            Case("our own CPU load is subtracted, not mistaken for a user");
            var c = new Config();
            var p = new Policy(c);
            for (int i = 0; i < 5; i++)
            {
                Snapshot s = Sn(true, false, true, true, 600, T0.AddSeconds(i));
                s.Cpu = new CpuSample();
                s.Cpu.Valid = true;
                // The machine is at 95 per cent and ALL of it is ours.
                s.Cpu.MachinePct = 95; s.Cpu.OwnPct = 95; s.Cpu.ForeignPct = 0;
                Verdict v = p.Evaluate(s);
                Ok(v.MachineState == MachineState.Idle,
                    "sample " + i + ": a machine flat out with OUR work is still idle (got " +
                    v.MachineState + ")");
            }
        }

        /// Foreign CPU is a VOTE, not a veto, on the same three-sample rule as the
        /// GPU's tier 2. One sample over the line is a Windows Update check, not a
        /// person, and treating it as one would throttle every job twice an hour.
        static void ForeignCpuAloneIsNeverOneSampleWorthOfBusy()
        {
            Case("one second of somebody else's CPU is not a person");
            var c = new Config();
            var p = new Policy(c);
            Verdict v = null;
            for (int i = 0; i < 2; i++)
            {
                Snapshot s = Sn(true, false, true, true, 600, T0.AddSeconds(i));
                s.Cpu = new CpuSample();
                s.Cpu.Valid = true;
                s.Cpu.MachinePct = 90; s.Cpu.OwnPct = 0; s.Cpu.ForeignPct = 90;
                v = p.Evaluate(s);
            }
            Ok(v.MachineState == MachineState.Idle,
                "two samples of foreign load have not carried yet (got " + v.MachineState + ")");
            Snapshot third = Sn(true, false, true, true, 600, T0.AddSeconds(2));
            third.Cpu = new CpuSample();
            third.Cpu.Valid = true;
            third.Cpu.MachinePct = 90; third.Cpu.OwnPct = 0; third.Cpu.ForeignPct = 90;
            v = p.Evaluate(third);
            Ok(v.MachineState == MachineState.Busy,
                "the third consecutive sample carries (got " + v.MachineState + ")");
        }

        /// THE DEFECT THIS PREVENTS, and it is the one the owner asked for.
        ///
        /// Policy.Classify returns MachineState.Busy for ANY tier 1 veto, and the
        /// Busy row zeroes both columns. So a Steam game started, the GPU veto
        /// fired, the whole row went to zero, and the CPU job was killed for a card
        /// it had never opened - on a machine with twelve threads sitting idle.
        /// That defeats the entire point of selling two resources.
        ///
        /// A game contends the CARD. The processor falls back to the LightUse row,
        /// which is careful rather than generous, because a tier 1 veto is the
        /// strongest evidence this program has that somebody is at the machine -
        /// stronger than the session signals, since a full-screen game leaves
        /// GetLastInputInfo idle while somebody plays it with a controller.
        ///
        /// This test used to assert the opposite ("a game stops CPU work as well as
        /// GPU work"). The assertion it made about the GPU is kept exactly.
        static void AGameOnTheCardDoesNotStopTheProcessor()
        {
            Case("a game takes the card and leaves the processor");
            var c = new Config();
            var p = new Policy(c);
            Snapshot s = Sn(true, false, true, true, 0, T0);
            s.Launchers.SteamRunningAppId = 12345;
            s.Launchers.SteamRunningAppName = "Something";
            Verdict v = p.Evaluate(s);

            Ok(v.MachineState == MachineState.Busy, "a game is still the busy row");
            Ok(v.GpuContended, "and the card is what is contended");
            Ok(!v.CpuContended, "the processor is not");
            Ok(!p.CanRunGpu(Mode.Auto), "no GPU work, exactly as before");
            Ok(!v.Limits.Gpu, "the GPU column is closed");

            ResourceLimits light = c.LimitsFor(MachineState.LightUse);
            Ok(p.CanRunCpu(Mode.Auto), "but CPU work carries on");
            Ok(v.Limits.CpuPct == light.CpuPct,
                "at the light-use cap, not the busy one (got " + v.Limits.CpuPct + ")");
            Ok(v.Limits.CpuPct > 0 && v.Limits.CpuPct <= 25,
                "which is careful, because somebody is demonstrably at the machine");
            Ok(Config.NormalisePriority(v.Limits.Priority) == "idle", "and at idle priority");
        }

        /// THE MIRROR, and it was equally broken. A sixteen-thread compile forces
        /// MachineState.Busy through the foreign-CPU vote, the Busy row zeroes the
        /// GPU column too, and a GPU job died for a processor it was barely using.
        /// A compile contends the PROCESSOR; the card falls back to LightUse.
        static void ACompileDoesNotStopTheCard()
        {
            Case("a compile takes the processor and leaves the card");
            var c = new Config();
            var p = new Policy(c);
            // Get past the start-up cooldown first: the policy begins Blocked, so
            // without this the GPU assertion below would pass or fail for a reason
            // that has nothing to do with the compile.
            p.Evaluate(Sn(true, false, true, true, 0, T0));
            p.Evaluate(Sn(true, false, true, true, 0, T0.AddSeconds(95)));
            Ok(p.CanRunGpu(Mode.Auto), "the card is ours before the compile starts");

            Verdict v = null;
            // Three consecutive samples of foreign load, because one is an update
            // check and not a person.
            for (int i = 0; i < 3; i++)
            {
                Snapshot s = Sn(true, false, true, true, 0, T0.AddSeconds(96 + i));
                s.Cpu = new CpuSample();
                s.Cpu.Valid = true;
                s.Cpu.MachinePct = 95; s.Cpu.OwnPct = 0; s.Cpu.ForeignPct = 95;
                v = p.Evaluate(s);
            }
            Ok(v.MachineState == MachineState.Busy, "sustained foreign CPU load is the busy row");
            Ok(v.CpuContended, "and the processor is what is contended");
            Ok(!v.GpuContended, "the card is not");
            Ok(v.Limits.CpuPct == c.LimitsFor(MachineState.Busy).CpuPct,
                "so the CPU column takes the busy row");
            Ok(v.Limits.Gpu == c.LimitsFor(MachineState.LightUse).Gpu,
                "and the GPU column takes the light-use row");
            Ok(p.CanRunGpu(Mode.Auto),
                "a GPU job survives a compile, because the compile is not on the card");
        }

        /// FAIL CLOSED WHEN NOTHING EXPLAINS THE BUSY. The per-resource fallback is
        /// new code on the yield path, which is the one path in this program that
        /// must do nothing but evaluate and stop. A Busy that neither flag explains
        /// - blindness, a missing session block, a signal nobody has classified yet
        /// - must hand out the whole Busy row and not half of a generous one.
        static void AnUnexplainedBusyGetsTheWholeBusyRow()
        {
            Case("a busy nobody can explain is still the busy row");
            var c = new Config();
            var p = new Policy(c);
            // Blind: signed in, unlocked, and no helper reporting.
            Verdict v = p.Evaluate(Sn(true, false, false, false, 0, T0));
            Ok(v.Blind, "the agent cannot see the owner");
            Ok(v.MachineState == MachineState.Busy, "which is the busy row");
            Ok(v.GpuContended && v.CpuContended, "blindness contends everything");
            ResourceLimits busy = c.LimitsFor(MachineState.Busy);
            Ok(v.Limits.SameAs(busy), "so the whole busy row is handed out, unsplit");

            // And a snapshot with no session block at all, which Classify also
            // calls Busy and which no resource flag would otherwise cover.
            var p2 = new Policy(c);
            var bare = new Snapshot();
            bare.At = T0; bare.GpuHealthy = true;
            bare.Gpu = new GpuSample(); bare.Gpu.Valid = true; bare.Gpu.PState = "P8";
            bare.Gpu.ClockMemMhz = 405; bare.Gpu.PowerWatts = 34.0; bare.Gpu.UtilGpu = 5;
            bare.Launchers = new LauncherSignals();
            Verdict v2 = p2.Evaluate(bare);
            Ok(v2.MachineState == MachineState.Busy, "no session signals is the busy row");
            Ok(v2.Limits.SameAs(busy), "and it too is handed out whole");
        }

        /// THE COOLDOWN HAS TO REMEMBER WHY IT IS HOLDING, not only how hard.
        ///
        /// The ratchet keeps the worst MachineState seen in ninety seconds. If it
        /// kept the state and forgot the contention, the very next clear sample
        /// would find nothing contended, fall back to LightUse for BOTH resources,
        /// and hand back the card the cooldown exists to withhold - a game paused
        /// at a menu would get its GPU stolen one second after the veto cleared.
        static void TheCooldownRemembersWhichResourceWasContended()
        {
            Case("the cooldown remembers which resource was contended");
            var c = new Config();
            var p = new Policy(c);
            Snapshot game = Sn(true, false, true, true, 0, T0);
            game.Launchers.GameProcesses.Add("somegame.exe");
            Verdict v = p.Evaluate(game);
            Ok(v.GpuContended && !v.Limits.Gpu, "the game closes the card");

            // The game exits. One second later every signal is clear, but the
            // cooldown is still holding Busy.
            v = p.Evaluate(Sn(true, false, true, true, 0, T0.AddSeconds(1)));
            Ok(v.MachineState == MachineState.Busy, "still held at busy by the cooldown");
            Ok(v.GpuContended, "and still remembers that it was the card");
            Ok(!v.Limits.Gpu, "so the card stays closed for the whole cooldown");

            // Ninety seconds later it relaxes properly.
            v = p.Evaluate(Sn(true, false, true, true, 0, T0.AddSeconds(95)));
            Ok(v.MachineState != MachineState.Busy,
                "and after the cooldown it lets go (got " + v.MachineState + ")");
        }

        /// RESTRICT INSTANTLY, RELAX SLOWLY. Tightening a cap costs nothing and
        /// must happen the moment the owner appears. Loosening it the moment they
        /// pause is how you get a machine that surges every time somebody stops
        /// typing to read a paragraph, so relaxing waits out the same cooldown the
        /// GPU uses.
        static void TheLadderTightensAtOnceAndLoosensOnTheCooldown()
        {
            Case("the ladder tightens at once and loosens on the cooldown");
            var c = new Config();
            var p = new Policy(c);
            // Typing: light use, 10 per cent.
            Verdict v = p.Evaluate(Sn(true, false, true, true, 0, T0));
            Ok(v.Limits.CpuPct == 10, "typing gives 10 per cent");
            // Now they lock the machine. More generous, but not yet.
            v = p.Evaluate(Sn(true, true, true, true, 0, T0.AddSeconds(1)));
            Ok(v.MachineState == MachineState.LightUse,
                "one second after locking it is still held at light use (got " +
                v.MachineState + ")");
            // Still held one second before the cooldown expires.
            v = p.Evaluate(Sn(true, true, true, true, 0, T0.AddSeconds(89)));
            Ok(v.MachineState == MachineState.LightUse,
                "and at 89 s, one short of the 90 s cooldown");
            v = p.Evaluate(Sn(true, true, true, true, 0, T0.AddSeconds(91)));
            Ok(v.MachineState == MachineState.Locked,
                "at 91 s it relaxes to locked (got " + v.MachineState + ")");
            Ok(v.Limits.CpuPct == 100, "which is unlimited CPU");
            // And going the other way is instant.
            v = p.Evaluate(Sn(true, false, true, true, 0, T0.AddSeconds(92)));
            Ok(v.MachineState == MachineState.LightUse && v.Limits.CpuPct == 10,
                "unlocking drops straight back to 10 per cent with no cooldown");
        }

        /// A speech job running on the CPU has no interest in the card, and before
        /// the split it sat out a ninety second cooldown caused by a game on a GPU
        /// it never touched. CanRunCpu deliberately does not look at WorkerState.
        static void ACpuServiceDoesNotWaitOutAGpuCooldown()
        {
            Case("a CPU service does not wait out a GPU cooldown");
            var c = new Config();
            var p = new Policy(c);
            // A game, then it closes. The GPU cools down for ninety seconds.
            Snapshot game = Sn(true, false, true, true, 600, T0);
            game.Launchers.GameProcesses.Add("somegame.exe");
            p.Evaluate(game);
            // Ninety-one seconds later the machine has been idle throughout, so
            // the ladder has relaxed but the GPU cooldown is only just over.
            Verdict v = null;
            for (int i = 1; i <= 95; i++)
                v = p.Evaluate(Sn(true, false, true, true, 600, T0.AddSeconds(i)));
            Ok(p.CanRunCpu(Mode.Auto), "the CPU is available again");
            Ok(v.Limits.CpuPct > 0, "with a non-zero cap (" + v.Limits.CpuPct + "%)");

            // And immediately after the game closes, the GPU is still cooling down
            // while a CPU service is blocked only by the ladder, not by the card.
            var q = new Policy(c);
            Snapshot g2 = Sn(true, false, true, true, 600, T0);
            g2.Launchers.GameProcesses.Add("somegame.exe");
            q.Evaluate(g2);
            q.Evaluate(Sn(true, false, true, true, 600, T0.AddSeconds(1)));
            Ok(!q.CanRun(Mode.Auto), "one second after the game closes the GPU is still draining");
        }

        /// A typo in a settings file must not quietly hand the machine over. An
        /// unrecognised priority is the most restrictive one, never the least.
        static void AnUnknownPriorityBecomesIdleNotNormal()
        {
            Case("an unrecognised priority becomes idle, not normal");
            Ok(Config.NormalisePriority("wibble") == "idle", "nonsense becomes idle");
            Ok(Config.NormalisePriority("") == "idle", "empty becomes idle");
            Ok(Config.NormalisePriority(null) == "idle", "null becomes idle");
            Ok(Config.NormalisePriority("Below Normal") == "belownormal", "spacing and case are forgiven");
            Ok(Config.NormalisePriority("NORMAL") == "normal", "normal still means normal");
        }

        /// The settings window writes the matrix into worker.ini and the agent
        /// reads it back at the next start. A setting that silently resets at the
        /// next login is a setting the owner stops trusting, and these are the
        /// settings they reach for precisely when they do not trust something yet.
        static void LimitsSurviveARoundTripThroughWorkerIni()
        {
            Case("the limits matrix survives a round trip through worker.ini");
            string dir = Path.Combine(Path.GetTempPath(), "idlegpu-limits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string ini = Path.Combine(dir, "worker.ini");
                // THE VALUES HERE ARE MONOTONE ON PURPOSE, and this test used to
                // use ones that were not - a below-normal priority and 3000 MiB of
                // headroom for LightUse, both LOOSER than the Idle row above it.
                // The grid is now tightened at load, because the cooldown ratchet
                // takes for granted that a worse state has a smaller row, so those
                // values were quietly repaired and the round trip failed. That is
                // the loader working. Round-trip fidelity is asserted on values a
                // sane grid can hold; the repair has its own test, see
                // AGridThatInvertsTheLadderIsRepairedNotObeyed.
                File.WriteAllLines(ini, new string[] {
                    "# a comment that must survive",
                    "StartMode = Auto",
                    "[limits]",
                    "limits.lightuse.cpupct = 7",
                    "limits.lightuse.priority = idle",
                    "limits.lightuse.workingsetmib = 4096",
                    "limits.lightuse.minfreemib = 7000",
                    "limits.lightuse.gpu = no",
                    "limits.locked.cpupct = 100",
                });
                Config c = Config.Load(ini);
                ResourceLimits l = c.LimitsFor(MachineState.LightUse);
                Ok(l.CpuPct == 7, "cpupct read back as 7 (got " + l.CpuPct + ")");
                Ok(Config.NormalisePriority(l.Priority) == "idle", "priority read back");
                Ok(l.WorkingSetMib == 4096, "working set read back");
                Ok(l.MinFreeMib == 7000, "min free read back");
                Ok(!l.Gpu, "gpu = no read back");
                Ok(c.LimitsFor(MachineState.Locked).CpuPct == 100, "another state read back too");
                // A state that was not mentioned keeps its default rather than
                // being blanked by the presence of the section.
                Ok(c.LimitsFor(MachineState.NobodyHome).CpuPct == 100,
                    "an unmentioned state keeps its default");
                // Out of range is clamped rather than throwing the line away.
                File.AppendAllText(ini, Environment.NewLine + "limits.idle.cpupct = 400" + Environment.NewLine);
                Ok(Config.Load(ini).LimitsFor(MachineState.Idle).CpuPct == 100,
                    "an out of range cap is clamped, not ignored");
            }
            finally { Nuke(dir); }
        }

        /// Blindness about the USER fails closed, because the cost of being wrong
        /// is somebody's match. Blindness about FREE MEMORY fails open, because the
        /// cost of being wrong is a runner that never starts anything at all on a
        /// machine whose memory counter is unavailable.
        static void AnUnmeasurableFreeMemoryDoesNotStopEverything()
        {
            Case("an unreadable memory counter does not block every job for ever");
            string why;
            Ok(Policy.MemoryAllows(0, false, 6144, 6500, out why),
                "unknown free memory admits the job");
            Ok(Policy.MemoryAllows(0, true, 0, 6500, out why),
                "a state with no headroom rule admits the job");
        }

        /// Paging is felt in a way CPU contention is not: a throttled job makes
        /// things slower, a machine that is swapping makes things stop. Chatterbox
        /// is about 6.5 GiB resident, so on a machine with 2 GiB spare the cheapest
        /// fix is not to start.
        static void AJobIsNotStartedWithNoMemoryHeadroom()
        {
            Case("a 6.5 GiB job is not started on a machine with 2 GiB spare");
            string why;
            Ok(!Policy.MemoryAllows(2048, true, 6144, 6500, out why),
                "2,048 MiB free refuses a job needing 6,144 headroom plus 6,500");
            Ok(why.Length > 0, "and says why: " + why);
            Ok(Policy.MemoryAllows(14000, true, 6144, 6500, out why),
                "14,000 MiB free admits it");
            Ok(!Policy.MemoryAllows(6200, true, 6144, 6500, out why),
                "6,200 MiB free is enough for the headroom but not for the job as well");
        }

        /// THE DEFAULTS ARE MEASURED NUMBERS, so changing one has to be deliberate.
        /// From probe/p10_felt_hard.cs on spring, an eight-thread victim at normal
        /// priority against sixteen memory-streaming threads:
        ///
        ///   nothing running   p50 4.32 ms   7155 units
        ///   IDLE, no cap      p50 10.38 ms  4281 units    priority alone is NOT enough
        ///   IDLE + 50% cap    p50 10.38 ms  4391 units    50 per cent buys nothing
        ///   IDLE + 25% cap    p50 5.93 ms   5701 units
        ///   IDLE + 10% cap    p50 4.89 ms   6452 units    90 per cent of an idle machine
        ///
        /// So light use is 10 and not 25, and idle keeps a cap at all rather than
        /// running flat out at somebody who is one keystroke from being back.
        static void TheShippedCpuCapsAreTheMeasuredOnes()
        {
            Case("the shipped caps are the ones that were measured");
            var c = new Config();
            Ok(c.LimitsFor(MachineState.NobodyHome).CpuPct == 100, "nobody home: 100");
            Ok(c.LimitsFor(MachineState.Locked).CpuPct == 100, "locked: 100, the chosen default");
            Ok(c.LimitsFor(MachineState.Idle).CpuPct == 50, "signed in and idle: 50");
            Ok(c.LimitsFor(MachineState.LightUse).CpuPct == 10, "light use: 10");
            Ok(c.LimitsFor(MachineState.Busy).CpuPct == 0, "busy: nothing");
            Ok(c.LimitsFor(MachineState.LightUse).WorkingSetMib > 0,
                "and light use is the state that caps the working set, because that is "
                + "where a 6.5 GiB job gets in the way");
            Ok(c.LimitsFor(MachineState.NobodyHome).WorkingSetMib == 0,
                "while nobody home does not, because there is nobody to page out");
            Ok(Config.NormalisePriority(c.LimitsFor(MachineState.LightUse).Priority) == "idle",
                "light use runs at idle priority as well as capped, because neither alone is enough");
        }

        /// A menu of four rungs with a tick on none of them tells the owner
        /// nothing about what their machine is doing, which is the question they
        /// opened it to answer. At least one shipped default has to BE a rung, or
        /// the tray shows "Custom" out of the box for ever.
        static void EveryTrayRungIsReachableAndSaysWhatItDoes()
        {
            Case("every tray rung is reachable and describes itself");
            Ok(Config.RungNames.Length == 4, "four rungs, not a slider");
            var c = new Config();
            int matched = 0;
            foreach (MachineState st in Config.AllStates())
            {
                ResourceLimits l = c.LimitsFor(st);
                for (int i = 0; i < Config.RungNames.Length; i++)
                    if (Config.Rung(i).SameAs(l)) { matched++; break; }
            }
            Ok(matched >= 2,
                matched + " of the 5 shipped rows are exactly a named rung, so the menu is not "
                + "permanently showing Custom");
            for (int i = 0; i < Config.RungNames.Length; i++)
            {
                ResourceLimits r = Config.Rung(i);
                Ok(r.Describe().Length > 0, Config.RungNames[i] + " reads as: " + r.Describe());
                Ok(r.SameAs(r.Clone()), "and a clone of it compares equal, so the menu ticks it");
            }
            Ok(!Config.Rung(0).SameAs(Config.Rung(1)), "and no two rungs are the same rung");
            Ok(Config.Rung(3).CpuPct == 0 && !Config.Rung(3).Gpu,
                "and the last rung really does mean nothing at all");
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
