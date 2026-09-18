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
        [DllImport("kernel32.dll", SetLastError = true)]
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

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime,
                        ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        /// The union in JOBOBJECT_CPU_RATE_CONTROL_INFORMATION is one DWORD in
        /// every mode - CpuRate, or Weight, or MinRate and MaxRate packed as two
        /// WORDs - so one struct covers all of them and there is no need for
        /// explicit layout that C# 5 would make ugly.
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint Value; }

        const int JobObjectExtendedLimitInformation = 9;
        const int JobObjectBasicProcessIdList = 3;
        const int JobObjectBasicAccountingInformation = 1;
        const int JobObjectCpuRateControlInformation = 15;

        // Vertical, not horizontal. CpuRate is a proportion of the WHOLE MACHINE
        // expressed in hundredths of a per cent, measured on spring: a 50 per cent
        // cap on sixteen spinning threads produced 50.2 per cent of the machine,
        // which is 8.03 of its 16 logical processors. It says HOW MUCH, never
        // WHICH CORES, which is the distinction this whole feature turns on.
        //
        // JOB_OBJECT_LIMIT_AFFINITY is deliberately not used and not defined here.
        // Pinning to a subset of cores is the horizontal answer, and on a Ryzen
        // with a single CCD it also fights the scheduler's cache-aware placement.
        const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
        const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001;
        const uint JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x00000020;

        const uint IDLE_PRIORITY_CLASS = 0x00000040;
        const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        const uint NORMAL_PRIORITY_CLASS = 0x00000020;

        const int ERROR_PRIVILEGE_NOT_HELD = 1314;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
            IntPtr info, uint len, out uint returned);
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

        /// The limits currently written into the kernel for this job, so that a
        /// tick which changes nothing costs nothing. SetInformationJobObject on
        /// every tick would work; it would also be a syscall per service per
        /// second on the thread that must never wait for anything.
        ResourceLimits _applied;

        /// True once JOB_OBJECT_LIMIT_WORKINGSET has been refused for want of a
        /// privilege, so it is not retried every tick and so the tray can say why
        /// the memory cap is not in force.
        ///
        /// MEASURED, and it is the opposite way round from the documentation's
        /// warning. The docs attach SE_INC_BASE_PRIORITY_NAME to
        /// JOB_OBJECT_LIMIT_PRIORITY_CLASS. With that privilege removed from the
        /// token on spring, setting IDLE_PRIORITY_CLASS through the job still
        /// SUCCEEDED - lowering a priority never needed it - and it was the
        /// WORKING SET limit that failed, with 1314, ERROR_PRIVILEGE_NOT_HELD.
        /// The agent runs as SYSTEM from the boot task and has it; somebody
        /// running the tray by hand as an ordinary user does not, and gets the
        /// CPU cap and the priority without the memory cap rather than nothing.
        public bool WorkingSetDenied;
        public string LastLimitError = "";

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

        /// EVERY process in this job, not just the one we launched.
        ///
        /// The controller is a script; the thing that actually puts 2,249 MiB on
        /// the card is a python CHILD of it with a different pid. Exempting only
        /// the controller meant that child read as a foreign process holding GPU
        /// memory, the VRAM veto fired, and the agent killed its own job -- then
        /// cooled down for ninety seconds and did it again. Measured: four
        /// restarts in a row on a locked desktop with nobody near the machine.
        ///
        /// Read from the JOB OBJECT rather than by walking ParentProcessId. Every
        /// descendant is already assigned to it -- that is how KillTree works --
        /// so this is exact, needs no second enumeration of every process on the
        /// machine, and cannot be defeated by a process whose parent has exited.
        public List<int> Pids
        {
            get
            {
                var found = new List<int>();
                int own = Pid;
                if (own > 0) found.Add(own);
                if (_job == IntPtr.Zero) return found;

                // Two IntPtr-sized counts, then the ids. Sized for plenty of
                // children; a job larger than this returns what fits, and the
                // controller itself is already in the list either way.
                int slots = 256;
                int bytes = (IntPtr.Size * 2) + (IntPtr.Size * slots);
                IntPtr buf = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.WriteIntPtr(buf, 0, new IntPtr(slots));
                    Marshal.WriteIntPtr(buf, IntPtr.Size, IntPtr.Zero);
                    uint got;
                    if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList,
                                                   buf, (uint)bytes, out got))
                        return found;
                    int n = Marshal.ReadIntPtr(buf, IntPtr.Size).ToInt32();
                    if (n > slots) n = slots;
                    for (int i = 0; i < n; i++)
                    {
                        int pid = Marshal.ReadIntPtr(buf, (IntPtr.Size * 2) + (i * IntPtr.Size)).ToInt32();
                        if (pid > 0 && !found.Contains(pid)) found.Add(pid);
                    }
                }
                catch (Exception) { }
                finally { Marshal.FreeHGlobal(buf); }
                return found;
            }
        }

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

        /// Total CPU time this job's whole process tree has burned, in 100 ns
        /// ticks, or 0 when there is no job.
        ///
        /// THIS IS WHAT MAKES THE CPU SIGNAL HONEST. A whole-machine CPU reading
        /// has no owner attached to it, so without this the agent reads its own
        /// load as somebody else's and yields to itself - the exact defect the GPU
        /// tier 2 had to be patched around by suppressing its load votes. A job
        /// object accounts for every process assigned to it, including children
        /// the controller spawned, so subtracting this from the machine total is
        /// arithmetic rather than a guess.
        public long CpuTime100ns
        {
            get
            {
                if (_job == IntPtr.Zero) return 0;
                int len = Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    uint got;
                    if (!QueryInformationJobObject(_job, JobObjectBasicAccountingInformation,
                                                   buf, (uint)len, out got)) return 0;
                    var a = (JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)
                        Marshal.PtrToStructure(buf, typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                    return a.TotalUserTime + a.TotalKernelTime;
                }
                catch (Exception) { return 0; }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        public ResourceLimits AppliedLimits { get { return _applied; } }

        /// How many threads the controller should ask its framework for, set by
        /// the agent from the cap in force just before Start.
        ///
        /// WHY THE CHILD NEEDS TELLING AT ALL, given the cap is enforced by the
        /// kernel. Because the cap is a HARD CAP: once the job has spent its share
        /// of a scheduling interval, no thread in it runs until the next one. A
        /// torch process that defaults to one thread per core then has sixteen
        /// threads taking turns inside a tenth of the machine, which finishes no
        /// sooner than four would and evicts far more of the owner's cache on the
        /// way. The cap decides how much; this decides how thinly it is spread.
        public int CpuThreads;

        /// The thread count the child has been told about, so a tick that changes
        /// nothing writes nothing to a pipe the child may not be reading yet.
        int _threadsTold = -1;

        /// Tell a RUNNING controller how thinly to spread itself.
        ///
        /// THE GAP THIS CLOSES, and it was the one thing in this design that did
        /// not bind live. IDLEGPU_CPU_THREADS is an environment variable, so it is
        /// frozen at Process.Start: a job started while nobody was signed in came
        /// up with a thread per core, and when the owner sat down and the cap fell
        /// to ten per cent the kernel squeezed it correctly while the job carried
        /// on with sixteen threads inside 1.6 cores of budget. That is the worst
        /// configuration a hard cap can be given - every thread spends most of its
        /// life descheduled and the ones that do run thrash the cache the owner is
        /// using - so the cap was doing its job and the throughput cost was far
        /// worse than the cap alone would explain.
        ///
        /// The channel already exists. stdin carries YIELD; one more verb costs
        /// nothing and needs no second pipe, no socket and no file. A controller
        /// that does not understand THREADS ignores an unknown line, which is what
        /// the older ones already do, so this is safe against a mixed install.
        ///
        /// NOT WRITTEN WHILE STOPPING. Stop() closes stdin as its EOF signal, and
        /// writing to a closed pipe after that would raise on the sampling thread.
        public void TellThreads(int n)
        {
            if (n <= 0 || n == _threadsTold) return;
            if (_stopping || !Running) return;
            try
            {
                _proc.StandardInput.WriteLine("THREADS " + n.ToString(CultureInfo.InvariantCulture));
                _proc.StandardInput.Flush();
                _threadsTold = n;
                CpuThreads = n;
            }
            catch (Exception) { /* the child is going away; the job object still holds the cap */ }
        }

        /// Push a row of the matrix into the kernel, on a LIVE job.
        ///
        /// THE TRAY REQUIREMENT LIVES OR DIES HERE. A limit that could only be set
        /// when a job started, or that needed a restart to move, would make the
        /// settings window a lie: the owner would change a number, see the menu
        /// update, and the machine would carry on exactly as before until the next
        /// job. Every mechanism used below was checked against that on spring
        /// before it was chosen:
        ///
        ///   CPU rate control   set on a running job, the call returned in under a
        ///                      millisecond, and the job was measured under 10 per
        ///                      cent within the first 200 ms sampling window after
        ///                      dropping the cap from 80 per cent to 5. Raising it
        ///                      back to 90 was measured over 50 per cent again
        ///                      within 828 ms. Clearing it entirely with
        ///                      ControlFlags = 0 returned the job to 97.4 per cent.
        ///   priority class     set on a running job through the job object, which
        ///                      covers children the controller has already spawned.
        ///   working set        set and cleared on a running job.
        ///
        /// SO THE CPU PROMISE IS: one sampling tick to notice, plus about 200 ms
        /// for the cap to bind. Under one and a half seconds from the owner
        /// touching the keyboard to the job being throttled, and unlike the GPU it
        /// costs nothing, because the job is squeezed rather than killed and there
        /// is no model to load again afterwards.
        ///
        /// Idempotent. Returns true when it actually wrote something.
        public bool ApplyLimits(ResourceLimits want)
        {
            if (want == null || _job == IntPtr.Zero) return false;
            if (_applied != null && _applied.SameAs(want)) return false;

            // --- the cap -------------------------------------------------------
            //
            // The one decision in here is CpuRate.For, in Model.cs, so that it can
            // be tested without a job object. It carries the defect it was
            // extracted to fix: a row of zero used to fall through this branch with
            // a zeroed struct, and a zeroed struct CLEARS the cap, so a job on its
            // way out ran UNCAPPED for the whole grace period at the exact moment a
            // game started.
            var rate = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
            uint flags, value;
            CpuRate.For(want.CpuPct, out flags, out value);
            rate.ControlFlags = flags;
            rate.Value = value;
            SetJobInfo(JobObjectCpuRateControlInformation, rate, "cpu rate");

            // --- priority and the working set ----------------------------------
            //
            // One call, because JOBOBJECT_EXTENDED_LIMIT_INFORMATION carries both
            // and a second call would clobber the first: LimitFlags is the whole
            // set of limits in force, not a delta, so KILL_ON_JOB_CLOSE has to be
            // written every time or the job stops killing its tree.
            var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                                 | JOB_OBJECT_LIMIT_PRIORITY_CLASS;
            ext.BasicLimitInformation.PriorityClass = PriorityValue(want.Priority);
            bool wantWs = want.WorkingSetMib > 0 && !WorkingSetDenied;
            if (wantWs)
            {
                ext.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_WORKINGSET;
                // A minimum is mandatory when a maximum is set, and it must not be
                // zero. An eighth of the maximum leaves the job enough resident to
                // make progress while the trimming still bites.
                ulong maxBytes = (ulong)want.WorkingSetMib * 1048576UL;
                ulong minBytes = maxBytes / 8UL;
                if (minBytes < 16UL * 1048576UL) minBytes = 16UL * 1048576UL;
                if (minBytes >= maxBytes) minBytes = maxBytes / 2UL;
                ext.BasicLimitInformation.MinimumWorkingSetSize = new UIntPtr(minBytes);
                ext.BasicLimitInformation.MaximumWorkingSetSize = new UIntPtr(maxBytes);
            }
            if (!SetJobInfo(JobObjectExtendedLimitInformation, ext, "priority/working set")
                && wantWs && _lastSetErr == ERROR_PRIVILEGE_NOT_HELD)
            {
                // Degrade, and say so, rather than losing the priority too. An
                // ordinary user's token has no SeIncreaseBasePriorityPrivilege, so
                // the memory cap is not available to them; the CPU cap and the
                // priority are, and they are the larger half.
                WorkingSetDenied = true;
                LastLimitError = "the working set cap needs SeIncreaseBasePriorityPrivilege, "
                               + "which this account does not have; the CPU cap and priority are still in force";
                _log(_svc.Id + ": " + LastLimitError);
                var noWs = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                noWs.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                                      | JOB_OBJECT_LIMIT_PRIORITY_CLASS;
                noWs.BasicLimitInformation.PriorityClass = PriorityValue(want.Priority);
                SetJobInfo(JobObjectExtendedLimitInformation, noWs, "priority");
            }

            _applied = want.Clone();
            return true;
        }

        static uint PriorityValue(string p)
        {
            string t = Config.NormalisePriority(p);
            if (t == "normal") return NORMAL_PRIORITY_CLASS;
            if (t == "belownormal") return BELOW_NORMAL_PRIORITY_CLASS;
            return IDLE_PRIORITY_CLASS;
        }

        int _lastSetErr;

        /// The Win32 error is captured INSIDE this method, before the FreeHGlobal
        /// in the finally block. GetLastError is per thread and any intervening
        /// call can overwrite it, so reading it at the call site is a race that
        /// shows up as the working set limit failing for an error code that was
        /// really the allocator's.
        bool SetJobInfo<T>(int infoClass, T value, string what)
        {
            int len = Marshal.SizeOf(typeof(T));
            IntPtr p = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                if (SetInformationJobObject(_job, infoClass, p, (uint)len)) { _lastSetErr = 0; return true; }
                _lastSetErr = Marshal.GetLastWin32Error();
                return false;
            }
            catch (Exception ex)
            {
                _lastSetErr = 0;
                LastLimitError = what + ": " + ex.Message;
                return false;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

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
                // A new kernel object holds none of the old one's limits, so the
                // memo of what has been written has to go with it. Without this a
                // restarted controller would inherit the PREVIOUS job's cap in the
                // agent's bookkeeping and never have it written, which is the
                // quiet kind of wrong: the menu says 10 per cent and the machine
                // runs flat out.
                _applied = null;
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
                if (CpuThreads > 0)
                    psi.EnvironmentVariables["IDLEGPU_CPU_THREADS"] =
                        CpuThreads.ToString(CultureInfo.InvariantCulture);

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
        readonly SystemCpu _cpu = new SystemCpu();
        readonly GpuEngineUtil _engines = new GpuEngineUtil();
        readonly Policy _policy;
        readonly object _lock = new object();

        readonly List<ServiceDef> _services = new List<ServiceDef>();
        readonly Dictionary<string, JobRunner> _jobs =
            new Dictionary<string, JobRunner>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, JobStore> _queues =
            new Dictionary<string, JobStore>(StringComparer.OrdinalIgnoreCase);

        /// Services already reported as held back for want of memory, so the log
        /// says it once rather than twice a second for as long as the machine is
        /// full.
        readonly HashSet<string> _memoryHeld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly ConcurrentQueue<Command> _commands = new ConcurrentQueue<Command>();
        int _commandDepth;

        Thread _fast, _slow, _sched;
        volatile bool _stop;
        Snapshot _snap = new Snapshot();
        List<ProcessGpuUse> _lastVram = new List<ProcessGpuUse>();
        Dictionary<string, double> _lastEngines = new Dictionary<string, double>();
        DateTime _countersAt = DateTime.MinValue;
        long _ownCpu100ns;
        DateTime _ownCpuAt = DateTime.MinValue;
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
                    s.Cpu = SampleCpu(s.At);
                    s.Memory = SystemMemory.Read();

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
                        // The whole job object, not just the controller. See
                        // JobRunner.Pids: the child is what holds the VRAM.
                        foreach (int pid in j.Pids)
                            if (pid > 0) s.OwnJobPids.Add(pid);
                    }

                    Verdict v = _policy.Evaluate(s);
                    lock (_lock) { _snap = s; }

                    // Before the stop decision, not after. A job that is about to
                    // be stopped anyway loses nothing by being throttled first, and
                    // a job that is NOT being stopped - the ordinary case, where
                    // the owner has come back to a machine that is only allowed
                    // ten per cent of it now - gets its new cap in this tick rather
                    // than the next one.
                    //
                    // Always-on is the exception, and it is the same exception the
                    // GPU makes: somebody who has explicitly said "use my machine
                    // anyway" has asked for exactly that, and quietly capping them
                    // to ten per cent would be an override that overrides nothing.
                    ApplyLimits(Mode == Mode.AlwaysOn ? Config.Unlimited : v.Limits);

                    bool can = _policy.CanRun(Mode);
                    // PER SERVICE, not one answer for the whole runner. A GPU
                    // service is stopped by the GPU detector; a CPU service is
                    // stopped when its row of the matrix says nothing, which is
                    // Busy. Asking one question for both meant a CPU job died
                    // every time a game started, for a card it was not using.
                    bool gpuCan = _policy.CanRunGpu(Mode);
                    bool cpuCan = _policy.CanRunCpu(Mode);
                    {
                        string why = Mode == Mode.Off ? "switched off" : v.ReasonText;
                        if (Mode != Mode.Off && string.IsNullOrEmpty(why)) why = v.StateReason;
                        foreach (JobRunner j in _jobs.Values)
                        {
                            if (!j.Running || j.Stopping) continue;
                            if (j.Service.WantsGpu ? gpuCan : cpuCan) continue;
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

        /// Machine CPU, our CPU, and the difference.
        ///
        /// OWN CPU IS SUBTRACTED RATHER THAN THE SIGNAL BEING SUPPRESSED, which is
        /// the one place the CPU can do better than the GPU. The GPU's tier 2 has
        /// to go silent while our own job runs, because nvidia-smi reports a whole
        /// card with no owner attached and the agent otherwise yields to the load
        /// it created. Job object accounting names its own processes exactly, so
        /// here the foreign share is arithmetic and the signal keeps working while
        /// a job is running - which is when it matters most, because that is when
        /// the owner walks up to a machine that is already busy.
        CpuSample SampleCpu(DateTime at)
        {
            var c = new CpuSample();
            c.At = at;
            double machine = _cpu.ReadPct();

            long own = 0;
            foreach (JobRunner j in _jobs.Values) own += j.CpuTime100ns;
            double ownPct = 0;
            if (_ownCpuAt != DateTime.MinValue)
            {
                double wall = (at - _ownCpuAt).TotalSeconds;
                // A job that ended took its accounting with it when the handle
                // closed, so the delta goes negative. That is not a measurement,
                // it is bookkeeping, and it must not turn into a negative foreign
                // load that reads as an idle machine.
                if (wall > 0 && own >= _ownCpu100ns)
                    ownPct = 100.0 * ((own - _ownCpu100ns) / 10000000.0)
                             / (wall * Environment.ProcessorCount);
            }
            _ownCpu100ns = own; _ownCpuAt = at;

            if (machine < 0) return c;     // first sample: a rate needs two reads
            c.Valid = true;
            c.MachinePct = machine;
            c.OwnPct = ownPct > machine ? machine : ownPct;
            c.ForeignPct = machine - c.OwnPct;
            if (c.ForeignPct < 0) c.ForeignPct = 0;
            return c;
        }

        /// Write the row of the matrix into every live job.
        ///
        /// ON THE FAST LOOP DELIBERATELY. This IS the yield path for the CPU: it
        /// is what turns "the owner just touched the keyboard" into a cap the
        /// scheduler enforces, and measured on spring the cap binds within 200 ms
        /// of the call. Putting it on the slow loop would double the promise for
        /// no reason. It costs nothing when nothing has changed, because
        /// JobRunner.ApplyLimits compares against what it last wrote and returns
        /// without a syscall.
        void ApplyLimits(ResourceLimits want)
        {
            if (want == null) return;
            int threads = Config.ThreadsFor(want, Environment.ProcessorCount, Topology.PhysicalCores);
            foreach (JobRunner j in _jobs.Values)
            {
                if (!j.Running) continue;
                try
                {
                    if (j.ApplyLimits(want))
                        Append(j.Service.Id + ": limits now " + want.Describe());
                    // The kernel side of the cap binds in about 200 ms whatever the
                    // child is doing. The thread count is a REQUEST: the controller
                    // applies it between units of work, because torch cannot be
                    // asked to change its thread pool in the middle of a generate().
                    // So the cap is the promise and this is the optimisation, and
                    // they are deliberately not the same mechanism.
                    if (!j.Service.WantsGpu) j.TellThreads(threads);
                }
                catch (Exception ex) { Log("could not apply limits: " + ex.Message); }
            }
        }

        /// Is there enough free memory to start this service right now.
        ///
        /// WHY AN ADMISSION CHECK AND NOT ONLY A CAP. The two answer different
        /// questions and this project needs both. A working set cap contains a job
        /// that has already started; it cannot undo the moment where a 6.5 GiB
        /// model loads onto a machine that had 2 GiB spare and the owner's browser
        /// goes to disk. Paging is felt in a way CPU contention is not - a
        /// throttled job makes things slower, a machine that is swapping makes
        /// things stop - so the cheapest fix is not to start.
        ///
        /// The reservation is the row's MinFreeMib plus whatever the service
        /// itself declared it needs, so a service that says nothing is admitted on
        /// the headroom rule alone rather than being blocked for ever.
        public bool MemoryAllows(ServiceDef svc, ResourceLimits limits, out string why)
        {
            why = "";
            if (limits == null || limits.MinFreeMib <= 0) return true;
            MemorySample m;
            lock (_lock) { m = _snap == null ? null : _snap.Memory; }
            bool known = m != null && m.Valid;
            return Policy.MemoryAllows(known ? m.AvailableMib : 0, known,
                limits.MinFreeMib, svc == null ? 0 : svc.NeedsMemoryMib, out why);
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
                    // THE POLICY THREAD STAYS THE SINGLE WRITER. A route or a CLI
                    // verb that reached into Config.Limits from a listener thread
                    // would be racing the sampling loop for the row it is about to
                    // hand to the kernel. Everything arrives here instead, on the
                    // one thread that already owns the decision, which is why the
                    // whole-object swap in SetLimits is safe with no lock.
                    else if (cmd.Kind == "profile")
                    {
                        SetProfile(cmd.Value);
                    }
                    else if (cmd.Kind == "limits")
                    {
                        MachineState st;
                        ResourceLimits row;
                        if (Config.ParseLimitsCommand(_c, cmd.Value, out st, out row))
                        {
                            SetLimits(st, row);
                            SaveLimits();
                        }
                        else Log("could not read the limits command: " + cmd.Value);
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

                    // ONE CONTROLLER PER DEVICE, NOT ONE PER MACHINE. This gate was
                    // `if (!JobRunning)`, and JobRunning is true when ANY runner is
                    // running, so a GPU service and a CPU service could never run at
                    // the same time. That is the product the whole matrix exists to
                    // describe: a game takes the card and leaves twelve threads
                    // idle, and the runner is supposed to sell those threads. With
                    // one machine-wide gate it could not, and the second half of
                    // this work would have been decoration.
                    StartBest(true);
                    StartBest(false);
                }
                catch (Exception ex) { Log("scheduler: " + ex.Message); }
                Sleep(_c.SchedulerPollMs);
            }
        }

        /// Start the best queued service of one device group, if that group is free.
        ///
        /// The two groups are independent all the way down: their own busy check,
        /// their own verdict, their own priority contest. What they SHARE is the
        /// memory admission check, because there is one pool of system memory and
        /// two jobs drawing on it.
        void StartBest(bool wantsGpu)
        {
            foreach (JobRunner j in _jobs.Values)
                if (j.Service.WantsGpu == wantsGpu && (j.Running || j.Stopping)) return;

            ResourceLimits limits = _policy.Limits;
            if (!(wantsGpu ? _policy.CanRunGpu(Mode) : _policy.CanRunCpu(Mode))) return;

            // KEEP THE WARM JOB, TAKE NO NEW ONES. Admit is a separate field from
            // CpuPct precisely so this state is expressible: a job that has already
            // paid its 22 second model load is worth holding at a trickle, and a
            // new one is not worth starting on a machine somebody is using.
            if (!_policy.AdmitsNewWork(Mode))
            {
                string groupKey = wantsGpu ? "\0gpu-admit" : "\0cpu-admit";
                if (_admitHeld.Add(groupKey))
                    Log((wantsGpu ? "gpu" : "cpu") + ": not starting anything new, "
                        + _policy.Last.StateReason);
                return;
            }
            _admitHeld.Remove(wantsGpu ? "\0gpu-admit" : "\0cpu-admit");

            ServiceDef best = null;
            foreach (ServiceDef s in _services)
            {
                if (s.WantsGpu != wantsGpu) continue;
                JobStore q = _queues[s.Id];
                if (q.QueuedCount() <= 0) continue;
                string why;
                if (!MemoryAllows(s, limits, out why))
                {
                    if (_memoryHeld.Add(s.Id)) Log(s.Id + ": not starting yet, " + why);
                    continue;
                }
                _memoryHeld.Remove(s.Id);
                if (best == null || s.Priority > best.Priority) best = s;
            }
            if (best == null) return;

            ResourceLimits use = Mode == Mode.AlwaysOn ? Config.Unlimited : limits;
            JobRunner jr = _jobs[best.Id];
            jr.CpuThreads = Config.ThreadsFor(use, Environment.ProcessorCount, Topology.PhysicalCores);
            if (jr.Start()) jr.ApplyLimits(use);
        }

        readonly HashSet<string> _admitHeld = new HashSet<string>(StringComparer.Ordinal);

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
            // The matrix row in force, and the row itself. Published rather than
            // only shown in the tray, so a headless agent, `type state.json` and
            // GET /v1/status all say the same thing about what this machine is
            // currently willing to give up.
            sb.Append(Json.P("machine_state", Json.Esc(Config.StateKey(v.MachineState)))).Append(",");
            sb.Append(Json.P("machine_state_reason", Json.Esc(v.StateReason))).Append(",");
            sb.Append("\"limits\":").Append(Json.Obj(
                Json.P("gpu", v.Limits.Gpu ? "true" : "false"),
                Json.P("cpu_pct", Json.Num(v.Limits.CpuPct)),
                Json.P("priority", Json.Esc(Config.NormalisePriority(v.Limits.Priority))),
                Json.P("working_set_mib", Json.Num(v.Limits.WorkingSetMib)),
                Json.P("min_free_mib", Json.Num(v.Limits.MinFreeMib)),
                Json.P("admit", v.Limits.Admit ? "true" : "false"))).Append(",");
            // WHICH RESOURCE, not only that something is contended. A client
            // reading machine_state = busy cannot tell a game from a compile, and
            // those want opposite decisions from anything choosing between a CPU
            // and a GPU service.
            sb.Append(Json.P("gpu_contended", v.GpuContended ? "true" : "false")).Append(",");
            sb.Append(Json.P("cpu_contended", v.CpuContended ? "true" : "false")).Append(",");
            sb.Append(Json.P("profile", Json.Esc(_c.EffectiveProfile()))).Append(",");
            sb.Append(Json.P("profile_label", Json.Esc(_c.EffectiveProfileLabel()))).Append(",");
            sb.Append("\"cpu\":");
            if (s.Cpu != null && s.Cpu.Valid)
                sb.Append(Json.Obj(
                    Json.P("machine_pct", Json.Num(Math.Round(s.Cpu.MachinePct, 1))),
                    Json.P("own_pct", Json.Num(Math.Round(s.Cpu.OwnPct, 1))),
                    Json.P("foreign_pct", Json.Num(Math.Round(s.Cpu.ForeignPct, 1))),
                    Json.P("logical_processors", Json.Num(Environment.ProcessorCount))));
            else sb.Append("null");
            sb.Append(",");
            sb.Append("\"memory\":");
            if (s.Memory != null && s.Memory.Valid)
                sb.Append(Json.Obj(
                    Json.P("total_mib", Json.Num(s.Memory.TotalMib)),
                    Json.P("available_mib", Json.Num(s.Memory.AvailableMib)),
                    Json.P("load_pct", Json.Num(s.Memory.LoadPct))));
            else sb.Append("null");
            sb.Append(",");

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
                    // THE THREE FIELDS THE BLIND VETO IS DECIDED ON, none of
                    // which used to be published. `state: blocked` with the
                    // reason "cannot observe the user" was therefore impossible
                    // to diagnose from outside the machine: an owner could not
                    // tell a helper that had never started from one whose report
                    // had just expired, and neither could the server that had
                    // stopped sending it work. Diagnosing it once cost a remote
                    // login. See Policy.Evaluate's tier 0.
                    Json.P("has_console_user", s.Session.HasConsoleUser ? "true" : "false"),
                    Json.P("console_user", Json.Esc(s.Session.ConsoleUserName)),
                    Json.P("presence_fresh", s.Session.PresenceFresh ? "true" : "false"),
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
            // THE RUNNER SELLS TWO THINGS NOW, so one boolean can no longer answer
            // "will you work for me". A client that reads only gpu_available walks
            // away from a machine that would happily have run its job on twelve
            // idle threads, and one that reads only cpu_available walks into a
            // game. Both are published, and so is the per service `available`
            // below, which is the one a client should actually read: it is this
            // machine's answer for THAT service, already resolved.
            sb.Append(Json.P("cpu_available", _policy.CanRunCpu(Mode) ? "true" : "false")).Append(",");
            sb.Append(Json.P("machine_state", Json.Esc(Config.StateKey(_policy.MachineState)))).Append(",");
            sb.Append(Json.P("machine_state_reason", Json.Esc(_policy.Last.StateReason))).Append(",");
            sb.Append("\"limits\":").Append(Json.Obj(
                Json.P("gpu", _policy.Limits.Gpu ? "true" : "false"),
                Json.P("cpu_pct", Json.Num(_policy.Limits.CpuPct)),
                Json.P("priority", Json.Esc(Config.NormalisePriority(_policy.Limits.Priority))),
                Json.P("working_set_mib", Json.Num(_policy.Limits.WorkingSetMib)),
                Json.P("min_free_mib", Json.Num(_policy.Limits.MinFreeMib)),
                Json.P("admit", _policy.Limits.Admit ? "true" : "false"))).Append(",");
            // WHICH RESOURCE IS CONTENDED, not only that something is. A client
            // reading machine_state = busy cannot tell whether the card has gone to
            // a game or the processor to a compile, and those want opposite
            // decisions from a router that has both a CPU and a GPU service to
            // choose between.
            sb.Append(Json.P("gpu_contended", _policy.Last.GpuContended ? "true" : "false")).Append(",");
            sb.Append(Json.P("cpu_contended", _policy.Last.CpuContended ? "true" : "false")).Append(",");
            sb.Append(Json.P("profile", Json.Esc(_c.EffectiveProfile()))).Append(",");
            sb.Append(Json.P("profile_label", Json.Esc(_c.EffectiveProfileLabel()))).Append(",");
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
                // AVAILABLE, PER SERVICE, ALREADY RESOLVED. A GPU service is
                // gated by the GPU detector; a CPU service is gated only by the
                // limits matrix, and putting the two behind one boolean was what
                // made a CPU speech job wait out a ninety second cooldown for a
                // card it never touched. Memory is in it too, because a runner
                // that will not start a 6.5 GiB job on a full machine should say
                // so rather than accepting the work and sitting on it.
                bool serviceCan = (d.WantsGpu ? can && _policy.Limits.Gpu : _policy.CanRunCpu(Mode));
                string memWhy = "";
                // Admission is part of "will you take my job", not a footnote to
                // it. A runner whose Busy row keeps its warm job at a trickle and
                // takes nothing new must say no to a NEW job, or a client will
                // submit and wait on a queue that is not going to move.
                if (serviceCan && !_policy.AdmitsNewWork(Mode))
                {
                    serviceCan = false;
                    memWhy = "not starting anything new: " + _policy.Last.StateReason;
                }
                if (serviceCan && !MemoryAllows(d, _policy.Limits, out memWhy)) serviceCan = false;
                else if (serviceCan) memWhy = "";
                sb.Append(Install.Json(d, st,
                    registered && j.Running,
                    q == null ? 0 : q.QueuedCount(),
                    q == null ? null : q.Manifest(_c.MaxPassthroughBytes),
                    d.YieldGraceSeconds > 0 ? d.YieldGraceSeconds : _c.YieldGraceSeconds,
                    d.WantsGpu ? "gpu" : "cpu", serviceCan, memWhy));
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

        /// Write the whole limits matrix back to worker.ini.
        ///
        /// WHY REWRITE RATHER THAN APPEND. The settings window can change any of
        /// twenty five values, and appending twenty five lines every time somebody
        /// pressed Apply would grow the file without bound and leave the reader
        /// looking at whichever copy came last. So every limits.* line is dropped
        /// and one current block is written at the end, and every OTHER line in
        /// the file - comments, service sections, the listener settings somebody
        /// hand-edited - is preserved exactly as it was.
        ///
        /// The change is already live before this is called; this is only about
        /// surviving a restart. A setting that silently resets at the next login
        /// is a setting the owner stops trusting, and these are the settings they
        /// reach for precisely when they do not yet trust something.
        public void SaveLimits()
        {
            try
            {
                if (string.IsNullOrEmpty(_c.ConfigPath)) return;
                var lines = new List<string>();
                if (File.Exists(_c.ConfigPath))
                    foreach (string raw in File.ReadAllLines(_c.ConfigPath))
                    {
                        string t = raw.Trim().ToLowerInvariant().Replace(" ", "");
                        if (t.StartsWith("limits.") && t.Contains("=")) continue;
                        if (t.StartsWith("profile=")) continue;
                        if (t.StartsWith("profile.") && t.Contains("=")) continue;
                        if (t == "[limits]") continue;
                        if (t.StartsWith("[profile.")) continue;
                        lines.Add(raw);
                    }
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                lines.Add("");
                lines.Add("[limits]");
                lines.Add("# What this machine will give up in each state. Written by the");
                lines.Add("# settings window; safe to edit by hand.");
                lines.Add("#");
                lines.Add("# Profile names a whole posture and fills every cell below. Any");
                lines.Add("# limits.<state>.<field> line then overrides that one cell, whatever");
                lines.Add("# order the two appear in, and the runner reports the posture as");
                lines.Add("# `custom` as soon as one of them differs.");
                lines.Add("Profile = " + _c.Profile);
                foreach (MachineState st in Config.AllStates())
                {
                    ResourceLimits r = _c.LimitsFor(st);
                    string k = "limits." + Config.StateKey(st) + ".";
                    lines.Add("");
                    lines.Add("# " + Config.StateLabel(st) + ": " + r.Describe());
                    lines.Add(k + "gpu = " + (r.Gpu ? "yes" : "no"));
                    lines.Add(k + "cpupct = " + r.CpuPct.ToString(CultureInfo.InvariantCulture));
                    lines.Add(k + "priority = " + Config.NormalisePriority(r.Priority));
                    lines.Add(k + "workingsetmib = " + r.WorkingSetMib.ToString(CultureInfo.InvariantCulture));
                    lines.Add(k + "minfreemib = " + r.MinFreeMib.ToString(CultureInfo.InvariantCulture));
                    lines.Add(k + "admit = " + (r.Admit ? "yes" : "no"));
                }
                foreach (string id in _c.SavedProfiles.Keys)
                {
                    Dictionary<MachineState, ResourceLimits> grid = _c.SavedProfiles[id];
                    lines.Add("");
                    lines.Add("[profile." + id + "]");
                    string name;
                    if (_c.SavedProfileNames.TryGetValue(id, out name) && !string.IsNullOrEmpty(name))
                        lines.Add("profile." + id + ".name = " + name);
                    foreach (MachineState st in Config.AllStates())
                    {
                        ResourceLimits r;
                        if (!grid.TryGetValue(st, out r) || r == null) continue;
                        string k = "profile." + id + "." + Config.StateKey(st) + ".";
                        lines.Add(k + "gpu = " + (r.Gpu ? "yes" : "no"));
                        lines.Add(k + "cpupct = " + r.CpuPct.ToString(CultureInfo.InvariantCulture));
                        lines.Add(k + "priority = " + Config.NormalisePriority(r.Priority));
                        lines.Add(k + "workingsetmib = " + r.WorkingSetMib.ToString(CultureInfo.InvariantCulture));
                        lines.Add(k + "minfreemib = " + r.MinFreeMib.ToString(CultureInfo.InvariantCulture));
                        lines.Add(k + "admit = " + (r.Admit ? "yes" : "no"));
                    }
                }
                // WRITTEN WHOLE, THEN MOVED. A half-written worker.ini is a machine
                // with no limits on it at the next start, which is the one failure
                // this file must not have: the agent would come up believing it had
                // been given permission it was never given. The rename is the only
                // step the next reader can observe.
                string tmp = _c.ConfigPath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(_c.ConfigPath)) File.Delete(_c.ConfigPath);
                File.Move(tmp, _c.ConfigPath);
            }
            catch (Exception ex) { Log("could not save limits: " + ex.Message); }
        }

        /// Switch the whole posture, and make it bite now.
        ///
        /// SWITCHING A PROFILE DOES NOT RESTART ANYTHING. It re-applies: every live
        /// job gets the new row through SetInformationJobObject on its existing
        /// handle, which measured under a millisecond on spring and bound inside
        /// the first 200 ms window. The one exception is a posture whose row for
        /// the CURRENT state means stop, and that goes through the ordinary yield
        /// path - so the log line names the profile and the row that did it, or the
        /// owner reads it as "the menu killed my job".
        public void SetProfile(string id)
        {
            Dictionary<MachineState, ResourceLimits> want = _c.ResolveProfile(id);
            if (want == null) { Log("no posture called " + id); return; }
            _c.ApplyProfile(id);
            Append("posture set to " + _c.AnyProfileLabel(id) + " ("
                + _c.LimitsFor(_policy.MachineState).Describe() + " in the state this machine is in)");
            ApplyLimits(Mode == Mode.AlwaysOn ? Config.Unlimited : _policy.Limits);
            SaveLimits();
        }

        /// Save the live grid as a named posture, so it comes back in the tray.
        public void SaveProfileAs(string id, string name)
        {
            string norm = Config.NormaliseProfile(id);
            if (norm.Length == 0) return;
            var grid = new Dictionary<MachineState, ResourceLimits>();
            foreach (MachineState st in Config.AllStates()) grid[st] = _c.LimitsFor(st).Clone();
            _c.SavedProfiles[norm] = grid;
            if (!string.IsNullOrEmpty(name)) _c.SavedProfileNames[norm] = name;
            _c.Profile = norm;
            Append("posture saved as " + _c.AnyProfileLabel(norm));
            SaveLimits();
        }

        /// Replace one row of the matrix and make it bite now.
        ///
        /// The tray and the settings window call this. The write is a whole-object
        /// swap into the dictionary rather than a field-by-field edit, so the
        /// sampling thread can only ever read a complete row: C# 5 has no records
        /// and no reference assignment guarantees worth relying on beyond that a
        /// reference store is atomic, which is exactly what this needs.
        public void SetLimits(MachineState st, ResourceLimits r)
        {
            if (r == null) return;
            _c.Limits[st] = r.Clone();
            // The ratchet in Policy.Evaluate believes a worse state has a smaller
            // row. The settings window's spinners and the tray's rungs do not
            // enforce that on their own, so it is enforced here, once, on the way
            // in - not at the point where a job would be handed more machine than
            // the owner meant to lend it.
            foreach (string fix in _c.TightenGrid()) Append(fix);
            Append("limits for " + Config.StateLabel(st).ToLowerInvariant() + " set to "
                + _c.LimitsFor(st).Describe() + "; posture is now " + _c.EffectiveProfileLabel());
            r = _c.LimitsFor(st);
            // Bind immediately rather than at the next job. The whole point of
            // making this editable from the tray is that the owner changes it
            // while something is running and feels the difference.
            if (_policy.MachineState == st) ApplyLimits(Mode == Mode.AlwaysOn ? Config.Unlimited : r);
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
