// The loop, the things it supervises, and the scheduler that picks between them.
//
// THREE THREADS, AND THE RULE THAT SEPARATES THEM.
//
//   _fast   FastPollMs, default 1000 ms. Reads the signals, evaluates the policy,
//           and if the answer is "the user wants the GPU" queues a Stop. It also
//           publishes the status document into a volatile string. It takes _lock
//           for exactly one short copy of the counter snapshot and holds no lock
//           while it decides or stops. THIS IS THE YIELD PATH. Nothing else may
//           run on it, and nothing outside it may hold a lock it needs.
//
//   _slow   CounterPollMs, default 2000 ms. Reads the performance counters, which
//           cost 98 ms measured, and writes state.json out of the string _fast
//           published. File I/O lives here so that it is never a syscall on the
//           yield path.
//
//   _sched  SchedulerPollMs, default 500 ms. Decides WHICH service runs. Selecting
//           one means listing every service's pending directory, which is a
//           syscall per service; on _fast that would be a syscall on the yield
//           path, so it is not on _fast.
//
// The listener runs on its own threads again and reaches the loop through exactly
// one channel: a bounded command queue that _fast drains in O(1) at the top of
// its tick. A client can flood, stall or attack the listener and the worst it can
// do is starve the listener's own connection pool. It cannot add a millisecond to
// a yield, because it never touches _fast and never takes _lock.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IdleGpu
{
    /// Runs one service's controller as a child process inside a Windows job object.
    ///
    /// WHY A JOB OBJECT AND NOT Process.Kill(). A controller that loads a deep
    /// learning framework spawns worker processes and CUDA helper threads. Killing
    /// the parent alone orphans those, and an orphaned CUDA context keeps its VRAM
    /// allocated, which on a card the user is about to want back for a game is
    /// exactly the failure this program exists to prevent. A job object with
    /// KILL_ON_JOB_CLOSE takes the whole tree down together, and takes it down even
    /// if the agent itself is killed, because the handle closes when the agent's
    /// process object is torn down by the kernel.
    public class JobRunner : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObjectW(IntPtr sec, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong r, w, o, rt, wt, ot; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        const long ControllerLogCapBytes = 8L * 1024 * 1024;

        IntPtr _job = IntPtr.Zero;
        Process _proc;
        readonly ServiceDef _svc;
        readonly Config _c;
        readonly Action<string> _log;
        readonly string _childLogPath;
        readonly object _childLogLock = new object();

        /// Re-entrancy guard for Stop().
        ///
        /// THE DEFECT THIS PREVENTS, which was measured rather than imagined.
        /// FastLoop ticks every FastPollMs (1000 ms) and the grace period is
        /// YieldGraceSeconds (2 s). Running stays true for the whole grace period,
        /// so "!can && Running" was still true on the next tick and a SECOND Stop
        /// was queued while the first was still waiting. Its `if (!Running) return`
        /// did not catch that. The consequences all landed on the numbers this
        /// program exists to produce: Yields counted one yield twice, LastYieldMs
        /// was overwritten by the second call's stopwatch, and the second
        /// WriteLine ran against a closed pipe. A listener that raises the job rate
        /// makes it fire on essentially every yield.
        volatile bool _stopping;

        public JobRunner(ServiceDef svc, Config c, Action<string> log)
        {
            _svc = svc; _c = c; _log = log;
            _childLogPath = Path.Combine(Path.Combine(c.DataDir, "logs"), svc.Id + ".log");
        }

        public ServiceDef Service { get { return _svc; } }

        public bool Running
        {
            get
            {
                try { return _proc != null && !_proc.HasExited; }
                catch (Exception) { return false; }
            }
        }

        public bool Stopping { get { return _stopping; } }

        public int Pid { get { try { return _proc == null ? -1 : _proc.Id; } catch (Exception) { return -1; } } }

        // The measurement this program exists to produce. LastYieldMs is the wall
        // time from the policy saying yield to the child process being gone, which
        // is when the driver gets the CUDA context and the weights back.
        // LastYieldWasKill says whether the polite stdin stage was enough or
        // whether the job object had to take it, because "it yielded in 2.1 s"
        // means something different if 2.0 s of that was the grace timer.
        public int LastYieldMs = -1;
        public bool LastYieldWasKill;
        public int Yields;
        public int Starts;
        public DateTime StartedAt = DateTime.MinValue;

        public bool Start()
        {
            if (Running || _stopping) return Running;
            if (string.IsNullOrEmpty(_svc.Command))
            {
                _log(_svc.Id + ": no Command configured, cannot start");
                return false;
            }
            try
            {
                ReapHandle();
                _job = CreateJobObjectW(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr p = Marshal.AllocHGlobal(len);
                Marshal.StructureToPtr(info, p, false);
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, p, (uint)len);
                Marshal.FreeHGlobal(p);

                var psi = new ProcessStartInfo(_svc.Command, _svc.Arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                // stdin is the yield channel, and it does double duty.
                //
                // 1. One line, "YIELD", when the policy says the user wants the GPU
                //    back. A controller has a thread blocked on stdin that sets a
                //    flag, and checks that flag between units of work. It cannot be
                //    checked DURING a unit, which is why the manifest declares how
                //    long a unit can be.
                //
                // 2. When the agent dies for any reason - killed, crashed, logged
                //    out - the kernel closes this pipe and the child's read returns
                //    EOF. That is a dead man's switch for free: the controller
                //    exits on its own instead of becoming an orphan holding a CUDA
                //    context. The job object already covers the ordinary case; this
                //    covers it a second way, at the cost of one flag.
                psi.RedirectStandardInput = true;
                // A headless agent has no console, so without this a controller's
                // diagnostics go nowhere and a failure to start looks like silence.
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                if (!string.IsNullOrEmpty(_svc.WorkingDir)) psi.WorkingDirectory = _svc.WorkingDir;

                // THE SAME CONTAINED ENVIRONMENT THE PROVISIONING SCRIPT GOT, and
                // for the same reason. A controller that imports torch will look for
                // weights in HF_HOME, and if that is not set it defaults to
                // %USERPROFILE%\.cache\huggingface and re-downloads three gigabytes
                // outside the contained directory the first time it runs. Setting it
                // at provisioning time only would leave the download containment
                // intact and the RUNTIME containment broken, which is the harder of
                // the two to notice: everything works, and the uninstall story is
                // quietly false. Set on the child, never on the machine.
                foreach (KeyValuePair<string, string> kv in Install.ContainedEnvironment(_svc, _c))
                    psi.EnvironmentVariables[kv.Key] = kv.Value;

                PrepareChildLog();
                _proc = Process.Start(psi);
                _proc.OutputDataReceived += ChildLine;
                _proc.ErrorDataReceived += ChildLine;
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                AssignProcessToJobObject(_job, _proc.Handle);
                Starts++;
                StartedAt = DateTime.UtcNow;
                _log(_svc.Id + ": started controller pid " + _proc.Id.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                _log(_svc.Id + ": failed to start controller: " + ex.Message);
                return false;
            }
        }

        void PrepareChildLog()
        {
            try
            {
                string dir = Path.GetDirectoryName(_childLogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // Truncate rather than rotate. A controller log is a debugging aid,
                // not a record, and an unbounded file in the contained directory is
                // a slow way to fill somebody's disk.
                if (File.Exists(_childLogPath) && new FileInfo(_childLogPath).Length > ControllerLogCapBytes)
                    File.Delete(_childLogPath);
            }
            catch (Exception) { }
        }

        void ChildLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            try
            {
                lock (_childLogLock)
                    File.AppendAllText(_childLogPath,
                        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + e.Data +
                        Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// Ask, wait the grace period, then take it.
        public void Stop(string why)
        {
            if (_stopping) return;
            _stopping = true;
            try
            {
                if (!Running) { ReapHandle(); return; }
                var sw = Stopwatch.StartNew();
                Yields++;
                LastYieldWasKill = false;
                int grace = _svc.YieldGraceSeconds > 0 ? _svc.YieldGraceSeconds : _c.YieldGraceSeconds;
                _log(_svc.Id + ": yielding the GPU (" + why + "), grace " +
                     grace.ToString(CultureInfo.InvariantCulture) + "s");
                try
                {
                    // The polite stage, and it has to be something the child can
                    // actually hear. This used to be CloseMainWindow(), which does
                    // nothing at all to a console process with no window, so the
                    // "cooperative" stage was a no-op and the grace period simply
                    // elapsed before the kill every single time. Measuring a yield
                    // with that in place would have measured the grace timer.
                    //
                    // One line on stdin, then close the pipe. Closing matters: it is
                    // the EOF that tells a controller which is not watching for
                    // YIELD, or one wedged inside a unit of work, that the agent has
                    // finished with it.
                    _proc.StandardInput.WriteLine("YIELD");
                    _proc.StandardInput.Flush();
                    _proc.StandardInput.Close();
                }
                catch (Exception) { }
                try
                {
                    if (!_proc.WaitForExit(grace * 1000))
                    {
                        _log(_svc.Id + ": grace expired; killing the job tree");
                        LastYieldWasKill = true;
                        KillTree();
                    }
                    else
                    {
                        // The handle used to be closed only on the kill path, so a
                        // controller that exited politely leaked one kernel object
                        // per yield. Invisible at one yield an hour; not invisible
                        // once a listener is raising the job rate.
                        ReapHandle();
                    }
                }
                catch (Exception) { LastYieldWasKill = true; KillTree(); }
                LastYieldMs = (int)sw.ElapsedMilliseconds;
                _log(_svc.Id + ": gpu released after " + LastYieldMs.ToString(CultureInfo.InvariantCulture) +
                     " ms (" + (LastYieldWasKill ? "killed" : "exited on request") + ")");
            }
            finally { _stopping = false; }
        }

        /// Close the job handle once the child is gone. Safe to call repeatedly.
        /// Called by the scheduler when it notices a controller exited on its own,
        /// which is the normal end of a controller that has drained its queue.
        public void ReapHandle()
        {
            try
            {
                if (_proc != null && !_proc.HasExited) return;
            }
            catch (Exception) { }
            try { if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; } }
            catch (Exception) { }
        }

        void KillTree()
        {
            // Closing the job handle kills every process in it. That is the whole
            // point of the job object: no child survives to hold VRAM.
            try { if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; } }
            catch (Exception) { }
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); }
            catch (Exception) { }
        }

        public void Dispose() { KillTree(); }
    }

    /// One control message from the network to the loop. The ONLY thing a client
    /// can push into the policy thread, and it is drained in O(1) at the top of a
    /// tick.
    public class Command
    {
        public string Kind = "";      // "mode"
        public string Value = "";
    }

    /// Owns the samplers, the policy, the services and the schedule.
    public class Agent : IDisposable
    {
        readonly Config _c;
        readonly GpuMonitor _gpu;
        readonly GpuProcessMemory _vram = new GpuProcessMemory();
        readonly GpuEngineUtil _engines = new GpuEngineUtil();
        readonly Policy _policy;
        readonly object _lock = new object();

        readonly List<ServiceDef> _services = new List<ServiceDef>();
        readonly Dictionary<string, JobRunner> _jobs =
            new Dictionary<string, JobRunner>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, JobStore> _queues =
            new Dictionary<string, JobStore>(StringComparer.OrdinalIgnoreCase);

        readonly ConcurrentQueue<Command> _commands = new ConcurrentQueue<Command>();
        int _commandDepth;

        Thread _fast, _slow, _sched;
        volatile bool _stop;
        Snapshot _snap = new Snapshot();
        List<ProcessGpuUse> _lastVram = new List<ProcessGpuUse>();
        Dictionary<string, double> _lastEngines = new Dictionary<string, double>();
        DateTime _countersAt = DateTime.MinValue;
        PresenceReport _presence;
        DateTime _presenceAt = DateTime.MinValue;

        /// How stale a presence report may be before it is ignored.
        ///
        /// The helper posts every second. Five seconds is enough to ride out a
        /// missed tick or a slow moment without ever letting a helper that has
        /// DIED keep the agent believing it can see the user. When this expires
        /// the agent goes back to refusing while somebody is signed in, which is
        /// the safe answer, and it does so within one sampling tick.
        public static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(5);

        /// Called by the API when the logon helper posts. Cheap on purpose: it
        /// takes the lock, stores two fields and returns, so a helper that hangs
        /// or a caller that floods cannot sit in the sampling loop or, worse, in
        /// the yield path.
        public void SetPresence(PresenceReport r)
        {
            lock (_lock) { _presence = r; _presenceAt = DateTime.UtcNow; }
        }
        int _lastCounterCostMs = -1;

        /// The published status document. Written by _fast once per tick, read by
        /// the listener with no lock at all.
        ///
        /// WHY IT IS PUBLISHED RATHER THAN COMPUTED ON DEMAND. Building it needs
        /// the current snapshot, which lives behind _lock. If GET /v1/status
        /// computed it, every poller in the world would be taking a lock that the
        /// yield path also takes, and a flood of status requests would become
        /// contention on the one thread that must never wait for anything. It also
        /// closes a real gap: this document used to be produced only when the tray
        /// timer fired, so a headless agent, which is what --watch and an SSH
        /// session are, published nothing at all.
        volatile string _status = "{}";

        public Mode Mode = Mode.Auto;
        public event Action<WorkerState, string> StateChanged;
        public Action<string> Log = delegate(string s) { };

        /// Non-null while a service's provisioning script is downloading, carrying
        /// its own progress line. The tray shows this instead of a state, in its
        /// own colour, because a grey icon during a multi gigabyte first run reads
        /// as broken and gets killed halfway.
        public string ProvisionStatus
        {
            get
            {
                try
                {
                    if (!File.Exists(_c.ProvisionStatusPath)) return null;
                    string t = File.ReadAllText(_c.ProvisionStatusPath).Trim();
                    return t.Length == 0 ? null : t;
                }
                catch (Exception) { return null; }
            }
        }

        public Agent(Config c)
        {
            _c = c;
            _gpu = new GpuMonitor(120);
            _policy = new Policy(c);
            foreach (ServiceDef s in c.Services)
            {
                // KNOWN is not REGISTERED. A section in worker.ini makes a service
                // known; being opted in AND having finished its provisioning makes
                // it registered. An enabled service whose 6 GB download never
                // finished must not get a queue directory clients can post into,
                // because a lease sitting in a queue nothing will ever claim looks
                // to the client exactly like a busy GPU. GET /v1/services still
                // reports it, with installed:false, which is the answer that tells
                // the caller it is the owner's to fix.
                if (!s.Enabled || !Install.IsInstalled(s)) continue;
                _services.Add(s);
                _jobs[s.Id] = new JobRunner(s, c, delegate(string m) { Log(m); Append(m); });
                var q = new JobStore(s.Id, s.QueueDir);
                try { q.EnsureDirs(); }
                catch (Exception ex) { Log("cannot create queue for " + s.Id + ": " + ex.Message); }
                _queues[s.Id] = q;
            }
            try { Mode = (Mode)Enum.Parse(typeof(Mode), c.StartMode, true); }
            catch (Exception) { Mode = Mode.Auto; }
        }

        public Config Config { get { return _c; } }
        public Policy Policy { get { return _policy; } }
        public Snapshot Current { get { lock (_lock) { return _snap; } } }
        public int CounterCostMs { get { lock (_lock) { return _lastCounterCostMs; } } }
        public string PublishedStatus { get { return _status; } }
        public List<ServiceDef> Services { get { return _services; } }

        /// Is that service's controller alive right now.
        ///
        /// Read off the JobRunner, which is a HasExited check on a Process object
        /// and takes no lock. The listener calls this per request and must never
        /// touch _lock; see the header of this file.
        public bool IsRunning(string serviceId)
        {
            JobRunner j;
            return _jobs.TryGetValue(serviceId, out j) && j.Running;
        }

        public JobStore Queue(string serviceId)
        {
            JobStore q;
            return _queues.TryGetValue(serviceId, out q) ? q : null;
        }

        public JobRunner Runner(string serviceId)
        {
            JobRunner j;
            return _jobs.TryGetValue(serviceId, out j) ? j : null;
        }

        public bool JobRunning
        {
            get
            {
                foreach (JobRunner j in _jobs.Values) if (j.Running) return true;
                return false;
            }
        }

        public string RunningService
        {
            get
            {
                foreach (KeyValuePair<string, JobRunner> kv in _jobs)
                    if (kv.Value.Running) return kv.Key;
                return null;
            }
        }

        // Aggregates, so the tray and the status document keep saying one number
        // for the whole runner rather than one per service.
        public int JobStarts { get { int n = 0; foreach (JobRunner j in _jobs.Values) n += j.Starts; return n; } }
        public int Yields { get { int n = 0; foreach (JobRunner j in _jobs.Values) n += j.Yields; return n; } }

        public int LastYieldMs
        {
            get
            {
                int best = -1;
                foreach (JobRunner j in _jobs.Values) if (j.LastYieldMs >= 0) best = j.LastYieldMs;
                return best;
            }
        }

        public bool LastYieldWasKill
        {
            get
            {
                foreach (JobRunner j in _jobs.Values) if (j.LastYieldMs >= 0) return j.LastYieldWasKill;
                return false;
            }
        }

        /// Queue a control message from the network. Bounded, and it says no
        /// rather than growing: an unbounded queue drained once per tick is a
        /// memory leak with a network interface on it.
        public bool Submit(Command cmd)
        {
            if (Interlocked.Increment(ref _commandDepth) > 64)
            {
                Interlocked.Decrement(ref _commandDepth);
                return false;
            }
            _commands.Enqueue(cmd);
            return true;
        }

        public void Start()
        {
            _gpu.Start();
            _fast = new Thread(FastLoop); _fast.IsBackground = true; _fast.Start();
            _slow = new Thread(SlowLoop); _slow.IsBackground = true; _slow.Start();
            _sched = new Thread(SchedLoop); _sched.IsBackground = true; _sched.Start();
        }

        void SlowLoop()
        {
            while (!_stop)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    // Raw, unfiltered. The threshold, the allowlist and the own-job
                    // exemption used to be applied here, which put three
                    // consequential judgements outside the module that holds the
                    // decisions and outside anything a test could reach. They live
                    // in Policy.ForeignVram now; this loop just reads the counter.
                    List<ProcessGpuUse> all = _vram.Read();
                    Dictionary<string, double> eng = _engines.ReadByEngineType();
                    lock (_lock)
                    {
                        _lastVram = all;
                        _lastEngines = eng;
                        _countersAt = DateTime.UtcNow;
                        _lastCounterCostMs = (int)sw.ElapsedMilliseconds;
                    }
                }
                catch (Exception ex) { Log("counter sample failed: " + ex.Message); }
                // Written here rather than on _fast so that publishing the status
                // is never a disk write on the yield path.
                WriteState();
                Sleep(_c.CounterPollMs);
            }
        }

        void FastLoop()
        {
            WorkerState prev = WorkerState.Blocked;
            while (!_stop)
            {
                try
                {
                    DrainCommands();

                    var s = new Snapshot();
                    s.At = DateTime.UtcNow;
                    s.Gpu = _gpu.Latest;
                    s.GpuHealthy = _gpu.Healthy;
                    s.Session = Win.Read();
                    s.Launchers = Launchers.Read(_c.GameProcessNames, _c.AntiCheatServices);

                    // WHAT SESSION 0 CANNOT SEE, SUPPLIED BY SOMEBODY WHO CAN.
                    // Only ever an overlay, and only when this agent is outside
                    // the console session: in the console session the local reads
                    // are the truth and a helper could only make them worse.
                    //
                    // The launcher fields are merged rather than replaced. Process
                    // scanning and the anti-cheat service check work perfectly
                    // well from session 0 and are already in s.Launchers; it is
                    // Steam's running app id that is missing, because it lives in
                    // the user's registry hive. Replacing the whole object would
                    // throw away two working vetoes to gain one.
                    if (!s.Session.RunningInConsoleSession)
                    {
                        PresenceReport pr = null;
                        lock (_lock)
                        {
                            if (_presence != null
                                && (DateTime.UtcNow - _presenceAt) < PresenceTtl)
                                pr = _presence;
                        }
                        if (pr != null)
                        {
                            s.Session.PresenceFresh = true;
                            s.Session.InputIdleSeconds = pr.InputIdleSeconds;
                            s.Session.Locked = pr.Locked;
                            s.Session.ForegroundIsFullScreen = pr.ForegroundIsFullScreen;
                            s.Session.ForegroundProcess = pr.ForegroundProcess;
                            if (s.Launchers != null && pr.SteamRunningAppId != 0)
                            {
                                s.Launchers.SteamRunningAppId = pr.SteamRunningAppId;
                                s.Launchers.SteamRunningAppName = pr.SteamRunningAppName;
                            }
                        }
                    }
                    lock (_lock)
                    {
                        s.GpuProcesses = _lastVram;
                        s.CountersFresh = (DateTime.UtcNow - _countersAt) < TimeSpan.FromSeconds(30);
                        double d;
                        s.Util3d = _lastEngines.TryGetValue("3d", out d) ? d : 0;
                        s.UtilVideoDecode = _lastEngines.TryGetValue("videodecode", out d) ? d : 0;
                        s.UtilVideoEncode = _lastEngines.TryGetValue("videoencode", out d) ? d : 0;
                    }

                    // Our own controllers are never evidence of a user. Read here
                    // rather than in the policy because the policy must stay free
                    // of anything that touches a process handle. Every one of them,
                    // not just the newest: during a handoff two are alive, and an
                    // unexempted second controller reads as a game and makes the
                    // agent yield to itself.
                    foreach (JobRunner j in _jobs.Values)
                    {
                        int pid = j.Pid;
                        if (pid > 0) s.OwnJobPids.Add(pid);
                    }

                    Verdict v = _policy.Evaluate(s);
                    lock (_lock) { _snap = s; }

                    bool can = _policy.CanRun(Mode);
                    if (!can)
                    {
                        string why = Mode == Mode.Off ? "switched off" : v.ReasonText;
                        foreach (JobRunner j in _jobs.Values)
                        {
                            if (!j.Running || j.Stopping) continue;
                            JobRunner target = j;
                            // Off the sampling thread, deliberately. Stop() waits up
                            // to the grace period for the child to go; called inline
                            // it blocked this loop for that whole time, so no sample
                            // was taken and the tray showed stale state during
                            // exactly the seconds whose latency this program exists
                            // to measure.
                            ThreadPool.QueueUserWorkItem(delegate(object ignored)
                            {
                                try { target.Stop(why); } catch (Exception) { }
                            });
                        }
                    }

                    // Publish, with no lock held and no disk touched. The listener
                    // hands this exact string to every caller of GET /v1/status.
                    _status = BuildStatus(s, v, can);

                    if (_policy.State != prev)
                    {
                        prev = _policy.State;
                        // Every transition, with its reason and the mode it happened
                        // under. This is the whole evidence base for the false-idle
                        // question, so it is written even in Off mode: a week of
                        // Off-mode transitions is what shows the detector is
                        // trustworthy before it is ever allowed to take the GPU.
                        Append(string.Format(CultureInfo.InvariantCulture,
                            "state {0} mode={1}{2} reason={3}",
                            Policy.Describe(_policy.State), Mode,
                            _policy.IsOverriding(Mode) ? " OVERRIDDEN" : "",
                            v.ReasonText));
                        var h = StateChanged;
                        if (h != null) h(_policy.State, v.ReasonText);
                    }
                }
                catch (Exception ex) { Log("sample failed: " + ex.Message); }
                Sleep(_c.FastPollMs);
            }
        }

        void DrainCommands()
        {
            Command cmd;
            while (_commands.TryDequeue(out cmd))
            {
                Interlocked.Decrement(ref _commandDepth);
                try
                {
                    if (cmd.Kind == "mode")
                    {
                        Mode m;
                        try { m = (Mode)Enum.Parse(typeof(Mode), cmd.Value, true); }
                        catch (Exception) { continue; }
                        if (m == Mode) continue;
                        Mode = m;
                        Append("mode set to " + Mode + " over the API");
                        SaveMode();
                    }
                }
                catch (Exception) { }
            }
        }

        /// Which service gets the GPU.
        ///
        /// ONE GPU MEANS ONE CONTROLLER. The rule is deliberately the simplest one
        /// that is defensible: start nothing unless the policy says the machine is
        /// ours and nothing of ours is already running, then start the highest
        /// priority service that has queued work. There is no preemption, so a long
        /// Hashcat run starves a queued speech job until it finishes or the user
        /// takes the machine back. That is a stated limitation, not an oversight,
        /// and the mitigation lives in the controller contract rather than here: a
        /// controller is asked to exit once its own queue has been empty for a
        /// while, which hands the GPU back to the scheduler without any preemption
        /// machinery existing at all.
        void SchedLoop()
        {
            while (!_stop)
            {
                try
                {
                    foreach (JobRunner j in _jobs.Values)
                        if (!j.Running) j.ReapHandle();

                    // Rescue leases whose controller was killed mid-unit. See
                    // JobStore.RequeueOrphans: without this the job and the
                    // scheduler wait for each other for ever. Only for services
                    // with no live controller, and only on this thread.
                    foreach (ServiceDef s in _services)
                    {
                        JobRunner j = _jobs[s.Id];
                        if (j.Running || j.Stopping) continue;
                        int moved = _queues[s.Id].RequeueOrphans();
                        if (moved > 0)
                            Log(s.Id + ": requeued " + moved.ToString(CultureInfo.InvariantCulture) +
                                " job(s) whose controller was killed before it could put them back");
                    }

                    if (_policy.CanRun(Mode) && !JobRunning)
                    {
                        ServiceDef best = null;
                        foreach (ServiceDef s in _services)
                        {
                            JobRunner j = _jobs[s.Id];
                            if (j.Stopping) { best = null; break; }
                            JobStore q = _queues[s.Id];
                            if (q.QueuedCount() <= 0) continue;
                            if (best == null || s.Priority > best.Priority) best = s;
                        }
                        if (best != null) _jobs[best.Id].Start();
                    }
                }
                catch (Exception ex) { Log("scheduler: " + ex.Message); }
                Sleep(_c.SchedulerPollMs);
            }
        }

        void Sleep(int ms)
        {
            // Chopped so shutdown is responsive without an event handle.
            int left = ms;
            while (left > 0 && !_stop) { int n = Math.Min(200, left); Thread.Sleep(n); left -= n; }
        }

        /// Build the status document from a snapshot already in hand.
        ///
        /// Takes the snapshot as an argument rather than reading Current, so that
        /// the one caller on the yield path builds it without touching _lock.
        public string BuildStatus(Snapshot s, Verdict v, bool can)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append(Json.P("timestamp", Json.Esc(s.At.ToString("o", CultureInfo.InvariantCulture)))).Append(",");
            sb.Append(Json.P("mode", Json.Esc(Mode.ToString()))).Append(",");
            sb.Append(Json.P("state", Json.Esc(Policy.Describe(_policy.State)))).Append(",");
            sb.Append(Json.P("can_run", can ? "true" : "false")).Append(",");
            sb.Append(Json.P("job_running", JobRunning ? "true" : "false")).Append(",");
            sb.Append(Json.P("running_service", RunningService == null ? "null" : Json.Esc(RunningService))).Append(",");
            sb.Append(Json.P("seconds_until_available", Json.Num(_policy.SecondsUntilAvailable))).Append(",");
            sb.Append(Json.P("reason", Json.Esc(v.ReasonText))).Append(",");
            sb.Append(Json.P("veto", v.IsVeto ? "true" : "false")).Append(",");
            // Always-on runs jobs regardless of the verdict. It must still PUBLISH
            // the verdict it is overriding, otherwise switching to Always-on
            // silently destroys the false-idle evidence being collected.
            sb.Append(Json.P("overriding", _policy.IsOverriding(Mode) ? "true" : "false")).Append(",");
            sb.Append(Json.P("job_starts", Json.Num(JobStarts))).Append(",");
            sb.Append(Json.P("yields", Json.Num(Yields))).Append(",");
            sb.Append(Json.P("last_yield_ms", Json.Num(LastYieldMs))).Append(",");
            sb.Append(Json.P("last_yield_was_kill", LastYieldWasKill ? "true" : "false")).Append(",");

            sb.Append("\"services\":[");
            for (int i = 0; i < _services.Count; i++)
            {
                ServiceDef d = _services[i];
                JobRunner j = _jobs[d.Id];
                JobStore q = _queues[d.Id];
                if (i > 0) sb.Append(",");
                sb.Append(Json.Obj(
                    Json.P("id", Json.Esc(d.Id)),
                    Json.P("priority", Json.Num(d.Priority)),
                    Json.P("running", j.Running ? "true" : "false"),
                    Json.P("pid", Json.Num(j.Pid)),
                    Json.P("queued", Json.Num(q.QueuedCount())),
                    Json.P("starts", Json.Num(j.Starts)),
                    Json.P("yields", Json.Num(j.Yields)),
                    Json.P("last_yield_ms", Json.Num(j.LastYieldMs))));
            }
            sb.Append("],");

            sb.Append("\"gpu\":");
            if (s.Gpu != null && s.Gpu.Valid)
            {
                sb.Append(Json.Obj(
                    Json.P("healthy", s.GpuHealthy ? "true" : "false"),
                    Json.P("utilisation_pct", Json.Num(s.Gpu.UtilGpu)),
                    Json.P("memory_utilisation_pct", Json.Num(s.Gpu.UtilMem)),
                    Json.P("encoder_pct", Json.Num(s.Gpu.UtilEncoder)),
                    Json.P("decoder_pct", Json.Num(s.Gpu.UtilDecoder)),
                    Json.P("memory_clock_mhz", Json.Num(s.Gpu.ClockMemMhz)),
                    Json.P("sm_clock_mhz", Json.Num(s.Gpu.ClockSmMhz)),
                    Json.P("pstate", Json.Esc(s.Gpu.PState)),
                    Json.P("power_watts", Json.Num(s.Gpu.PowerWatts)),
                    Json.P("memory_used_mib", Json.Num(s.Gpu.MemUsedMiB))));
            }
            else sb.Append("null");
            sb.Append(",");

            sb.Append("\"engines\":").Append(Json.Obj(
                Json.P("three_d_pct", Json.Num(s.Util3d)),
                Json.P("video_decode_pct", Json.Num(s.UtilVideoDecode)),
                Json.P("video_encode_pct", Json.Num(s.UtilVideoEncode)),
                Json.P("fresh", s.CountersFresh ? "true" : "false"),
                Json.P("sample_cost_ms", Json.Num(_lastCounterCostMs)))).Append(",");

            sb.Append("\"session\":");
            if (s.Session != null)
            {
                sb.Append(Json.Obj(
                    Json.P("own_session", Json.Num(s.Session.OwnSessionId)),
                    Json.P("console_session", Json.Num(s.Session.ConsoleSessionId)),
                    Json.P("in_console_session", s.Session.RunningInConsoleSession ? "true" : "false"),
                    Json.P("locked", s.Session.Locked ? "true" : "false"),
                    Json.P("input_idle_seconds", Json.Num(s.Session.InputIdleSeconds)),
                    Json.P("foreground_process", Json.Esc(s.Session.ForegroundProcess)),
                    Json.P("foreground_fullscreen", s.Session.ForegroundIsFullScreen ? "true" : "false")));
            }
            else sb.Append("null");
            sb.Append(",");

            sb.Append("\"launchers\":");
            if (s.Launchers != null)
            {
                sb.Append(Json.Obj(
                    Json.P("steam_running_appid", Json.Num(s.Launchers.SteamRunningAppId)),
                    Json.P("steam_running_app", Json.Esc(s.Launchers.SteamRunningAppName)),
                    Json.P("anti_cheat_running", s.Launchers.ValorantAntiCheatActive ? "true" : "false"),
                    Json.P("game_processes", "[" + string.Join(",",
                        s.Launchers.GameProcesses.Select(Json.Esc).ToArray()) + "]")));
            }
            else sb.Append("null");
            sb.Append(",");

            // Only the processes the POLICY judged foreign, not the raw counter
            // read. The raw list is fifteen shell processes on an idle desktop and
            // burying the one that matters in it would defeat the point of the
            // document, which is that a human can read it and see why.
            List<ProcessGpuUse> foreign = _policy.ForeignVram(s);
            sb.Append("\"foreign_vram\":[");
            for (int i = 0; i < foreign.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Obj(
                    Json.P("pid", Json.Num(foreign[i].Pid)),
                    Json.P("name", Json.Esc(foreign[i].Name)),
                    Json.P("dedicated_mib", Json.Num(foreign[i].DedicatedMiB))));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// The status document, for a caller outside the loop. Prefers the string
        /// _fast published and only builds one if the loop has not ticked yet,
        /// which is the case for --once.
        public string StatusJson()
        {
            if (_status != null && _status.Length > 2) return _status;
            Snapshot s = Current;
            return BuildStatus(s, _policy.Last, _policy.CanRun(Mode));
        }

        /// Every registered service, with the manifest its controller published.
        ///
        /// THE MANIFEST IS THE CAPABILITY ADVERTISEMENT AND THE AGENT DOES NOT
        /// WRITE IT. A controller publishes service.json into its own queue
        /// directory the first time it runs, and this passes it through untouched.
        /// That is what makes adding a service a matter of writing a controller:
        /// the generic layer learns what the new thing can do from the new thing,
        /// not from a recompile.
        /// EVERY KNOWN SERVICE, not every running one.
        ///
        /// The list is built from the config rather than from what got registered,
        /// so a service that is known but not installed appears here with
        /// installed:false instead of vanishing. A client that cannot see the
        /// difference between "this runner has no speech service" and "this runner's
        /// speech service is not installed yet" has no way to tell its user which of
        /// those two things to do about it.
        ///
        /// `gpu_available` is reported ONCE, at the top, next to the reason, because
        /// it is a property of the machine and not of any service. Repeating it per
        /// service is how the two get conflated.
        public string ServicesJson()
        {
            bool can = _policy.CanRun(Mode);
            bool overriding = _policy.IsOverriding(Mode);
            var sb = new StringBuilder();
            sb.Append("{");
            // gpu_available answers "will this runner start work right now", which
            // is the question a client actually has. gpu_state answers "what does
            // the policy think the machine is doing". THEY CAN DISAGREE, and the
            // disagreement is not a bug: in AlwaysOn the operator has said to run
            // anyway, so available is true while the state is Busy or Blocked.
            // `overriding` is what makes that legible instead of looking like a
            // contradiction, and it is also the flag a well behaved client should
            // show a human, because it means somebody's game is being slowed down
            // on purpose.
            sb.Append(Json.P("gpu_available", can ? "true" : "false")).Append(",");
            sb.Append(Json.P("gpu_state", Json.Esc(Policy.Describe(_policy.State)))).Append(",");
            sb.Append(Json.P("overriding", overriding ? "true" : "false")).Append(",");
            sb.Append(Json.P("mode", Json.Esc(Mode.ToString()))).Append(",");
            sb.Append(Json.P("seconds_until_available",
                Json.Num(can ? 0 : _policy.SecondsUntilAvailable))).Append(",");
            sb.Append("\"services\":[");
            List<ServiceDef> known = _c.Services;
            for (int i = 0; i < known.Count; i++)
            {
                ServiceDef d = known[i];
                if (i > 0) sb.Append(",");
                JobRunner j; JobStore q;
                bool registered = _jobs.TryGetValue(d.Id, out j);
                _queues.TryGetValue(d.Id, out q);
                ServiceStatus st = Install.Status(d, false);
                st.DiskBytes = CachedDiskBytes(d);
                sb.Append(Install.Json(d, st,
                    registered && j.Running,
                    q == null ? 0 : q.QueuedCount(),
                    q == null ? null : q.Manifest(_c.MaxPassthroughBytes),
                    d.YieldGraceSeconds > 0 ? d.YieldGraceSeconds : _c.YieldGraceSeconds));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        readonly Dictionary<string, long> _diskBytes = new Dictionary<string, long>();
        readonly Dictionary<string, DateTime> _diskAt = new Dictionary<string, DateTime>();
        readonly object _diskLock = new object();

        /// What a service is using on disk, measured but not on every request.
        ///
        /// Walking a six gigabyte directory tree is tens of thousands of syscalls,
        /// and GET /v1/services is a thing pollers poll. A minute of staleness in a
        /// number that only changes when somebody installs or removes something is
        /// not staleness anybody can perceive.
        ///
        /// _diskLock is its OWN lock and is never _lock. Nothing on the yield path
        /// touches either this dictionary or this lock, which is the invariant that
        /// keeps a listener incapable of delaying a yield.
        long CachedDiskBytes(ServiceDef d)
        {
            lock (_diskLock)
            {
                DateTime at;
                long bytes;
                if (_diskAt.TryGetValue(d.Id, out at) &&
                    (DateTime.UtcNow - at).TotalSeconds < 60 &&
                    _diskBytes.TryGetValue(d.Id, out bytes))
                    return bytes;
            }
            long measured = Install.DiskBytes(d);
            lock (_diskLock)
            {
                _diskBytes[d.Id] = measured;
                _diskAt[d.Id] = DateTime.UtcNow;
            }
            return measured;
        }

        /// Append one timestamped line to worker.log.
        void Append(string line)
        {
            try
            {
                string dir = Path.GetDirectoryName(_c.LogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_c.LogPath,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public void LogLine(string line) { Log(line); Append(line); }

        /// Persist the mode back into worker.ini, rewriting the StartMode line in
        /// place if it is there and appending it if it is not. Deliberately leaves
        /// every other line, comment and blank alone: this file is one a human
        /// edits, and a settings writer that reformats somebody's comments out of
        /// existence is a settings writer they stop using.
        public void SaveMode()
        {
            try
            {
                if (string.IsNullOrEmpty(_c.ConfigPath)) return;
                var lines = new List<string>();
                bool replaced = false;
                if (File.Exists(_c.ConfigPath))
                {
                    foreach (string raw in File.ReadAllLines(_c.ConfigPath))
                    {
                        string t = raw.Trim();
                        if (!t.StartsWith("#") && !t.StartsWith(";") &&
                            t.ToLowerInvariant().Replace(" ", "").StartsWith("startmode="))
                        {
                            lines.Add("StartMode = " + Mode);
                            replaced = true;
                        }
                        else lines.Add(raw);
                    }
                }
                if (!replaced) lines.Add("StartMode = " + Mode);
                File.WriteAllLines(_c.ConfigPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// state.json, next to the agent, for anything on the box that would rather
        /// read a file than open a socket: a scheduled task, a status bar, a human
        /// with `type`. It is the same document the API serves, written from the
        /// same published string, so the two can never disagree.
        public void WriteState()
        {
            try
            {
                string dir = Path.GetDirectoryName(_c.StatePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = _c.StatePath + ".tmp";
                File.WriteAllText(tmp, _status, new UTF8Encoding(false));
                if (File.Exists(_c.StatePath)) File.Delete(_c.StatePath);
                File.Move(tmp, _c.StatePath);   // atomic-enough rename; no torn reads
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _stop = true;
            foreach (JobRunner j in _jobs.Values)
            {
                try { j.Stop("agent shutting down"); } catch (Exception) { }
                try { j.Dispose(); } catch (Exception) { }
            }
            try { _gpu.Dispose(); } catch (Exception) { }
        }
    }
}
