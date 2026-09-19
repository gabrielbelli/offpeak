// Two services, one install directory, and the ways that can go wrong.
//
// WHY THIS IS A SEPARATE BINARY FROM Tests.cs. That file was carrying an
// unrelated uncommitted change while this work was done, and a merge conflict in
// a test file is the cheapest way to lose a test nobody notices is gone. The two
// suites link the same sources and run one after the other; run-tests.ps1 sums
// the exit codes.
//
// WHAT IS BEING PROTECTED. A second speech engine costs 3.8 GB of weights instead
// of another 8.8 GB tree only because it shares the first one's virtual
// environment, its torch and its CPython. Sharing is worth that, and it makes two
// previously harmless things dangerous:
//
//   `service remove` deletes InstallDir recursively. On either of a sharing pair
//     that takes the OTHER service's weights, its venv and its python.exe, and
//     leaves it Enabled = true pointing at an interpreter that is gone.
//   InstallDir could not be written in the shipped example at all until now,
//     because only Command, Arguments and WorkingDir expanded %RUNTIME%. So
//     sharing a tree meant an absolute machine-specific path in a file that ships
//     to strangers - and worker.ini.example claimed the sharing in a comment
//     while the code provisioned a second complete tree beside it.
//
// Every test is named after the mistake it stops.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OffPeak
{
    static class SharedTreeTests
    {
        static int _failed, _passed;
        static string _fixtureRoot;

        static void Main(string[] args)
        {
            _fixtureRoot = args.Length > 0 ? args[0]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");

            InstallDirExpandsPlaceholdersBeforeDefaulting();
            ReadyMarkerExpandsAndInstallCannotSelfReference();
            AnInstallDirWithNoPlaceholderStillDefaultsAndStillWorks();

            RemoveLeavesTheSharedTreeAndClearsOnlyItsOwnMarker();
            RemoveRefusesToDeleteATreeAnotherServiceShares();
            PurgeWorksOnceEveryShareHasBeenRemoved();
            RemovingAServiceThatSharesNothingStillDeletesItsTree();
            ASharedMarkerIsNeverDeletedByOneOfItsOwners();

            ManifestIdMismatchIsWarnedAtInstall();
            AnOlderControllerWithNoManifestIdIsNotAMismatch();
            AManifestIdNestedInsideSomethingElseIsNotTheServiceId();

            TheShippedTurboSectionSharesTheChatterboxTreeAndKeepsItsOwnMarker();
            TheShippedTurboSectionNeverOutranksTheEngineWithNoFallback();

            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed", _passed, _failed);
            Environment.Exit(_failed == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------ helpers ---

        static void Ok(bool cond, string what)
        {
            if (cond) { _passed++; Console.WriteLine("  pass  " + what); }
            else { _failed++; Console.WriteLine("  FAIL  " + what); }
        }

        static void Case(string name) { Console.WriteLine(); Console.WriteLine(name); }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "offpeak-s-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Nuke(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch (Exception) { }
        }

        static Config LoadIni(string root, params string[] lines)
        {
            string ini = Path.Combine(root, "worker.ini");
            var all = new List<string>();
            all.Add("DataDir = " + root);
            all.AddRange(lines);
            all.Add("");
            File.WriteAllLines(ini, all.ToArray(), new UTF8Encoding(false));
            return Config.Load(ini);
        }

        /// A tree with something in it, so a wrongly deleted one is detectable.
        static void Populate(string dir, string marker)
        {
            Directory.CreateDirectory(Path.Combine(dir, "venv", "Scripts"));
            File.WriteAllText(Path.Combine(dir, "venv", "Scripts", "python.exe"), "not really");
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "models", "weights.safetensors"), new string('w', 512));
            if (marker != null) File.WriteAllText(marker, "installed");
        }

        static string FindExample()
        {
            var tried = new List<string>();
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                tried.Add(Path.Combine(dir, "worker.ini.example"));
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            if (_fixtureRoot != null)
            {
                string up = Path.GetDirectoryName(_fixtureRoot.TrimEnd(Path.DirectorySeparatorChar));
                if (up != null)
                {
                    tried.Add(Path.Combine(up, "worker.ini.example"));
                    string up2 = Path.GetDirectoryName(up.TrimEnd(Path.DirectorySeparatorChar));
                    if (up2 != null) tried.Add(Path.Combine(up2, "worker.ini.example"));
                }
            }
            foreach (string p in tried) if (File.Exists(p)) return p;
            return null;
        }

        // --------------------------------------------------------- expansion ---

        /// THE DEFECT THIS PREVENTS. Only Command, Arguments and WorkingDir went
        /// through Expand, so InstallDir could not use %RUNTIME% at all. A section
        /// that wanted to share another service's tree therefore had to write an
        /// absolute path - this machine's path, in a file that ships to strangers -
        /// and worker.ini.example's chatterbox-cpu section did not, so it claimed a
        /// shared tree in a comment while defaulting to a private one in the code.
        /// The two engines then disagreed by about seven gigabytes.
        static void InstallDirExpandsPlaceholdersBeforeDefaulting()
        {
            Case("InstallDir expands, so two sections can name one tree");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.alpha]",
                    "Command = %INSTALL%\\venv\\python.exe",
                    "InstallDir = %RUNTIME%\\shared",
                    "[service.beta]",
                    "Command = %INSTALL%\\venv\\python.exe",
                    "InstallDir = %RUNTIME%\\shared");

                ServiceDef a = c.Service("alpha"), b = c.Service("beta");
                Ok(a.InstallDir.IndexOf('%') < 0, "no placeholder survives into InstallDir: " + a.InstallDir);
                Ok(a.InstallDir.Equals(Path.Combine(c.RuntimeRoot, "shared"), StringComparison.OrdinalIgnoreCase),
                    "and it lands under RuntimeRoot rather than being taken literally");
                Ok(Install.SamePath(a.InstallDir, b.InstallDir),
                    "so the two sections resolve to the SAME directory, which is the point");
                Ok(Install.SharingInstallDir(a, c.Services).Count == 1,
                    "and each one can see that the other is in it");
                // The whole reason for the ordering: Command's %INSTALL% must be the
                // expanded InstallDir, not the raw string and not the default.
                Ok(a.Command.Equals(Path.Combine(a.InstallDir, "venv", "python.exe"),
                                    StringComparison.OrdinalIgnoreCase),
                    "%INSTALL% in Command resolves to the expanded tree: " + a.Command);
            }
            finally { Nuke(root); }
        }

        /// ReadyMarker is what makes a shared tree separable: distinct markers
        /// inside one directory are how two engines are installed and removed on
        /// their own. It has to expand for that to be writable in a shipped file,
        /// and it has to expand AFTER InstallDir or %INSTALL% means the previous
        /// service's directory.
        static void ReadyMarkerExpandsAndInstallCannotSelfReference()
        {
            Case("ReadyMarker expands against this service's own tree");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.alpha]",
                    "Command = a.exe",
                    "InstallDir = %RUNTIME%\\shared",
                    "ReadyMarker = %INSTALL%\\.installed",
                    "[service.beta]",
                    "Command = b.exe",
                    "InstallDir = %RUNTIME%\\shared",
                    "ReadyMarker = %INSTALL%\\.installed-turbo",
                    // %INSTALL% inside InstallDir is the name of the thing being
                    // computed. It must not silently resolve to the previous
                    // service's directory, which is what a naive ordering does.
                    "[service.gamma]",
                    "Command = g.exe",
                    "InstallDir = %INSTALL%\\nope");

                ServiceDef a = c.Service("alpha"), b = c.Service("beta"), g = c.Service("gamma");
                Ok(a.ReadyMarker.Equals(Path.Combine(a.InstallDir, ".installed"), StringComparison.OrdinalIgnoreCase),
                    "alpha's marker is inside the shared tree: " + a.ReadyMarker);
                Ok(b.ReadyMarker.Equals(Path.Combine(b.InstallDir, ".installed-turbo"), StringComparison.OrdinalIgnoreCase),
                    "beta's is a DIFFERENT file in the SAME tree: " + b.ReadyMarker);
                Ok(!Install.SamePath(a.ReadyMarker, b.ReadyMarker),
                    "so 'installed' stays two separate facts about one directory");

                Ok(g.InstallDir.IndexOf("%INSTALL%", StringComparison.Ordinal) >= 0,
                    "%INSTALL% inside InstallDir is left literal rather than self-resolving");
                Ok(!Install.SamePath(g.InstallDir, a.InstallDir),
                    "and above all it does not silently become the PREVIOUS service's tree, " +
                    "which would provision gamma into alpha's directory with no error anywhere");
            }
            finally { Nuke(root); }
        }

        /// The change must not move anybody who never asked for it.
        static void AnInstallDirWithNoPlaceholderStillDefaultsAndStillWorks()
        {
            Case("a section that says nothing about InstallDir is unaffected");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root, "[service.plain]", "Command = p.exe");
                ServiceDef d = c.Service("plain");
                Ok(d.InstallDir.Equals(Path.Combine(c.RuntimeRoot, "plain"), StringComparison.OrdinalIgnoreCase),
                    "still defaults to RuntimeRoot\\<id>: " + d.InstallDir);
                Ok(d.ReadyMarker.Equals(Path.Combine(d.InstallDir, ".installed"), StringComparison.OrdinalIgnoreCase),
                    "and the marker still defaults inside it");
                Ok(d.WorkingDir == d.InstallDir, "and WorkingDir still follows it");
            }
            finally { Nuke(root); }
        }

        // ------------------------------------------------------------ removal ---

        /// THE DEFECT THIS PREVENTS, and it is the one that costs real data.
        /// Directory.Delete(InstallDir, true) on either of a sharing pair takes the
        /// other one's weights, the shared virtual environment and its python.exe.
        /// The survivor keeps Enabled = true and a Command pointing at an
        /// interpreter that no longer exists, so the agent relaunches it every
        /// 500 ms for ever - loud in logs\ and invisible everywhere else.
        static void RemoveLeavesTheSharedTreeAndClearsOnlyItsOwnMarker()
        {
            Case("removing one of two sharers leaves the tree standing");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.base]",
                    "Command = %INSTALL%\\venv\\Scripts\\python.exe",
                    "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared",
                    "ReadyMarker = %INSTALL%\\.installed",
                    "[service.turbo]",
                    "Command = %INSTALL%\\venv\\Scripts\\python.exe",
                    "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared",
                    "ReadyMarker = %INSTALL%\\.installed-turbo");

                ServiceDef b = c.Service("base"), t = c.Service("turbo");
                Populate(b.InstallDir, b.ReadyMarker);
                File.WriteAllText(t.ReadyMarker, "installed");
                Ok(Install.IsInstalled(b) && Install.IsInstalled(t), "both start installed");

                string err, note;
                bool okRemove = Install.Remove(t, c.Services, false, out err, out note);
                Ok(okRemove, "removing turbo succeeds: " + (err ?? "no error"));
                Ok(!Install.IsInstalled(t), "turbo now reads as not installed");
                Ok(Install.IsInstalled(b), "AND base is still installed");
                Ok(File.Exists(Path.Combine(b.InstallDir, "venv", "Scripts", "python.exe")),
                    "the shared interpreter survived, so base still has something to run");
                Ok(File.Exists(Path.Combine(b.InstallDir, "models", "weights.safetensors")),
                    "and so did base's weights");
                Ok(note != null && note.IndexOf("base", StringComparison.Ordinal) >= 0,
                    "and the person is told WHO the tree was left for");
                Ok(note != null && note.IndexOf("--purge", StringComparison.Ordinal) >= 0,
                    "and how to reclaim the disk when they really mean it");
            }
            finally { Nuke(root); }
        }

        /// SYMMETRIC, or removal order becomes a trap: remove A then B and the
        /// disk comes back, remove B then A and it does not.
        static void RemoveRefusesToDeleteATreeAnotherServiceShares()
        {
            Case("--purge refuses while another service is still installed there");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.base]", "Command = c.exe", "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared", "ReadyMarker = %INSTALL%\\.installed",
                    "[service.turbo]", "Command = c.exe", "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared", "ReadyMarker = %INSTALL%\\.installed-turbo");

                ServiceDef b = c.Service("base"), t = c.Service("turbo");
                Populate(b.InstallDir, b.ReadyMarker);
                File.WriteAllText(t.ReadyMarker, "installed");

                string err, note;
                Ok(!Install.Remove(t, c.Services, true, out err, out note),
                    "purging turbo while base is installed is REFUSED");
                Ok(err != null && err.IndexOf("base", StringComparison.Ordinal) >= 0,
                    "and the refusal names who is still in there: " + err);
                Ok(Directory.Exists(b.InstallDir), "nothing was deleted");
                Ok(Install.IsInstalled(b), "and base is untouched");

                // The other way round, to prove it is not one-directional.
                Ok(!Install.Remove(b, c.Services, true, out err, out note),
                    "and purging base while turbo is installed is refused too");
                Ok(Directory.Exists(t.InstallDir), "still nothing deleted");
            }
            finally { Nuke(root); }
        }

        static void PurgeWorksOnceEveryShareHasBeenRemoved()
        {
            Case("--purge does reclaim the disk when it is finally safe");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.base]", "Command = c.exe", "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared", "ReadyMarker = %INSTALL%\\.installed",
                    "[service.turbo]", "Command = c.exe", "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared", "ReadyMarker = %INSTALL%\\.installed-turbo");

                ServiceDef b = c.Service("base"), t = c.Service("turbo");
                Populate(b.InstallDir, b.ReadyMarker);
                File.WriteAllText(t.ReadyMarker, "installed");

                string err, note;
                Install.Remove(t, c.Services, false, out err, out note);
                Install.Remove(b, c.Services, false, out err, out note);
                Ok(!Install.IsInstalled(b) && !Install.IsInstalled(t), "neither is installed now");
                Ok(Directory.Exists(b.InstallDir), "but the several gigabytes are still on the disk");

                Ok(Install.Remove(b, c.Services, true, out err, out note),
                    "so --purge is now allowed: " + (err ?? "no error"));
                Ok(!Directory.Exists(b.InstallDir), "and the tree is finally gone");
            }
            finally { Nuke(root); }
        }

        /// The ordinary case must not have changed at all.
        static void RemovingAServiceThatSharesNothingStillDeletesItsTree()
        {
            Case("a service with a tree of its own is removed exactly as before");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.alone]", "Command = c.exe", "Provision = p.ps1",
                    "[service.other]", "Command = o.exe", "Provision = p.ps1");
                ServiceDef a = c.Service("alone");
                Populate(a.InstallDir, a.ReadyMarker);

                string err, note;
                Ok(Install.Remove(a, c.Services, false, out err, out note),
                    "removed without --purge: " + (err ?? "no error"));
                Ok(!Directory.Exists(a.InstallDir), "and the whole tree went, as it always did");
                Ok(note == null, "with no shared-tree note, because there is nothing shared");

                // And the one-argument overload, which older callers still use.
                ServiceDef o = c.Service("other");
                Populate(o.InstallDir, o.ReadyMarker);
                string err2;
                Ok(Install.Remove(o, out err2), "the old two-argument Remove still works");
                Ok(!Directory.Exists(o.InstallDir), "and still deletes the tree");
            }
            finally { Nuke(root); }
        }

        /// chatterbox and chatterbox-cpu are the SAME download - same weights, same
        /// venv, same marker - because one is the other one run on the processor.
        /// Deleting that marker for either would silently uninstall both.
        static void ASharedMarkerIsNeverDeletedByOneOfItsOwners()
        {
            Case("two services that share a marker share an install");
            string root = TempDir();
            try
            {
                Config c = LoadIni(root,
                    "[service.gpu]", "Command = c.exe", "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared", "ReadyMarker = %INSTALL%\\.installed",
                    "[service.cpu]", "Command = c.exe", "Provision = p.ps1",
                    "InstallDir = %RUNTIME%\\shared", "ReadyMarker = %INSTALL%\\.installed");

                ServiceDef g = c.Service("gpu"), p = c.Service("cpu");
                Populate(g.InstallDir, g.ReadyMarker);
                Ok(Install.IsInstalled(g) && Install.IsInstalled(p),
                    "installing once installed both, which is the truth about them");

                string err, note;
                Install.Remove(p, c.Services, false, out err, out note);
                Ok(Install.IsInstalled(g),
                    "removing one did NOT uninstall the other by deleting the shared marker");
                Ok(note != null && note.IndexOf("same download", StringComparison.Ordinal) >= 0,
                    "and the person is told why nothing was deleted: " + note);
            }
            finally { Nuke(root); }
        }

        // ----------------------------------------------------------- manifest ---

        /// THE SINGLE MOST LIKELY COPY-PASTE IN A TWO-ENGINE SERVICE, and the only
        /// one that produces the wrong audio with no error anywhere. Nothing
        /// reconciles the [service.<id>] header with the id inside the manifest the
        /// controller publishes, because the agent splices that document in
        /// verbatim on purpose.
        static void ManifestIdMismatchIsWarnedAtInstall()
        {
            Case("a manifest that calls itself something else is reported");
            var d = new ServiceDef();
            d.Id = "chatterbox-turbo";

            string copied = "{\"id\":\"chatterbox\",\"description\":\"speech\",\"unit_seconds\":8}";
            string warn = Install.ManifestIdMismatch(d, copied);
            Ok(warn != null, "the mismatch is caught");
            Ok(warn != null && warn.IndexOf("chatterbox-turbo", StringComparison.Ordinal) >= 0 &&
               warn.IndexOf("'chatterbox'", StringComparison.Ordinal) >= 0,
                "and BOTH names are in the message, or nobody can tell which to change");

            string right = "{\"id\":\"chatterbox-turbo\",\"description\":\"turbo\"}";
            Ok(Install.ManifestIdMismatch(d, right) == null, "a matching manifest is silent");
        }

        /// An older controller that publishes no id is not wrong, and reporting it
        /// would train people to ignore this warning.
        static void AnOlderControllerWithNoManifestIdIsNotAMismatch()
        {
            Case("absence is never reported as disagreement");
            var d = new ServiceDef();
            d.Id = "chatterbox-turbo";
            Ok(Install.ManifestIdMismatch(d, null) == null, "no manifest at all is silent");
            Ok(Install.ManifestIdMismatch(d, "{\"description\":\"no id here\"}") == null,
                "a manifest with no id is silent");
            Ok(Install.ManifestIdMismatch(d, "") == null, "an empty manifest is silent");
        }

        /// The id being looked for is the manifest's OWN, not one belonging to
        /// something nested inside it.
        static void AManifestIdNestedInsideSomethingElseIsNotTheServiceId()
        {
            Case("only the manifest's own id counts");
            var d = new ServiceDef();
            d.Id = "chatterbox-turbo";
            string nested = "{\"artefacts\":{\"id\":\"chatterbox\"},\"id\":\"chatterbox-turbo\"}";
            Ok(Install.ManifestIdMismatch(d, nested) == null,
                "an id nested one level down does not raise a false alarm");
        }

        // ------------------------------------------------- the shipped example ---

        /// The example file is what a stranger copies. If the turbo section in it
        /// does not actually share the tree, everything above is theory.
        static void TheShippedTurboSectionSharesTheChatterboxTreeAndKeepsItsOwnMarker()
        {
            Case("the shipped turbo section shares chatterbox's tree");
            string example = FindExample();
            if (example == null) { Ok(true, "worker.ini.example is not beside the tests; skipped"); return; }
            string root = TempDir();
            try
            {
                string ini = Path.Combine(root, "worker.ini");
                var lines = new List<string>(File.ReadAllLines(example));
                lines.Insert(0, "DataDir = " + root);
                File.WriteAllLines(ini, lines.ToArray(), new UTF8Encoding(false));
                Config c = Config.Load(ini);

                ServiceDef baseline = c.Service("chatterbox");
                ServiceDef turbo = c.Service("chatterbox-turbo");
                ServiceDef cpu = c.Service("chatterbox-cpu");
                Ok(turbo != null, "the example ships a [service.chatterbox-turbo] section");
                if (turbo == null) return;

                Ok(!turbo.Enabled, "and it ships OPTED OUT, like everything else here: " +
                    "a machine whose owner never asks for turbo downloads none of it");
                Ok(Install.SamePath(turbo.InstallDir, baseline.InstallDir),
                    "it installs into chatterbox's tree, which is what makes it 3.8 GB and not 8.8");
                Ok(!Install.SamePath(turbo.ReadyMarker, baseline.ReadyMarker),
                    "but keeps its OWN ready marker, so the two are separately installable");
                Ok(turbo.ReadyMarker.EndsWith(".installed-turbo", StringComparison.OrdinalIgnoreCase),
                    "which is .installed-turbo: " + turbo.ReadyMarker);
                Ok(turbo.InstallDir.IndexOf('%') < 0 && turbo.ReadyMarker.IndexOf('%') < 0,
                    "and neither field is left with an unexpanded placeholder in it");

                Ok(cpu != null && Install.SamePath(cpu.InstallDir, baseline.InstallDir),
                    "chatterbox-cpu now really does share the tree its comment always claimed");
                Ok(cpu != null && Install.SamePath(cpu.ReadyMarker, baseline.ReadyMarker),
                    "and shares the marker too, because it is the same download");

                Ok(turbo.Command.StartsWith(turbo.InstallDir, StringComparison.OrdinalIgnoreCase),
                    "turbo runs the interpreter inside the shared tree: " + turbo.Command);
                Ok(turbo.Arguments.IndexOf("--engine turbo", StringComparison.Ordinal) >= 0,
                    "and asks the shared controller for the turbo checkpoint by name");
                Ok(turbo.ProvisionArguments.IndexOf("-Engine turbo", StringComparison.Ordinal) >= 0,
                    "and provisioning is told which weights to fetch");
                Ok(baseline.Arguments.IndexOf("--engine multilingual", StringComparison.Ordinal) >= 0,
                    "while baseline names its own engine rather than relying on a default");
            }
            finally { Nuke(root); }
        }

        /// THE STARVATION THIS PREVENTS. One controller per device group and no
        /// preemption, so the higher priority takes the card and the other waits.
        /// Baseline serves 23 languages and two expression controls that have no
        /// substitute anywhere; turbo's only loss is speed, and it still has a
        /// processor to fall back to. If turbo outranked baseline, a steady turbo
        /// trickle would push every baseline job off the card, and each of those
        /// jobs would report machine_busy - THE SAME STRING A GAME PRODUCES - so
        /// nobody at either end could tell it from a week of gaming.
        static void TheShippedTurboSectionNeverOutranksTheEngineWithNoFallback()
        {
            Case("turbo sits below baseline in the queue");
            string example = FindExample();
            if (example == null) { Ok(true, "worker.ini.example is not beside the tests; skipped"); return; }
            string root = TempDir();
            try
            {
                string ini = Path.Combine(root, "worker.ini");
                var lines = new List<string>(File.ReadAllLines(example));
                lines.Insert(0, "DataDir = " + root);
                File.WriteAllLines(ini, lines.ToArray(), new UTF8Encoding(false));
                Config c = Config.Load(ini);

                ServiceDef baseline = c.Service("chatterbox");
                ServiceDef turbo = c.Service("chatterbox-turbo");
                if (turbo == null) { Ok(true, "no turbo section; skipped"); return; }

                Ok(turbo.Priority < baseline.Priority,
                    "turbo's priority " + turbo.Priority + " is below baseline's " + baseline.Priority);
                Ok(turbo.WantsGpu, "turbo asks for the card, so the comparison is a real one");
                Ok(turbo.YieldGraceSeconds == baseline.YieldGraceSeconds,
                    "and its yield grace matches baseline's: the grace tears down the SAME " +
                    "CUDA context, and a longer one on a faster engine is somebody's game " +
                    "waiting for nothing");
                Ok(turbo.NeedsMemoryMib >= baseline.NeedsMemoryMib,
                    "its memory figure is not sized down from a VRAM reading: this row is " +
                    "SYSTEM memory and decides whether starting the job pages the owner's machine");
            }
            finally { Nuke(root); }
        }
    }
}
