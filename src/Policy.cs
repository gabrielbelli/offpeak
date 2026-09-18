// The decision.
//
// THE SHAPE OF THE PROBLEM. "Utilisation below N per cent" is wrong in both
// directions and both were confirmed by measurement rather than assumed:
//
//   Wrong low.  A paused game draws nothing. It still owns its VRAM and expects
//               to resume in one frame. nvidia-smi cannot see that on this
//               machine at all - used_memory is [N/A] for every process, measured
//               (probe p1), because of WDDM. The OS performance counters can, and
//               do (probe p2), so the veto is built on those.
//
//   Wrong high. A browser decoding video raises utilisation without touching the
//               shaders. The engine-type split separates the two: measured at
//               idle, 3d 1.06 and videodecode 0.00, and video playback moves the
//               second and not the first.
//
//   Wrong late. utilisation.gpu is a driver-side average over a sampling window,
//               and on an idle desktop it never reads below 5 per cent anyway
//               (150 of 150 samples were 5 or 6, tests/fixtures/idle_desktop.csv).
//               A threshold under 5 is unreachable on this machine; a threshold
//               over 6 is noise-bound.
//
// THE ANSWER IS NOT A BETTER THRESHOLD, IT IS A DIFFERENT CLASS OF SIGNAL.
// Ranked by how early they fire:
//
//   1. Steam's RunningAppID     set before the game renders its first frame
//   2. the vgc service          starts with Valorant, before the game window
//   3. a game process exists    survives pause, alt-tab and minimise
//   4. foreign VRAM held        survives pause; invisible to nvidia-smi here
//   5. memory clock / pstate    the driver's own answer to "is this real work"
//   6. power draw               idle 33.7-35.7 W against 200+ W under load
//   7. utilisation              last, weakest, advisory only
//
// Signals 1-4 are vetoes: any one of them alone means yield, immediately, with no
// confirmation window, because each means "a game exists" rather than "a game is
// currently drawing", and that is the question that actually matters.
// Signals 5-7 vote, and need three consecutive seconds to carry.
//
// ON TIME. Evaluate() takes its clock from Snapshot.At rather than from
// DateTime.UtcNow. That is deliberate and it is the single change that makes the
// ninety-second cooldown testable: a test can replay ninety seconds of recorded
// samples in a millisecond and get exactly the transitions the tray would get.
// A policy whose slowest path can only be tested by waiting is a policy whose
// slowest path does not get tested.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace IdleGpu
{
    public class Policy
    {
        readonly Config _c;
        int _busyStreak;
        int _cpuBusyStreak;
        DateTime _lastWanted = DateTime.MinValue;
        MachineState _worstRecent = MachineState.Busy;
        DateTime _worstRecentAt = DateTime.MinValue;
        // The ratchet has to remember WHY it is holding, not only how hard. If it
        // held Busy for ninety seconds while forgetting that the Busy was a game,
        // the per-resource fallback would look at a clear sample, find nothing
        // contended, and quietly hand back the row the cooldown exists to withhold.
        bool _worstGpuContended = true;
        bool _worstCpuContended = true;
        DateTime _now = DateTime.MinValue;
        WorkerState _state = WorkerState.Blocked;

        public Policy(Config c)
        {
            _c = c;
            // Start Blocked, not Available. An agent that has not yet seen a single
            // sample knows nothing, and "knows nothing" must never mean "help
            // yourself to the GPU".
        }

        public WorkerState State { get { return _state; } }
        public Verdict Last = new Verdict();

        /// The row of the matrix in force right now. Never null.
        public ResourceLimits Limits { get { return Last.Limits; } }
        public MachineState MachineState { get { return Last.MachineState; } }

        public int SecondsUntilAvailable
        {
            get
            {
                if (_lastWanted == DateTime.MinValue) return _c.ClearCooldownSeconds;
                double s = _c.ClearCooldownSeconds - (_now - _lastWanted).TotalSeconds;
                return s <= 0 ? 0 : (int)Math.Ceiling(s);
            }
        }

        /// The VRAM veto, in the module that owns the decision.
        ///
        /// WHY IT MOVED HERE. The threshold, the allowlist and the own-job
        /// exemption are three judgements with real consequences: too low and a
        /// browser costs the user throughput, too high and a windowed game costs
        /// them a match. They were in Agent.SlowLoop, where no test could reach
        /// them and where the policy saw only an already-filtered list.
        ///
        /// The trip point is 512 MiB. Measured on spring's idle desktop the
        /// largest single consumer was dwm at 169.4 MiB and the largest outside
        /// the shell was CamoStudio at 112.3 MiB, so 512 is three times the
        /// biggest thing an idle desktop produces and far below what any game
        /// holds. This is the check that catches the expensive case: a game paused
        /// at a menu draws nothing but still owns its VRAM and resumes in a frame.
        public List<ProcessGpuUse> ForeignVram(Snapshot s)
        {
            var foreign = new List<ProcessGpuUse>();
            if (s.GpuProcesses == null) return foreign;
            foreach (ProcessGpuUse u in s.GpuProcesses)
            {
                if (u.DedicatedMiB < _c.ForeignVramBusyMiB) continue;
                // Never yield to our own job: it is the thing we are deciding
                // about, and counting its VRAM as evidence of a user would make the
                // policy oscillate the moment a job allocated anything.
                if (s.OwnJobPids != null && s.OwnJobPids.Contains(u.Pid)) continue;
                bool allowed = false;
                foreach (string a in _c.VramAllowlist)
                    if (string.Equals(a.Trim(), u.Name, StringComparison.OrdinalIgnoreCase)) { allowed = true; break; }
                if (!allowed) foreign.Add(u);
            }
            foreign.Sort(delegate(ProcessGpuUse a, ProcessGpuUse b)
                { return b.DedicatedMiB.CompareTo(a.DedicatedMiB); });
            return foreign;
        }

        /// Which row of the matrix this instant lands on.
        ///
        /// TAKEN AFTER THE VETOES ARE KNOWN, not before, because "a game exists"
        /// is the strongest evidence there is that somebody is at the machine and
        /// it is evidence the session signals do not carry: a full-screen game
        /// leaves GetLastInputInfo idle while somebody plays it with a controller.
        ///
        /// BLIND IS BUSY. The tier 0 checks mean the agent has lost the ability to
        /// see the user coming, and the correct row for "I cannot tell" is the one
        /// that takes nothing. Missing information must never read as permission.
        static MachineState Classify(Snapshot s, Verdict v, Config c, out string why)
        {
            if (v.Blind) { why = "cannot see whether anybody is at this machine"; return MachineState.Busy; }
            if (v.IsVeto) { why = v.ReasonText; return MachineState.Busy; }

            SessionSignals ss = s.Session;
            if (ss == null) { why = "no session signals"; return MachineState.Busy; }

            if (!ss.HasConsoleUser) { why = "nobody is signed in"; return MachineState.NobodyHome; }
            if (ss.Locked) { why = "the desktop is locked"; return MachineState.Locked; }

            // Can we see input at all. From session 0 GetLastInputInfo answers for
            // the CALLING session and reports a number that is a lie about the
            // user, so the boot agent only has this through the logon helper. When
            // it is missing the answer is LightUse, the careful row, and never
            // Idle: an unmeasurable idle time is not a measured one.
            bool canSeeInput = ss.RunningInConsoleSession || ss.PresenceFresh;
            if (canSeeInput && ss.InputIdleSeconds >= 0 && ss.InputIdleSeconds >= c.IdleAfterSeconds)
            {
                why = string.Format(CultureInfo.InvariantCulture,
                    "signed in, no input for {0}s", ss.InputIdleSeconds);
                return MachineState.Idle;
            }
            why = canSeeInput
                ? "signed in and using the machine"
                : "signed in, and this agent cannot measure input idle time from its session";
            return MachineState.LightUse;
        }

        /// The row, after the Busy state has been split by resource.
        ///
        /// THE CASE THIS EXISTS FOR, in the owner's words: a game takes the GPU
        /// while leaving twelve threads idle, and a compile takes the CPU while
        /// the card sits at five per cent. Selling the two independently is the
        /// point of doing any of this, and a single Busy row that zeroes both
        /// columns makes it impossible - the runner stops speech work on sixteen
        /// idle threads because somebody opened Counter-Strike.
        ///
        /// So when the state is Busy, an UNCONTENDED resource takes its fields
        /// from LightUse. Not from Idle and not from Locked: a tier 1 veto is the
        /// strongest evidence this program has that a person is at the machine,
        /// stronger than the session signals, because a full-screen game leaves
        /// GetLastInputInfo idle while somebody plays it with a controller.
        /// LightUse is the row for "somebody is here", and that is exactly what
        /// has been established.
        ///
        /// THE ADMISSION FIELDS FOLLOW THE CPU. MinFreeMib and Admit are about
        /// system memory and about starting new work, and system memory is the
        /// CPU side's resource - the GPU has its own veto on VRAM in tier 1. A
        /// game therefore does not stop the runner admitting a CPU job, and a
        /// compile does.
        ///
        /// FAIL CLOSED. Both flags clear means nothing explained the Busy, and an
        /// unexplained Busy gets the whole Busy row.
        ResourceLimits ResolveLimits(MachineState st, bool gpuContended, bool cpuContended)
        {
            ResourceLimits row = _c.LimitsFor(st);
            if (st != MachineState.Busy) return row;
            if (gpuContended && cpuContended) return row;
            if (!gpuContended && !cpuContended) return row;

            ResourceLimits light = _c.LimitsFor(MachineState.LightUse);
            ResourceLimits m = row.Clone();
            if (!gpuContended) m.Gpu = light.Gpu;
            if (!cpuContended)
            {
                m.CpuPct = light.CpuPct;
                m.Priority = light.Priority;
                m.WorkingSetMib = light.WorkingSetMib;
                m.MinFreeMib = light.MinFreeMib;
                m.Admit = light.Admit;
            }
            return m;
        }

        public Verdict Evaluate(Snapshot s)
        {
            var v = new Verdict();
            _now = s.At;
            if (_lastWanted == DateTime.MinValue) _lastWanted = s.At;

            // --- TIER 0: can we judge at all? --------------------------------
            //
            // Fail closed. Every one of these means the agent has lost the ability
            // to see the user coming, and the correct response to blindness is to
            // stop, not to carry on and hope.

            if (!s.GpuHealthy)
            {
                v.WantsGpu = true; v.IsVeto = true; v.Blind = true;
                v.Reasons.Add("nvidia-smi stream is stale or dead");
            }

            if (s.Session != null && !s.Session.RunningInConsoleSession
                && s.Session.HasConsoleUser && !s.Session.Locked
                && !s.Session.PresenceFresh)
            {
                // BLIND, AND SOMEBODY IS THERE. Measured on spring (probe p3,
                // re-confirmed 2026-09-05): from session 0 GetForegroundWindow()
                // returns 0 and GetLastInputInfo() reports the calling session's
                // own idle time - 620953 ms, which is a lie about the user. This
                // agent cannot see the person it exists to get out of the way of,
                // and somebody IS signed in and not locked, so it must not claim
                // the GPU.
                //
                // THE TWO CONDITIONS ABOVE ARE THE WHOLE POINT. This used to veto
                // on the session mismatch alone, which made a Windows service
                // impossible: a service is permanently in session 0, so it was
                // permanently blind, so it could never run. But "I cannot see the
                // user" and "there is no user" are different states, and the
                // second is the safest moment this program will ever get. See the
                // clause below.
                v.WantsGpu = true; v.IsVeto = true; v.Blind = true;
                v.Reasons.Add(string.Format(CultureInfo.InvariantCulture,
                    "agent is in session {0}, console is session {1} and {2} is "
                    + "signed in: cannot observe the user",
                    s.Session.OwnSessionId, s.Session.ConsoleSessionId,
                    s.Session.ConsoleUserName));
            }
            else if (s.Session != null && !s.Session.RunningInConsoleSession)
            {
                // NOBODY IS AT THIS MACHINE, or the desktop is locked. Not a veto,
                // and not "blind" either: there is nothing to be blind to. At the
                // sign-in screen the console session exists but has no user name,
                // and a locked session has a user who is demonstrably not at the
                // keyboard. Both are recorded so `status` explains why a service
                // is allowed to work rather than leaving it looking like the check
                // was skipped.
                //
                // This is the state a service lives in from boot until somebody
                // signs in, and it is most of a gaming PC's uptime.
                // THREE STATES, NOT TWO, AND THE THIRD ONE IS THE COMMON ONE.
                //
                // This said "the desktop is locked" whenever a console user
                // existed, without ever consulting Locked. A boot agent is
                // permanently outside the console session, so that string was
                // published on EVERY verdict it reached this branch with --
                // including, measured on spring 2026-09-09, a session that was
                // signed in, unlocked, idle for 930 s and correctly classified
                // Idle. `status` read "the desktop is locked, so nobody is at
                // the machine" beside `"locked": false` in its own JSON.
                //
                // Nothing behaved wrongly. The REPORT was wrong, which is worse
                // in one specific way: it is the only thing an owner can read,
                // and it says this agent works only while the screen is locked.
                // The reason a verdict gives has to be a fact about the verdict.
                v.Reasons.Add(!s.Session.HasConsoleUser
                    ? "nobody is signed in at this machine"
                    : s.Session.Locked
                        ? "the desktop is locked, so nobody is at the machine"
                        : "the logon helper is reporting, so this agent can see "
                          + s.Session.ConsoleUserName + " from session "
                          + s.Session.OwnSessionId.ToString(CultureInfo.InvariantCulture));
            }

            // --- TIER 1: vetoes. A game EXISTS. -------------------------------

            if (s.Launchers != null)
            {
                if (s.Launchers.SteamRunningAppId != 0)
                {
                    v.WantsGpu = true; v.IsVeto = true;
                    string name = string.IsNullOrEmpty(s.Launchers.SteamRunningAppName)
                        ? "appid " + s.Launchers.SteamRunningAppId.ToString(CultureInfo.InvariantCulture)
                        : s.Launchers.SteamRunningAppName;
                    v.Reasons.Add("Steam is running " + name);
                }
                if (s.Launchers.ValorantAntiCheatActive)
                {
                    // vgc, the user-mode service, not vgk. Measured on spring
                    // 2026-09-05: vgc Stopped/Manual, vgk Running/System. vgk is
                    // always running and is therefore worthless as a signal.
                    v.WantsGpu = true; v.IsVeto = true;
                    v.Reasons.Add("anti-cheat service " +
                        (string.IsNullOrEmpty(s.Launchers.AntiCheatService)
                            ? "is running" : s.Launchers.AntiCheatService + " is running"));
                }
                if (s.Launchers.GameProcesses.Count > 0)
                {
                    v.WantsGpu = true; v.IsVeto = true;
                    v.Reasons.Add("game process present: " + string.Join(", ", s.Launchers.GameProcesses.ToArray()));
                }
            }

            List<ProcessGpuUse> foreign = ForeignVram(s);
            if (foreign.Count > 0)
            {
                ProcessGpuUse top = foreign[0];
                v.WantsGpu = true; v.IsVeto = true;
                v.Reasons.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} (pid {1}) holds {2:N0} MiB of GPU memory", top.Name, top.Pid, top.DedicatedMiB));
            }

            if (s.Session != null && s.Session.ForegroundIsFullScreen && !s.Session.Locked)
            {
                // A full-screen window is not proof of a game, but on a desktop it is
                // proof that the user is doing one thing with their whole screen, and
                // the cost of being wrong here is a job rather than a match.
                v.WantsGpu = true; v.IsVeto = true;
                v.Reasons.Add("full-screen foreground window: " + s.Session.ForegroundProcess);
            }

            // --- TIER 2: load votes. Something is DRAWING. --------------------

            // NOT WHILE THE THING DRAWING IS OURS. Every signal below is a
            // WHOLE-GPU reading with no owner attached to it, so the moment our
            // own controller puts a model on the card the memory clock goes to
            // 6801 MHz and the performance state to P2, and this tier reads that
            // as somebody else at the machine.
            //
            // MEASURED, not hypothetical. The first real job through this runner
            // never finished: the agent started the controller, saw the clocks it
            // had itself caused about thirteen seconds later, yielded, killed the
            // job tree, cooled down for ninety seconds and did it again. Four
            // times, with the job sitting queued throughout, on a locked desktop
            // with nobody near it.
            //
            // The VRAM rule already had this exemption -- see Snapshot.OwnJobPids
            // and Policy.ForeignVram, added when a handoff made the agent yield to
            // its own second controller. The load votes never got it, and they are
            // the ones a GPU job trips hardest.
            //
            // TIER 1 IS DELIBERATELY NOT SUPPRESSED. Those vetoes identify a GAME
            // -- Steam's running app id, a named process, the anti-cheat service,
            // a full-screen window, another process holding VRAM -- and every one
            // of them stays true whatever this agent is running. A game starting
            // while our job holds the GPU must still win, in under six seconds,
            // and it still does.
            bool ourOwnLoad = s.OwnJobPids != null && s.OwnJobPids.Count > 0;

            var load = new List<string>();
            if (s.Gpu != null && s.Gpu.Valid && !ourOwnLoad)
            {
                if (s.Gpu.ClockMemMhz > _c.MemClockBusyMhz)
                    load.Add(string.Format(CultureInfo.InvariantCulture,
                        "memory clock {0} MHz over the {1} MHz trip point",
                        s.Gpu.ClockMemMhz, _c.MemClockBusyMhz));

                if (!_c.IdlePStates.Contains(s.Gpu.PState))
                    load.Add("performance state " + s.Gpu.PState);

                if (s.Gpu.PowerWatts > _c.PowerBusyWatts)
                    load.Add(string.Format(CultureInfo.InvariantCulture,
                        "power draw {0:N1} W", s.Gpu.PowerWatts));

                if (s.Gpu.UtilGpu > _c.UtilGpuBusyPct)
                    load.Add(string.Format(CultureInfo.InvariantCulture,
                        "utilisation {0}%", s.Gpu.UtilGpu));
            }

            // Deliberately NOT a load vote: utilisation.decoder and the videodecode
            // engine. Video playback uses a fixed-function block, not the shaders,
            // and a Chatterbox job and a YouTube tab can share this card. Yielding to
            // a video would make the worker useless on a desktop that is nearly always
            // playing something. Measured at idle: videodecode 0.00 throughout, so
            // this is a decision about what to ignore, not about noise.
            if (s.CountersFresh && s.Util3d > _c.Util3dBusyPct && !ourOwnLoad)
                load.Add(string.Format(CultureInfo.InvariantCulture, "3D engine at {0:N0}%", s.Util3d));

            if (load.Count > 0)
            {
                _busyStreak++;
                if (_busyStreak >= _c.BusyConfirmSamples)
                {
                    v.WantsGpu = true;
                    foreach (string r in load) v.Reasons.Add(r);
                }
            }
            else
            {
                _busyStreak = 0;
            }
            if (v.IsVeto) _busyStreak = 0;

            // --- THE CPU'S OWN LOAD VOTE ---------------------------------------
            //
            // Deliberately NOT symmetrical with the GPU's tier 2, and the
            // difference is the point. The GPU's load votes have to be SUPPRESSED
            // while our own job runs, because a whole-card reading has no owner
            // attached to it and the agent ended up yielding to the load it had
            // itself created. Measured: four restarts in a row on a locked desktop
            // with nobody near the machine.
            //
            // The CPU does not have that problem, because a job object accounts
            // for its own processes exactly. CpuSample.ForeignPct is machine CPU
            // minus our own, so this fires on somebody else's compile and cannot
            // fire on ours. That is strictly better than suppression: the agent
            // keeps a working CPU signal WHILE a job is running, which the GPU
            // still cannot do.
            //
            // It votes rather than vetoing, on the same three-sample confirmation
            // as the GPU's tier 2, because one sample over the line is a Windows
            // Update check and not a person.
            if (s.Cpu != null && s.Cpu.Valid && s.Cpu.ForeignPct > _c.ForeignCpuBusyPct)
            {
                _cpuBusyStreak++;
                if (_cpuBusyStreak >= _c.BusyConfirmSamples)
                    v.Reasons.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0:N0}% of this machine's CPU is somebody else's work", s.Cpu.ForeignPct));
            }
            else _cpuBusyStreak = 0;

            bool cpuBusy = _cpuBusyStreak >= _c.BusyConfirmSamples;

            // --- which resource is contended -----------------------------------
            //
            // A tier 1 veto is GPU evidence: Steam's running app id, the anti-cheat
            // service, a named game process, a foreign process holding VRAM, a
            // full-screen foreground window. Only two of those plausibly want the
            // processor as well, and a modern title that does still leaves half a
            // sixteen thread machine idle, so none of them closes the CPU column on
            // its own. The GPU tier 2 streak is the same kind of evidence, weaker.
            //
            // Blindness contends EVERYTHING. When the agent cannot see the owner it
            // is not entitled to an opinion about either resource.
            bool gpuContended = v.IsVeto || v.WantsGpu;
            bool cpuContended = cpuBusy;
            if (v.Blind) { gpuContended = true; cpuContended = true; }

            // --- which row of the matrix ---------------------------------------

            string stateWhy;
            MachineState now = Classify(s, v, _c, out stateWhy);
            if (cpuBusy && now < MachineState.Busy)
            {
                now = MachineState.Busy;
                stateWhy = "somebody else is using this machine's CPU";
            }
            // A Busy that no signal explains is a Busy nobody can price. Classify
            // also returns Busy for a missing session block, which is neither a
            // game nor a compile; treat it as both, because it is blindness by
            // another name.
            if (now == MachineState.Busy && !gpuContended && !cpuContended)
            {
                gpuContended = true; cpuContended = true;
            }

            // RESTRICT INSTANTLY, RELAX SLOWLY, on the same clock as the GPU's
            // cooldown. Tightening a cap costs nothing and must happen the moment
            // the owner appears; loosening it again the moment they pause is how
            // you get a machine that surges every time somebody stops typing to
            // read a paragraph. So the effective row is the most restrictive one
            // seen inside ClearCooldownSeconds.
            if (_worstRecentAt == DateTime.MinValue || now >= _worstRecent)
            {
                _worstRecent = now; _worstRecentAt = s.At;
                _worstGpuContended = gpuContended; _worstCpuContended = cpuContended;
            }
            else if ((s.At - _worstRecentAt).TotalSeconds >= _c.ClearCooldownSeconds)
            {
                _worstRecent = now; _worstRecentAt = s.At;
                _worstGpuContended = gpuContended; _worstCpuContended = cpuContended;
            }
            MachineState effective = now >= _worstRecent ? now : _worstRecent;
            if (effective > now)
            {
                // Holding a worse row than this sample deserves. Carry the reason
                // with it, or the fallback below would read a clear sample and
                // reopen the column the cooldown is there to keep shut.
                gpuContended = gpuContended || _worstGpuContended;
                cpuContended = cpuContended || _worstCpuContended;
            }

            v.MachineState = effective;
            v.GpuContended = gpuContended;
            v.CpuContended = cpuContended;
            v.Limits = ResolveLimits(effective, gpuContended, cpuContended);
            v.StateReason = effective == now
                ? stateWhy
                : stateWhy + "; holding " + Config.StateLabel(effective).ToLowerInvariant()
                  + " until the cooldown expires";

            // --- state machine ------------------------------------------------

            if (v.WantsGpu) _lastWanted = s.At;

            if (v.Blind)
                _state = WorkerState.Blocked;   // cannot judge; say so rather than blame a game
            else if (v.WantsGpu)
                _state = WorkerState.Busy;
            else if ((s.At - _lastWanted).TotalSeconds >= _c.ClearCooldownSeconds)
                _state = WorkerState.Available;
            else
                _state = WorkerState.Draining;   // clear, but inside the cooldown

            Last = v;
            return v;
        }

        /// The single question the rest of the program asks.
        public bool CanRun(Mode mode)
        {
            if (mode == Mode.Off) return false;
            if (mode == Mode.AlwaysOn) return true;
            return _state == WorkerState.Available;
        }

        /// May a service that wants the GPU run right now.
        ///
        /// The GPU detector is still the gate, exactly as before; the matrix's GPU
        /// column is an EXTRA veto on top of it, and it ships as yes in every row
        /// but Busy so that installing this release changes nobody's behaviour.
        /// It exists so an owner can say "not while I am at the machine, even if
        /// it looks idle", which the detector alone cannot express.
        public bool CanRunGpu(Mode mode)
        {
            if (!CanRun(mode)) return false;
            if (mode == Mode.AlwaysOn) return true;
            return Last.Limits == null || Last.Limits.Gpu;
        }

        /// May a service that wants only the CPU run right now.
        ///
        /// Note what is NOT here: WorkerState. A CPU service does not care that a
        /// game owns the GPU, it cares that the owner is at the machine, and that
        /// is what the matrix row says. Reusing the GPU's Available would have
        /// made every CPU job wait out a ninety second cooldown for a card it
        /// never touches.
        public bool CanRunCpu(Mode mode)
        {
            if (mode == Mode.Off) return false;
            if (mode == Mode.AlwaysOn) return true;
            return Last.Limits != null && Last.Limits.CpuPct > 0;
        }

        /// May a NEW job start, as opposed to may a running one continue.
        ///
        /// TWO QUESTIONS, NOT ONE, and conflating them is what makes a CPU
        /// limiter feel like a killer. A running job has already paid for itself:
        /// on this workload that is a 22 second model load and 4.7 GiB of
        /// allocation, and throttling it to a trickle costs the owner almost
        /// nothing while keeping all of that warm. Starting a NEW one on a machine
        /// somebody is using costs them the load, the allocation and the memory
        /// for as long as it runs. So the Busy row keeps the warm job and admits
        /// nothing, which MinFreeMib could only have expressed as a number so
        /// large it would have been a lie about memory.
        public bool AdmitsNewWork(Mode mode)
        {
            if (mode == Mode.Off) return false;
            if (mode == Mode.AlwaysOn) return true;
            return Last.Limits == null || Last.Limits.Admit;
        }

        /// True when the user's chosen mode is running a job that the detector
        /// would have stopped.
        ///
        /// WHY THIS IS A FIRST-CLASS QUESTION AND NOT AN IMPLEMENTATION DETAIL.
        /// Always-on exists so somebody who does not yet trust Auto has somewhere
        /// to go. But an override that silently hides the verdict it is overriding
        /// throws away the only evidence this proof of concept is trying to
        /// collect. In Always-on the policy keeps evaluating, keeps logging and
        /// keeps writing its verdict to state.json - so the 3070 can be benchmarked
        /// on demand while the detector, which is not in charge, still accumulates
        /// the false-idle record that decides whether Auto is trustworthy.
        ///
        /// The tray draws this state as a hollow ring rather than a filled dot, so
        /// "I am using your GPU while you game" is never invisible.
        public bool IsOverriding(Mode mode)
        {
            return mode == Mode.AlwaysOn && _state != WorkerState.Available;
        }

        /// The admission check, as arithmetic, so it is testable without a machine.
        ///
        /// WHY ADMISSION AND NOT ONLY A CAP. They answer different questions. A
        /// working set cap contains a job that has already started; it cannot undo
        /// the moment where a 6.5 GiB model loads onto a machine with 2 GiB spare
        /// and the owner's browser goes to disk. A job not started costs a wait; a
        /// job started on a machine with no headroom costs the owner their session.
        ///
        /// UNMEASURABLE IS NOT A VETO, which is the opposite of the rule tier 0
        /// uses for the GPU, and deliberately so. Blindness about the USER must
        /// fail closed because the cost of being wrong is somebody's match.
        /// Blindness about free memory must fail open, because the cost of being
        /// wrong is a runner that never starts anything on a machine whose memory
        /// counter is unavailable, and that is a worse failure than a job that is
        /// occasionally started at a bad moment.
        public static bool MemoryAllows(long availableMib, bool availableKnown,
                                        int minFreeMib, int needsMib, out string why)
        {
            why = "";
            if (minFreeMib <= 0) return true;
            if (!availableKnown) return true;
            long need = minFreeMib + (needsMib > 0 ? needsMib : 0);
            if (availableMib >= need) return true;
            why = string.Format(CultureInfo.InvariantCulture,
                "only {0:N0} MiB of memory is free and this needs {1:N0} MiB of headroom",
                availableMib, need);
            return false;
        }

        public static string Describe(WorkerState st)
        {
            switch (st)
            {
                case WorkerState.Available: return "available";
                case WorkerState.Busy: return "busy";
                case WorkerState.Draining: return "cooling down";
                default: return "blocked";
            }
        }
    }
}
