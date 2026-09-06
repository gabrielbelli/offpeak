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
        DateTime _lastWanted = DateTime.MinValue;
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
                && s.Session.HasConsoleUser && !s.Session.Locked)
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
                v.Reasons.Add(s.Session.HasConsoleUser
                    ? "the desktop is locked, so nobody is at the machine"
                    : "nobody is signed in at this machine");
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

            var load = new List<string>();
            if (s.Gpu != null && s.Gpu.Valid)
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
            if (s.CountersFresh && s.Util3d > _c.Util3dBusyPct)
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
