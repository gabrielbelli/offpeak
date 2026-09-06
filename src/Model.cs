// The data the policy decides on, and nothing else.
//
// WHY THIS FILE EXISTS AS A SEPARATE FILE. These types used to live in
// Signals.cs, next to the P/Invoke that fills them in. That made the policy
// untestable in practice: to compile Policy.cs you had to compile Signals.cs,
// and Signals.cs pulls in user32, kernel32, PDH, the registry and the service
// control manager. A decision that the user's gaming session depends on was
// therefore only exercisable on a Windows desktop with a GPU in it, which is to
// say it was only exercisable in exactly the situation where getting it wrong is
// expensive.
//
// Every type below is plain data with no behaviour and no platform dependency.
// Model.cs + Config.cs + Policy.cs + Replay.cs compile and run on their own,
// which is what tests/run-tests.ps1 does. There is no second implementation of
// the policy for testing - the tests drive the same Policy.Evaluate the tray
// runs, fed from recorded samples instead of from live hardware.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace IdleGpu
{
    public enum Mode { Auto, AlwaysOn, Off }

    /// How present the owner is, which is the row of the limits matrix.
    ///
    /// WHY A LADDER AND NOT A BOOLEAN. "Is the machine free" has one answer and
    /// this machine has five, because a game takes the GPU while leaving twelve
    /// threads idle and a compile takes every thread while the GPU sits at five
    /// per cent. Selling the two independently is the whole point, and that needs
    /// a state that both resources can be priced against.
    ///
    /// EVERY ROW MUST BE DECIDABLE FROM A SIGNAL THAT EXISTS, or it is a row that
    /// lies. What each one is read from:
    ///
    ///   NobodyHome  Session.HasConsoleUser is false. WTSGetActiveConsoleSessionId
    ///               finds a session with no user name: the sign-in screen, or a
    ///               machine that has booted and nobody has logged into yet. This
    ///               is most of a gaming PC's uptime and the safest state there is.
    ///   Locked      Session.Locked. Somebody is signed in and demonstrably not at
    ///               the keyboard. Read from the console session directly when the
    ///               agent is in it, and from the logon helper's report when it is
    ///               not, which is the case for the boot task.
    ///   Idle        Signed in, unlocked, and InputIdleSeconds is at or over
    ///               IdleAfterSeconds. GetLastInputInfo, which only answers for
    ///               the calling session, so from session 0 this needs the helper
    ///               and falls back to LightUse when the helper is not reporting.
    ///   LightUse    Signed in, unlocked, input recently. Somebody is reading,
    ///               typing or browsing. This is the state the CPU ladder has to
    ///               be careful in, and the only one it has to be careful in.
    ///   Busy        A tier 1 veto fired: Steam has a running app, the anti-cheat
    ///               service is up, a named game process exists, a foreign process
    ///               holds VRAM, or the foreground window is full screen. Also
    ///               reached when foreign CPU load alone is over the trip point,
    ///               which is what catches a compile.
    ///
    /// Ordered from most free to least. Higher ordinal means yield harder, which
    /// makes "the more restrictive of two states" a Math.Max.
    public enum MachineState { NobodyHome = 0, Locked = 1, Idle = 2, LightUse = 3, Busy = 4 }

    /// What this agent may take in one machine state. One row of the matrix.
    ///
    /// THE MECHANISM BEHIND EVERY FIELD WAS MEASURED, not assumed, on spring
    /// (Ryzen 7 5700X3D, 16 logical processors, 31.9 GiB, Windows 11 26200) with
    /// probe/p8_cpu.cs, probe/p9_felt.cs and probe/p10_felt_hard.cs. The numbers
    /// are in docs/CPU-LIMITS.md. In short:
    ///
    ///   CpuPct        JOB_OBJECT_CPU_RATE_CONTROL_INFORMATION with HARD_CAP, as
    ///                 a proportion of the WHOLE MACHINE and not of one core.
    ///                 Measured: a 50 per cent cap on sixteen spinning threads
    ///                 produced 50.2 per cent of the machine, which is 8.03 cores.
    ///                 This is vertical: it says how much, never which cores.
    ///                 0 means do not run at all, because CpuRate 0 is rejected by
    ///                 SetInformationJobObject with INVALID_ARGS, so zero has to
    ///                 mean stop rather than cap.
    ///   Priority      IDLE_PRIORITY_CLASS, the "niceness" half. Necessary and NOT
    ///                 SUFFICIENT: measured against a memory-streaming load and an
    ///                 eight-thread victim, idle priority alone still cost the
    ///                 victim 40 per cent of its throughput and moved its median
    ///                 unit from 4.32 ms to 10.38 ms, because cache eviction has
    ///                 already happened by the time the scheduler preempts.
    ///   WorkingSetMib JOB_OBJECT_LIMIT_WORKINGSET maximum. Trims the JOB, so the
    ///                 job pages instead of the owner's browser. Measured: a child
    ///                 that committed 512 MiB held a 191 MiB working set under a
    ///                 192 MiB limit and exited 0. Deliberately NOT a commit cap:
    ///                 JOB_OBJECT_LIMIT_JOB_MEMORY killed the same child with
    ///                 0xC0000005, and a limiter that kills the job is a different
    ///                 product from one that squeezes it.
    ///   MinFreeMib    Admission. Available physical memory below which a job is
    ///                 not STARTED. A job not started costs a wait; a job started
    ///                 on a machine with no headroom costs the owner their session,
    ///                 and paging is felt in a way CPU contention is not.
    public class ResourceLimits
    {
        public bool Gpu = true;
        public int CpuPct = 100;
        public string Priority = "normal";     // normal | belownormal | idle
        public int WorkingSetMib;              // 0 = do not limit the working set
        public int MinFreeMib;                 // 0 = start regardless of free memory

        public ResourceLimits Clone()
        {
            var r = new ResourceLimits();
            r.Gpu = Gpu; r.CpuPct = CpuPct; r.Priority = Priority;
            r.WorkingSetMib = WorkingSetMib; r.MinFreeMib = MinFreeMib;
            return r;
        }

        public bool SameAs(ResourceLimits o)
        {
            return o != null && o.Gpu == Gpu && o.CpuPct == CpuPct
                && string.Equals(o.Priority, Priority, StringComparison.OrdinalIgnoreCase)
                && o.WorkingSetMib == WorkingSetMib && o.MinFreeMib == MinFreeMib;
        }

        /// One line a person can read in a menu, in the words the menu uses.
        public string Describe()
        {
            if (CpuPct <= 0 && !Gpu) return "nothing";
            var parts = new List<string>();
            parts.Add(Gpu ? "GPU yes" : "GPU no");
            parts.Add(CpuPct <= 0 ? "CPU no"
                : "CPU " + CpuPct.ToString(CultureInfo.InvariantCulture) + "%");
            if (!string.Equals(Priority, "normal", StringComparison.OrdinalIgnoreCase))
                parts.Add(Priority.ToLowerInvariant() == "idle" ? "idle priority" : "low priority");
            if (WorkingSetMib > 0)
                parts.Add("RAM " + WorkingSetMib.ToString(CultureInfo.InvariantCulture) + " MiB");
            return string.Join(", ", parts.ToArray());
        }
    }

    public enum WorkerState
    {
        Available,   // the GPU is ours to use
        Busy,        // the user, or something of theirs, has it
        Draining,    // clear, but inside the cooldown; a job is being wound down
        Blocked      // we cannot judge safely, so we behave as if busy
    }

    public class GpuSample
    {
        public DateTime At;
        public int UtilGpu;         // per cent, whole-GPU, averaged by the driver
        public int UtilMem;         // per cent of time the memory bus was busy
        public int UtilEncoder;     // NVENC
        public int UtilDecoder;     // NVDEC
        public int ClockMemMhz;     // memory clock
        public int ClockSmMhz;
        public string PState;       // P0 fastest .. P12 idle
        public double PowerWatts;
        public int MemUsedMiB;
        public bool Valid;
    }

    /// The whole machine's CPU, and the part of it that is ours.
    ///
    /// THE DEFECT THIS SHAPE PREVENTS, and it is the same one the GPU tier 2 had.
    /// Every whole-machine load reading has no owner attached to it, so the moment
    /// our own controller starts, the agent reads the load it created as somebody
    /// else at the machine and yields to itself. The GPU could only paper over
    /// that by SUPPRESSING its load votes while a job ran, which throws away a
    /// real signal to avoid a false one.
    ///
    /// The CPU does not have to make that trade. A job object accounts for its own
    /// processes exactly, so OwnPct is a measurement rather than a guess and
    /// ForeignPct is arithmetic. The agent yields to other people's load while a
    /// job is running, which the GPU still cannot do.
    ///
    /// Read from GetSystemTimes rather than from PDH. Two reasons and both matter
    /// for something meant to be published: a PDH counter path is LOCALISED, so
    /// "\Processor Information(_Total)\% Processor Time" does not exist on a
    /// German or Portuguese Windows, and GetSystemTimes costs a syscall against
    /// PDH's measured 98 ms per counter sample.
    public class CpuSample
    {
        public DateTime At;
        public double MachinePct;      // 0..100 of the whole machine
        public double OwnPct;          // the part of it inside our job objects
        public double ForeignPct;      // MachinePct - OwnPct, floored at 0
        public bool Valid;
    }

    /// Free memory, for the admission check.
    ///
    /// AvailableMib is GlobalMemoryStatusEx's ullAvailPhys, which is the honest
    /// answer to "would starting a 6.5 GiB job hurt right now": it is physical
    /// memory the system can hand out without taking it from somebody, standby
    /// and free pages together. Commit headroom (ullAvailPageFile) is the wrong
    /// number for this question, because a machine with a large page file has
    /// plenty of it while already paging hard.
    ///
    /// Measured cost on spring: 0.001 ms per call, so it is free to poll.
    public class MemorySample
    {
        public DateTime At;
        public long TotalMib;
        public long AvailableMib;
        public int LoadPct;
        public bool Valid;
    }

    public class ProcessGpuUse
    {
        public int Pid;
        public string Name;
        public double DedicatedMiB;
    }

    public class SessionSignals
    {
        public uint ConsoleSessionId;
        public uint OwnSessionId;
        public bool RunningInConsoleSession;
        public bool Locked;
        // IS ANYBODY SIGNED IN AT ALL, as opposed to signed in and unobservable.
        // Being outside the console session used to be one state, "blind", and it
        // was treated as an absolute veto. That conflated two situations that
        // could not be more different: somebody is at the keyboard and this agent
        // cannot see them, versus nobody is at the keyboard at all. The second is
        // the SAFEST moment there is to use the machine, and it was the one being
        // refused. A service starting at boot lives in that state permanently,
        // which is why it was impossible before this field existed.
        public bool HasConsoleUser;
        public string ConsoleUserName;
        // A HELPER IN THE USER'S SESSION IS REPORTING, AND ITS REPORT IS RECENT.
        // An agent at boot lives in session 0, where Windows will not say who is
        // at the keyboard, so it can only work while nobody is signed in. That
        // gives up the case the machine spends most of its evenings in: signed
        // in, and doing nothing heavy. A small helper started at logon reads the
        // signals in the session that HAS them and posts them here, and while
        // those reports keep arriving the agent is not blind and the ordinary
        // policy applies. When they stop, this goes false within seconds and the
        // conservative answer comes straight back.
        public bool PresenceFresh;
        public int InputIdleSeconds;      // -1 when not measurable from this session
        public int ForegroundPid;
        public string ForegroundProcess;
        public string ForegroundTitle;
        public bool ForegroundIsFullScreen;
    }

    /// What the logon helper reports, and nothing else.
    ///
    /// Two groups, and both are needed. The session signals because
    /// GetForegroundWindow and GetLastInputInfo answer for the CALLING session
    /// and lie from session 0. Steam because it writes the running app id to
    /// HKCU\Software\Valve\Steam, and a process running as SYSTEM reads SYSTEM's
    /// hive, where there is no Steam and never will be -- so the tier 1 veto
    /// that fires before a game renders its first frame is blind at boot too,
    /// and that is the fastest signal the policy has.
    public class PresenceReport
    {
        public int InputIdleSeconds = -1;
        public bool Locked;
        public bool ForegroundIsFullScreen;
        public string ForegroundProcess = "";
        public int SteamRunningAppId;
        public string SteamRunningAppName = "";
    }

    public class LauncherSignals
    {
        public int SteamRunningAppId;     // 0 when Steam is running no game
        public string SteamRunningAppName;
        public bool ValorantAntiCheatActive;
        public string AntiCheatService = "";
        public List<string> GameProcesses = new List<string>();
    }

    /// One instant, as the policy sees it.
    ///
    /// NOTE ON GpuProcesses. This is the RAW counter read - every process holding
    /// dedicated GPU memory, allowlisted or not. It used to be pre-filtered in
    /// Agent.SlowLoop, so the policy received only processes that had already been
    /// judged suspicious and its VRAM rule was reduced to "is this list empty".
    /// That put the threshold, the allowlist and the own-job exemption - three
    /// decisions with real consequences - outside the module that is supposed to
    /// hold the decisions, and outside anything a test could reach. They live in
    /// Policy.EvaluateVram now and the agent just hands over what it read.
    public class Snapshot
    {
        public DateTime At;
        public GpuSample Gpu;
        public bool GpuHealthy;
        public SessionSignals Session;
        public LauncherSignals Launchers;
        public List<ProcessGpuUse> GpuProcesses = new List<ProcessGpuUse>();
        /// Every controller process this agent currently owns. Never evidence of
        /// a user, because they are the thing being decided about.
        ///
        /// WHY A SET AND NOT AN int. It was a single pid when the agent supervised
        /// exactly one job. One runner now hosts many services, and although only
        /// one controller runs at a time, a handoff overlaps two: the outgoing one
        /// is inside its grace period while the incoming one has already started.
        /// With a single pid the unexempted one reads as a foreign process holding
        /// VRAM, the VRAM veto fires, and the agent yields to itself.
        public HashSet<int> OwnJobPids = new HashSet<int>();
        public double Util3d;
        public double UtilVideoDecode;
        public double UtilVideoEncode;
        public bool CountersFresh;
        public CpuSample Cpu;
        public MemorySample Memory;
    }

    public class Verdict
    {
        public bool WantsGpu;               // something other than us wants it now
        public List<string> Reasons = new List<string>();
        public bool IsVeto;                 // tier-1 signal, so no confirmation window

        /// Tier 0: the agent cannot see the user at all, so it is not entitled to
        /// an opinion about whether they are there.
        ///
        /// This is a different thing from Busy and the tray says so. "Yielded to
        /// you" means the detector is working and found you; "Blocked" means the
        /// detector is broken or blind, which is the user's cue that something
        /// needs looking at rather than that a game is running. Before this flag
        /// existed every tier-0 condition set WantsGpu and the state machine
        /// mapped it to Busy, which made WorkerState.Blocked unreachable after the
        /// first sample and turned the tray's grey icon into dead code.
        public bool Blind;

        /// Which row of the limits matrix this instant lands on.
        public MachineState MachineState = MachineState.Busy;

        /// The row itself, already resolved. Never null after Evaluate.
        public ResourceLimits Limits = new ResourceLimits();

        /// Why that state and not the one above it, in one line, for the tray.
        public string StateReason = "";

        public string ReasonText
        {
            get { return Reasons.Count == 0 ? "clear" : string.Join("; ", Reasons.ToArray()); }
        }
    }
}
