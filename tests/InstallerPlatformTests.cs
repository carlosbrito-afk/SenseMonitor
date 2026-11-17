using System;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The machine side of the installer, exercised in temporary folders only: the Start Menu shortcuts (real ShellLink
    // objects, a temp "Programs" folder) and the undo actions of an install.
    public static class InstallerPlatformTests
    {
        private static string Temp()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-inst-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static void Gone(string d)
        {
            try { Directory.Delete(d, true); }
            catch (Exception) { }
        }

        [Test]
        public static void Shortcuts_AreCreated_WithTheRightTargetAndArguments_AndRemovedAgain()
        {
            string programs = Temp();
            try
            {
                string exe = Path.Combine(programs, "fake", "SenseMonitor.exe");
                string err = StartMenu.Create(programs, exe, Path.GetDirectoryName(exe));
                Assert.Null(err);
                string folder = Path.Combine(programs, "SenseMonitor");
                string t, a;
                Assert.True(StartMenu.ReadLink(Path.Combine(folder, "SenseMonitor.lnk"), out t, out a), "opens");
                Assert.Equal(exe, t);
                Assert.Equal("", a);
                Assert.True(StartMenu.ReadLink(Path.Combine(folder, "Desinstalar SenseMonitor.lnk"), out t, out a), "opens");
                Assert.Equal(exe, t);
                Assert.Equal("--desinstalar", a);
                // creating again replaces (a reinstall)
                Assert.Null(StartMenu.Create(programs, exe, Path.GetDirectoryName(exe)));
                Assert.Equal(2, Directory.GetFiles(folder).Length);
                // removing takes the two files and the empty folder
                Assert.Null(StartMenu.Remove(programs));
                Assert.True(!Directory.Exists(folder), "folder gone");
                Assert.Null(StartMenu.Remove(programs)); // nothing there: fine
            }
            finally
            {
                Gone(programs);
            }
        }

        [Test]
        public static void RemovingShortcuts_LeavesAFolderThatHoldsSomethingElse()
        {
            string programs = Temp();
            try
            {
                string exe = Path.Combine(programs, "SenseMonitor.exe");
                Assert.Null(StartMenu.Create(programs, exe, programs));
                string folder = Path.Combine(programs, "SenseMonitor");
                File.WriteAllText(Path.Combine(folder, "notas.txt"), "x");
                Assert.Null(StartMenu.Remove(programs));
                Assert.True(File.Exists(Path.Combine(folder, "notas.txt")), "the user's file stays");
                Assert.True(!File.Exists(Path.Combine(folder, "SenseMonitor.lnk")), "ours are gone");
            }
            finally
            {
                Gone(programs);
            }
        }

        [Test]
        public static void ShortcutFolder_ThatIsALink_IsRefused_AndItsTargetUntouched()
        {
            string programs = Temp();
            string elsewhere = Temp();
            try
            {
                string victim = Path.Combine(elsewhere, "SenseMonitor.lnk");
                File.WriteAllText(victim, "not ours to touch");
                string folder = Path.Combine(programs, "SenseMonitor");
                int rc = Cmd("mklink /J \"" + folder + "\" \"" + elsewhere + "\"");
                if (rc != 0) return; // junctions not allowed here: nothing to prove
                Assert.True(StartMenu.Create(programs, Path.Combine(programs, "SenseMonitor.exe"), programs) != null, "refused");
                Assert.True(StartMenu.Remove(programs) != null, "left alone");
                Assert.Equal("not ours to touch", File.ReadAllText(victim));
            }
            finally
            {
                try { Directory.Delete(Path.Combine(programs, "SenseMonitor"), false); }
                catch (Exception) { }
                Gone(programs);
                Gone(elsewhere);
            }
        }

        private static int Cmd(string line)
        {
            int code;
            ToolRunner.Run(Environment.SystemDirectory + "\\cmd.exe", "/d /c " + line, 15000, out code);
            return code;
        }

        // ---- undo actions ----

        [Test]
        public static void RemoveCopiedExe_TakesTheExeAndTheTemporaryCopy_NothingElse()
        {
            string d = Temp();
            try
            {
                string exe = Path.Combine(d, "SenseMonitor.exe");
                string tmp = Path.Combine(d, "SenseMonitor.instalando.tmp");
                string other = Path.Combine(d, "outro.txt");
                File.WriteAllText(exe, "x"); File.WriteAllText(tmp, "x"); File.WriteAllText(other, "x");
                Assert.Null(InstallSystem.RemoveCopiedExe(exe, tmp));
                Assert.True(!File.Exists(exe) && !File.Exists(tmp), "both gone");
                Assert.True(File.Exists(other), "the rest stays");
                Assert.Null(InstallSystem.RemoveCopiedExe(exe, tmp)); // idempotent
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void RemoveConfigFile_TakesOnlyTheExesConfig()
        {
            string d = Temp();
            try
            {
                string exe = Path.Combine(d, "SenseMonitor.exe");
                File.WriteAllText(exe, "x");
                File.WriteAllText(exe + ".config", "x");
                File.WriteAllText(Path.Combine(d, "other.config"), "x");
                Assert.Null(InstallSystem.RemoveConfigFile(exe));
                Assert.True(!File.Exists(exe + ".config"), "config gone");
                Assert.True(File.Exists(exe) && File.Exists(Path.Combine(d, "other.config")), "nothing else");
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void RestorePreviousExe_PutsTheOldOneBackOverTheNewOne()
        {
            string d = Temp();
            try
            {
                string exe = Path.Combine(d, "SenseMonitor.exe");
                string prev = Path.Combine(d, "SenseMonitor.anterior.exe");
                File.WriteAllText(exe, "new");
                File.WriteAllText(prev, "old");
                Assert.Null(InstallSystem.RestorePreviousExe(exe, prev));
                Assert.Equal("old", File.ReadAllText(exe));
                Assert.True(!File.Exists(prev), "the backup was consumed");
                Assert.True(InstallSystem.RestorePreviousExe(exe, prev) != null, "no backup: says so, touches nothing");
                Assert.Equal("old", File.ReadAllText(exe));
                File.Delete(exe);
                File.WriteAllText(prev, "older");
                Assert.Null(InstallSystem.RestorePreviousExe(exe, prev));
                Assert.Equal("older", File.ReadAllText(exe));
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void RemoveFreshInstallFolder_OnlyTheExactInstallFolder_WithEverythingInIt()
        {
            string pf = Temp();
            try
            {
                string dir = Path.Combine(pf, "SenseMonitor");
                Directory.CreateDirectory(Path.Combine(dir, "tmp"));
                File.WriteAllText(Path.Combine(dir, "tmp", "task.xml"), "x");
                File.WriteAllText(Path.Combine(dir, "SenseMonitor.exe"), "x");
                string sibling = Path.Combine(pf, "Outro");
                Directory.CreateDirectory(sibling);
                Assert.True(InstallSystem.RemoveFreshInstallFolder(sibling, pf) != null, "a sibling is refused");
                Assert.True(InstallSystem.RemoveFreshInstallFolder(pf, pf) != null, "the parent is refused");
                Assert.True(Directory.Exists(sibling) && Directory.Exists(dir), "refusals touch nothing");
                Assert.Null(InstallSystem.RemoveFreshInstallFolder(dir, pf));
                Assert.True(!Directory.Exists(dir), "gone with its content");
                Assert.True(Directory.Exists(sibling), "sibling intact");
                Assert.Null(InstallSystem.RemoveFreshInstallFolder(dir, pf)); // already gone
            }
            finally
            {
                Gone(pf);
            }
        }

        [Test]
        public static void RemoveFreshInstallFolder_ALinkInside_IsRemovedAsALink_TheTargetStays()
        {
            string pf = Temp();
            string target = Temp();
            try
            {
                string dir = Path.Combine(pf, "SenseMonitor");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
                if (Cmd("mklink /J \"" + Path.Combine(dir, "atualizacao") + "\" \"" + target + "\"") != 0) return;
                Assert.Null(InstallSystem.RemoveFreshInstallFolder(dir, pf));
                Assert.True(!Directory.Exists(dir), "folder gone");
                Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
            }
            finally
            {
                Gone(pf);
                Gone(target);
            }
        }

        [Test]
        public static void EveryRollbackStep_HasAText()
        {
            foreach (RollbackStep s in (RollbackStep[])Enum.GetValues(typeof(RollbackStep)))
            {
                Assert.True(InstallerText.Line(s) != "Desfazendo", "text for " + s);
            }
        }
    }
}
