// Entry point.
//
// Modes exist so the two claims this proof of concept has to prove can each be
// checked without a tray icon and without a human at the console:
//
//   --once       print one full signal snapshot as JSON and exit
//   --watch N    print one line per second for N seconds and exit
//   --calibrate  append every signal to a CSV for N minutes and exit
//   --serve      the agent and its listener, headless, until Ctrl-C
//   --presence   report this session's signals to an agent running at boot
//   --limits     prove the CPU cap, the priority and the memory cap on THIS
//                machine, using the same JobRunner the tray uses, and exit
//   --spin N S   internal, the load --limits measures
//   --tray       the actual product (default)
//
// --watch and --calibrate exit on their own. That is not decoration: it is what
// makes the thing safe to drive over SSH against somebody's gaming PC without
// leaving a process behind.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace IdleGpu
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
        const int ATTACH_PARENT_PROCESS = -1;
        const int STD_OUTPUT_HANDLE = -11;

        /// Only the WINDOWS SUBSYSTEM build (idlegpuw.exe) needs this, and only for
        /// the diagnostic modes somebody might run it in by hand. The console build
        /// (idlegpu.exe) already has a console and takes the early return below.
        ///
        /// WHY THERE ARE TWO BINARIES AT ALL. A single winexe was tried and is
        /// unusable from a shell: a Windows-subsystem process is not waited for,
        /// so measured over SSH on spring `idlegpu service list` printed nothing,
        /// set no exit code, and dumped its output into the middle of the next
        /// command. Redirecting to a file produced zero bytes. AttachConsole does
        /// not fix that, because the problem is that the shell has already returned.
        /// build.ps1 emits both subsystems from these same sources, the way
        /// python.exe and pythonw.exe do.
        static void BorrowParentConsole()
        {
            try
            {
                // ONLY when there is nowhere to write yet.
                //
                // The defect this guard prevents, found by running the client over
                // SSH: a shell that captures or redirects this process's output
                // hands it a real pipe as its standard output handle, and
                // AttachConsole then succeeds anyway (there is a console further up
                // the tree) so the code below replaced that pipe with the console
                // screen buffer. Every line the client printed went to a buffer
                // nobody was reading and `idlegpu status | Select-String` returned
                // nothing at all, with no error. If a handle is already there, it is
                // the one the caller wants written to.
                IntPtr existing = GetStdHandle(STD_OUTPUT_HANDLE);
                if (existing != IntPtr.Zero && existing != new IntPtr(-1)) return;
                if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
                var so = new StreamWriter(Console.OpenStandardOutput());
                so.AutoFlush = true;
                Console.SetOut(so);
                var se = new StreamWriter(Console.OpenStandardError());
                se.AutoFlush = true;
                Console.SetError(se);
            }
            catch (Exception) { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // .NET Framework 4.8 with a TargetFrameworkAttribute defaults
            // ServicePointManager.SecurityProtocol to SystemDefault, which is
            // correct. This pins it anyway, because the value is process wide, it
            // is one deleted attribute away from Ssl3|Tls (measured on spring, see
            // src/AssemblyInfo.cs), and the cost of being explicit is one line.
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;   // 12288 = Tls13

            string configPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "worker.ini");
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[i + 1];

            Config cfg = Config.Load(configPath);

            // A bare word first argument is a client command; a --flag is an agent
            // mode. One binary either way, so there is one thing to copy onto a
            // machine and one thing to keep in step.
            if (args.Length > 0 && !args[0].StartsWith("-"))
            {
                BorrowParentConsole();
                return Cli.Run(args, cfg);
            }

            var rest = new List<string>();
            string mode = "--tray";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) { i++; continue; }
                if (args[i].StartsWith("--") && mode == "--tray" && args[i] != "--tray") { mode = args[i]; continue; }
                rest.Add(args[i]);
            }

            if (mode != "--tray") BorrowParentConsole();

            if (mode == "--once") return Once(cfg);
            if (mode == "--watch") return Watch(cfg, rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 20);
            if (mode == "--calibrate") return Calibrate(cfg, rest.Count > 0 ? rest[0] : "calibration.csv",
                                                        rest.Count > 1 ? int.Parse(rest[1], CultureInfo.InvariantCulture) : 10);
            if (mode == "--serve") return Serve(cfg, rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 0);
            // The logon half of a boot install. See Cli.Presence.
            if (mode == "--presence") return Cli.Presence(cfg, rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 0);
            if (mode == "--limits") return LimitsSelfTest(cfg);
            if (mode == "--spin") return Spin(rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 20);
            if (mode == "--help" || mode == "-h") { Cli.Usage(); return 0; }
            return Tray(cfg);
        }

        /// Prove the vertical limits on THIS machine, in about fifty seconds.
        ///
        /// WHY THIS SHIPS RATHER THAN LIVING IN probe/. Everything the CPU ladder
        /// promises is a claim about a kernel API on a machine nobody here has
        /// seen. CpuRate is documented as a share of the whole machine; it was
        /// measured as one on a Ryzen 7 5700X3D and nowhere else. The working set
        /// cap needs a privilege whose absence is silent. A stranger installing
        /// this deserves to be able to check both in one command rather than
        /// believing a number in a README that was measured on somebody else's
        /// desk.
        ///
        /// It uses the SAME JobRunner.ApplyLimits the tray uses, against a real
        /// child process in a real job object, so it cannot pass while the shipped
        /// path is broken. It self-terminates, like --watch and --calibrate, so it
        /// is safe to run over SSH against somebody's gaming PC.
        static int LimitsSelfTest(Config cfg)
        {
            int cores = Environment.ProcessorCount;
            Console.WriteLine("machine: {0} logical processors", cores);
            MemorySample mem = SystemMemory.Read();
            if (mem.Valid)
                Console.WriteLine("memory : {0:N0} MiB total, {1:N0} MiB available, {2}% load",
                    mem.TotalMib, mem.AvailableMib, mem.LoadPct);
            Console.WriteLine();

            var svc = new ServiceDef();
            svc.Id = "limits-selftest";
            svc.Command = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            svc.Arguments = "--spin 60";
            svc.WorkingDir = Path.GetTempPath();
            svc.YieldGraceSeconds = 1;
            var runner = new JobRunner(svc, cfg, delegate(string m) { Console.WriteLine("  " + m); });
            if (!runner.Start()) { Console.WriteLine("could not start the load"); return 1; }
            try
            {
                System.Threading.Thread.Sleep(1500);
                Console.WriteLine("{0,-38} {1}", "limits applied", "measured share of the whole machine");
                foreach (ResourceLimits l in SelfTestRungs())
                {
                    runner.ApplyLimits(l);
                    System.Threading.Thread.Sleep(700);      // let the cap settle
                    double pct = MeasureJobPct(runner, 2500, cores);
                    Console.WriteLine("{0,-38} {1,8:N1}%  = {2,5:N2} cores",
                        l.Describe(), pct, pct * cores / 100.0);
                }
                Console.WriteLine();
                Console.WriteLine(runner.WorkingSetDenied
                    ? "working set cap: NOT AVAILABLE to this account. " + runner.LastLimitError
                    : "working set cap: accepted by the kernel");
                Console.WriteLine();
                Console.WriteLine("Read the middle column against the first. A cap that is a share of ONE");
                Console.WriteLine("core would read about 1/{0} of these numbers; a share of the WHOLE", cores);
                Console.WriteLine("machine reads them as printed. Vertical, not horizontal: nothing above");
                Console.WriteLine("pins anything to a particular core.");
            }
            finally { runner.Stop("self test finished"); runner.Dispose(); }
            return 0;
        }

        static List<ResourceLimits> SelfTestRungs()
        {
            var outp = new List<ResourceLimits>();
            int[] caps = new int[] { 100, 50, 25, 10, 5, 100 };
            string[] prios = new string[] { "normal", "idle", "idle", "idle", "idle", "normal" };
            for (int i = 0; i < caps.Length; i++)
            {
                var l = new ResourceLimits();
                l.Gpu = true; l.CpuPct = caps[i]; l.Priority = prios[i];
                // The last row is the first row again, on purpose: it proves the
                // cap comes back OFF on a live job. Giving CPU back as the owner
                // stops needing it is half the promise, and a limiter that can only
                // tighten is a limiter that ratchets a machine down to nothing.
                l.WorkingSetMib = i == 3 ? 512 : 0;
                outp.Add(l);
            }
            return outp;
        }

        static double MeasureJobPct(JobRunner r, int ms, int cores)
        {
            long a = r.CpuTime100ns;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Thread.Sleep(ms);
            sw.Stop();
            long b = r.CpuTime100ns;
            if (b < a || sw.Elapsed.TotalSeconds <= 0) return 0;
            return 100.0 * ((b - a) / 10000000.0) / (sw.Elapsed.TotalSeconds * cores);
        }

        /// The load --limits measures. Deliberately one thread per logical
        /// processor, because a cap that is only ever tested against one thread
        /// cannot tell "half the machine" from "half a core".
        static int Spin(int seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            var threads = new List<System.Threading.Thread>();
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                var t = new System.Threading.Thread(delegate()
                {
                    double x = 1.000001;
                    while (DateTime.UtcNow < until)
                        for (int k = 0; k < 200000; k++) x = x * 1.0000001 + 0.0000001;
                    GC.KeepAlive(x);
                });
                t.IsBackground = true; t.Start(); threads.Add(t);
            }
            foreach (System.Threading.Thread t in threads) t.Join();
            return 0;
        }

        /// Start the listener beside an agent, or explain why it did not start.
        ///
        /// A misconfiguration here must be loud. An operator who binds 0.0.0.0
        /// without a token has made a mistake that nothing will make them notice,
        /// because the thing appears to work.
        static Listener StartListener(Config cfg, Agent a)
        {
            if (!cfg.ListenerEnabled) { a.LogLine("listener disabled in worker.ini"); return null; }
            try
            {
                // Only when a non-loopback bind needs one. On loopback the runner
                // works tokenless out of the box, and writing a key file nobody
                // asked for would be one more thing in the contained directory.
                if (!cfg.BindIsLoopback() && string.IsNullOrEmpty(cfg.EffectiveApiKey()) &&
                    !string.IsNullOrEmpty(cfg.ApiKeyFile))
                {
                    if (ApiKeys.EnsureFile(cfg.ApiKeyFile))
                        a.LogLine("generated an API key at " + cfg.ApiKeyFile);
                }
                var l = new Listener(cfg, a, a.LogLine);
                l.Start();
                return l;
            }
            catch (Exception ex)
            {
                a.LogLine("LISTENER DID NOT START: " + ex.Message);
                Console.Error.WriteLine("[agent] listener did not start: " + ex.Message);
                return null;
            }
        }

        /// Headless, which is what an SSH session and a Run key without a desktop
        /// both are. Runs until Ctrl-C or the given number of seconds.
        static int Serve(Config cfg, int seconds)
        {
            using (Agent a = Spin(cfg, 3))
            {
                Listener l = StartListener(cfg, a);
                try
                {
                    var quit = new ManualResetEvent(false);
                    Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                    {
                        e.Cancel = true;
                        quit.Set();
                    };
                    if (l != null)
                    {
                        Console.WriteLine("listening on https://{0}:{1}/", cfg.Bind, l.BoundPort);
                        Console.WriteLine("fingerprint {0}", l.Pin);
                    }
                    if (seconds > 0) quit.WaitOne(seconds * 1000);
                    else quit.WaitOne();
                }
                finally { if (l != null) l.Dispose(); }
                return 0;
            }
        }

        static Agent Spin(Config cfg, int settleSeconds)
        {
            var a = new Agent(cfg);
            a.Log = delegate(string s) { Console.Error.WriteLine("[agent] " + s); };
            a.Start();
            // The GPU stream emits at 1 Hz and the engine counters are rate counters
            // needing two reads, so nothing is trustworthy for the first few seconds.
            Thread.Sleep(settleSeconds * 1000);
            return a;
        }

        static int Once(Config cfg)
        {
            using (Agent a = Spin(cfg, 9))
            {
                Console.WriteLine(a.StatusJson());
                return a.Policy.CanRun(a.Mode) ? 0 : 1;
            }
        }

        static int Watch(Config cfg, int seconds)
        {
            using (Agent a = Spin(cfg, 9))
            {
                Console.WriteLine("{0,-8} {1,-13} {2,-5} {3,-6} {4,-7} {5,-7} {6,-6} {7,-6} {8}",
                    "time", "state", "util", "power", "memclk", "pstate", "eng3d", "vram", "reason");
                for (int i = 0; i < seconds; i++)
                {
                    Snapshot s = a.Current;
                    GpuSample g = s.Gpu;
                    Console.WriteLine("{0,-8} {1,-13} {2,-5} {3,-6} {4,-7} {5,-7} {6,-6} {7,-6} {8}",
                        DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        Policy.Describe(a.Policy.State),
                        g == null ? "-" : g.UtilGpu.ToString(CultureInfo.InvariantCulture),
                        g == null ? "-" : g.PowerWatts.ToString("0.0", CultureInfo.InvariantCulture),
                        g == null ? "-" : g.ClockMemMhz.ToString(CultureInfo.InvariantCulture),
                        g == null ? "-" : g.PState,
                        s.CountersFresh ? s.Util3d.ToString("0.0", CultureInfo.InvariantCulture) : "-",
                        a.Policy.ForeignVram(s).Count.ToString(CultureInfo.InvariantCulture),
                        a.Policy.Last.ReasonText);
                    Thread.Sleep(1000);
                }
                return 0;
            }
        }

        /// The honest answer to a question this investigation could not settle from
        /// a read-only SSH session: what the signals look like WITH A GAME RUNNING.
        ///
        /// Nothing may be installed on spring and nothing may be started that
        /// outlives a command, so no load could be generated to measure the busy side
        /// of every threshold. The idle side is measured to four significant figures
        /// (probe p5, 92 samples); the busy side is inferred. This mode closes that
        /// gap by having the user run it once while they play, which turns the
        /// remaining assumption into a measurement without anyone guessing.
        static int Calibrate(Config cfg, string csv, int minutes)
        {
            using (Agent a = Spin(cfg, 3))
            {
                bool fresh = !File.Exists(csv);
                using (var w = new StreamWriter(csv, true, new UTF8Encoding(false)))
                {
                    // The header is Replay.Header itself rather than a copy of it.
                    // These two must not drift: the whole value of this mode is
                    // that the CSV it produces drops straight into
                    // tests/fixtures/ and becomes a regression test with nobody
                    // transcribing numbers.
                    if (fresh) w.WriteLine(Replay.Header);
                    int n = minutes * 60;
                    for (int i = 0; i < n; i++)
                    {
                        Snapshot s = a.Current;
                        GpuSample g = s.Gpu;
                        // The RAW top consumer, before the allowlist. Recording the
                        // post-filter answer would make "dwm holding 169 MiB" and
                        // "nothing at all" identical rows, and the replay tests
                        // could not then exercise Policy.ForeignVram at all.
                        ProcessGpuUse top = null;
                        foreach (ProcessGpuUse u in s.GpuProcesses)
                            if (top == null || u.DedicatedMiB > top.DedicatedMiB) top = u;
                        w.WriteLine(string.Join(",", new string[] {
                            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                            Policy.Describe(a.Policy.State),
                            g == null ? "" : g.UtilGpu.ToString(CultureInfo.InvariantCulture),
                            g == null ? "" : g.UtilMem.ToString(CultureInfo.InvariantCulture),
                            g == null ? "" : g.UtilEncoder.ToString(CultureInfo.InvariantCulture),
                            g == null ? "" : g.UtilDecoder.ToString(CultureInfo.InvariantCulture),
                            g == null ? "" : g.ClockMemMhz.ToString(CultureInfo.InvariantCulture),
                            g == null ? "" : g.ClockSmMhz.ToString(CultureInfo.InvariantCulture),
                            g == null ? "" : g.PState,
                            g == null ? "" : g.PowerWatts.ToString("0.00", CultureInfo.InvariantCulture),
                            g == null ? "" : g.MemUsedMiB.ToString(CultureInfo.InvariantCulture),
                            s.Util3d.ToString("0.00", CultureInfo.InvariantCulture),
                            s.UtilVideoDecode.ToString("0.00", CultureInfo.InvariantCulture),
                            s.UtilVideoEncode.ToString("0.00", CultureInfo.InvariantCulture),
                            s.GpuHealthy ? "1" : "0",
                            s.CountersFresh ? "1" : "0",
                            s.Session == null ? "" : s.Session.OwnSessionId.ToString(CultureInfo.InvariantCulture),
                            s.Session == null ? "" : s.Session.ConsoleSessionId.ToString(CultureInfo.InvariantCulture),
                            s.Session == null ? "" : s.Session.ConsoleUserName,
                            s.Session == null ? "" : (s.Session.Locked ? "1" : "0"),
                            s.Session == null ? "" : s.Session.InputIdleSeconds.ToString(CultureInfo.InvariantCulture),
                            s.Session == null ? "" : (s.Session.ForegroundIsFullScreen ? "1" : "0"),
                            s.Session == null ? "" : Csv(s.Session.ForegroundProcess),
                            s.Launchers == null ? "" : s.Launchers.SteamRunningAppId.ToString(CultureInfo.InvariantCulture),
                            s.Launchers == null ? "" : Csv(s.Launchers.SteamRunningAppName),
                            s.Launchers == null ? "" : (s.Launchers.ValorantAntiCheatActive ? "1" : "0"),
                            s.Launchers == null ? "" : s.Launchers.AntiCheatService,
                            s.Launchers == null ? "" : Csv(string.Join(" ", s.Launchers.GameProcesses.ToArray())),
                            top == null ? "" : top.Pid.ToString(CultureInfo.InvariantCulture),
                            top == null ? "" : Csv(top.Name),
                            top == null ? "0" : top.DedicatedMiB.ToString("0.0", CultureInfo.InvariantCulture),
                            Csv(a.Policy.Last.ReasonText)
                        }));
                        w.Flush();   // survive a hard reboot mid-game, which is the point
                        Thread.Sleep(1000);
                    }
                }
                Console.Error.WriteLine("[agent] wrote " + csv);
                return 0;
            }
        }

        static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static int Tray(Config cfg)
        {
            var a = new Agent(cfg);
            a.Start();
            Listener l = StartListener(cfg, a);
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(a));
            }
            finally
            {
                // Deterministic disposal, so the transient CNG key container that
                // holding the certificate created is removed rather than left for
                // the next start's sweep. See src/Certs.cs.
                if (l != null) l.Dispose();
                a.Dispose();
            }
            return 0;
        }
    }
}
