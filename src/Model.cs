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

namespace IdleGpu
{
    public enum Mode { Auto, AlwaysOn, Off }

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

        public string ReasonText
        {
            get { return Reasons.Count == 0 ? "clear" : string.Join("; ", Reasons.ToArray()); }
        }
    }
}
