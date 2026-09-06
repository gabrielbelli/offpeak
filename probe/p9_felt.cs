// p9: is a background job at idle priority actually FELT, and what does a cap add.
//
// p8 established that a job object can be capped, and that the cap moves on a
// live job in about 200 ms. It did not answer the question the owner actually
// cares about, which is not "how many per cent" but "did I notice".
//
// THE MEASUREMENT. A victim process at NORMAL priority does fixed units of work
// and records how long each unit took. That is the closest cheap analogue of a
// frame: a game misses a frame when a unit of work that should take 16 ms takes
// longer. The interesting number is not the mean, which hides everything, but
// the 99th percentile, because one late frame in a hundred is a stutter a person
// sees.
//
// Four conditions, same victim, same units:
//   0  nothing else running                  the floor
//   1  16 spinners at NORMAL priority        what a naive background job costs
//   2  16 spinners at IDLE priority          what priority alone buys
//   3  16 spinners at IDLE + 25% hard cap    what the ladder buys
//   4  16 spinners at IDLE + 5% hard cap     the "somebody is gaming" rung
//
// Usage:
//   p9_felt.exe                run every condition
//   p9_felt.exe victim S       internal: be the victim for S seconds
//   p9_felt.exe spin N S       internal: N busy threads for S seconds

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

static class P9
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
    struct EXT_LIMIT
    {
        public BASIC_LIMIT Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CPU_RATE { public uint ControlFlags; public uint Value; }

    static int Cores = Environment.ProcessorCount;
    static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    static void Main(string[] a)
    {
        if (a.Length >= 1 && a[0] == "victim") { Victim(int.Parse(a[1])); return; }
        if (a.Length >= 1 && a[0] == "spin") { Spin(int.Parse(a[1]), int.Parse(a[2])); return; }

        Console.WriteLine("machine: {0} logical processors, elevated={1}", Cores, IsElevated());
        Console.WriteLine("victim: NORMAL priority, one thread, fixed units. p99 is the number that matters.");
        Console.WriteLine();
        Console.WriteLine("{0,-42} {1}", "condition", "unit latency ms:  p50     p95     p99      max   units");

        Run("0  nothing else running", 0, ProcessPriorityClass.Normal, 0);
        Run("1  16 spinners at NORMAL", Cores, ProcessPriorityClass.Normal, 0);
        Run("2  16 spinners at IDLE", Cores, ProcessPriorityClass.Idle, 0);
        Run("3  16 spinners at IDLE + 25% hard cap", Cores, ProcessPriorityClass.Idle, 2500);
        Run("4  16 spinners at IDLE + 5% hard cap", Cores, ProcessPriorityClass.Idle, 500);
        Run("5  16 spinners at NORMAL + 25% hard cap", Cores, ProcessPriorityClass.Normal, 2500);
    }

    static void Run(string label, int spinners, ProcessPriorityClass prio, uint cpuRate)
    {
        IntPtr job = IntPtr.Zero;
        Process load = null;
        if (spinners > 0)
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            var e = new EXT_LIMIT(); e.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
            SetStruct(job, JobObjectExtendedLimitInformation, e);
            var psi = new ProcessStartInfo(Self, "spin " + spinners + " 14");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            load = Process.Start(psi);
            AssignProcessToJobObject(job, load.Handle);
            try { load.PriorityClass = prio; } catch (Exception) { }
            if (cpuRate > 0)
            {
                var r = new CPU_RATE(); r.ControlFlags = RATE_ENABLE | RATE_HARD_CAP; r.Value = cpuRate;
                SetStruct(job, JobObjectCpuRateControlInformation, r);
            }
            Thread.Sleep(1200);   // let it get going before the victim starts
        }

        var vpsi = new ProcessStartInfo(Self, "victim 8");
        vpsi.UseShellExecute = false; vpsi.CreateNoWindow = true; vpsi.RedirectStandardOutput = true;
        Process v = Process.Start(vpsi);
        v.PriorityClass = ProcessPriorityClass.Normal;
        string outp = v.StandardOutput.ReadToEnd();
        v.WaitForExit(30000);

        if (job != IntPtr.Zero) CloseHandle(job);
        if (load != null) { try { load.WaitForExit(3000); } catch (Exception) { } }

        Console.WriteLine("{0,-42} {1}", label, outp.Trim());
        Thread.Sleep(500);
    }

    static bool SetStruct<T>(IntPtr job, int cls, T val)
    {
        int len = Marshal.SizeOf(typeof(T));
        IntPtr p = Marshal.AllocHGlobal(len);
        try { Marshal.StructureToPtr(val, p, false); return SetInformationJobObject(job, cls, p, (uint)len); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// One unit is a fixed amount of arithmetic, sized to take roughly 8 ms on an
    /// unloaded machine, with a 8 ms sleep between units. That is deliberately
    /// frame-shaped: a short burst of work on a deadline, repeatedly.
    static void Victim(int seconds)
    {
        var lat = new List<double>();
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        double x = 1.000001;
        var sw = new Stopwatch();
        while (DateTime.UtcNow < until)
        {
            sw.Restart();
            for (int k = 0; k < 3000000; k++) x = x * 1.0000001 + 0.0000001;
            sw.Stop();
            lat.Add(sw.Elapsed.TotalMilliseconds);
            Thread.Sleep(8);
        }
        GC.KeepAlive(x);
        lat.Sort();
        Console.WriteLine("{0,7:N2} {1,7:N2} {2,7:N2} {3,8:N2}  {4,6}",
            Pick(lat, 0.50), Pick(lat, 0.95), Pick(lat, 0.99), lat[lat.Count - 1], lat.Count);
    }

    static double Pick(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        int i = (int)Math.Floor(q * (sorted.Count - 1));
        return sorted[i];
    }

    static void Spin(int threads, int seconds)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        var ts = new List<Thread>();
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(delegate()
            {
                double x = 1.000001;
                while (DateTime.UtcNow < until) { for (int k = 0; k < 200000; k++) x = x * 1.0000001 + 0.0000001; }
                GC.KeepAlive(x);
            });
            t.IsBackground = true; t.Start(); ts.Add(t);
        }
        foreach (Thread t in ts) t.Join();
    }

    static bool IsElevated()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }
}
