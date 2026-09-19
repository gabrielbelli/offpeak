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
using System.Text;
using System.Windows.Forms;

namespace OffPeak
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
            public CheckBox Admit;
            public Label Name;
        }

        readonly Dictionary<string, RadioButton> _postures = new Dictionary<string, RadioButton>();
        readonly Label _postureBlurb;
        readonly Label _ramNote;
        readonly Label _postureHead;
        readonly Button _saveAs;
        bool _loading;

        public SettingsForm(Agent agent)
        {
            _agent = agent;

            Text = "offpeak: what this machine gives up";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(790, 520);

            // POSTURE FIRST, BECAUSE MOST PEOPLE WILL NEVER READ THE GRID. Twenty
            // five numbers is a form, and nobody fills in a form to lend somebody
            // a computer. Three named answers sit above it, and the grid below is
            // for the person who does want to price each cell.
            _postureHead = new Label();
            _postureHead.Text = "Posture:";
            _postureHead.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            _postureHead.SetBounds(12, 12, 380, 18);
            Controls.Add(_postureHead);

            int px = 12;
            foreach (string id in Config.ProfileIds())
            {
                string which = id;
                var rb = new RadioButton();
                rb.Text = Config.ProfileLabel(id);
                rb.AutoSize = true;
                rb.Location = new Point(px, 32);
                rb.CheckedChanged += delegate
                {
                    if (_loading || !rb.Checked) return;
                    _agent.SetProfile(which);
                    LoadFrom(_agent.Config.Limits);
                    RefreshNow();
                };
                Controls.Add(rb);
                _postures[id] = rb;
                px += Math.Max(150, TextRenderer.MeasureText(rb.Text, rb.Font).Width + 40);
            }

            _postureBlurb = new Label();
            _postureBlurb.SetBounds(12, 56, 760, 18);
            _postureBlurb.ForeColor = SystemColors.GrayText;
            Controls.Add(_postureBlurb);

            var intro = new Label();
            intro.Text =
                "The machine is in one of these states. For each one, say how much of it this "
                + "runner may take.\r\n"
                + "CPU per cent is a share of the WHOLE machine, not of one core, and it is a "
                + "cap on how much rather than on which cores.";
            intro.SetBounds(12, 80, 766, 34);
            Controls.Add(intro);

            _now = new Label();
            _now.SetBounds(12, 118, 766, 18);
            _now.ForeColor = Color.FromArgb(0, 90, 40);
            Controls.Add(_now);

            var grid = new TableLayoutPanel();
            grid.SetBounds(12, 144, 766, 250);
            grid.ColumnCount = 7;
            grid.RowCount = Config.AllStates().Length + 1;
            grid.AutoSize = false;
            int[] widths = new int[] { 170, 50, 100, 116, 116, 130, 70 };
            foreach (int w in widths)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            Controls.Add(grid);

            grid.Controls.Add(Head("State"), 0, 0);
            grid.Controls.Add(Head("GPU"), 1, 0);
            grid.Controls.Add(Head("CPU cap %"), 2, 0);
            grid.Controls.Add(Head("Priority"), 3, 0);
            grid.Controls.Add(Head("RAM cap MiB"), 4, 0);
            grid.Controls.Add(Head("Free MiB to start"), 5, 0);
            grid.Controls.Add(Head("New jobs"), 6, 0);

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

                // KEEPING A WARM JOB IS NOT THE SAME AS INVITING A NEW ONE, and
                // this column is the only place that difference is expressible. A
                // CPU yield throttles rather than kills, so a job that has already
                // paid its model load is worth holding at a trickle while the
                // runner takes nothing new.
                row.Admit = new CheckBox();
                row.Admit.Checked = cur.Admit;
                row.Admit.Size = new Size(40, 24);
                grid.Controls.Add(row.Admit, 6, r);

                _rows[st] = row;
                r++;
            }

            var help = new Label();
            help.Text =
                "CPU cap 0 means do not run in that state at all; Windows has no zero cap, so zero has to mean stop.\r\n"
                + "RAM cap 0 means do not limit the job's working set. A working set cap makes the JOB page rather "
                + "than the machine.\r\n"
                + "Free MiB to start is an admission check: a job is not STARTED when free memory is already below it.";
            help.Text +=
                "\r\nNew jobs is admission: unticked keeps a job that is already running and starts no more.";
            help.SetBounds(12, 400, 766, 66);
            help.ForeColor = SystemColors.GrayText;
            Controls.Add(help);

            // THE ONE THING THE NUMBERS CANNOT SAY: whether the RAM ceiling is
            // actually in force. It needs SeIncreaseBasePriorityPrivilege, which
            // SYSTEM has and an ordinary account does not, and the kernel refuses
            // it with 1314 rather than failing loudly. A number shown while the
            // kernel has refused it is the worst kind of wrong, so the whole column
            // is greyed rather than footnoted.
            _ramNote = new Label();
            _ramNote.SetBounds(12, 466, 766, 18);
            _ramNote.ForeColor = Color.FromArgb(150, 60, 0);
            _ramNote.Visible = false;
            Controls.Add(_ramNote);

            var defaults = new Button();
            defaults.Text = "Restore defaults";
            defaults.SetBounds(12, 482, 140, 28);
            defaults.Click += delegate { LoadFrom(Config.ProfileLimits(Config.ProfileBalanced)); };
            Controls.Add(defaults);

            _saveAs = new Button();
            _saveAs.Text = "Save as...";
            _saveAs.SetBounds(160, 482, 110, 28);
            _saveAs.Click += delegate { SaveAs(); };
            Controls.Add(_saveAs);

            var apply = new Button();
            apply.Text = "Apply";
            apply.SetBounds(590, 482, 90, 28);
            apply.Click += delegate { Apply(); };
            Controls.Add(apply);

            var close = new Button();
            close.Text = "Close";
            close.SetBounds(688, 482, 90, 28);
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

                string eff = _agent.Config.EffectiveProfile();
                _postureHead.Text = "Posture: " + _agent.Config.EffectiveProfileLabel();
                _postureBlurb.Text = eff == "custom"
                    ? "Edited away from a named posture. Save as... puts it in the tray under a name of your own."
                    : "\"" + Config.ProfileBlurb(eff) + ".\"";
                _loading = true;
                foreach (KeyValuePair<string, RadioButton> kv in _postures)
                    kv.Value.Checked = kv.Key == eff;
                _loading = false;
                _saveAs.Enabled = eff == "custom";

                // Greyed, not footnoted. A RAM figure the kernel has refused is a
                // lie whichever way it is dressed up.
                bool denied = false;
                foreach (ServiceDef d in _agent.Services)
                {
                    JobRunner j = _agent.Runner(d.Id);
                    if (j != null && j.WorkingSetDenied) { denied = true; break; }
                }
                _ramNote.Visible = denied;
                if (denied)
                    _ramNote.Text = "RAM cap unavailable: this account does not hold "
                        + "SeIncreaseBasePriorityPrivilege, so the column below is not in force. "
                        + "The CPU cap and the priority are.";
                foreach (KeyValuePair<MachineState, Row> kv in _rows)
                    kv.Value.WorkingSet.Enabled = !denied;
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
                kv.Value.Admit.Checked = l.Admit;
            }
        }

        /// Save the grid as it stands under a name, so it comes back in the tray.
        ///
        /// No dialog designer and no InputBox: a small modal built the same way as
        /// everything else here, because the deliverable stays one binary with
        /// nothing beside it.
        void SaveAs()
        {
            Apply();
            using (var f = new Form())
            {
                f.Text = "Save this posture";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.Font = SystemFonts.MessageBoxFont;
                f.ClientSize = new Size(360, 110);
                var lab = new Label();
                lab.Text = "Name it. It will appear in the tray beside the three built in ones.";
                lab.SetBounds(12, 12, 336, 32);
                f.Controls.Add(lab);
                var box = new TextBox();
                box.SetBounds(12, 48, 336, 24);
                f.Controls.Add(box);
                var ok = new Button();
                ok.Text = "Save"; ok.SetBounds(178, 78, 80, 26);
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok); f.AcceptButton = ok;
                var cancel = new Button();
                cancel.Text = "Cancel"; cancel.SetBounds(266, 78, 80, 26);
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel); f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string name = box.Text.Trim();
                if (name.Length == 0) return;
                // The id is the name with everything a config key cannot hold
                // taken out of it, so the file stays readable and the parser stays
                // one shape.
                var id = new StringBuilder();
                foreach (char ch in name.ToLowerInvariant())
                    if (char.IsLetterOrDigit(ch)) id.Append(ch);
                if (id.Length == 0) return;
                _agent.SaveProfileAs(id.ToString(), name);
                RefreshNow();
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
                l.Admit = kv.Value.Admit.Checked;
                _agent.SetLimits(kv.Key, l);
            }
            _agent.SaveLimits();
            // The grid may have been tightened on the way in, because the cooldown
            // ratchet assumes the ladder is monotone. Read it back rather than
            // leaving the spinners showing numbers that are not in force.
            LoadFrom(_agent.Config.Limits);
            RefreshNow();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _tick.Stop(); _tick.Dispose(); } catch (Exception) { }
            base.OnFormClosed(e);
        }
    }
}
