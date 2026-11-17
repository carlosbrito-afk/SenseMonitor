using System;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The suite must never touch the user's real files (%APPDATA%\SenseMonitor\diario.log, config.json, the state folder ...).
    // TestRunner points UserData at a throw-away folder before the first test and fails any test that still resolves a real
    // folder, naming it. These tests pin that guard down.
    public static class SandboxTests
    {
        private static bool Under(string path, string root)
        {
            string full = Path.GetFullPath(path);
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full.TrimEnd('\\'), r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void Inside(string what, string path)
        {
            Assert.True(!string.IsNullOrEmpty(TestRunner.Sandbox), "the runner created a sandbox");
            Assert.True(Under(path, TestRunner.Sandbox), what + " must live in the test sandbox, but is " + path);
            Assert.True(!Under(path, UserData.RealRoaming) && !Under(path, UserData.RealLocal) && !Under(path, UserData.RealCommon),
                what + " points at a real user folder: " + path);
        }

        [Test]
        public static void EveryUserDataPath_LivesInTheSandbox()
        {
            Inside("Config.DefaultPath", Config.DefaultPath);
            Inside("FileJournal.DefaultPath", FileJournal.DefaultPath);
            Inside("Safety.Dir", Safety.Dir);
            Inside("Safety.Journal", ((FileJournal)Safety.Journal).FilePath);
            Inside("ChangeLogStore.DefaultPath", ChangeLogStore.DefaultPath);
            Inside("PerfJournalFile.DefaultPath", PerfJournalFile.DefaultPath());
            Inside("PerfScoreboard.DefaultPath", PerfScoreboard.DefaultPath);
            Inside("TempLogStore.DefaultDirectory", TempLogStore.DefaultDirectory());
            Inside("StateFolder.DefaultPath", StateFolder.DefaultPath);
            Inside("UndoStore.DefaultDir", UndoStore.DefaultDir);
            Inside("UpdateStore.DataDir", UpdateStore.DataDir);
            Inside("AlertStore.SettingsPath", AlertStore.SettingsPath);
            Inside("AlertStore.StatePath", AlertStore.StatePath);
            Inside("OverlayStore.GesturePath", OverlayStore.GesturePath);
            Inside("OverlayStore.WidgetPath", OverlayStore.WidgetPath);
            Inside("Safety.StateDir", Safety.StateDir);
        }

        [Test]
        public static void TheJournalOfTheSuite_WritesIntoTheSandbox_NotIntoTheRealFile()
        {
            string line = "teste de isolamento " + Guid.NewGuid().ToString("N");
            Safety.Journal.Write(JournalKind.Action, line);
            string real = Path.Combine(UserData.RealRoaming, "diario.log");
            string mine = Path.Combine(TestRunner.Sandbox, "diario.log");
            Assert.True(File.Exists(mine) && File.ReadAllText(mine).Contains(line), "the line is in the sandbox journal");
            bool leaked = false;
            try { leaked = File.Exists(real) && File.ReadAllText(real).Contains(line); }
            catch (IOException) { }
            Assert.True(!leaked, "the line must not be in the real journal");
        }

        [Test]
        public static void ReenablingServices_WithoutAJournal_UsesTheSandboxJournal()
        {
            // the path that wrote "serviço ASMSvc: restaurado para Automatic e iniciado" into the real diario.log
            Assert.True(Under(((FileJournal)Safety.Journal).FilePath, TestRunner.Sandbox), "the fallback journal of ReenableAcerServices is the sandbox one");
        }

        [Test]
        public static void NoTestHasResolvedARealFolderSoFar()
        {
            Assert.Equal(0, UserData.ViolationCount);
        }

        [Test]
        public static void TheGuard_RecordsARealFolderUse_ByTheTestHost()
        {
            Assert.True(UserData.IsTestHost, "this process is the test host");
            string saved = UserData.Override;
            int mark = UserData.ViolationCount;
            string p = null;
            try
            {
                UserData.Override = null;
                p = Config.DefaultPath; // resolves the real folder
            }
            finally
            {
                UserData.Override = saved;
            }
            bool recorded = UserData.ViolationCount == mark + 1;
            string text = recorded ? UserData.Violations[mark] : "";
            UserData.ForgetViolationsFrom(mark); // provoked on purpose: the runner must not fail this test for it
            Assert.True(recorded, "the real use was recorded");
            Assert.True(text.Contains("Roaming"), text);
            Assert.True(text.Contains("TheGuard_RecordsARealFolderUse_ByTheTestHost"), "it names the test: " + text);
            Assert.Equal(Path.Combine(UserData.RealRoaming, "config.json"), p);
        }

        [Test]
        public static void SandboxOverride_RedirectsTheThreeFolders()
        {
            Assert.Equal(TestRunner.Sandbox, UserData.Roaming);
            Assert.Equal(Path.Combine(TestRunner.Sandbox, "local"), UserData.Local);
            Assert.Equal(Path.Combine(TestRunner.Sandbox, "common"), UserData.Common);
        }
    }
}
