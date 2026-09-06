// The whole policy, seen at once.
//
// TWO SURFACES, DIVIDED ON PURPOSE. The tray menu is for what you change without
// stopping what you are doing: the state you are in right now, four named rungs,
// one click. This window is for the matrix - five states by five settings - and
// it is where you go when you want to think about it rather than react to it.
// Neither is a shortcut to the other: putting twenty five values in a menu would
// make the menu useless, and making the menu open this window for everything
// would make the menu pointless.
//
// WHY THIS IS FREE. build.ps1 already references System.Windows.Forms.dll and
// System.Drawing.dll, because TrayApp.cs needs NotifyIcon and ContextMenuStrip
// and draws its own icon. A Form costs no new reference, no NuGet package, no
// build change and nothing outside the contained directory. It is laid out in
// code rather than in a designer for the same reason the icon is drawn rather
// than shipped: the deliverable stays one binary with nothing beside it.
//
// APPLY BINDS IMMEDIATELY. Agent.SetLimits writes the row and, when it is the row
// in force, pushes it into every live job object in the same call. Measured on
// spring, a CPU cap changed on a running job binds inside 200 ms. A settings
// window whose changes took effect at the next job would be a window that lies.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace IdleGpu
{
    public class SettingsForm : Form
    {
        readonly Agent _agent;
        readonly Dictionary<MachineState, Row> _rows = new Dictionary<MachineState, Row>();
        readonly Label _now;
        readonly Timer _tick;

        class Row
        {
            public CheckBox Gpu;
            public NumericUpDown Cpu;
            public ComboBox Priority;
            public NumericUpDown WorkingSet;
            public NumericUpDown MinFree;
            public Label Name;
        }

        public SettingsForm(Agent agent)
        {
            _agent = agent;

            Text = "idlegpu: what this machine gives up";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(760, 430);

            var intro = new Label();
            intro.Text =
                "The machine is in one of these states. For each one, say how much of it this "
                + "runner may take.\r\n"
                + "CPU per cent is a share of the WHOLE machine, not of one core, and it is a "
                + "cap on how much rather than on which cores.";
            intro.SetBounds(12, 10, 736, 34);
            Controls.Add(intro);

            _now = new Label();
            _now.SetBounds(12, 48, 736, 18);
            _now.ForeColor = Color.FromArgb(0, 90, 40);
            Controls.Add(_now);

            var grid = new TableLayoutPanel();
            grid.SetBounds(12, 74, 736, 250);
            grid.ColumnCount = 6;
            grid.RowCount = Config.AllStates().Length + 1;
            grid.AutoSize = false;
            int[] widths = new int[] { 190, 60, 110, 130, 120, 120 };
            foreach (int w in widths)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            Controls.Add(grid);

            grid.Controls.Add(Head("State"), 0, 0);
            grid.Controls.Add(Head("GPU"), 1, 0);
            grid.Controls.Add(Head("CPU cap %"), 2, 0);
            grid.Controls.Add(Head("Priority"), 3, 0);
            grid.Controls.Add(Head("RAM cap MiB"), 4, 0);
            grid.Controls.Add(Head("Free MiB to start"), 5, 0);

            int r = 1;
            foreach (MachineState st in Config.AllStates())
            {
                ResourceLimits cur = _agent.Config.LimitsFor(st);
                var row = new Row();

                row.Name = new Label();
                row.Name.Text = Config.StateLabel(st);
                row.Name.AutoSize = false;
                row.Name.Size = new Size(186, 24);
                row.Name.TextAlign = ContentAlignment.MiddleLeft;
                grid.Controls.Add(row.Name, 0, r);

                row.Gpu = new CheckBox();
                row.Gpu.Checked = cur.Gpu;
                row.Gpu.Size = new Size(40, 24);
                grid.Controls.Add(row.Gpu, 1, r);

                // 0 is not "no cap", it is "do not run at all", because
                // SetInformationJobObject refuses a CpuRate of zero. The tooltip
                // says so rather than leaving somebody to discover it.
                row.Cpu = Spin(0, 100, cur.CpuPct);
                grid.Controls.Add(row.Cpu, 2, r);

                row.Priority = new ComboBox();
                row.Priority.DropDownStyle = ComboBoxStyle.DropDownList;
                row.Priority.Items.AddRange(new object[] { "normal", "belownormal", "idle" });
                row.Priority.SelectedItem = Config.NormalisePriority(cur.Priority);
                row.Priority.Size = new Size(120, 24);
                grid.Controls.Add(row.Priority, 3, r);

                row.WorkingSet = Spin(0, 65536, cur.WorkingSetMib);
                row.WorkingSet.Increment = 512;
                grid.Controls.Add(row.WorkingSet, 4, r);

                row.MinFree = Spin(0, 65536, cur.MinFreeMib);
                row.MinFree.Increment = 512;
                grid.Controls.Add(row.MinFree, 5, r);

                _rows[st] = row;
                r++;
            }

            var help = new Label();
            help.Text =
                "CPU cap 0 means do not run in that state at all; Windows has no zero cap, so zero has to mean stop.\r\n"
                + "RAM cap 0 means do not limit the job's working set. A working set cap makes the JOB page rather "
                + "than the machine.\r\n"
                + "Free MiB to start is an admission check: a job is not STARTED when free memory is already below it.";
            help.SetBounds(12, 330, 736, 52);
            help.ForeColor = SystemColors.GrayText;
            Controls.Add(help);

            var defaults = new Button();
            defaults.Text = "Restore defaults";
            defaults.SetBounds(12, 390, 140, 28);
            defaults.Click += delegate { LoadFrom(Config.DefaultLimits()); };
            Controls.Add(defaults);

            var apply = new Button();
            apply.Text = "Apply";
            apply.SetBounds(560, 390, 90, 28);
            apply.Click += delegate { Apply(); };
            Controls.Add(apply);

            var close = new Button();
            close.Text = "Close";
            close.SetBounds(658, 390, 90, 28);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            CancelButton = close;

            _tick = new Timer();
            _tick.Interval = 1000;
            _tick.Tick += delegate { RefreshNow(); };
            _tick.Start();
            RefreshNow();
        }

        static Label Head(string s)
        {
            var l = new Label();
            l.Text = s;
            l.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            l.AutoSize = false;
            l.Size = new Size(120, 22);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        static NumericUpDown Spin(int lo, int hi, int val)
        {
            var n = new NumericUpDown();
            n.Minimum = lo; n.Maximum = hi;
            n.Value = val < lo ? lo : (val > hi ? hi : val);
            n.Size = new Size(100, 24);
            return n;
        }

        /// The row in force, updated once a second, so that changing a number and
        /// watching the effect is one window rather than two.
        void RefreshNow()
        {
            try
            {
                MachineState st = _agent.Policy.MachineState;
                ResourceLimits l = _agent.Policy.Limits;
                string ram = "";
                Snapshot s = _agent.Current;
                if (s != null && s.Memory != null && s.Memory.Valid)
                    ram = string.Format(CultureInfo.InvariantCulture,
                        ", {0:N0} MiB free of {1:N0}", s.Memory.AvailableMib, s.Memory.TotalMib);
                string cpu = "";
                if (s != null && s.Cpu != null && s.Cpu.Valid)
                    cpu = string.Format(CultureInfo.InvariantCulture,
                        ", CPU {0:N0}% of which {1:N0}% is ours", s.Cpu.MachinePct, s.Cpu.OwnPct);
                _now.Text = "Right now: " + Config.StateLabel(st) + " - " + l.Describe() + cpu + ram;
                foreach (KeyValuePair<MachineState, Row> kv in _rows)
                    kv.Value.Name.Font = new Font(SystemFonts.MessageBoxFont,
                        kv.Key == st ? FontStyle.Bold : FontStyle.Regular);
            }
            catch (Exception) { }
        }

        void LoadFrom(Dictionary<MachineState, ResourceLimits> src)
        {
            foreach (KeyValuePair<MachineState, Row> kv in _rows)
            {
                ResourceLimits l;
                if (!src.TryGetValue(kv.Key, out l) || l == null) continue;
                kv.Value.Gpu.Checked = l.Gpu;
                kv.Value.Cpu.Value = l.CpuPct;
                kv.Value.Priority.SelectedItem = Config.NormalisePriority(l.Priority);
                kv.Value.WorkingSet.Value = Math.Min(l.WorkingSetMib, (int)kv.Value.WorkingSet.Maximum);
                kv.Value.MinFree.Value = Math.Min(l.MinFreeMib, (int)kv.Value.MinFree.Maximum);
            }
        }

        void Apply()
        {
            foreach (KeyValuePair<MachineState, Row> kv in _rows)
            {
                var l = new ResourceLimits();
                l.Gpu = kv.Value.Gpu.Checked;
                l.CpuPct = (int)kv.Value.Cpu.Value;
                l.Priority = Config.NormalisePriority((string)kv.Value.Priority.SelectedItem);
                l.WorkingSetMib = (int)kv.Value.WorkingSet.Value;
                l.MinFreeMib = (int)kv.Value.MinFree.Value;
                _agent.SetLimits(kv.Key, l);
            }
            _agent.SaveLimits();
            RefreshNow();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _tick.Stop(); _tick.Dispose(); } catch (Exception) { }
            base.OnFormClosed(e);
        }
    }
}
