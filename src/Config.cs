// Configuration and its defaults.
//
// EVERY DEFAULT BELOW IS A MEASUREMENT OR A MARGIN ON ONE. The measurements were
// taken on spring (RTX 3070, driver 610.47, Windows 11 Pro 10.0.26200) on
// 2026-09-05, with the desktop up and the machine untouched: dwm, explorer,
// ShellHost, SearchHost, StartMenuExperienceHost, two msedgewebview2, Raycast,
// steamwebhelper, CamoStudio and Riot Vanguard's vgtray all resident on the GPU.
// That is a realistic floor, not a clean-room one, which is the point.
//
// The 92-sample, 90-second idle baseline (probe p5) was:
//
//   utilisation.gpu   min 5     max 6      mean 5.13   p95 6
//   utilisation.mem   min 3     max 4      mean 3.73   p95 4
//   encoder/decoder   0 throughout
//   clocks.mem        810 MHz, ZERO variance across all 92 samples
//   pstate            P5 for all 92 samples
//   power.draw        min 33.68 W  max 35.13 W  mean 34.59 W   (range 1.45 W)
//   memory.used       416 MiB, zero variance
//
// Note what that rules out: "utilisation.gpu below 5 per cent" can never be
// satisfied on this machine. The naive threshold is not merely imprecise here, it
// is unreachable. The clock, the pstate and the power figure all separate far
// more cleanly than utilisation does, and that is why the policy leans on them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleGpu
{
    public class Config
    {
        // -- GPU load thresholds -------------------------------------------------

        /// Measured idle memory clock: 810 MHz, unchanging across 92 samples.
        /// The GDDR6 on a 3070 sits here whenever nothing is rendering and steps to
        /// several thousand MHz the moment a 3D application takes the adapter. Any
        /// value above this floor by a wide margin is real work, so the threshold is
        /// set well clear of it rather than just above it.
        public int MemClockBusyMhz = 1500;

        // A WARNING ABOUT THIS SIGNAL, found by reading what else runs on spring.
        //
        // The test machine runs a script on a loop that applies
        // `nvidia-smi -lgc 1800` and `-pl 270` every ten minutes. That LOCKS THE
        // CORE CLOCK, and it is why clocks.sm reads exactly 1800 in all 150
        // samples of the idle baseline. Plenty of people run something like it:
        // overclocking utilities, undervolting profiles and fan-curve tools all do. The core clock is therefore a
        // constant on this machine and worthless as a load signal - which is why
        // the policy reads clocks.MEM instead, and clocks.mem is not what -lgc
        // pins. Measured idle memory clock is 810 MHz with zero variance.
        //
        // If anyone ever adds `-lmc` to that script, this signal dies silently:
        // the memory clock would then be a constant too and MemClockBusyMhz would
        // never fire again. The tier-1 vetoes would carry on working, so the
        // failure would be a quiet loss of sensitivity rather than a visible
        // break. Worth knowing before debugging it from scratch.

        /// P5 for 100 per cent of the idle baseline. P0 and P2 are the performance
        /// states; P8 is deeper idle. Treating anything faster than P5 as load gives
        /// a signal that moves before utilisation does, because the driver raises the
        /// pstate in anticipation of work rather than in response to it.
        public string[] IdlePStates = new string[] { "P5", "P8", "P12" };

        /// Idle power measured 33.68-35.13 W. Gaming load on a 3070 is an order of
        /// magnitude above that. 70 W leaves 35 W of headroom over the measured idle
        /// ceiling - roughly double the entire idle draw - so it cannot be tripped by
        /// desktop compositing, and still fires long before a game reaches frame rate.
        public double PowerBusyWatts = 70.0;

        /// Utilisation is the weakest signal here and is used only as one of several
        /// load votes. Idle p95 was 6 per cent, so 25 is four times the measured
        /// noise floor.
        public int UtilGpuBusyPct = 25;

        /// Sum of GPU Engine "3d" utilisation across processes. Measured 0.95 at idle.
        public double Util3dBusyPct = 20.0;

        // -- The paused-game veto ------------------------------------------------

        /// Dedicated GPU memory, in MiB, held by any process outside the allowlist
        /// that means "a game is resident, whether or not it is currently drawing".
        ///
        /// The largest non-allowlisted consumer measured at idle was CamoStudio at
        /// 112.3 MiB and the largest of all was dwm at 172.4 MiB (probe p2). 512 MiB
        /// is three times the largest thing measured on an idle desktop and far below
        /// what any game holds. This is the check that catches the case the naive
        /// policy gets wrong in the expensive direction: a game paused at a menu draws
        /// nothing but still owns its VRAM and expects to resume instantly.
        public double ForeignVramBusyMiB = 512.0;

        /// Processes permitted to hold GPU memory without being read as a game. Every
        /// name here was observed resident on spring's idle desktop (probes p1, p2).
        public string[] VramAllowlist = new string[] {
            "dwm", "explorer", "csrss", "ShellHost", "SearchHost",
            "StartMenuExperienceHost", "ShellExperienceHost", "ApplicationFrameHost",
            "SystemSettings", "CrossDeviceResume", "TextInputHost", "SearchApp",
            // pid 4, the kernel. Measured holding 4.0 MiB on spring's idle desktop
            // 2026-09-05. It was missing from this list, which was harmless only
            // because 4 MiB is three orders of magnitude under the trip point.
            "System",
            "msedgewebview2", "Raycast", "steamwebhelper", "steam", "steamservice",
            "CamoStudio", "vgtray", "LogonUI", "sihost", "ctfmon"
        };

        /// Process names whose presence alone means yield. Derived from what is
        /// installed on spring (probe p6: Steam library plus C:\Riot Games).
        public string[] GameProcessNames = new string[] {
            "cs2", "RainbowSix", "RainbowSixGame", "ReadyOrNot", "GRB",
            "VALORANT-Win64-Shipping", "VALORANT", "LeagueOfLegends"
        };

        // -- Timing --------------------------------------------------------------

        /// How many consecutive 1 Hz GPU samples must show load before yielding on
        /// GPU evidence alone. Three seconds. Short because the cost of being wrong
        /// in this direction is one abandoned job; long enough that a single frame of
        /// a window animation does not trip it.
        /// How long without input before "signed in" becomes "idle".
        ///
        /// Five minutes, matching the shortest screen blank most people leave set,
        /// so the state the agent calls idle is one the owner would also call
        /// idle. Shorter reads a pause for thought as absence.
        public int IdleAfterSeconds = 300;

        /// Foreign CPU, per cent of the whole machine, at which the owner counts
        /// as busy on the CPU alone.
        ///
        /// FOREIGN, not total: our own job's CPU is subtracted exactly from the
        /// job object's accounting, so this fires on somebody else's compile and
        /// never on our own load. See CpuSample for why the GPU could not do that.
        ///
        /// 40 per cent of sixteen threads is six and a half cores, which no
        /// desktop reaches by sitting there. Measured on spring's idle desktop the
        /// whole machine sat under 4 per cent.
        public double ForeignCpuBusyPct = 40.0;

        public int BusyConfirmSamples = 3;

        /// How long every signal must stay clear before returning to Available.
        ///
        /// This is the asymmetry that makes the whole design safe. Ninety seconds is
        /// longer than a level load, longer than alt-tabbing to a browser mid-match,
        /// and longer than the gap between two rounds. A false "busy" costs one job.
        /// A false "idle" costs the user their game, so the cooldown is deliberately
        /// far longer than the detection window.
        public int ClearCooldownSeconds = 90;

        /// Poll interval for the cheap Win32 and registry signals.
        public int FastPollMs = 1000;

        /// Poll interval for the performance counters.
        ///
        /// Two seconds, not five. The counters are the ONLY way an unknown game -
        /// one with no Steam appid and no name in GameProcessNames - is detected,
        /// via the VRAM veto, so this interval is that path's entire detection
        /// latency. Read in-process through PDH the whole set costs 98 ms
        /// (probe p7), against Get-Counter's measured 1069-1851 ms for the same
        /// data, which is the reason this program is not a PowerShell script.
        /// Paying three extra seconds of the user's frame time to save 98 ms of
        /// one core every two seconds is the wrong way round; at 2000 ms the
        /// counters cost under 5 per cent of one core and the unknown-game path
        /// drops from a ~7 s worst case to ~3 s.
        public int CounterPollMs = 2000;

        /// Grace period given to a running job when the policy says yield, before
        /// the job object is closed and the whole tree dies.
        ///
        /// Two seconds, and the kill is the NORMAL path rather than the exception.
        /// The reason is a hard limit rather than a preference: Chatterbox's
        /// generate() has no interruption point inside it - measured, and the
        /// chatterbox controller declares it in its manifest as unit_seconds 24.3
        /// on this machine. So the child cannot abort mid-chunk. What it can do is decline to start another chunk and drop
        /// the one in flight, which is what the YIELD line on stdin asks for.
        ///
        /// Waiting politely for a chunk to finish would cost a bounded but
        /// unmeasured number of seconds of the user's frame time, and the stated
        /// constraint is that the user has full priority. The cooperative stage
        /// exists only to save a chunk that is already computed and one write away
        /// from being delivered; anything slower than that gets killed. Killing
        /// mid-CUDA-kernel is safe by construction - process teardown returns the
        /// context and the allocation to the driver, and a chunk only becomes real
        /// when the whole array is delivered, so there is no partial write to
        /// corrupt.
        public int YieldGraceSeconds = 2;

        /// Names of services whose presence means a game, checked with the service
        /// control manager. Riot's Vanguard installs two: vgk, the kernel driver,
        /// which is Running/System permanently and therefore says nothing, and vgc,
        /// the user mode service, which starts with the game. Measured on spring
        /// 2026-09-05: vgc Stopped/Manual, vgk Running/System. This is a list
        /// rather than a constant because the next anti cheat to matter will not
        /// be called vgc.
        public string[] AntiCheatServices = new string[] { "vgc" };

        // -- Where everything lives ----------------------------------------------

        /// THE CONTAINED DIRECTORY. Every file this program writes at run time is
        /// under here: the log, the published state, the TLS key, the API key, the
        /// per-service queues, the content addressed asset store, and whatever a
        /// service's own provisioning puts in runtime\. Uninstall is deleting it
        /// and removing one autostart entry.
        ///
        /// The single documented exception is one transient CNG key container that
        /// Schannel insists on writing to %APPDATA%\Microsoft\Crypto\Keys while a
        /// TLS certificate is loaded. src/Certs.cs explains why it cannot be
        /// redirected and how it is swept.
        public string DataDir = DefaultDataDir();

        /// Where this build keeps everything: the directory the executable is in.
        ///
        /// NOT %LOCALAPPDATA%, WHICH IS PER ACCOUNT AND BROKE THE BOOT TASK.
        /// The installer copies the exe, worker.ini, the services and the runtime
        /// into one folder, so the folder holding the exe IS the install and
        /// deriving from it is both correct and account independent. Reading
        /// %LOCALAPPDATA% instead meant a task running as SYSTEM resolved to
        /// C:\Windows\System32\config\systemprofile\AppData\Local\idlegpu, an
        /// empty directory, and reported no installed services at all while two
        /// sat provisioned and ready a few folders away. Measured, not feared.
        ///
        /// The fallback covers a build run from somewhere unreadable or a host
        /// that will not answer for its own assembly location, and it restores
        /// exactly the old behaviour rather than inventing a third one.
        static string DefaultDataDir()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch (Exception) { }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "idlegpu");
        }

        /// The tray mode, persisted. Auto | AlwaysOn | Off.
        public string StartMode = "Auto";

        /// Where the agent appends one line per state change. Plain text with a
        /// timestamp and a reason on every line and nothing that needs a parser,
        /// because this is the file a false-idle trial is read out of.
        public string LogPath = "";

        /// Written by a service's provisioning script while it downloads. One line
        /// of plain text, e.g. "torch and CUDA, 1.4 of 2.5 GB". Present means setup
        /// is in progress; absent means it is not. A file rather than a pipe
        /// because provisioning is a separate script the user can run on its own.
        public string ProvisionStatusPath = "";

        public string ConfigPath = "";
        public string StatePath = "";

        /// Content addressed inputs, shared by every service. See AssetStore.
        public string AssetsDir = "";

        /// Parent of the per-service queue directories. A service may override its
        /// own with QueueDir.
        public string QueueRoot = "";

        /// Parent of the per-service install directories. EVERYTHING ANY SERVICE
        /// EVER DOWNLOADS lives under here, one directory per service, so that
        /// `idlegpu service remove chatterbox` is one recursive delete and the
        /// number `idlegpu service cost` prints is a real measurement rather than a
        /// claim in a README.
        ///
        /// Deliberately NOT the same directory as the controller scripts. The
        /// scripts are a few kilobytes that came from the repository and are put
        /// there by install.ps1; the downloads are gigabytes that came off the
        /// internet. Removing a service must reclaim the second without destroying
        /// the first, because destroying the first means `idlegpu service install`
        /// can no longer find the provisioning script it is being asked to re-run.
        public string RuntimeRoot = "";

        /// Where the shipped controller scripts live, copied out of the repository
        /// by install.ps1. Read-only at run time and never deleted by `service
        /// remove`. A service's Provision path is resolved against this.
        public string ScriptsRoot = "";

        // -- The listener --------------------------------------------------------

        /// Loopback by default, and that is a considered default rather than
        /// timidity. Measured on spring: a non-elevated process binds 0.0.0.0 with
        /// no reservation, but it CANNOT open the Windows Firewall, so the socket
        /// is reachable from the machine itself and nowhere else until somebody
        /// with administrator rights allows it once. Worse, if a standard user is
        /// prompted and dismisses it, Windows writes a BLOCK rule and never asks
        /// again: permanent silent unreachability that no amount of reading this
        /// agent's log will explain. Loopback sidesteps all of that, works over an
        /// SSH tunnel immediately, and needs no firewall prompt on a fresh clone.
        public string Bind = "127.0.0.1";

        /// A default, not an assumption. Below the Windows dynamic port range
        /// (which starts at 49152) so it cannot collide with an ephemeral port.
        public int Port = 47600;

        public bool ListenerEnabled = true;

        /// All relative to DataDir unless given as absolute paths.
        public string CertPath = "";
        public string CertKeyLedgerPath = "";

        /// The name in the certificate's subject and its first subject alternative
        /// name. Defaults to this machine's name, detected rather than assumed.
        public string ServerName = "";

        /// Extra subject alternative names, comma separated. The LAN address or
        /// DNS name clients will use, if that is not the machine name.
        public string[] CertExtraSans = new string[0];

        public int CertYears = 5;

        /// The bearer token. ApiKeyFile wins if both are set; a file keeps the
        /// secret out of a config a user might paste into an issue.
        public string ApiKey = "";
        public string ApiKeyFile = "";

        /// Who may connect when Bind is not loopback. Empty means nobody, which is
        /// why the agent refuses to start a non-loopback listener with no
        /// allowlist and no key: an accidental 0.0.0.0 must fail loudly at start
        /// rather than quietly serve the LAN.
        public string[] AllowedCidrs = new string[0];

        /// Slowloris and flood limits. Named and configurable because a hand
        /// written server on a socket that may reach a LAN needs these where a
        /// reader can find them, not buried in the parser.
        public int MaxRequestBytes = 8 * 1024 * 1024;
        public int MaxHeaderCount = 64;
        public int MaxHeaderLineBytes = 8192;
        public int HandshakeTimeoutMs = 5000;
        public int RequestTimeoutMs = 15000;

        /// The listener's own bounded connection pool. A hostile client can starve
        /// this and nothing else: it is structurally incapable of adding a
        /// millisecond to a yield, because the yield runs on a different thread
        /// that takes no lock this one can hold.
        public int MaxConnections = 16;

        /// Largest manifest or terminal record passed through to a client. A
        /// controller that writes a 200 MB service.json is misbehaving and should
        /// not be able to make the agent allocate 200 MB on a request thread.
        public int MaxPassthroughBytes = 256 * 1024;

        // -- The scheduler -------------------------------------------------------

        /// How often the scheduler thread looks for queued work.
        ///
        /// This is a separate thread from the policy loop on purpose. Selecting a
        /// service means enumerating every service's pending directory, which is a
        /// syscall per service. On the policy thread that would be a syscall on the
        /// yield path, which is the one path in this program that must do nothing
        /// but evaluate and stop.
        public int SchedulerPollMs = 500;

        /// Services, in the order they were declared. One [service.<id>] section
        /// each. See worker.ini.example and docs/ADDING-A-SERVICE.md.
        /// The limits matrix: one ResourceLimits per MachineState.
        ///
        /// EVERY CELL NEEDS A DEFENSIBLE DEFAULT, because most people will never
        /// open the settings window. These are the defaults and where they come
        /// from. The CPU numbers are read off probe/p10_felt_hard.cs, which put an
        /// eight-thread victim at normal priority against sixteen memory-streaming
        /// threads on spring and measured what the victim lost:
        ///
        ///   nothing else running        p50 4.32 ms  p99  7.82 ms  7155 units
        ///   16 threads at NORMAL        p50 7.90 ms  p99 40.94 ms  1929 units
        ///   16 threads at IDLE          p50 10.38    p99 13.28     4281 units
        ///   IDLE + 50% cap              p50 10.38    p99 13.18     4391 units
        ///   IDLE + 25% cap              p50 5.93     p99 12.53     5701 units
        ///   IDLE + 10% cap              p50 4.89     p99 11.89     6452 units
        ///   IDLE +  5% cap              p50 4.74     p99 10.61     6725 units
        ///
        /// Two things fall straight out of that table and both shaped these
        /// defaults. Idle priority alone is NOT enough: it still cost 40 per cent
        /// of the victim's throughput, because a memory-streaming thread has
        /// already evicted the victim's cache lines by the time the scheduler
        /// preempts it. And a 50 per cent cap on sixteen threads is no better than
        /// no cap at all, because eight streaming threads saturate the memory
        /// controller on their own. The cap only starts buying anything back at 25
        /// and is close to invisible at 10.
        ///
        /// So LightUse, the one state where somebody is actually at the machine,
        /// is 10 per cent. Idle is 50, which is a hedge rather than a limit and is
        /// there so that a job does not have the machine flat out at the moment
        /// somebody walks back to it.
        ///
        /// NOBODY SIGNED IN AND LOCKED ARE DELIBERATELY UNLIMITED, which is the
        /// owner's own stated default: when nobody is at the machine there is
        /// nobody to disturb, so there is nothing to throttle. Locked keeps
        /// below-normal priority as a courtesy to whatever the owner left running,
        /// a backup or a download, and costs essentially nothing on an idle box.
        public Dictionary<MachineState, ResourceLimits> Limits = DefaultLimits();

        public static Dictionary<MachineState, ResourceLimits> DefaultLimits()
        {
            return ProfileLimits(ProfileBalanced);
        }

        /// What Always-on means, and what "nobody is home" ships as. Named rather
        /// than rebuilt, because it is compared against on every sampling tick.
        public static readonly ResourceLimits Unlimited = L(true, 100, "normal", 0, 0, true);

        static ResourceLimits L(bool gpu, int cpu, string prio, int ws, int minFree, bool admit)
        {
            var r = new ResourceLimits();
            r.Gpu = gpu; r.CpuPct = cpu; r.Priority = prio;
            r.WorkingSetMib = ws; r.MinFreeMib = minFree; r.Admit = admit;
            return r;
        }

        // -- profiles ------------------------------------------------------------
        //
        // A NAME ON TOP OF THE GRID, because twenty-five numbers is a form and
        // nobody fills in a form to lend a computer. A profile is a complete
        // matrix with a name and one sentence saying what it means, and it is
        // what the tray offers first. The grid stays underneath it, unchanged, for
        // anybody who does want to price each cell.
        //
        // OFF IS NOT A PROFILE, deliberately. Mode.Off already means "sell
        // nothing", and a profile of the same name would give the runner two
        // encodings of one state and a status line reading "Auto, profile Off".
        // Mode is the big switch; a profile is what Auto MEANS. The tray shows
        // them in the same region so the owner sees four answers, but only three
        // of them are matrices.

        public const string ProfileGenerous = "generous";
        public const string ProfileBalanced = "balanced";
        public const string ProfileAway = "away";

        public static string[] ProfileIds()
        {
            return new string[] { ProfileGenerous, ProfileBalanced, ProfileAway };
        }

        public static string ProfileLabel(string id)
        {
            switch (NormaliseProfile(id))
            {
                case ProfileGenerous: return "Generous";
                case ProfileAway: return "Only when I am away";
                default: return "Balanced";
            }
        }

        public static string ProfileBlurb(string id)
        {
            switch (NormaliseProfile(id))
            {
                case ProfileGenerous: return "use it unless I am actually gaming";
                case ProfileAway: return "never while I am signed in and unlocked";
                default: return "use it while I am away, and stay out of my way when I am here";
            }
        }

        public static string NormaliseProfile(string id)
        {
            string t = (id ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (t == ProfileGenerous) return ProfileGenerous;
            if (t == ProfileAway || t == "onlywhenaway" || t == "onlywheniamaway") return ProfileAway;
            if (t == ProfileBalanced) return ProfileBalanced;
            return t;   // a saved custom profile keeps its own id
        }

        /// The whole matrix a profile stands for.
        ///
        /// WHERE THE CPU NUMBERS COME FROM. probe/p10_cpu.ps1 on spring: an eight
        /// thread foreground victim over a 48 MiB array against sixteen background
        /// threads each streaming 128 MiB, which is more than the whole 96 MiB L3.
        ///
        ///   condition            p50       p99      units kept
        ///   nothing running      4.32 ms   7.82 ms  7155
        ///   16 at NORMAL         7.90     40.94     1929
        ///   16 at IDLE          10.38     13.28     4281
        ///   IDLE + 50% cap      10.38     13.18     4391
        ///   IDLE + 25% cap       5.93     12.53     5701
        ///   IDLE + 10% cap       4.89     11.89     6452
        ///   IDLE +  5% cap       4.74     10.61     6725
        ///
        /// Two things fall straight out and both shaped every cell. IDLE PRIORITY
        /// ALONE IS NOT ENOUGH - it still cost 40 per cent of the victim's
        /// throughput, because a memory-streaming thread has already evicted the
        /// victim's cache lines by the time the scheduler preempts. And A 50 PER
        /// CENT CAP BUYS NOTHING - eight streaming threads saturate the memory
        /// controller on their own. The cap starts paying at 25 and is close to
        /// invisible at 10 (90 per cent of throughput kept) and 5 (94 per cent).
        ///
        /// THE GPU COLUMN IS YES IN EVERY ROW BUT BUSY, in all three profiles
        /// except Away. That is a deliberate choice between three things that used
        /// to disagree: this file shipped LightUse as no, worker.ini.example
        /// shipped it as yes, and Policy.CanRunGpu's docstring promised yes. The
        /// promise wins, because the GPU detector is already the gate for the GPU
        /// and shipping a matrix that silently starts refusing GPU work while
        /// somebody reads a web page would change behaviour nobody asked to
        /// change. An owner who wants the other answer picks "Only when I am
        /// away", or sets limits.lightuse.gpu = no.
        public static Dictionary<MachineState, ResourceLimits> ProfileLimits(string id)
        {
            var d = new Dictionary<MachineState, ResourceLimits>();
            switch (NormaliseProfile(id))
            {
                case ProfileGenerous:
                    d[MachineState.NobodyHome] = L(true, 100, "normal", 0, 512, true);
                    d[MachineState.Locked] = L(true, 100, "normal", 0, 1024, true);
                    d[MachineState.Idle] = L(true, 100, "belownormal", 0, 2048, true);
                    d[MachineState.LightUse] = L(true, 25, "idle", 12288, 4096, true);
                    d[MachineState.Busy] = L(false, 10, "idle", 4096, 0, false);
                    break;

                case ProfileAway:
                    // Five rows collapsed into two, which is the argument for
                    // profiles in one table: somebody who wants a two-state world
                    // gets it without having to learn there are five rows.
                    d[MachineState.NobodyHome] = L(true, 100, "normal", 0, 1024, true);
                    d[MachineState.Locked] = L(true, 100, "belownormal", 0, 2048, true);
                    d[MachineState.Idle] = L(false, 0, "idle", 0, 0, false);
                    d[MachineState.LightUse] = L(false, 0, "idle", 0, 0, false);
                    d[MachineState.Busy] = L(false, 0, "idle", 0, 0, false);
                    break;

                default:
                    d[MachineState.NobodyHome] = L(true, 100, "normal", 0, 1024, true);
                    d[MachineState.Locked] = L(true, 100, "belownormal", 0, 2048, true);
                    d[MachineState.Idle] = L(true, 50, "idle", 0, 4096, true);
                    // The working set ceiling on LightUse is the one that keeps a
                    // 4.7 GiB model out of the owner's way. It does not stop the
                    // job allocating, it stops the job HOLDING: measured, a child
                    // that committed 512 MiB ran to completion with its working set
                    // trimmed to 191 MiB under a 192 MiB limit. The job pages, the
                    // owner's browser does not.
                    d[MachineState.LightUse] = L(true, 10, "idle", 8192, 6144, true);
                    // BUSY IS ZERO IN BALANCED, not the five per cent the measured
                    // throughput numbers would support, and that is the cautious
                    // reading on purpose: every CPU figure above came from a
                    // synthetic memory-streaming victim rather than from a game,
                    // so this is the one cell in the grid nobody has proved. The
                    // per-resource split means it rarely bites anyway - a game
                    // contends the CARD, so a CPU job falls back to the LightUse
                    // row rather than to this one. Somebody who wants the warm job
                    // kept at a trickle picks Generous, which ships 10.
                    d[MachineState.Busy] = L(false, 0, "idle", 0, 0, false);
                    break;
            }
            return d;
        }

        /// Two settings that are per profile because they encode how twitchy the
        /// owner wants the detector to be, rather than what it is safe to do.
        ///
        /// NOT PER PROFILE, and the distinction matters: ClearCooldownSeconds,
        /// BusyConfirmSamples and FastPollMs stay global. Those are safety
        /// properties, not intents. Ninety seconds is chosen for human behaviour -
        /// longer than a level load, longer than a round gap - and three samples
        /// cannot be one, because a pid that exits between two samples spikes
        /// ForeignPct for exactly one tick.
        public static void ProfileTuning(string id, out int idleAfterSeconds, out double foreignCpuBusyPct)
        {
            switch (NormaliseProfile(id))
            {
                case ProfileGenerous: idleAfterSeconds = 120; foreignCpuBusyPct = 70.0; break;
                case ProfileAway: idleAfterSeconds = 300; foreignCpuBusyPct = 30.0; break;
                default: idleAfterSeconds = 300; foreignCpuBusyPct = 40.0; break;
            }
        }

        /// The posture in force, before any per-cell override is counted.
        public string Profile = ProfileBalanced;

        /// Custom postures saved from the settings window, by id.
        public Dictionary<string, Dictionary<MachineState, ResourceLimits>> SavedProfiles =
            new Dictionary<string, Dictionary<MachineState, ResourceLimits>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SavedProfileNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// Does the live grid still match the profile it claims to be.
        ///
        /// PRECEDENCE, STATED ONCE. `Profile = <id>` resolves to a full matrix;
        /// any limits.<state>.<field> line present then overrides that one cell;
        /// and if any override differs from the named profile the effective
        /// profile is "custom" and the tray says "Custom, based on Balanced".
        /// Nothing is silently reinterpreted, and nobody has to guess whether the
        /// name or the numbers won.
        public bool MatchesProfile(string id)
        {
            Dictionary<MachineState, ResourceLimits> want = ResolveProfile(id);
            if (want == null) return false;
            foreach (MachineState st in AllStates())
            {
                ResourceLimits a = LimitsFor(st);
                ResourceLimits b;
                if (!want.TryGetValue(st, out b) || b == null) return false;
                if (!a.SameAs(b)) return false;
            }
            return true;
        }

        public Dictionary<MachineState, ResourceLimits> ResolveProfile(string id)
        {
            string norm = NormaliseProfile(id);
            Dictionary<MachineState, ResourceLimits> saved;
            // TIGHTENED ON THE WAY OUT, so that "does the live grid match this
            // posture" compares like with like. A saved posture is whatever
            // somebody typed, and the live grid is always monotone because the
            // loader repairs it. Without this the two could never be equal, and a
            // hand-written custom profile would report as "custom" the instant it
            // was applied - which is exactly the state it is meant to escape.
            if (SavedProfiles.TryGetValue(norm, out saved) && saved != null) return Tighten(saved);
            foreach (string p in ProfileIds()) if (p == norm) return Tighten(ProfileLimits(norm));
            return null;
        }

        /// What the tray and the API should call the posture in force. Returns
        /// "custom" when the grid has been edited away from its named profile.
        public string EffectiveProfile()
        {
            if (MatchesProfile(Profile)) return NormaliseProfile(Profile);
            foreach (string p in ProfileIds()) if (MatchesProfile(p)) return p;
            foreach (string k in SavedProfiles.Keys) if (MatchesProfile(k)) return NormaliseProfile(k);
            return "custom";
        }

        public string EffectiveProfileLabel()
        {
            string eff = EffectiveProfile();
            if (eff != "custom") return AnyProfileLabel(eff);
            return "Custom, based on " + AnyProfileLabel(Profile);
        }

        public string AnyProfileLabel(string id)
        {
            string norm = NormaliseProfile(id);
            string name;
            if (SavedProfileNames.TryGetValue(norm, out name) && !string.IsNullOrEmpty(name)) return name;
            return ProfileLabel(norm);
        }

        /// Fill every cell from a named posture, and move the two tunings with it.
        public void ApplyProfile(string id)
        {
            Dictionary<MachineState, ResourceLimits> want = ResolveProfile(id);
            if (want == null) return;
            Profile = NormaliseProfile(id);
            var fresh = new Dictionary<MachineState, ResourceLimits>();
            foreach (MachineState st in AllStates())
            {
                ResourceLimits r;
                fresh[st] = want.TryGetValue(st, out r) && r != null
                    ? r.Clone() : L(false, 0, "idle", 0, 0, false);
            }
            Limits = fresh;
            int idleAfter; double busyPct;
            ProfileTuning(Profile, out idleAfter, out busyPct);
            IdleAfterSeconds = idleAfter;
            ForeignCpuBusyPct = busyPct;
            TightenGrid();
        }

        /// Make the grid monotone, because the cooldown ratchet assumes it is.
        ///
        /// Policy.Evaluate holds the WORST MachineState seen inside ninety seconds
        /// and then looks its row up, which takes for granted that a worse state
        /// has a smaller row. Nothing enforced that. A grid where Busy is more
        /// generous than LightUse - one hand edit, one mistyped spinner, one saved
        /// custom posture - would invert the ratchet and hand the job MORE machine
        /// at the moment the owner sat down, which is the exact opposite of the
        /// only promise this program makes.
        ///
        /// REPAIRED RATHER THAN REFUSED. A person who mistypes a number must not
        /// end up with an agent that will not start. Each row is tightened to be
        /// no looser than the row above it, and the repair is reported so it is
        /// not silent.
        public List<string> TightenGrid()
        {
            var fixes = new List<string>();
            MachineState[] ladder = AllStates();
            for (int i = 1; i < ladder.Length; i++)
            {
                ResourceLimits looser = LimitsFor(ladder[i - 1]);
                ResourceLimits here = LimitsFor(ladder[i]);
                if (here.NoMoreThan(looser)) continue;
                ResourceLimits fixedRow = ResourceLimits.Tighter(here, looser);
                Limits[ladder[i]] = fixedRow;
                fixes.Add(StateLabel(ladder[i]) + " was more generous than "
                    + StateLabel(ladder[i - 1]).ToLowerInvariant()
                    + ", so it was tightened to: " + fixedRow.Describe());
            }
            return fixes;
        }

        /// The same repair on a grid that is not the live one, for profiles.
        public static Dictionary<MachineState, ResourceLimits> Tighten(
            Dictionary<MachineState, ResourceLimits> grid)
        {
            var outp = new Dictionary<MachineState, ResourceLimits>();
            MachineState[] ladder = AllStates();
            foreach (MachineState st in ladder)
            {
                ResourceLimits r;
                outp[st] = grid != null && grid.TryGetValue(st, out r) && r != null
                    ? r.Clone() : L(false, 0, "idle", 0, 0, false);
            }
            for (int i = 1; i < ladder.Length; i++)
                if (!outp[ladder[i]].NoMoreThan(outp[ladder[i - 1]]))
                    outp[ladder[i]] = ResourceLimits.Tighter(outp[ladder[i]], outp[ladder[i - 1]]);
            return outp;
        }

        /// The named rungs the tray offers, as data rather than as menu code.
        ///
        /// FOUR, NOT A SLIDER. A slider in a tray menu is a target nobody can hit
        /// with a mouse they are also using to play a game, and the difference
        /// between 34 and 37 per cent is not one anybody can feel. The numbers come
        /// from probe/p10_felt_hard.cs: at a 10 per cent cap a foreground workload
        /// kept 90 per cent of its throughput, at 25 per cent 80 per cent, and at
        /// 50 per cent the cap bought nothing at all because half a sixteen thread
        /// machine already saturates the memory controller.
        ///
        /// They live here rather than in TrayApp so that the tests can check the
        /// menu offers something a person can actually reach: a rung list whose
        /// entries match no shipped default would leave the menu permanently
        /// showing "Custom", which tells the owner nothing about their machine.
        public static string[] RungNames = new string[]
            { "Take the whole machine", "Take most of it", "Stay out of the way", "Nothing at all" };

        public static ResourceLimits Rung(int i)
        {
            if (i == 0) return L(true, 100, "normal", 0, 1024, true);
            if (i == 1) return L(true, 50, "belownormal", 0, 2048, true);
            if (i == 2) return L(true, 10, "idle", 8192, 6144, true);
            return L(false, 0, "idle", 0, 0, false);
        }

        /// How many threads a cap is worth, on this machine.
        ///
        /// WHY THE CHILD NEEDS TELLING AT ALL, given the kernel enforces the cap.
        /// Because it is a HARD cap: once the job has spent its share of a
        /// scheduling interval, no thread in it runs until the next one. A torch
        /// process that defaults to one thread per logical processor then has
        /// sixteen threads taking turns inside a tenth of a machine, which
        /// finishes no sooner than two would and evicts far more of the owner's
        /// cache on the way. The cap decides how much; this decides how thinly it
        /// is spread.
        ///
        /// AT ONE HUNDRED PER CENT THE ANSWER IS EVERY LOGICAL THREAD, and this
        /// was the physical core count until it was measured on the machine that
        /// actually runs the work.
        ///
        /// The physical-core rule was inferred from the NAS sweep, where
        /// Chatterbox went 0.230 realtime at 8 threads to 0.285 at 16 with
        /// per-thread efficiency halving, and from the reasoning that two sibling
        /// threads share the L1, the L2 and the front end, so SMT should buy
        /// almost nothing on a model whose advantage is a 96 MiB L3 it walks
        /// every token. Sound reasoning, wrong answer, and the difference is that
        /// the NAS is an 18 core Xeon from 2016 and the runner is an 8 core Ryzen
        /// from 2022. A rule inferred on one chip does not transfer to another.
        ///
        /// MEASURED DIRECTLY on the runner, one model load, no cap, no job
        /// object, only the thread count varying:
        ///
        ///      2 threads  0.132x
        ///      4 threads  0.211x
        ///      8 threads  0.249x     &lt;- what the physical-core rule gave
        ///     12 threads  0.271x     +9% over 8
        ///     16 threads  0.271x     +0.1% over 12, which is noise
        ///
        /// So the old rule left 9% on the table on the only machine anybody has
        /// run this on. The knee is 12 and 16 costs nothing over it, so the
        /// hundred per cent row takes the whole machine, which is also what the
        /// row MEANS: it applies when nobody is signed in.
        ///
        /// CpuThreadsAtFull overrides it for anyone whose chip disagrees. Leaving
        /// four threads to the machine costs 0.1% here and may be worth more than
        /// that elsewhere.
        ///
        /// DETECTED, NEVER ASSUMED. Both counts are passed in. A stranger's
        /// machine is not sixteen threads and is not eight cores, and when the
        /// physical count cannot be read the logical one is used.
        /// How many threads the hundred per cent row takes. 0 means every
        /// logical thread, which is the measured answer on the only machine this
        /// has run on. See ThreadsFor for the numbers.
        ///
        /// Static because ThreadsFor is, and ThreadsFor is static because the
        /// policy is plain data the tests drive without a Config at all.
        public static int CpuThreadsAtFull;

        public static int ThreadsFor(ResourceLimits l, int logical, int physical)
        {
            if (logical < 1) logical = 1;
            if (physical < 1 || physical > logical) physical = logical;
            if (l == null || l.CpuPct <= 0) return 0;      // 0 = leave the default alone
            if (l.CpuPct >= 100) return CpuThreadsAtFull > 0
                ? (CpuThreadsAtFull > logical ? logical : CpuThreadsAtFull)
                : logical;
            int n = (int)Math.Round(logical * l.CpuPct / 100.0);
            if (n < 1) n = 1;
            return n > physical ? physical : n;
        }

        public ResourceLimits LimitsFor(MachineState st)
        {
            ResourceLimits r;
            if (Limits != null && Limits.TryGetValue(st, out r) && r != null) return r;
            // A row nobody configured is the most restrictive row, never the most
            // generous one. Missing configuration must not read as permission.
            return L(false, 0, "idle", 0, 0, false);
        }

        public static string StateKey(MachineState st)
        {
            switch (st)
            {
                case MachineState.NobodyHome: return "nobodyhome";
                case MachineState.Locked: return "locked";
                case MachineState.Idle: return "idle";
                case MachineState.LightUse: return "lightuse";
                default: return "busy";
            }
        }

        /// The label a person sees, in the settings window and in the tray.
        public static string StateLabel(MachineState st)
        {
            switch (st)
            {
                case MachineState.NobodyHome: return "Nobody signed in";
                case MachineState.Locked: return "Signed in, locked";
                case MachineState.Idle: return "Signed in, idle";
                case MachineState.LightUse: return "Signed in, light use";
                default: return "Busy or gaming";
            }
        }

        public static MachineState[] AllStates()
        {
            return new MachineState[] { MachineState.NobodyHome, MachineState.Locked,
                MachineState.Idle, MachineState.LightUse, MachineState.Busy };
        }

        public List<ServiceDef> Services = new List<ServiceDef>();

        public ServiceDef Service(string id)
        {
            foreach (ServiceDef s in Services)
                if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// Fill in everything that defaults to a path under DataDir. Called after
        /// loading so that a DataDir set in the INI moves all of them together.
        public void Resolve()
        {
            if (string.IsNullOrEmpty(LogPath)) LogPath = Path.Combine(DataDir, "worker.log");
            if (string.IsNullOrEmpty(StatePath)) StatePath = Path.Combine(DataDir, "state.json");
            if (string.IsNullOrEmpty(AssetsDir)) AssetsDir = Path.Combine(DataDir, "assets");
            if (string.IsNullOrEmpty(QueueRoot)) QueueRoot = Path.Combine(DataDir, "queues");
            if (string.IsNullOrEmpty(CertPath)) CertPath = Path.Combine(DataDir, "certs", "server.pfx");
            if (string.IsNullOrEmpty(CertKeyLedgerPath))
                CertKeyLedgerPath = Path.Combine(DataDir, "certs", "cng-key-containers.txt");
            if (string.IsNullOrEmpty(ProvisionStatusPath))
                ProvisionStatusPath = Path.Combine(DataDir, "runtime", "provision.status");
            if (string.IsNullOrEmpty(ServerName))
            {
                // Detected, never assumed. A stranger's machine is not called
                // spring and their LAN is not this LAN.
                try { ServerName = Environment.MachineName.ToLowerInvariant(); }
                catch (Exception) { ServerName = "localhost"; }
            }
            if (string.IsNullOrEmpty(RuntimeRoot)) RuntimeRoot = Path.Combine(DataDir, "runtime");
            if (string.IsNullOrEmpty(ScriptsRoot)) ScriptsRoot = Path.Combine(DataDir, "services");
            foreach (ServiceDef s in Services)
            {
                if (string.IsNullOrEmpty(s.QueueDir)) s.QueueDir = Path.Combine(QueueRoot, s.Id);
                if (s.YieldGraceSeconds <= 0) s.YieldGraceSeconds = YieldGraceSeconds;
                if (string.IsNullOrEmpty(s.InstallDir)) s.InstallDir = Path.Combine(RuntimeRoot, s.Id);
                if (string.IsNullOrEmpty(s.ReadyMarker)) s.ReadyMarker = Path.Combine(s.InstallDir, ".installed");

                // THE DEFECT THIS PREVENTS: a shipped worker.ini.example that
                // cannot be used unedited. Every path a service needs is derived
                // from DataDir, which is derived from the user's own profile, so a
                // literal path in the example file is this machine's path and is
                // wrong on every other machine. That is the thing rule 6 is about,
                // and it is also just friction: an example file that works when you
                // uncomment it is worth more than one that documents what to type.
                //
                // Absolute paths still work; nothing here requires the placeholders.
                s.Command = Expand(s.Command, s);
                s.Arguments = Expand(s.Arguments, s);
                s.WorkingDir = Expand(s.WorkingDir, s);
                if (string.IsNullOrEmpty(s.WorkingDir)) s.WorkingDir = s.InstallDir;
            }
        }

        string Expand(string v, ServiceDef s)
        {
            if (string.IsNullOrEmpty(v) || v.IndexOf('%') < 0) return v;
            v = v.Replace("%RUNTIME%", RuntimeRoot);
            v = v.Replace("%SCRIPTS%", ScriptsRoot);
            v = v.Replace("%QUEUE%", s.QueueDir);
            v = v.Replace("%INSTALL%", s.InstallDir);
            v = v.Replace("%DATA%", DataDir);
            return v;
        }

        /// The bearer token actually in force, or empty when there is none.
        public string EffectiveApiKey()
        {
            try
            {
                if (!string.IsNullOrEmpty(ApiKeyFile) && File.Exists(ApiKeyFile))
                    return File.ReadAllText(ApiKeyFile).Trim();
            }
            catch (Exception) { }
            return ApiKey == null ? "" : ApiKey.Trim();
        }

        public bool BindIsLoopback()
        {
            string b = (Bind ?? "").Trim();
            return b == "127.0.0.1" || b == "::1" || b.ToLowerInvariant() == "localhost";
        }

        // -- Loading -------------------------------------------------------------

        /// Deliberately an INI, not JSON. There is no JSON parser in the .NET
        /// Framework subset available to the legacy csc.exe without dragging in
        /// System.Web.Extensions, and a settings file a human edits by hand at 2am
        /// should not be able to fail on a trailing comma.
        public static Config Load(string path)
        {
            var c = new Config();
            c.ConfigPath = path == null ? "" : path;
            if (path == null || !File.Exists(path)) { c.Resolve(); return c; }

            // TWO PASSES, BECAUSE PRECEDENCE MUST NOT DEPEND ON LINE ORDER.
            // `Profile = away` fills every cell; `limits.busy.cpupct = 50`
            // overrides one. If both were applied as they were read, a file with
            // the profile line UNDERNEATH the override would silently lose the
            // override, and a file with it above would keep it - the same file
            // meaning two different things depending on how somebody happened to
            // type it. So the overrides are collected here and replayed after the
            // profile has been resolved, whatever order they appear in.
            var overrides = new List<string[]>();
            string wantProfile = null;

            ServiceDef current = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string section = line.Substring(1, line.Length - 2).Trim();
                    current = null;
                    if (section.StartsWith("service.", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = section.Substring("service.".Length).Trim();
                        if (JobStore.SafeServiceId(id))
                        {
                            current = c.Service(id);
                            if (current == null) { current = new ServiceDef(); current.Id = id; c.Services.Add(current); }
                        }
                    }
                    // Any other section header, [listener] and [agent] included, is
                    // decoration: the key names below are unique across the whole
                    // file, so sections group settings for a human reader without
                    // the parser needing to care which one it is in.
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                try
                {
                    if (current != null) { ApplyService(current, k, v); continue; }
                    if (k == "profile") { wantProfile = v; continue; }
                    if (k.StartsWith("limits.", StringComparison.Ordinal)) { overrides.Add(new string[] { k, v }); continue; }
                    Apply(c, k, v);
                }
                catch (Exception) { /* a bad line must not stop the agent starting */ }
            }

            // The profile first, so that its two tunings and its whole matrix are
            // in place, then the per-cell overrides on top of it.
            if (wantProfile != null && c.ResolveProfile(wantProfile) != null) c.ApplyProfile(wantProfile);
            else if (wantProfile != null) c.Profile = NormaliseProfile(wantProfile);
            foreach (string[] kv in overrides)
            {
                try { ApplyLimit(c, kv[0], kv[1]); }
                catch (Exception) { }
            }
            c.GridRepairs = c.TightenGrid();
            c.Resolve();
            return c;
        }

        /// Rows the loader had to tighten because the file was not monotone, in
        /// the words the log and the settings window use. Empty on a sane file.
        public List<string> GridRepairs = new List<string>();

        static void ApplyService(ServiceDef s, string k, string v)
        {
            switch (k)
            {
                case "command": s.Command = v; break;
                case "arguments": s.Arguments = v; break;
                case "workingdir": s.WorkingDir = v; break;
                case "queuedir": s.QueueDir = v; break;
                case "priority": s.Priority = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "labels": s.Labels = Split(v); break;
                case "yieldgraceseconds": s.YieldGraceSeconds = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "enabled": s.Enabled = Truthy(v); break;
                case "description": s.Description = v; break;
                case "provision": s.Provision = v; break;
                case "provisionarguments": s.ProvisionArguments = v; break;
                case "installdir": s.InstallDir = v; break;
                case "readymarker": s.ReadyMarker = v; break;
                case "sizehint": s.SizeHint = v; break;
                case "device": s.Device = v; break;
                case "needsmemorymib": s.NeedsMemoryMib = int.Parse(v, CultureInfo.InvariantCulture); break;
            }
        }

        static bool Truthy(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        static string[] Split(string v)
        {
            var outp = new List<string>();
            foreach (string p in (v ?? "").Split(','))
                if (p.Trim().Length > 0) outp.Add(p.Trim());
            return outp.ToArray();
        }

        static void Apply(Config c, string k, string v)
        {
            switch (k)
            {
                case "memclockbusymhz": c.MemClockBusyMhz = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "powerbusywatts": c.PowerBusyWatts = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "utilgpubusypct": c.UtilGpuBusyPct = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "util3dbusypct": c.Util3dBusyPct = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "foreignvrambusymib": c.ForeignVramBusyMiB = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "busyconfirmsamples": c.BusyConfirmSamples = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "clearcooldownseconds": c.ClearCooldownSeconds = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "fastpollms": c.FastPollMs = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "counterpollms": c.CounterPollMs = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "yieldgraceseconds": c.YieldGraceSeconds = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "schedulerpollms": c.SchedulerPollMs = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "idleafterseconds": c.IdleAfterSeconds = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "foreigncpubusypct": c.ForeignCpuBusyPct = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "gameprocessnames": c.GameProcessNames = Split(v); break;
                case "cputhreadsatfull": CpuThreadsAtFull = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "vramallowlist": c.VramAllowlist = Split(v); break;
                case "idlepstates": c.IdlePStates = Split(v); break;
                case "anticheatservices": c.AntiCheatServices = Split(v); break;
                case "startmode": c.StartMode = v; break;
                case "datadir": c.DataDir = v; break;
                case "logpath": c.LogPath = v; break;
                case "statepath": c.StatePath = v; break;
                case "assetsdir": c.AssetsDir = v; break;
                case "queueroot": c.QueueRoot = v; break;
                case "runtimeroot": c.RuntimeRoot = v; break;
                case "scriptsroot": c.ScriptsRoot = v; break;
                case "provisionstatuspath": c.ProvisionStatusPath = v; break;

                case "listenerenabled": c.ListenerEnabled = Truthy(v); break;
                case "bind": c.Bind = v; break;
                case "port": c.Port = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "certpath": c.CertPath = v; break;
                case "certkeyledgerpath": c.CertKeyLedgerPath = v; break;
                case "servername": c.ServerName = v; break;
                case "certextrasans": c.CertExtraSans = Split(v); break;
                case "certyears": c.CertYears = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "apikey": c.ApiKey = v; break;
                case "apikeyfile": c.ApiKeyFile = v; break;
                case "allowedcidrs": c.AllowedCidrs = Split(v); break;
                case "maxrequestbytes": c.MaxRequestBytes = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "maxheadercount": c.MaxHeaderCount = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "maxheaderlinebytes": c.MaxHeaderLineBytes = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "handshaketimeoutms": c.HandshakeTimeoutMs = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "requesttimeoutms": c.RequestTimeoutMs = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "maxconnections": c.MaxConnections = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "maxpassthroughbytes": c.MaxPassthroughBytes = int.Parse(v, CultureInfo.InvariantCulture); break;

                // Client side. Only the CLI reads these; the agent ignores them.
                case "clienthost": c.ClientHost = v; break;
                case "clientport": c.ClientPort = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "certfingerprint": c.CertFingerprint = v; break;

                // The limits matrix. Flat keys rather than real sections, because
                // the parser deliberately treats section headers as decoration -
                // see Load - and one more special case in it is one more thing
                // that can go wrong in a file somebody edits at 2am.
                //
                //   limits.lightuse.cpupct = 10
                //   limits.locked.gpu      = yes
                default: ApplyLimit(c, k, v); break;
            }
        }

        /// limits.<state>.<field>. Unknown states and unknown fields are ignored
        /// rather than throwing, on the same rule as every other line: a bad line
        /// must not stop the agent starting.
        static void ApplyLimit(Config c, string k, string v)
        {
            if (k.StartsWith("profile.", StringComparison.Ordinal)) { ApplySavedProfile(c, k, v); return; }
            if (!k.StartsWith("limits.", StringComparison.Ordinal)) return;
            string[] parts = k.Split('.');
            if (parts.Length != 3) return;
            MachineState st = MachineState.Busy;
            if (!StateFromKey(parts[1], out st)) return;
            ResourceLimits r;
            if (!c.Limits.TryGetValue(st, out r) || r == null)
            {
                r = new ResourceLimits(); c.Limits[st] = r;
            }
            SetField(r, parts[2], v);
        }

        /// One row, as `state field=value field=value`, for POST /v1/limits and
        /// for `idlegpu limits`.
        ///
        /// WHY A STRING AND NOT A STRUCT. Command carries one string across the
        /// queue that keeps the policy thread the single writer, and inventing a
        /// second payload type for one route would mean a second thing to keep in
        /// step with the parser that reads worker.ini. The field names are the SAME
        /// names as the file's, deliberately, so anybody who has read one surface
        /// can use the other without a table.
        ///
        /// FIELDS NOT MENTIONED KEEP THEIR CURRENT VALUE. A caller that wants to
        /// change one cap should not have to restate the priority, the ceiling and
        /// the headroom, and a caller that forgets one should not silently reset it
        /// to a default it never chose.
        public static bool ParseLimitsCommand(Config c, string spec, out MachineState st, out ResourceLimits row)
        {
            st = MachineState.Busy; row = null;
            if (c == null || string.IsNullOrEmpty(spec)) return false;
            string[] words = spec.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            if (!StateFromKey(words[0].Trim().ToLowerInvariant(), out st)) return false;
            row = c.LimitsFor(st).Clone();
            for (int i = 1; i < words.Length; i++)
            {
                int eq = words[i].IndexOf('=');
                if (eq <= 0) continue;
                string f = words[i].Substring(0, eq).Trim().ToLowerInvariant().Replace("_", "");
                string v = words[i].Substring(eq + 1).Trim();
                try { SetField(row, f, v); }
                catch (Exception) { /* one bad field must not lose the others */ }
            }
            return true;
        }

        static bool StateFromKey(string key, out MachineState st)
        {
            st = MachineState.Busy;
            foreach (MachineState s in AllStates())
                if (StateKey(s) == key) { st = s; return true; }
            return false;
        }

        static void SetField(ResourceLimits r, string field, string v)
        {
            switch (field)
            {
                case "gpu": r.Gpu = Truthy(v); break;
                case "cpupct": r.CpuPct = Clamp(int.Parse(v, CultureInfo.InvariantCulture), 0, 100); break;
                case "priority": r.Priority = NormalisePriority(v); break;
                case "workingsetmib": r.WorkingSetMib = Math.Max(0, int.Parse(v, CultureInfo.InvariantCulture)); break;
                case "minfreemib": r.MinFreeMib = Math.Max(0, int.Parse(v, CultureInfo.InvariantCulture)); break;
                case "admit": r.Admit = Truthy(v); break;
            }
        }

        /// profile.<id>.name = Night shift, and profile.<id>.<state>.<field>.
        ///
        /// The SAME flat-key shape as [limits], and for the same reason: the
        /// parser treats section headers as decoration, so one shape covers both
        /// and there is no second thing to get wrong in a file somebody edits at
        /// 2am. A saved posture appears in the tray list beside the three shipped
        /// ones; there is no manager and no deletion beyond editing this file,
        /// which is a deliberate limit on how much furniture this grows.
        static void ApplySavedProfile(Config c, string k, string v)
        {
            string[] parts = k.Split('.');
            if (parts.Length < 3) return;
            string id = NormaliseProfile(parts[1]);
            if (id.Length == 0) return;
            if (parts.Length == 3 && parts[2] == "name") { c.SavedProfileNames[id] = v; return; }
            if (parts.Length != 4) return;
            MachineState st;
            if (!StateFromKey(parts[2], out st)) return;
            Dictionary<MachineState, ResourceLimits> grid;
            if (!c.SavedProfiles.TryGetValue(id, out grid) || grid == null)
            {
                // A saved posture starts from Balanced rather than from nothing, so
                // a file that names three cells still describes a whole machine.
                grid = ProfileLimits(ProfileBalanced);
                c.SavedProfiles[id] = grid;
            }
            ResourceLimits r;
            if (!grid.TryGetValue(st, out r) || r == null) { r = new ResourceLimits(); grid[st] = r; }
            SetField(r, parts[3], v);
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// One spelling, whatever was typed. An unrecognised value becomes "idle"
        /// rather than "normal": a typo in a settings file must not quietly hand
        /// the machine over.
        public static string NormalisePriority(string v)
        {
            string t = (v ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (t == "normal") return "normal";
            if (t == "belownormal" || t == "below" || t == "low") return "belownormal";
            return "idle";
        }

        // -- CLI client settings -------------------------------------------------
        //
        // The CLI reads the same worker.ini when it is run on the machine that
        // hosts the agent, and --host / --port / --fingerprint / --key override
        // them for a remote runner over the LAN or an SSH tunnel.

        public string ClientHost = "";
        public int ClientPort = 0;

        /// The SHA-256 the client pins, lower case hex, colons optional. Printed
        /// by `idlegpu fingerprint` and by the agent on first run.
        public string CertFingerprint = "";
    }

    /// One service the runner hosts.
    ///
    /// A service is a controller process plus a queue directory. The generic layer
    /// knows nothing else about it: not what a job means, not what comes back, not
    /// what it needs installed. That is the whole architecture in one class.
    public class ServiceDef
    {
        public string Id = "";
        public string Command = "";
        public string Arguments = "";
        public string WorkingDir = "";
        public string QueueDir = "";
        public string Description = "";

        /// Higher wins when more than one service has queued work. There is one
        /// GPU, so there is one running controller, and there is no fairness
        /// beyond this number: a long job starves a queued one until it finishes
        /// or the user takes the machine back. That is a stated limitation rather
        /// than an oversight, and it is why a controller is asked to exit when its
        /// own queue has been empty for a while.
        public int Priority = 10;

        /// Free text, comma separated, published as-is. This is how one runner
        /// offers speech, image generation and password recovery at once without
        /// the generic layer having a word for any of them.
        public string[] Labels = new string[0];

        /// Overrides the global grace period. A speech controller can drop the
        /// chunk in flight in well under a second; a Hashcat controller has to
        /// write a restore file first and needs longer.
        public int YieldGraceSeconds = 0;

        /// Which resource this service is actually after.
        ///
        /// Read from the manifest's own `device` field, which chatterbox already
        /// publishes, so the runner does not have to be told twice. It decides
        /// which gate the scheduler puts the service behind: a GPU service waits
        /// for the GPU detector, a CPU service waits only for the matrix row, and
        /// before this distinction existed a CPU-only speech job sat out a ninety
        /// second cooldown caused by a game on a card it never touched.
        public string Device = "gpu";

        public bool WantsGpu
        {
            get
            {
                string d = (Device ?? "gpu").Trim().ToLowerInvariant();
                return d != "cpu" && d != "none" && d != "fake";
            }
        }

        /// Roughly how much system memory this service needs resident, in MiB, for
        /// the admission check. 0 means it has not said.
        ///
        /// Chatterbox on the CPU is about 6,500 MiB measured, which on a 31.9 GiB
        /// machine is a fifth of it. That is not a number to discover by watching
        /// somebody's browser start swapping.
        public int NeedsMemoryMib;

        // -- Opting in -----------------------------------------------------------
        //
        // NOTHING IS FETCHED UNTIL SOMEBODY ASKS FOR IT. This block is the whole
        // mechanism, and the default below is the load bearing part of it.
        //
        // Enabled is FALSE by default, so a [service.chatterbox] section that
        // arrives in the shipped example file is a service this build KNOWS ABOUT
        // and has not installed. It costs nothing, downloads nothing and appears in
        // `idlegpu service list` as known. It becomes real only when somebody runs
        // `idlegpu service install <id>`, which runs Provision and then writes
        // Enabled = true back into worker.ini.
        //
        // The defect this prevents is the obvious one and it is expensive: a person
        // installs a 426 KB agent to lend a machine's idle GPU, and the base install
        // quietly pulls six gigabytes of torch and speech weights they never asked
        // for onto a gaming PC. There is no code path in this program that runs
        // Provision on its own.

        /// Opted in. False means known-but-not-installed: the agent does not
        /// register it, does not create its queue and never launches it.
        public bool Enabled = false;

        /// What to run to install this service, relative to the repository or the
        /// install directory. Empty means the service needs nothing fetched, which
        /// is the case for a controller that is a single script (see services/echo).
        public string Provision = "";
        public string ProvisionArguments = "";

        /// THE DIRECTORY THIS SERVICE OWNS, and the only thing removing the service
        /// deletes. Everything a service's provisioning downloads goes here: its
        /// Python, its site-packages, its model weights, its caches. Defaults to
        /// <DataDir>\services\<id>.
        ///
        /// This is also the answer to "what does it cost": the recursive size of
        /// this directory is the honest number, measured rather than declared.
        public string InstallDir = "";

        /// The file whose existence means provisioning finished. Defaults to
        /// <InstallDir>\.installed. Written by the provisioning script as its LAST
        /// act, so a download that was interrupted half way leaves the service
        /// reading as not installed rather than as broken.
        public string ReadyMarker = "";

        /// What the download will cost, in the service author's own words, shown
        /// BEFORE anything is fetched. Free text on purpose: a service knows what
        /// it is about to pull down and the generic layer never will.
        public string SizeHint = "";
    }

    /// A JSON writer sized to this program's needs and no larger.
    /// WHY hand-rolled: the agent must build with the in-box csc.exe against the
    /// in-box reference assemblies, with no NuGet restore and no network. Pulling
    /// System.Web.Extensions in for one status object is a worse trade than
    /// thirty lines of escaping.
    public static class Json
    {
        public static string Esc(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        public static string Num(double d)
        {
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Obj(params string[] pairs)
        {
            return "{" + string.Join(",", pairs) + "}";
        }

        public static string P(string key, string rawValue)
        {
            return Esc(key) + ":" + rawValue;
        }

        /// Is this text one well-formed JSON object?
        ///
        /// THE DEFECT THIS PREVENTS, and it was found by driving the real CLI on
        /// the real machine. The submit handler used to check only that the body
        /// started with { and ended with }, on the principle that what a job MEANS
        /// is the service's business and the agent should not parse it. That
        /// principle is right and the check was still wrong: a body that is not
        /// JSON at all passed it, got written into a lease, returned 202 Accepted,
        /// and then failed inside the controller with "the lease could not be
        /// parsed" once the scheduler eventually launched it. The client had
        /// already gone away, the error arrived nowhere near the mistake, and the
        /// mistake was a shell quoting rule rather than anything about the service.
        ///
        /// (The trigger was PowerShell, which strips double quotes when it passes
        /// arguments to a native executable, so {"text":"hi"} arrives as {text:hi}.
        /// PowerShell is the shell on the machines this runs on, so this is the
        /// normal case, not an exotic one.)
        ///
        /// This VALIDATES without INTERPRETING: it walks the text confirming the
        /// grammar and builds nothing. The agent still has no idea what any of the
        /// keys mean, which is the property that lets a new service be added without
        /// touching any of this. It just refuses to write a lease that the
        /// controller is certain to choke on, at the moment the client can be told.
        public static bool LooksLikeObject(string s)
        {
            if (s == null) return false;
            int i = 0;
            if (!SkipWs(s, ref i)) return false;
            if (i >= s.Length || s[i] != '{') return false;
            if (!SkipValue(s, ref i)) return false;
            SkipWs(s, ref i);
            return i >= s.Length;   // trailing anything is a malformed document
        }

        static bool SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            return true;
        }

        /// Recursive descent with a hard depth limit.
        ///
        /// The limit is not decoration. This runs on a listener thread, before any
        /// authentication decision has finished mattering, against bytes chosen by
        /// whoever opened the socket; "[[[[[..." repeated eight thousand times is a
        /// stack overflow, and a stack overflow on .NET cannot be caught and takes
        /// the whole agent down with it, including the policy loop that is the only
        /// thing giving somebody their GPU back.
        static bool SkipValue(string s, ref int i, int depth = 0)
        {
            if (depth > 64) return false;
            if (!SkipWs(s, ref i) || i >= s.Length) return false;
            char c = s[i];

            if (c == '{' || c == '[')
            {
                bool obj = c == '{';
                char close = obj ? '}' : ']';
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == close) { i++; return true; }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (obj)
                    {
                        if (i >= s.Length || s[i] != '"') return false;
                        if (!SkipString(s, ref i)) return false;
                        SkipWs(s, ref i);
                        if (i >= s.Length || s[i] != ':') return false;
                        i++;
                    }
                    if (!SkipValue(s, ref i, depth + 1)) return false;
                    SkipWs(s, ref i);
                    if (i >= s.Length) return false;
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == close) { i++; return true; }
                    return false;
                }
            }

            if (c == '"') return SkipString(s, ref i);
            if (Lit(s, ref i, "true") || Lit(s, ref i, "false") || Lit(s, ref i, "null")) return true;
            return SkipNumber(s, ref i);
        }

        static bool Lit(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length) return false;
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static bool SkipString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return false;
            i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (i + 1 >= s.Length) return false;
                    char n = s[i + 1];
                    if ("\"\\/bfnrt".IndexOf(n) >= 0) { i += 2; continue; }
                    if (n == 'u')
                    {
                        if (i + 5 >= s.Length) return false;
                        for (int k = 2; k <= 5; k++)
                            if (!Uri.IsHexDigit(s[i + k])) return false;
                        i += 6;
                        continue;
                    }
                    return false;
                }
                if (c == '"') { i++; return true; }
                if (c < 0x20) return false;   // a raw control character is not a JSON string
                i++;
            }
            return false;
        }

        static bool SkipNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            // JSON's integer part is `0` or [1-9][0-9]*. A leading zero is not
            // laxness, it is a different document: Python's json.loads refuses
            // {"a":01}, so accepting it here would put a lease on disk that the
            // controller cannot read, which is the exact failure this validator
            // exists to move forward in time.
            if (i >= s.Length || s[i] < '0' || s[i] > '9') return false;
            if (s[i] == '0') i++;
            else while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                int frac = 0;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; frac++; }
                if (frac == 0) return false;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                int exp = 0;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; exp++; }
                if (exp == 0) return false;
            }
            return i > start;
        }

        /// Pull one top-level string value out of a small JSON object.
        ///
        /// THIS IS A SCANNER, NOT A PARSER, and it is used for exactly one thing:
        /// reading {"mode":"Auto"} off a request body so that curl and a JSON
        /// client work without a query string. It refuses to go inside a nested
        /// object or array, so it cannot be fooled into returning a value from
        /// somewhere else in the document, and it returns null on anything it does
        /// not understand. Everything else in this program treats JSON as opaque
        /// bytes precisely so that a real parser is never needed.
        public static string PeekString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int depth = 0;
            int i = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '{' || c == '[') { depth++; i++; continue; }
                if (c == '}' || c == ']') { depth--; i++; continue; }
                if (c != '"') { i++; continue; }
                int close;
                string name = ScanString(json, i, out close);
                if (name == null) return null;
                i = close;
                if (depth != 1 || name != key) continue;
                while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
                if (i >= json.Length || json[i] != ':') continue;
                i++;
                while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
                if (i >= json.Length || json[i] != '"') return null;
                return ScanString(json, i, out close);
            }
            return null;
        }

        static string ScanString(string s, int start, out int after)
        {
            var sb = new StringBuilder();
            int i = start + 1;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (i + 1 >= s.Length) break;
                    char n = s[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && i + 5 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out code))
                        { sb.Append((char)code); i += 6; continue; }
                        break;
                    }
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') { after = i + 1; return sb.ToString(); }
                sb.Append(c);
                i++;
            }
            after = s.Length;
            return null;
        }
    }
}
