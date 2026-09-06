// p10: the sceptical version of p9.
//
// p9's victim was one thread doing register arithmetic on a sixteen-thread
// machine, so fifteen threads were always free and the result was too flattering:
// idle-priority spinners were invisible. Two things a real game does that p9 did
// not test:
//
//   1. IT USES MANY THREADS. With eight victim threads there is genuine
//      contention for runnable slots, which is where priority inversion and
//      scheduling quantum effects show up.
//   2. IT TOUCHES MEMORY. A 5700X3D has 96 MiB of L3. A background job that
//      streams memory evicts the victim's working set from that cache, and cache
//      pollution is not something priority protects against: the polluting thread
//      only has to run once to have already evicted the line.
//
// So this runs the same latency measurement with an eight-thread victim against a
// CACHE-THRASHING background load, which is the honest worst case for the claim
// that idle priority alone is enough.
//
// Usage:
//   p10_felt_hard.exe
//   p10_felt_hard.exe victim T S     internal: T threads for S seconds
//   p10_felt_hard.exe thrash N S     internal: N memory-streaming threads

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

static class P10
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr sec, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const int JobObjectExtendedLimitInformation = 9;
    const int JobObjectCpuRateControlInformation = 15;
    const uint KILL_ON_JOB_CLOSE = 0x2000;
    const uint RATE_ENABLE = 0x1, RATE_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct BASIC_LIMIT
    {
        public long A, B; public uint LimitFlags;
        public UIntPtr MinWs, MaxWs; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong r, w, o, rt, wt, ot; }
    [StructLayout(LayoutKind.Sequential)]
    struct EXT_LIMIT
    {
        public BASIC_LIMIT Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcess, PeakJob;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CPU_RATE { public uint ControlFlags; public uint Value; }

    static int Cores = Environment.ProcessorCount;
    static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    static void Main(string[] a)
    {
        if (a.Length >= 1 && a[0] == "victim") { Victim(int.Parse(a[1]), int.Parse(a[2])); return; }
        if (a.Length >= 1 && a[0] == "thrash") { Thrash(int.Parse(a[1]), int.Parse(a[2])); return; }

        int vt = Cores / 2;
        Console.WriteLine("machine: {0} logical processors. Victim: {1} threads at NORMAL, each doing", Cores, vt);
        Console.WriteLine("fixed frame-shaped units over a 48 MiB array. Load: memory-streaming, 128 MiB per thread.");
        Console.WriteLine();
        Console.WriteLine("{0,-46} {1}", "condition", "unit ms:   p50     p95     p99      max   units");

        Run("0  nothing else running", vt, 0, ProcessPriorityClass.Normal, 0);
        Run("1  16 thrashers at NORMAL", vt, Cores, ProcessPriorityClass.Normal, 0);
        Run("2  16 thrashers at IDLE", vt, Cores, ProcessPriorityClass.Idle, 0);
        Run("3  16 thrashers at IDLE + 50% cap", vt, Cores, ProcessPriorityClass.Idle, 5000);
        Run("4  16 thrashers at IDLE + 25% cap", vt, Cores, ProcessPriorityClass.Idle, 2500);
        Run("5  16 thrashers at IDLE + 10% cap", vt, Cores, ProcessPriorityClass.Idle, 1000);
        Run("6  16 thrashers at IDLE + 5% cap", vt, Cores, ProcessPriorityClass.Idle, 500);
    }

    static void Run(string label, int victimThreads, int loadThreads, ProcessPriorityClass prio, uint rate)
    {
        IntPtr job = IntPtr.Zero; Process load = null;
        if (loadThreads > 0)
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            var e = new EXT_LIMIT(); e.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
            SetStruct(job, JobObjectExtendedLimitInformation, e);
            var psi = new ProcessStartInfo(Self, "thrash " + loadThreads + " 16");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            load = Process.Start(psi);
            AssignProcessToJobObject(job, load.Handle);
            try { load.PriorityClass = prio; } catch (Exception) { }
            if (rate > 0)
            {
                var r = new CPU_RATE(); r.ControlFlags = RATE_ENABLE | RATE_HARD_CAP; r.Value = rate;
                SetStruct(job, JobObjectCpuRateControlInformation, r);
            }
            Thread.Sleep(2000);
        }
        var vpsi = new ProcessStartInfo(Self, "victim " + victimThreads + " 8");
        vpsi.UseShellExecute = false; vpsi.CreateNoWindow = true; vpsi.RedirectStandardOutput = true;
        Process v = Process.Start(vpsi);
        v.PriorityClass = ProcessPriorityClass.Normal;
        string outp = v.StandardOutput.ReadToEnd();
        v.WaitForExit(40000);
        if (job != IntPtr.Zero) CloseHandle(job);
        if (load != null) { try { load.WaitForExit(3000); } catch (Exception) { } }
        Console.WriteLine("{0,-46} {1}", label, outp.Trim());
        Thread.Sleep(500);
    }

    static bool SetStruct<T>(IntPtr job, int cls, T val)
    {
        int len = Marshal.SizeOf(typeof(T));
        IntPtr p = Marshal.AllocHGlobal(len);
        try { Marshal.StructureToPtr(val, p, false); return SetInformationJobObject(job, cls, p, (uint)len); }
        finally { Marshal.FreeHGlobal(p); }
    }

    static readonly object Gate = new object();
    static List<double> Lat = new List<double>();

    /// Frame-shaped: a burst of work over a 48 MiB array, then a short sleep.
    /// The array is big enough to matter to the cache and small enough that an
    /// unloaded machine keeps most of it in the 96 MiB L3.
    static void Victim(int threads, int seconds)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        var ts = new List<Thread>();
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(delegate()
            {
                int n = 48 * 1024 * 1024 / 4;
                var buf = new int[n];
                for (int k = 0; k < n; k += 16) buf[k] = k;
                var sw = new Stopwatch();
                var mine = new List<double>();
                while (DateTime.UtcNow < until)
                {
                    sw.Restart();
                    int acc = 0;
                    for (int k = 0; k < n; k += 16) acc += buf[k];
                    buf[0] = acc;
                    sw.Stop();
                    mine.Add(sw.Elapsed.TotalMilliseconds);
                    Thread.Sleep(4);
                }
                lock (Gate) Lat.AddRange(mine);
            });
            t.IsBackground = true; t.Start(); ts.Add(t);
        }
        foreach (Thread t in ts) t.Join();
        Lat.Sort();
        if (Lat.Count == 0) { Console.WriteLine("no samples"); return; }
        Console.WriteLine("{0,8:N2} {1,7:N2} {2,7:N2} {3,8:N2}  {4,6}",
            Pick(0.50), Pick(0.95), Pick(0.99), Lat[Lat.Count - 1], Lat.Count);
    }

    static double Pick(double q) { return Lat[(int)Math.Floor(q * (Lat.Count - 1))]; }

    /// Streams 128 MiB per thread, which is more than the whole L3, so it evicts
    /// whatever the victim had cached. This is the load priority cannot protect
    /// against by itself.
    static void Thrash(int threads, int seconds)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        var ts = new List<Thread>();
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(delegate()
            {
                int n = 128 * 1024 * 1024 / 4;
                var buf = new int[n];
                int acc = 0;
                while (DateTime.UtcNow < until)
                    for (int k = 0; k < n && DateTime.UtcNow < until; k += 16) { buf[k] = k ^ acc; acc += buf[k]; }
                GC.KeepAlive(acc);
            });
            t.IsBackground = true; t.Start(); ts.Add(t);
        }
        foreach (Thread t in ts) t.Join();
    }
}
