// p11: which of these mechanisms need a privilege, and which do not.
//
// The agent runs as SYSTEM from the boot task, which has every privilege there
// is, so the elevated answer is not the interesting one. The interesting one is
// what an ORDINARY user gets, because somebody running the tray by hand out of
// the contained directory has a filtered token with no
// SeIncreaseBasePriorityPrivilege, and the documentation for
// JOB_OBJECT_LIMIT_PRIORITY_CLASS says in as many words that the caller must
// enable that privilege.
//
// Writes one line per question to the file named in argv[0], because runas
// launches detached and its console output goes nowhere.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class P11
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
    const uint LIMIT_WORKINGSET = 0x1, LIMIT_PRIORITY_CLASS = 0x20, LIMIT_JOB_MEMORY = 0x200;
    const uint RATE_ENABLE = 0x1, RATE_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct BASIC_LIMIT
    {
        public long A, B; public uint LimitFlags; public UIntPtr MinWs, MaxWs;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
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

    static StringBuilder Log = new StringBuilder();
    static void Say(string f, params object[] a) { Log.AppendLine(string.Format(f, a)); }

    static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "spin")
        {
            DateTime u = DateTime.UtcNow.AddSeconds(int.Parse(args[1]));
            double x = 1.1; while (DateTime.UtcNow < u) { for (int k = 0; k < 100000; k++) x = x * 1.0000001 + 1e-7; }
            return;
        }
        string outPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "p11.txt");

        var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        bool elevated = new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Say("identity      : {0}", id.Name);
        Say("elevated      : {0}", elevated);
        Say("token         : {0}", id.ImpersonationLevel);

        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        var basic = new EXT_LIMIT(); basic.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
        Say("create job + KILL_ON_JOB_CLOSE : ok={0} err={1}",
            Set(job, JobObjectExtendedLimitInformation, basic), Marshal.GetLastWin32Error());

        var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "spin 6");
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        Process p = Process.Start(psi);
        AssignProcessToJobObject(job, p.Handle);
        Thread.Sleep(400);

        var r = new CPU_RATE(); r.ControlFlags = RATE_ENABLE | RATE_HARD_CAP; r.Value = 1000;
        Say("CPU rate control, 10% hard cap : ok={0} err={1}",
            Set(job, JobObjectCpuRateControlInformation, r), Marshal.GetLastWin32Error());

        var pri = new EXT_LIMIT();
        pri.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_PRIORITY_CLASS;
        pri.Basic.PriorityClass = 0x40;   // IDLE_PRIORITY_CLASS
        Say("job limit IDLE_PRIORITY_CLASS  : ok={0} err={1}",
            Set(job, JobObjectExtendedLimitInformation, pri), Marshal.GetLastWin32Error());

        var ws = new EXT_LIMIT();
        ws.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_WORKINGSET;
        ws.Basic.MinWs = new UIntPtr(16UL * 1024 * 1024);
        ws.Basic.MaxWs = new UIntPtr(256UL * 1024 * 1024);
        Say("job limit WORKINGSET 16..256M  : ok={0} err={1}",
            Set(job, JobObjectExtendedLimitInformation, ws), Marshal.GetLastWin32Error());

        try { p.PriorityClass = ProcessPriorityClass.Idle; p.Refresh();
              Say("Process.PriorityClass = Idle   : ok=True reads back {0}", p.PriorityClass); }
        catch (Exception ex) { Say("Process.PriorityClass = Idle   : FAILED {0}", ex.Message); }

        CloseHandle(job);
        try { p.WaitForExit(2000); } catch (Exception) { }

        // PHASE 2. The documented requirement for JOB_OBJECT_LIMIT_PRIORITY_CLASS
        // is SE_INC_BASE_PRIORITY_NAME, which an ordinary user's token does not
        // carry. Rather than arrange a second logon, remove the privilege from
        // THIS token -- SE_PRIVILEGE_REMOVED is irreversible for the process and
        // is exactly what a filtered token looks like -- and ask again.
        Say("");
        Say("--- with SeIncreaseBasePriorityPrivilege REMOVED from this token ---");
        Say("remove privilege               : {0}", RemovePrivilege("SeIncreaseBasePriorityPrivilege"));
        IntPtr job2 = CreateJobObjectW(IntPtr.Zero, null);
        var b2 = new EXT_LIMIT(); b2.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
        Set(job2, JobObjectExtendedLimitInformation, b2);
        var psi2 = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "spin 5");
        psi2.UseShellExecute = false; psi2.CreateNoWindow = true;
        Process p2 = Process.Start(psi2);
        AssignProcessToJobObject(job2, p2.Handle);
        Thread.Sleep(300);
        var r2 = new CPU_RATE(); r2.ControlFlags = RATE_ENABLE | RATE_HARD_CAP; r2.Value = 1000;
        Say("CPU rate control, 10% hard cap : ok={0} err={1}",
            Set(job2, JobObjectCpuRateControlInformation, r2), Marshal.GetLastWin32Error());
        var pri2 = new EXT_LIMIT();
        pri2.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_PRIORITY_CLASS;
        pri2.Basic.PriorityClass = 0x40;
        Say("job limit IDLE_PRIORITY_CLASS  : ok={0} err={1}",
            Set(job2, JobObjectExtendedLimitInformation, pri2), Marshal.GetLastWin32Error());
        var ws2 = new EXT_LIMIT();
        ws2.Basic.LimitFlags = KILL_ON_JOB_CLOSE | LIMIT_WORKINGSET;
        ws2.Basic.MinWs = new UIntPtr(16UL * 1024 * 1024);
        ws2.Basic.MaxWs = new UIntPtr(256UL * 1024 * 1024);
        Say("job limit WORKINGSET 16..256M  : ok={0} err={1}",
            Set(job2, JobObjectExtendedLimitInformation, ws2), Marshal.GetLastWin32Error());
        try { p2.PriorityClass = ProcessPriorityClass.Idle; p2.Refresh();
              Say("Process.PriorityClass = Idle   : ok=True reads back {0}", p2.PriorityClass); }
        catch (Exception ex) { Say("Process.PriorityClass = Idle   : FAILED {0}", ex.Message); }
        CloseHandle(job2);
        try { p2.WaitForExit(2000); } catch (Exception) { }

        File.WriteAllText(outPath, Log.ToString(), new UTF8Encoding(false));
        Console.Write(Log.ToString());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValueW(string sys, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8;
    const uint SE_PRIVILEGE_REMOVED = 0x4;

    static string RemovePrivilege(string name)
    {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tok))
            return "OpenProcessToken failed " + Marshal.GetLastWin32Error();
        LUID luid;
        if (!LookupPrivilegeValueW(null, name, out luid))
            return "LookupPrivilegeValue failed " + Marshal.GetLastWin32Error();
        var tp = new TOKEN_PRIVILEGES();
        tp.Count = 1; tp.Luid = luid; tp.Attributes = SE_PRIVILEGE_REMOVED;
        bool ok = AdjustTokenPrivileges(tok, false, ref tp, (uint)Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)),
                                        IntPtr.Zero, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        return ok && err == 0 ? "removed" : ("ok=" + ok + " err=" + err);
    }

    static bool Set<T>(IntPtr job, int cls, T val)
    {
        int len = Marshal.SizeOf(typeof(T));
        IntPtr q = Marshal.AllocHGlobal(len);
        try { Marshal.StructureToPtr(val, q, false); return SetInformationJobObject(job, cls, q, (uint)len); }
        finally { Marshal.FreeHGlobal(q); }
    }
}
