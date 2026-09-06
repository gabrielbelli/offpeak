// p8: what vertical CPU, priority and memory limiting actually do on this machine.
//
// Answers the questions the CPU work depends on, by measurement rather than by
// reading the documentation and hoping:
//
//   A  is CpuRate a share of ONE core or of the WHOLE machine
//   B  what does the scheduler do when a hard cap binds
//   C  can the cap be changed on a LIVE job, and how fast does it bite
//   D  weight-based mode, and MinRate/MaxRate with no hard cap
//   E  JOB_OBJECT_LIMIT_PRIORITY_CLASS: does it need a privilege
//   F  priority class set directly on the child instead
//   G  EcoQoS on a desktop Ryzen with no efficiency cores
//   H  a commit cap binding: what does the child SEE
//   I  a working set maximum: does the job page instead of the system
//   J  how cheaply can free memory be read
//
// Usage:
//   p8_cpu.exe            run every experiment
//   p8_cpu.exe spin N S   internal: N busy threads for S seconds
//   p8_cpu.exe eat MiB S  internal: commit MiB and touch it, hold S seconds

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

static class P8
{
    // ---- job object ------------------------------------------------------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr sec, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr info, uint len, out uint got);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const int JobObjectBasicAccountingInformation = 1;
    const int JobObjectBasicLimitInformation = 2;
    const int JobObjectExtendedLimitInformation = 9;
    const int JobObjectCpuRateControlInformation = 15;

    const uint KILL_ON_JOB_CLOSE = 0x2000;
    const uint LIMIT_WORKINGSET = 0x00000001;
    const uint LIMIT_PRIORITY_CLASS = 0x00000020;
    const uint LIMIT_PROCESS_MEMORY = 0x00000100;
    const uint LIMIT_JOB_MEMORY = 0x00000200;

    const uint RATE_ENABLE = 0x1;
    const uint RATE_WEIGHT_BASED = 0x2;
    const uint RATE_HARD_CAP = 0x4;
    const uint RATE_MIN_MAX = 0x10;

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
        public BASIC_LIMIT Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ACCOUNTING
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    // The union is a single DWORD in every mode, so one struct covers all of them.
    [StructLayout(LayoutKind.Sequential)]
    struct CPU_RATE { public uint ControlFlags; public uint Value; }

    // ---- EcoQoS ----------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct POWER_THROTTLING_PROCESS_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr proc, int cls, IntPtr info, uint len);
    const int ProcessPowerThrottling = 4;
    const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    // ---- memory ----------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    static int Cores = Environment.ProcessorCount;

    static void Main(string[] a)
    {
        if (a.Length >= 1 && a[0] == "spin") { Spin(int.Parse(a[1]), int.Parse(a[2])); return; }
        if (a.Length >= 1 && a[0] == "eat") { Eat(int.Parse(a[1]), int.Parse(a[2])); return; }

        Console.WriteLine("machine: {0} logical processors, {1}", Cores, Environment.OSVersion.Version);
        var ms = new MEMORYSTATUSEX(); ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref ms);
        Console.WriteLine("memory: total {0:N0} MiB, avail {1:N0} MiB, load {2}%, commit avail {3:N0} MiB",
            ms.ullTotalPhys / 1048576, ms.ullAvailPhys / 1048576, ms.dwMemoryLoad, ms.ullAvailPageFile / 1048576);
        Console.WriteLine();

