// Signal acquisition: is anybody using this GPU?
//
// EVERY THRESHOLD AND EVERY CHOICE OF SIGNAL IN THIS FILE COMES FROM A
// MEASUREMENT TAKEN ON THE TARGET MACHINE (spring, RTX 3070, driver 610.47,
// Windows 11 Pro 10.0.26200) ON 2026-09-05. The probe scripts that produced
// them are in ../probe and re-run unchanged. Numbers quoted in comments are
// from those runs, not from documentation.
//
// Written to C# 5. WHY: the only C# compiler on a stock Windows 11 is
// C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe, which is the
// pre-Roslyn compiler. Measured on spring: present, and no .NET SDK is
// installed, so there is no Roslyn to fall back on. No string interpolation,
// no null-conditional operator, no nameof.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace OffPeak
{
    // ---------------------------------------------------------------- GPU ---


    /// Streams nvidia-smi at 1 Hz from ONE long-lived child process.
    ///
    /// WHY A STREAM RATHER THAN REPEATED ONE-SHOTS. Measured on spring: a single
    /// `nvidia-smi --query-gpu=...` invocation costs 39-44 ms of wall time (probe
    /// p4, section B, five runs). Polling that at 1 Hz would be tolerable but at
    /// 4 Hz it is 16 per cent of a core spent on process creation alone, on a
    /// machine whose whole point is that we must not disturb it. `-l 1` costs one
    /// process for the lifetime of the agent and was measured (probe p4, A1) to
    /// emit one line per second, line-buffered, through a redirected stdout.
    ///
    /// WHY NOT `-l 1 -c N`. Measured (probe p4, A3): combining -c with --query-gpu
    /// makes nvidia-smi reject --query-gpu itself with
    ///   "ERROR: Option --query-gpu=utilization.gpu is not recognized"
    /// which is a misleading message for an argument that is perfectly valid on
    /// its own. -c belongs to `dmon`/`pmon`. Use -l alone and count lines here.
    public class GpuMonitor : IDisposable
    {
        const string Fields =
            "utilization.gpu,utilization.memory,utilization.encoder,utilization.decoder," +
            "clocks.mem,clocks.sm,pstate,power.draw,memory.used";

        readonly object _lock = new object();
        readonly Queue<GpuSample> _recent = new Queue<GpuSample>();
        readonly int _keep;
        Process _proc;
        Thread _reader;
        volatile bool _stop;
        GpuSample _latest = new GpuSample();

        public GpuMonitor(int keepSamples)
        {
            _keep = keepSamples;
            _latest.PState = "?";
        }

        public string ExePath = "nvidia-smi";

        public void Start()
        {
            var psi = new ProcessStartInfo(ExePath,
                "--query-gpu=" + Fields + " --format=csv,noheader,nounits -l 1");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            _proc = Process.Start(psi);
            _reader = new Thread(ReadLoop);
            _reader.IsBackground = true;
            _reader.Start();
        }

        void ReadLoop()
        {
            try
            {
                while (!_stop)
                {
                    string line = _proc.StandardOutput.ReadLine();
                    if (line == null) break;
                    GpuSample s = Parse(line);
                    if (!s.Valid) continue;
                    lock (_lock)
                    {
                        _latest = s;
                        _recent.Enqueue(s);
                        while (_recent.Count > _keep) _recent.Dequeue();
                    }
                }
            }
            catch (Exception) { /* process died; Latest goes stale and Healthy() reports it */ }
        }

        static int PInt(string s)
        {
            int v;
            // "[N/A]" and "[Not Supported]" both appear in nvidia-smi output; treat as -1.
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        static double PDbl(string s)
        {
            double v;
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        static GpuSample Parse(string line)
        {
            var s = new GpuSample();
            s.At = DateTime.UtcNow;
            s.PState = "?";
            string[] f = line.Split(',');
            if (f.Length < 9) return s;
            s.UtilGpu = PInt(f[0]);
            s.UtilMem = PInt(f[1]);
            s.UtilEncoder = PInt(f[2]);
            s.UtilDecoder = PInt(f[3]);
            s.ClockMemMhz = PInt(f[4]);
            s.ClockSmMhz = PInt(f[5]);
            s.PState = f[6].Trim();
            s.PowerWatts = PDbl(f[7]);
            s.MemUsedMiB = PInt(f[8]);
            s.Valid = s.UtilGpu >= 0 && s.ClockMemMhz >= 0;
            return s;
        }

        public GpuSample Latest { get { lock (_lock) { return _latest; } } }

        public GpuSample[] Recent { get { lock (_lock) { return _recent.ToArray(); } } }

        /// True while the stream is producing fresh samples. A dead nvidia-smi must
        /// mean "assume busy", never "assume idle" - see Policy.
        public bool Healthy
        {
            get
            {
                GpuSample l = Latest;
                return l.Valid && (DateTime.UtcNow - l.At) < TimeSpan.FromSeconds(10);
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); }
            catch (Exception) { }
            try { if (_proc != null) _proc.Dispose(); }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------ per-process GPU state ---


    /// Per-process GPU memory, from the Windows "GPU Process Memory" performance
    /// counter set.
    ///
    /// WHY THIS EXISTS AT ALL. On this machine nvidia-smi reports used_memory as
    /// [N/A] for EVERY process (probe p1, "COMPUTE APPS": fifteen processes, all
    /// [N/A]), and `nvidia-smi pmon` reports "-" for sm/mem/enc/dec per process.
    /// That is the WDDM driver model, not a fault, and it removes the obvious way
    /// to notice a game that is paused but still resident. The OS counters are not
    /// subject to that limitation: probe p2 read dwm at 172.4 MiB, CamoStudio at
    /// 112.3 MiB, explorer at 40.3 MiB, and the adapter total at 423.4 MiB against
    /// nvidia-smi's own 417 MiB for the same instant - a 1.5 per cent
    /// disagreement, which is two different accountings of the same truth rather
    /// than two different truths.
    ///
    /// These counters are a documented OS API read through PDH. Nothing is
    /// injected, no process is opened for write, no graphics API is hooked. That
    /// matters here specifically: Riot Vanguard (vgk, kernel-mode, measured
    /// Running with StartType System) is resident on this machine at all times.
    ///
    /// "Dedicated Usage" is an instantaneous gauge, so ONE sample is meaningful.
    /// Contrast "GPU Engine\Utilization Percentage", which is a rate and needs two
    /// samples an interval apart - measured cost of a single Get-Counter call on
    /// that set was 1069-1851 ms (probe p7), which is why utilisation attribution
    /// runs on a slow cadence and never gates a decision on its own.
    public class GpuProcessMemory
    {
        readonly Dictionary<int, string> _nameCache = new Dictionary<int, string>();

        public List<ProcessGpuUse> Read()
        {
            var outp = new List<ProcessGpuUse>();
            PerformanceCounterCategory cat;
            try { cat = new PerformanceCounterCategory("GPU Process Memory"); }
            catch (Exception) { return outp; }

            string[] instances;
            try { instances = cat.GetInstanceNames(); }
            catch (Exception) { return outp; }

            // Sum per pid: one process can appear under several adapter LUIDs.
            var byPid = new Dictionary<int, double>();
            foreach (string inst in instances)
            {
                int pid = PidFromInstance(inst);
                if (pid <= 0) continue;
                try
                {
                    using (var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true))
                    {
                        double mib = c.RawValue / 1048576.0;
                        if (!byPid.ContainsKey(pid)) byPid[pid] = 0;
                        byPid[pid] += mib;
                    }
                }
                catch (Exception) { /* instance vanished between enumerate and read */ }
            }

            foreach (var kv in byPid)
            {
                var u = new ProcessGpuUse();
                u.Pid = kv.Key;
                u.DedicatedMiB = kv.Value;
                u.Name = NameOf(kv.Key);
                outp.Add(u);
            }
            outp.Sort(delegate(ProcessGpuUse a, ProcessGpuUse b) { return b.DedicatedMiB.CompareTo(a.DedicatedMiB); });
            return outp;
        }

        /// Instance names look like
        ///   pid_1752_luid_0x00000000_0x0000fbc9_phys_0
        /// for GPU Process Memory and
        ///   pid_5640_luid_0x00000000_0x0000fbc9_phys_0_eng_0_engtype_3d
        /// for GPU Engine. Both measured verbatim in probe p2.
        public static int PidFromInstance(string inst)
        {
            if (inst == null || !inst.StartsWith("pid_")) return -1;
            int end = inst.IndexOf('_', 4);
            if (end < 0) return -1;
            int pid;
            if (int.TryParse(inst.Substring(4, end - 4), out pid)) return pid;
            return -1;
        }

        string NameOf(int pid)
        {
            string n;
            if (_nameCache.TryGetValue(pid, out n)) return n;
            try { n = Process.GetProcessById(pid).ProcessName; }
            catch (Exception) { n = "(gone)"; }
            if (_nameCache.Count > 512) _nameCache.Clear();
            _nameCache[pid] = n;
            return n;
        }
    }

    /// Utilisation split by engine type, summed across processes.
    /// Measured at idle (probe p2): 3d 0.95, copy 0.03, videodecode 0.00,
    /// videoencode 0.00. The split is the whole reason this class exists: it is
    /// what separates "a browser is decoding video", which lands on videodecode
    /// and leaves the shaders free, from "something is rendering", which lands on
    /// 3d. Utilisation alone cannot tell those apart and would yield to a YouTube
    /// tab for no reason.
    public class GpuEngineUtil
    {
        readonly Dictionary<string, PerformanceCounter> _counters = new Dictionary<string, PerformanceCounter>();
        DateTime _refreshed = DateTime.MinValue;

        public Dictionary<string, double> ReadByEngineType()
        {
            var sums = new Dictionary<string, double>();
            // Rebuild the instance list periodically: processes come and go, and a
            // stale PerformanceCounter throws on read rather than returning zero.
            if ((DateTime.UtcNow - _refreshed) > TimeSpan.FromSeconds(30)) Refresh();

            foreach (var kv in _counters)
            {
                string eng = EngineTypeOf(kv.Key);
                double v;
                try { v = kv.Value.NextValue(); }
                catch (Exception) { continue; }
                if (!sums.ContainsKey(eng)) sums[eng] = 0;
                sums[eng] += v;
            }
            return sums;
        }

        void Refresh()
        {
            foreach (var c in _counters.Values) { try { c.Dispose(); } catch (Exception) { } }
            _counters.Clear();
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (string inst in cat.GetInstanceNames())
                {
                    try
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        c.NextValue(); // prime: a rate counter's first read is always 0
                        _counters[inst] = c;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            _refreshed = DateTime.UtcNow;
        }

        public static string EngineTypeOf(string inst)
        {
            int i = inst.IndexOf("engtype_");
            if (i < 0) return "unknown";
            return inst.Substring(i + 8);
        }
    }

    // ------------------------------------------------------------ CPU and RAM ---

    /// The whole machine's CPU, from GetSystemTimes.
    ///
    /// WHY NOT PDH, given the GPU counters already use it. Two reasons, and both
    /// matter for something meant to be published rather than run on one desk.
    ///
    ///   1. PDH COUNTER PATHS ARE LOCALISED. "\Processor Information(_Total)\%
    ///      Processor Time" is an English string; on a German or Portuguese
    ///      Windows the counter exists under a translated name and the English
    ///      path returns PDH_CSTATUS_NO_OBJECT. The GPU counters get away with it
    ///      because "GPU Engine" happens not to be translated on the machines this
    ///      was measured on, which is luck rather than a design.
    ///   2. COST. One PDH counter sample was measured at 98 ms on spring, which is
    ///      why it lives on the slow loop. GetSystemTimes is a single syscall and
    ///      belongs on the fast loop, where the yield decision is.
    ///
    /// Stateful on purpose: CPU per cent is a rate, so it needs two reads. The
    /// first Read after construction returns Valid = false rather than a number
    /// made up out of one sample.
    public class SystemCpu
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint Low, High; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        static ulong V(FILETIME f) { return ((ulong)f.High << 32) | f.Low; }

        ulong _idle, _kernel, _user;
        bool _primed;

        /// Machine-wide busy per cent since the previous call, 0..100.
        /// Returns -1 until there are two samples to subtract.
        public double ReadPct()
        {
            FILETIME i, k, u;
            if (!GetSystemTimes(out i, out k, out u)) return -1;
            ulong ni = V(i), nk = V(k), nu = V(u);
            if (!_primed) { _idle = ni; _kernel = nk; _user = nu; _primed = true; return -1; }
            // GetSystemTimes reports kernel time INCLUSIVE of idle time, which is
            // the trap in this API: subtracting idle from the total is wrong unless
            // idle has already been removed from kernel.
            ulong dIdle = ni - _idle, dKernel = nk - _kernel, dUser = nu - _user;
            _idle = ni; _kernel = nk; _user = nu;
            ulong total = dKernel + dUser;
            if (total == 0) return -1;
            double busy = 100.0 * (double)(total - dIdle) / (double)total;
            return busy < 0 ? 0 : (busy > 100 ? 100 : busy);
        }
    }

    /// Free memory, for the admission check and the working set cap.
    ///
    /// Measured cost on spring: 0.001 ms per call over a thousand calls, so this
    /// can sit on the fast loop without being noticed.
    /// How many PHYSICAL cores this machine has, as opposed to logical ones.
    ///
    /// WHY IT IS WORTH A SYSCALL. Config.ThreadsFor caps a job's thread count at
    /// the physical core count, because Chatterbox's transformer is autoregressive
    /// at batch one and two sibling threads on one core share the L1, the L2 and
    /// the front end. Environment.ProcessorCount answers the logical question and
    /// there is no managed way to ask the other one without System.Management,
    /// which is a reference this project does not carry and a WMI query this
    /// project does not want on a machine with kernel anti-cheat on it.
    ///
    /// GetLogicalProcessorInformation is a documented kernel32 read with no
    /// privilege, no handle and no instrumentation. Counted ONCE and cached: the
    /// core count of a machine does not change while the agent runs, and this is
    /// read from the scheduler loop.
    ///
    /// FALLS BACK TO THE LOGICAL COUNT rather than to a guess. A machine whose
    /// topology cannot be read gets the behaviour it had before this existed.
    public static class Topology
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public UIntPtr ProcessorMask;
            public int Relationship;         // RelationProcessorCore = 0
            public ulong Reserved0;
            public ulong Reserved1;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

        const int RelationProcessorCore = 0;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        static int _cores = -1;

        public static int PhysicalCores
        {
            get
            {
                if (_cores > 0) return _cores;
                _cores = Count();
                return _cores;
            }
        }

        static int Count()
        {
            int logical = Environment.ProcessorCount;
            uint len = 0;
            try
            {
                if (GetLogicalProcessorInformation(IntPtr.Zero, ref len)) return logical;
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) return logical;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetLogicalProcessorInformation(buf, ref len)) return logical;
                    int size = Marshal.SizeOf(typeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION));
                    int n = (int)len / size;
                    int cores = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var e = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION)Marshal.PtrToStructure(
                            new IntPtr(buf.ToInt64() + (i * size)),
                            typeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION));
                        if (e.Relationship == RelationProcessorCore) cores++;
                    }
                    return cores > 0 && cores <= logical ? cores : logical;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception) { return logical; }
        }
    }

    public static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength; public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        /// ullAvailPhys, not commit headroom.
        ///
        /// The question the admission check asks is "would starting a 6.5 GiB job
        /// hurt right now", and the honest counter for that is physical memory the
        /// system can hand out without taking it off somebody: free plus standby.
        /// ullAvailPageFile is the wrong number - a machine with a large page file
        /// has gigabytes of it while already paging hard, which is exactly the
        /// state this check exists to refuse to add to.
        public static MemorySample Read()
        {
            var m = new MemorySample();
            m.At = DateTime.UtcNow;
            var x = new MEMORYSTATUSEX();
            x.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref x)) return m;
            m.TotalMib = (long)(x.ullTotalPhys / 1048576UL);
            m.AvailableMib = (long)(x.ullAvailPhys / 1048576UL);
            m.LoadPct = (int)x.dwMemoryLoad;
            m.Valid = true;
            return m;
        }
    }

    // ------------------------------------------------------- Windows session ---


    public static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("kernel32.dll")] static extern uint GetTickCount();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint sid);

        // WHO IS SIGNED IN ON THE CONSOLE, answerable from session 0 where
        // GetLastInputInfo and GetForegroundWindow are not. This is the whole
        // reason a service can be safe: it cannot see a user, but it CAN see
        // whether there is one.
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId,
            int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        const int WTSUserName = 5;

        /// The account signed in on the console session, or "" if nobody is.
        ///
        /// At the sign-in screen the console session exists and is running
        /// LogonUI, but it has no user name, so this returns "". That is the
        /// difference between "a machine waiting for somebody" and "somebody's
        /// desktop", and it is not visible any other way from session 0.
        static string ConsoleUser(uint sessionId)
        {
            IntPtr buf = IntPtr.Zero;
            uint bytes = 0;
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSUserName,
                                                 out buf, out bytes))
                    return "";
                string name = Marshal.PtrToStringUni(buf);
                return name == null ? "" : name.Trim();
            }
            catch (Exception) { return ""; }
            finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
        }

        const uint MONITOR_DEFAULTTONEAREST = 2;

        /// Collects everything the OS will tell us about the human at the keyboard.
        ///
        /// READ THIS BEFORE TRUSTING InputIdleSeconds OR ForegroundPid. These two
        /// are PER SESSION, and that is the single most important structural fact
        /// measured during this investigation. Probe p3 on spring:
        ///
        ///   ssh session id            : 0
        ///   query session             : console / <user> / id 1 / Active
        ///   WTSGetActiveConsoleSessionId: 1
        ///   GetForegroundWindow()     : 0        (session 0 has no desktop)
        ///   GetLastInputInfo()        : 620953 ms (session 0's own input, meaningless)
        ///
        /// So an agent started over SSH, or installed as a Windows service, sits in
        /// session 0 and is BLIND to the user it exists to get out of the way of. It
        /// would see a foreground window handle of zero and ten minutes of "idle"
        /// while the user was mid-match. The agent MUST run inside the interactive
        /// session, launched from the user's Run key or Startup folder. See
        /// RunningInConsoleSession, which the policy treats as a hard precondition.
        public static SessionSignals Read()
        {
            var s = new SessionSignals();
            s.ConsoleSessionId = WTSGetActiveConsoleSessionId();
            uint own;
            s.OwnSessionId = ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out own) ? own : 0xFFFFFFFF;
            s.RunningInConsoleSession = (s.OwnSessionId == s.ConsoleSessionId);
            s.ConsoleUserName = ConsoleUser(s.ConsoleSessionId);
            s.HasConsoleUser = s.ConsoleUserName.Length > 0;

            // Lock detection. LogonUI.exe exists only while the secure desktop is
            // up. Measured unlocked on spring (probe p3): no LogonUI process at all,
            // dwm and explorer both in session 1. A locked machine is the safest
            // possible time to run, so this is worth detecting even though the tray
            // app also gets SystemEvents.SessionSwitch, which is instant.
            s.Locked = false;
            try
            {
                foreach (var p in Process.GetProcessesByName("LogonUI"))
                {
                    if (p.SessionId == s.ConsoleSessionId) { s.Locked = true; }
                    p.Dispose();
                }
            }
            catch (Exception) { }

            s.InputIdleSeconds = -1;
            if (s.RunningInConsoleSession)
            {
                var li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (GetLastInputInfo(ref li))
                {
                    // Both are unsigned 32-bit millisecond counters that wrap every
                    // 49.7 days; the subtraction is correct across the wrap only if
                    // it is done in unsigned arithmetic, which this is.
                    s.InputIdleSeconds = (int)((GetTickCount() - li.dwTime) / 1000);
                }
            }

            s.ForegroundPid = -1;
            s.ForegroundProcess = "";
            s.ForegroundTitle = "";
            IntPtr h = GetForegroundWindow();
            if (h != IntPtr.Zero)
            {
                uint fpid;
                GetWindowThreadProcessId(h, out fpid);
                s.ForegroundPid = (int)fpid;
                try { s.ForegroundProcess = Process.GetProcessById((int)fpid).ProcessName; }
                catch (Exception) { s.ForegroundProcess = "(gone)"; }
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                s.ForegroundTitle = sb.ToString();
                s.ForegroundIsFullScreen = IsFullScreen(h);
            }
            return s;
        }

        /// Covers its whole monitor, work area included.
        ///
        /// WHY THIS AND NOT A FULLSCREEN HOOK. Exclusive fullscreen can be detected
        /// properly with IDXGIOutput or the Shell's fullscreen notification, but both
        /// mean loading graphics interfaces or registering shell hooks inside a
        /// process that lives on a machine running kernel-mode anti-cheat. Comparing
        /// a window rectangle to a monitor rectangle uses only user32 read calls that
        /// every window manager and screen reader makes, catches borderless-window
        /// mode (which is what most current games actually use), and cannot be
        /// mistaken for instrumentation. It is deliberately the weaker technique.
        public static bool IsFullScreen(IntPtr h)
        {
            RECT w;
            if (!GetWindowRect(h, out w)) return false;
            IntPtr mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfoW(mon, ref mi)) return false;
            RECT m = mi.rcMonitor;
            return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
        }
    }

    // -------------------------------------------------------------- launchers ---


    /// Reads what the launchers already publish about themselves.
    ///
    /// WHY THIS IS THE STRONGEST SIGNAL IN THE SYSTEM. Steam writes the appid of
    /// the game it is starting to HKCU\Software\Valve\Steam\RunningAppID BEFORE the
    /// game process has initialised its renderer, so this fires during the launcher
    /// splash - seconds ahead of any GPU-side evidence. Measured on spring (probe
    /// p6) with no game running: RunningAppID = 0, six app subkeys, all with
    /// Running = 0, and Counter-Strike 2 (730), Rainbow Six Siege Test Server
    /// (623990), Ready or Not and Ghost Recon Breakpoint present.
    ///
    /// It is also the signal that survives a paused game. A game sitting at a pause
    /// menu draws almost nothing - the naive "utilisation below N per cent" test
    /// calls that idle and hands its VRAM to a job - but RunningAppID stays set for
    /// as long as the process lives.
    ///
    /// Valorant does not use Steam. It is covered by the vgc service instead:
    /// measured on spring, vgk (the kernel driver) is Running with StartType System
    /// and is therefore useless as a signal because it is ALWAYS running, whereas
    /// vgc (the user-mode service) was measured Stopped with StartType Manual and
    /// starts only with the game. vgc is the one to watch. Reading a service's
    /// status is a plain SCM query and is not something an anti-cheat can mistake
    /// for tampering.
    public static class Launchers
    {
        /// The anti cheat service names come from configuration rather than from
        /// a literal here, because the next one to matter will not be called vgc.
        public static LauncherSignals Read(string[] gameProcessNames, string[] antiCheatServices)
        {
            var s = new LauncherSignals();
            s.SteamRunningAppId = 0;
            s.SteamRunningAppName = "";

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("RunningAppID");
                        if (v != null) s.SteamRunningAppId = Convert.ToInt32(v);
                    }
                }
                if (s.SteamRunningAppId != 0)
                {
                    using (RegistryKey a = Registry.CurrentUser.OpenSubKey(
                        @"Software\Valve\Steam\Apps\" + s.SteamRunningAppId.ToString(CultureInfo.InvariantCulture)))
                    {
                        if (a != null)
                        {
                            object n = a.GetValue("Name");
                            if (n != null) s.SteamRunningAppName = n.ToString();
                        }
                    }
                }
                else
                {
                    // Belt and braces: RunningAppID is cleared on a crash in some
                    // Steam builds, but the per-app Running flag is written
                    // separately. Cheap because there were six subkeys, not six
                    // hundred (measured, probe p6).
                    using (RegistryKey apps = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps"))
                    {
                        if (apps != null)
                        {
                            foreach (string sub in apps.GetSubKeyNames())
                            {
                                using (RegistryKey a = apps.OpenSubKey(sub))
                                {
                                    if (a == null) continue;
                                    object r = a.GetValue("Running");
                                    if (r != null && Convert.ToInt32(r) == 1)
                                    {
                                        int id;
                                        if (int.TryParse(sub, out id)) s.SteamRunningAppId = id;
                                        object n = a.GetValue("Name");
                                        if (n != null) s.SteamRunningAppName = n.ToString();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            s.ValorantAntiCheatActive = false;
            if (antiCheatServices != null)
            {
                foreach (string name in antiCheatServices)
                {
                    string n = name.Trim();
                    if (n.Length == 0) continue;
                    try
                    {
                        using (var sc = new ServiceController(n))
                        {
                            if (sc.Status == ServiceControllerStatus.Running ||
                                sc.Status == ServiceControllerStatus.StartPending)
                            {
                                s.ValorantAntiCheatActive = true;
                                s.AntiCheatService = n;
                                break;
                            }
                        }
                    }
                    catch (Exception) { /* not installed on this machine, which is fine */ }
                }
            }

            foreach (string want in gameProcessNames)
            {
                string w = want.Trim();
                if (w.Length == 0) continue;
                try
                {
                    Process[] found = Process.GetProcessesByName(w);
                    if (found.Length > 0) s.GameProcesses.Add(w);
                    foreach (var p in found) p.Dispose();
                }
                catch (Exception) { }
            }
            return s;
        }
    }
}
