// Turning a recorded CSV back into Snapshots.
//
// WHY THE FIXTURE FORMAT AND THE CALIBRATION FORMAT ARE THE SAME FORMAT. This
// reads exactly what `offpeak.exe --calibrate game.csv 20` writes. That
// is the whole point: when the user runs the agent through twenty minutes of a
// real match, the CSV it produces drops straight into tests/fixtures/ and becomes
// a regression test, with no conversion step and nobody transcribing numbers.
//
// The busy side of every tier-2 threshold is currently unmeasured - read-only
// probing on somebody's gaming PC cannot generate GPU load - so the busy fixtures
// in tests/fixtures/ are SYNTHETIC and are named to say so. Replacing them with a
// recorded match, or with a run of runtime/loadgen.py, is the point at which the
// thresholds stop being reasoned and start being measured.
//
// NOTE ON THE VRAM COLUMNS. The recorded row carries the top RAW GPU-memory
// consumer - name, pid and MiB, before any allowlist is applied - rather than a
// pre-filtered "foreign" figure. That is what lets a replay exercise
// Policy.ForeignVram itself: a fixture whose top consumer is dwm at 169 MiB must
// come back clear because dwm is allowlisted, and a fixture whose top consumer is
// cs2 at 3000 MiB must veto. Recording the post-filter answer would have made
// those two rows indistinguishable and the test worthless.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OffPeak
{
    public static class Replay
    {
        public const string Header =
            "iso_time,state,util_gpu,util_mem,enc,dec,mem_clk_mhz,sm_clk_mhz,pstate,power_w," +
            "mem_used_mib,eng_3d,eng_decode,eng_encode,gpu_healthy,counters_fresh," +
            "own_session,console_session,console_user,locked,input_idle_s,fullscreen,fg_process," +
            "steam_appid,steam_appname,vgc,anticheat_svc,game_procs,vram_top_pid,vram_top_name,vram_top_mib," +
            // THE SECOND RESOURCE, and the third time the absent-versus-empty trap
            // has come up in this file. See the comment on console_user below and
            // the one on the memory columns further down: the rule is that adding a
            // COLUMN can never change an old fixture's verdict, but choosing the
            // wrong DEFAULT for it silently rewrites history.
            "cpu_total_pct,cpu_own_pct,mem_total_mib,mem_avail_mib,mem_load_pct,reason";

        public static List<Snapshot> Load(string path)
        {
            var rows = new List<Snapshot>();
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0) return rows;

            string[] cols = SplitCsv(lines[0]);
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cols.Length; i++) idx[cols[i].Trim()] = i;

            for (int li = 1; li < lines.Length; li++)
            {
                if (lines[li].Trim().Length == 0) continue;
                string[] f = SplitCsv(lines[li]);
                rows.Add(Row(f, idx));
            }
            return rows;
        }

        static Snapshot Row(string[] f, Dictionary<string, int> idx)
        {
            var s = new Snapshot();
            s.At = Dt(Get(f, idx, "iso_time"));

            var g = new GpuSample();
            g.At = s.At;
            g.UtilGpu = I(Get(f, idx, "util_gpu"));
            g.UtilMem = I(Get(f, idx, "util_mem"));
            g.UtilEncoder = I(Get(f, idx, "enc"));
            g.UtilDecoder = I(Get(f, idx, "dec"));
            g.ClockMemMhz = I(Get(f, idx, "mem_clk_mhz"));
            g.ClockSmMhz = I(Get(f, idx, "sm_clk_mhz"));
            g.PState = Get(f, idx, "pstate");
            g.PowerWatts = D(Get(f, idx, "power_w"));
            g.MemUsedMiB = I(Get(f, idx, "mem_used_mib"));
            g.Valid = g.PState.Length > 0;
            s.Gpu = g;

            s.Util3d = D(Get(f, idx, "eng_3d"));
            s.UtilVideoDecode = D(Get(f, idx, "eng_decode"));
            s.UtilVideoEncode = D(Get(f, idx, "eng_encode"));

            // Default true when the column is absent, so a trace recorded by an
            // older build still replays as "the stream was fine".
            s.GpuHealthy = B(Get(f, idx, "gpu_healthy"), true);
            s.CountersFresh = B(Get(f, idx, "counters_fresh"), true);

            // THE CPU, AND WHY ABSENT IS NOT ZERO EVEN THOUGH ZERO IS HARMLESS.
            //
            // A missing cpu_total_pct would read as an idle processor, which
            // happens to preserve every existing verdict because zero is under
            // ForeignCpuBusyPct. That is luck rather than design, and it would put
            // a measurement in the record that was never taken: a fixture recorded
            // before this column existed says NOTHING about the processor, and
            // "nothing" is not "idle". Valid is false instead, which skips the CPU
            // tier entirely and is the honest reading of a GPU-only recording.
            //
            // ForeignPct is recomputed here rather than read, so a hand-written
            // fixture cannot assert a foreign load that contradicts its own two
            // numbers. Floored at zero: a pid that exits between two samples loses
            // its final slice, which makes the machine total momentarily smaller
            // than our own, and that is a one-tick artefact rather than negative
            // load. It is also why BusyConfirmSamples can never be 1.
            var cpu = new CpuSample();
            cpu.At = s.At;
            cpu.Valid = idx.ContainsKey("cpu_total_pct");
            if (cpu.Valid)
            {
                cpu.MachinePct = D(Get(f, idx, "cpu_total_pct"));
                cpu.OwnPct = D(Get(f, idx, "cpu_own_pct"));
                if (cpu.OwnPct > cpu.MachinePct) cpu.OwnPct = cpu.MachinePct;
                cpu.ForeignPct = cpu.MachinePct - cpu.OwnPct;
                if (cpu.ForeignPct < 0) cpu.ForeignPct = 0;
            }
            s.Cpu = cpu;

            // MEMORY, WHERE THE WRONG DEFAULT WOULD HAVE BEEN CATASTROPHIC RATHER
            // THAN MERELY DISHONEST.
            //
            // mem_avail_mib defaulting to 0 does not mean "we did not look", it
            // means ZERO BYTES FREE, and Policy.MemoryAllows would have refused
            // admission on every one of the fixtures recorded before this column
            // existed. Every one of them would have kept its policy verdict and
            // silently stopped being able to start a job.
            //
            // So Valid is false when the column is absent, and MemoryAllows fails
            // OPEN on that - which is the opposite of the rule tier 0 uses for the
            // user, and deliberately so. Blindness about the USER fails closed
            // because being wrong costs somebody their match. Blindness about free
            // memory fails open because being wrong costs a runner that never
            // starts anything, and that is the worse failure.
            //
            // THIS IS NOW THE THIRD COLUMN WITH THIS PROBLEM. Whoever adds the
            // fourth: the question is never "is the default harmless", it is "does
            // the default assert something the recording did not say".
            var mem = new MemorySample();
            mem.At = s.At;
            mem.Valid = idx.ContainsKey("mem_avail_mib");
            if (mem.Valid)
            {
                mem.TotalMib = I(Get(f, idx, "mem_total_mib"));
                mem.AvailableMib = I(Get(f, idx, "mem_avail_mib"));
                mem.LoadPct = I(Get(f, idx, "mem_load_pct"));
            }
            s.Memory = mem;

            var ss = new SessionSignals();
            ss.OwnSessionId = (uint)I(Get(f, idx, "own_session"));
            ss.ConsoleSessionId = (uint)I(Get(f, idx, "console_session"));
            ss.RunningInConsoleSession = ss.OwnSessionId == ss.ConsoleSessionId;
            // ABSENT AND EMPTY MEAN DIFFERENT THINGS HERE, which is why this is
            // not one line. A fixture recorded before this column existed says
            // nothing about who is signed in, and every one of those was written
            // when being outside the console session was an unconditional veto,
            // so the old meaning is preserved by assuming somebody is there. A
            // fixture that HAS the column and leaves it empty is asserting the
            // opposite -- an unattended machine -- which is the case a service
            // lives in and the reason the column was added.
            ss.ConsoleUserName = Get(f, idx, "console_user");
            ss.HasConsoleUser = idx.ContainsKey("console_user")
                ? ss.ConsoleUserName.Length > 0
                : true;
            ss.Locked = B(Get(f, idx, "locked"), false);
            ss.InputIdleSeconds = I(Get(f, idx, "input_idle_s"));
            ss.ForegroundIsFullScreen = B(Get(f, idx, "fullscreen"), false);
            ss.ForegroundProcess = Get(f, idx, "fg_process");
            ss.ForegroundTitle = "";
            s.Session = ss;

            var l = new LauncherSignals();
            l.SteamRunningAppId = I(Get(f, idx, "steam_appid"));
            l.SteamRunningAppName = Get(f, idx, "steam_appname");
            l.ValorantAntiCheatActive = B(Get(f, idx, "vgc"), false);
            // The NAME of the service that fired, not just that one did. The
            // policy reads its list of anti-cheat service names from config now,
            // so a recording that only carried a boolean could not tell a replay
            // WHICH service was seen, and the verdict text lost the one detail
            // that makes it actionable: vgc means a game, vgk means nothing
            // because vgk is always running. Recordings made before this column
            // existed simply have no name, which is what the empty default is for.
            l.AntiCheatService = Get(f, idx, "anticheat_svc");
            string gp = Get(f, idx, "game_procs");
            if (gp.Length > 0)
                foreach (string one in gp.Split(' '))
                    if (one.Trim().Length > 0) l.GameProcesses.Add(one.Trim());
            s.Launchers = l;

            string vn = Get(f, idx, "vram_top_name");
            double vm = D(Get(f, idx, "vram_top_mib"));
            if (vn.Length > 0 && vm > 0)
            {
                var u = new ProcessGpuUse();
                u.Pid = I(Get(f, idx, "vram_top_pid"));
                u.Name = vn;
                u.DedicatedMiB = vm;
                s.GpuProcesses.Add(u);
            }
            return s;
        }

        static string Get(string[] f, Dictionary<string, int> idx, string name)
        {
            int i;
            if (!idx.TryGetValue(name, out i)) return "";
            if (i >= f.Length) return "";
            return f[i] == null ? "" : f[i].Trim();
        }

        static int I(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static bool B(string s, bool dflt)
        {
            if (s.Length == 0) return dflt;
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        static DateTime Dt(string s)
        {
            DateTime v;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out v)) return v;
            return DateTime.MinValue;
        }

        /// Minimal CSV, matching what Program.Csv writes: commas separate, double
        /// quotes wrap a field containing a comma, and "" is a literal quote.
        public static string[] SplitCsv(string line)
        {
            var outp = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else q = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { outp.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            outp.Add(sb.ToString());
            return outp.ToArray();
        }
    }
}