        ExpA_HardCapScale();
        ExpC_LiveChange();
        ExpD_WeightAndMinMax();
        ExpE_PriorityViaJob();
        ExpF_PriorityDirect();
        ExpG_EcoQos();
        ExpH_CommitCap();
        ExpI_WorkingSet();
        ExpJ_MemoryReadCost();
    }

    // ------------------------------------------------------------------ helpers

    static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    static IntPtr NewJob()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        var e = new EXT_LIMIT();
        e.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
        Set(job, JobObjectExtendedLimitInformation, e);
        return job;
    }

    static bool Set<T>(IntPtr job, int cls, T val)
    {
        int len = Marshal.SizeOf(typeof(T));
        IntPtr p = Marshal.AllocHGlobal(len);
        try { Marshal.StructureToPtr(val, p, false); return SetInformationJobObject(job, cls, p, (uint)len); }
        finally { Marshal.FreeHGlobal(p); }
    }

    static bool SetRate(IntPtr job, uint flags, uint value, out int err)
    {
        var r = new CPU_RATE(); r.ControlFlags = flags; r.Value = value;
        bool ok = Set(job, JobObjectCpuRateControlInformation, r);
        err = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    static long JobCpu(IntPtr job)
    {
        int len = Marshal.SizeOf(typeof(ACCOUNTING));
        IntPtr p = Marshal.AllocHGlobal(len);
        try
        {
            uint got;
            if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, p, (uint)len, out got)) return 0;
            var acc = (ACCOUNTING)Marshal.PtrToStructure(p, typeof(ACCOUNTING));
            return acc.TotalUserTime + acc.TotalKernelTime;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// Measured machine-wide CPU per cent of the whole job, over one window.
    /// 100 means every logical processor busy with our job for the whole window.
    static double MeasurePct(IntPtr job, int ms)
    {
        long c0 = JobCpu(job);
        var sw = Stopwatch.StartNew();
        Thread.Sleep(ms);
        sw.Stop();
        long c1 = JobCpu(job);
        double cpuSec = (c1 - c0) / 10000000.0;
        return 100.0 * cpuSec / (sw.Elapsed.TotalSeconds * Cores);
    }

    static Process StartSpinner(IntPtr job, int threads, int seconds)
    {
        var psi = new ProcessStartInfo(Self, "spin " + threads + " " + seconds);
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        Process p = Process.Start(psi);
        AssignProcessToJobObject(job, p.Handle);
        return p;
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

    static void Eat(int mib, int seconds)
    {
        Console.Out.Flush();
        var blocks = new List<byte[]>();
        try
        {
            for (int i = 0; i < mib; i++)
            {
                var b = new byte[1048576];
                for (int k = 0; k < b.Length; k += 4096) b[k] = 1;   // touch, so it is resident
                blocks.Add(b);
                if (i % 128 == 0) Console.WriteLine("eat: {0} MiB committed", i);
            }
            Console.WriteLine("eat: {0} MiB committed, holding", mib);
            Thread.Sleep(seconds * 1000);
        }
        catch (Exception ex)
        {
            Console.WriteLine("eat: FAILED at {0} MiB with {1}: {2}", blocks.Count, ex.GetType().Name, ex.Message);
            Environment.Exit(3);
        }
    }

    // ------------------------------------------------------------------ A

    static void ExpA_HardCapScale()
    {
        Console.WriteLine("=== A: is CpuRate a share of ONE core or of the WHOLE machine ===");
        IntPtr job = NewJob();
        Process p = StartSpinner(job, Cores, 40);
        Thread.Sleep(1500);
        Console.WriteLine("  uncapped, {0} spinning threads: {1:N1}% of the machine",
            Cores, MeasurePct(job, 2000));
        int[] caps = new int[] { 50, 25, 10, 5 };
        foreach (int c in caps)
        {
            int err;
            bool ok = SetRate(job, RATE_ENABLE | RATE_HARD_CAP, (uint)(c * 100), out err);
            Thread.Sleep(500);
            double pct = MeasurePct(job, 2500);
            Console.WriteLine("  hard cap {0,3}% (CpuRate={1,5}): set={2} err={3} -> measured {4:N1}% of the machine = {5:N2} cores busy",
                c, c * 100, ok, err, pct, pct * Cores / 100.0);
        }
        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ C

    static void ExpC_LiveChange()
    {
        Console.WriteLine("=== C: changing the cap on a LIVE job, and how fast it bites ===");
        IntPtr job = NewJob();
        Process p = StartSpinner(job, Cores, 40);
        Thread.Sleep(1500);
        int err;
        SetRate(job, RATE_ENABLE | RATE_HARD_CAP, 8000, out err);
        Thread.Sleep(1000);
        Console.WriteLine("  at 80%: {0:N1}%", MeasurePct(job, 2000));

        // How long from the call returning to the job actually being down there.
        var sw = Stopwatch.StartNew();
        bool ok = SetRate(job, RATE_ENABLE | RATE_HARD_CAP, 500, out err);
        long callMs = sw.ElapsedMilliseconds;
        // Sample in 200 ms windows until it is under 10%.
        long biteMs = -1;
        for (int i = 0; i < 25; i++)
        {
            double pct = MeasurePct(job, 200);
            if (pct < 10.0) { biteMs = sw.ElapsedMilliseconds; break; }
        }
        Console.WriteLine("  set to 5% on the live job: ok={0} err={1}, call returned in {2} ms, under 10% after {3} ms",
            ok, err, callMs, biteMs);
        Console.WriteLine("  settled at 5%: {0:N1}%", MeasurePct(job, 2000));

        // And back up again, which is the other half of the tray requirement.
        sw.Restart();
        SetRate(job, RATE_ENABLE | RATE_HARD_CAP, 9000, out err);
        long upMs = -1;
        for (int i = 0; i < 25; i++)
            if (MeasurePct(job, 200) > 50.0) { upMs = sw.ElapsedMilliseconds; break; }
        Console.WriteLine("  raised back to 90%: over 50% again after {0} ms", upMs);

        // Removing the limit entirely.
        sw.Restart();
        ok = SetRate(job, 0, 0, out err);
        Console.WriteLine("  clearing rate control (ControlFlags=0): ok={0} err={1} -> {2:N1}%", ok, err, MeasurePct(job, 2000));
        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ D

    static void ExpD_WeightAndMinMax()
    {
        Console.WriteLine("=== D: weight-based, and MinRate/MaxRate without a hard cap ===");
        IntPtr job = NewJob();
        Process p = StartSpinner(job, Cores, 40);
        Thread.Sleep(1500);
        int err;
        bool ok = SetRate(job, RATE_ENABLE | RATE_WEIGHT_BASED, 1, out err);
        Thread.Sleep(500);
        Console.WriteLine("  weight 1 (lowest), machine otherwise idle: set={0} err={1} -> {2:N1}%", ok, err, MeasurePct(job, 2500));
        ok = SetRate(job, RATE_ENABLE | RATE_WEIGHT_BASED, 9, out err);
        Thread.Sleep(500);
        Console.WriteLine("  weight 9 (highest): set={0} err={1} -> {2:N1}%", ok, err, MeasurePct(job, 2500));

        // MIN_MAX packs two WORDs into the same DWORD: low is MinRate, high is MaxRate.
        uint minmax = (uint)(1000) | ((uint)(2000) << 16);
        ok = SetRate(job, RATE_ENABLE | RATE_MIN_MAX, minmax, out err);
        Thread.Sleep(500);
        Console.WriteLine("  min 10% / max 20%: set={0} err={1} -> {2:N1}%", ok, err, MeasurePct(job, 2500));

        // Documented as mutually exclusive. Confirm it actually refuses.
        ok = SetRate(job, RATE_ENABLE | RATE_MIN_MAX | RATE_HARD_CAP, minmax, out err);
        Console.WriteLine("  MIN_MAX + HARD_CAP together: set={0} err={1} (docs say this is invalid)", ok, err);
        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ E

    static void ExpE_PriorityViaJob()
    {
        Console.WriteLine("=== E: JOB_OBJECT_LIMIT_PRIORITY_CLASS, and the privilege it documents ===");
        Console.WriteLine("  this process is elevated: {0}", IsElevated());
        IntPtr job = NewJob();
        Process p = StartSpinner(job, 2, 20);
        Thread.Sleep(800);
        var e = new EXT_LIMIT();
        e.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_PRIORITY_CLASS;
        e.Basic.PriorityClass = 0x00000040;   // IDLE_PRIORITY_CLASS
        bool ok = Set(job, JobObjectExtendedLimitInformation, e);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Console.WriteLine("  set IDLE_PRIORITY_CLASS on the live job: ok={0} err={1}", ok, err);
        try { p.Refresh(); Console.WriteLine("  child priority now: {0}", p.PriorityClass); }
        catch (Exception ex) { Console.WriteLine("  could not read child priority: " + ex.Message); }
        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }
        Console.WriteLine();
    }

    static bool IsElevated()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pr = new System.Security.Principal.WindowsPrincipal(id);
            return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ F

    static void ExpF_PriorityDirect()
    {
        Console.WriteLine("=== F: priority class set on the CHILD PROCESS rather than through the job ===");
        IntPtr job = NewJob();
        Process p = StartSpinner(job, 2, 20);
        Thread.Sleep(800);
        try
        {
            p.PriorityClass = ProcessPriorityClass.Idle;
            p.Refresh();
            Console.WriteLine("  Process.PriorityClass = Idle: ok, reads back {0}", p.PriorityClass);
        }
        catch (Exception ex) { Console.WriteLine("  FAILED: {0}: {1}", ex.GetType().Name, ex.Message); }
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
            p.Refresh();
            Console.WriteLine("  and back to BelowNormal on the live child: reads back {0}", p.PriorityClass);
        }
        catch (Exception ex) { Console.WriteLine("  FAILED: {0}: {1}", ex.GetType().Name, ex.Message); }
        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ G

    static void ExpG_EcoQos()
    {
        Console.WriteLine("=== G: EcoQoS on this desktop Ryzen (no efficiency cores) ===");
        IntPtr job = NewJob();
        Process p = StartSpinner(job, Cores, 25);
        Thread.Sleep(1500);
        double before = MeasurePct(job, 2500);
        var st = new POWER_THROTTLING_PROCESS_STATE();
        st.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
        st.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        st.StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        int len = Marshal.SizeOf(typeof(POWER_THROTTLING_PROCESS_STATE));
        IntPtr ptr = Marshal.AllocHGlobal(len);
        Marshal.StructureToPtr(st, ptr, false);
        bool ok = SetProcessInformation(p.Handle, ProcessPowerThrottling, ptr, (uint)len);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(ptr);
        Thread.Sleep(1500);
        double after = MeasurePct(job, 2500);
        Console.WriteLine("  SetProcessInformation(EcoQoS): ok={0} err={1}", ok, err);
        Console.WriteLine("  throughput proxy, machine CPU%: before {0:N1} -> after {1:N1}", before, after);
        Console.WriteLine("  (CPU per cent does not fall under EcoQoS; a clock drop would show as the same");
        Console.WriteLine("   per cent doing less work, so this only proves the call is accepted.)");
        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ H

    static void ExpH_CommitCap()
    {
        Console.WriteLine("=== H: what a child SEES when a job commit cap binds ===");
        IntPtr job = NewJob();
        var e = new EXT_LIMIT();
        e.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_JOB_MEMORY;
        e.JobMemoryLimit = new UIntPtr(256UL * 1024 * 1024);
        bool ok = Set(job, JobObjectExtendedLimitInformation, e);
        Console.WriteLine("  job commit cap 256 MiB set at creation: ok={0} err={1}", ok, ok ? 0 : Marshal.GetLastWin32Error());

        var psi = new ProcessStartInfo(Self, "eat 512 3");
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        Process p = Process.Start(psi);
        AssignProcessToJobObject(job, p.Handle);
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        string[] lines = outp.Replace("\r", "").Split('\n');
        for (int i = Math.Max(0, lines.Length - 6); i < lines.Length; i++)
            if (lines[i].Length > 0) Console.WriteLine("    child> " + lines[i]);
        Console.WriteLine("  child exit code {0}", SafeExit(p));

        // And the question the tray asks: can the cap be RAISED on a live job.
        IntPtr job2 = NewJob();
        var e2 = new EXT_LIMIT();
        e2.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_JOB_MEMORY;
        e2.JobMemoryLimit = new UIntPtr(2048UL * 1024 * 1024);
        Set(job2, JobObjectExtendedLimitInformation, e2);
        Process q = null;
        try
        {
            var psi2 = new ProcessStartInfo(Self, "eat 64 6");
            psi2.UseShellExecute = false; psi2.CreateNoWindow = true;
            q = Process.Start(psi2);
            AssignProcessToJobObject(job2, q.Handle);
            Thread.Sleep(1500);
            e2.JobMemoryLimit = new UIntPtr(512UL * 1024 * 1024);
            bool ok2 = Set(job2, JobObjectExtendedLimitInformation, e2);
            Console.WriteLine("  lowering the commit cap on a LIVE job (2048 -> 512 MiB): ok={0} err={1}",
                ok2, ok2 ? 0 : Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
        CloseHandle(job2); CloseHandle(job);
        Console.WriteLine();
    }

    static string SafeExit(Process p)
    {
        try { return p.ExitCode.ToString(CultureInfo.InvariantCulture); } catch (Exception) { return "?"; }
    }

    // ------------------------------------------------------------------ I

    static void ExpI_WorkingSet()
    {
        Console.WriteLine("=== I: a working set MAXIMUM, on a machine that is not short of memory ===");
        IntPtr job = NewJob();
        var e = new EXT_LIMIT();
        e.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_WORKINGSET;
        e.Basic.MinimumWorkingSetSize = new UIntPtr(32UL * 1024 * 1024);
        e.Basic.MaximumWorkingSetSize = new UIntPtr(192UL * 1024 * 1024);
        bool ok = Set(job, JobObjectExtendedLimitInformation, e);
        Console.WriteLine("  working set 32..192 MiB set at creation: ok={0} err={1}", ok, ok ? 0 : Marshal.GetLastWin32Error());

        var psi = new ProcessStartInfo(Self, "eat 512 5");
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        Process p = Process.Start(psi);
        AssignProcessToJobObject(job, p.Handle);
        long peakWs = 0, peakPriv = 0;
        for (int i = 0; i < 60 && !p.HasExited; i++)
        {
            try { p.Refresh(); if (p.WorkingSet64 > peakWs) peakWs = p.WorkingSet64;
                  if (p.PrivateMemorySize64 > peakPriv) peakPriv = p.PrivateMemorySize64; }
            catch (Exception) { break; }
            Thread.Sleep(200);
        }
        p.WaitForExit(20000);
        Console.WriteLine("  child committed 512 MiB. peak WORKING SET {0:N0} MiB, peak PRIVATE (commit) {1:N0} MiB, exit {2}",
            peakWs / 1048576, peakPriv / 1048576, SafeExit(p));
        Console.WriteLine("  (if the working set stayed near 192 the job was trimmed and paged itself, which is the point)");

        // Live change, again, because the tray needs it.
        IntPtr job2 = NewJob();
        Process q = StartSpinner(job2, 1, 8);
        Thread.Sleep(500);
        var e2 = new EXT_LIMIT();
        e2.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_WORKINGSET;
        e2.Basic.MinimumWorkingSetSize = new UIntPtr(16UL * 1024 * 1024);
        e2.Basic.MaximumWorkingSetSize = new UIntPtr(64UL * 1024 * 1024);
        bool ok2 = Set(job2, JobObjectExtendedLimitInformation, e2);
        Console.WriteLine("  setting a working set range on a LIVE job: ok={0} err={1}", ok2, ok2 ? 0 : Marshal.GetLastWin32Error());
        // And clearing it again.
        var e3 = new EXT_LIMIT();
        e3.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
        bool ok3 = Set(job2, JobObjectExtendedLimitInformation, e3);
        Console.WriteLine("  clearing it again on the live job: ok={0} err={1}", ok3, ok3 ? 0 : Marshal.GetLastWin32Error());
        CloseHandle(job2); CloseHandle(job);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ J

    static void ExpJ_MemoryReadCost()
    {
        Console.WriteLine("=== J: how cheaply can 'is there headroom right now' be read ===");
        var ms = new MEMORYSTATUSEX(); ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) GlobalMemoryStatusEx(ref ms);
        sw.Stop();
        Console.WriteLine("  GlobalMemoryStatusEx x1000: {0} ms total, {1:N3} ms each",
            sw.ElapsedMilliseconds, sw.Elapsed.TotalMilliseconds / 1000.0);
        Console.WriteLine("  availPhys {0:N0} MiB, memoryLoad {1}%, availPageFile (commit headroom) {2:N0} MiB",
            ms.ullAvailPhys / 1048576, ms.dwMemoryLoad, ms.ullAvailPageFile / 1048576);
        Console.WriteLine();
    }
}
