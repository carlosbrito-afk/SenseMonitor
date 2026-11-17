using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class StartupRouterTests
    {
        private const string Inst = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";
        private const string Down = @"C:\Users\x\Downloads\SenseMonitor.exe";

        private static string[] A(params string[] a)
        {
            List<string> l = new List<string>();
            l.Add(Down);
            l.AddRange(a);
            return l.ToArray();
        }

        private static StartupRoute R(string[] args, string running, bool installed)
        {
            return StartupRouter.Decide(args, running, Inst, installed);
        }

        [Test]
        public static void NoArgs_FromDownloads_NothingInstalled_OffersInstall()
        {
            Assert.Equal(StartupAction.OfferInstall, R(A(), Down, false).Action);
            Assert.Equal(StartupAction.OfferInstall, R(null, Down, false).Action);
        }

        [Test]
        public static void NoArgs_OtherCases_RunNormally()
        {
            Assert.Equal(StartupAction.RunApp, R(A(), Inst, true).Action);       // the installed app itself
            Assert.Equal(StartupAction.RunApp, R(A(), Inst, false).Action);      // same path even if (oddly) not flagged installed
            Assert.Equal(StartupAction.OfferHandOff, R(A(), Down, true).Action); // an install exists: hand off to it, no install offer
        }

        [Test]
        public static void ExistingToolSwitches_StayWithTheOldRouting()
        {
            string[] tools = { "--registrar-inicio", "--validar-ventoinha", "--autoteste", "--teste-desempenho", "--auto" };
            foreach (string t in tools)
            {
                Assert.Equal(StartupAction.RunApp, R(A(t), Down, false).Action);
            }
        }

        [Test]
        public static void Install_AndUninstall_AreRecognized_CaseInsensitive()
        {
            Assert.Equal(StartupAction.Install, R(A("--instalar"), Down, false).Action);
            Assert.Equal(StartupAction.Install, R(A("--INSTALAR"), Down, true).Action);
            StartupRoute u = R(A("--desinstalar"), Inst, true);
            Assert.Equal(StartupAction.Uninstall, u.Action);
            Assert.True(!u.KeepData, "data removed by default");
            StartupRoute k = R(A("--desinstalar", "--manter-dados"), Inst, true);
            Assert.Equal(StartupAction.Uninstall, k.Action);
            Assert.True(k.KeepData, "keep data");
            Assert.True(R(A("--manter-dados", "--desinstalar"), Inst, true).KeepData, "order does not matter");
        }

        [Test]
        public static void KeepDataWithoutUninstall_IsRejected()
        {
            Assert.Equal(StartupAction.BadArguments, R(A("--manter-dados"), Inst, true).Action);
            Assert.Equal(StartupAction.BadArguments, R(A("--instalar", "--manter-dados"), Inst, true).Action);
        }

        [Test]
        public static void ConflictingFamilies_AreRejected()
        {
            Assert.Equal(StartupAction.BadArguments, R(A("--instalar", "--desinstalar"), Down, false).Action);
            Assert.Equal(StartupAction.BadArguments, R(A("--instalar", "--aplicar-atualizacao"), Down, false).Action);
        }

        [Test]
        public static void Apply_TakesNoValue_TheHelperReadsItsInstructionsFromAFile()
        {
            Assert.Equal(StartupAction.ApplyUpdate, R(A("--aplicar-atualizacao"), Down, true).Action);
            // a stray value (the old "<pid>" form) is refused rather than silently ignored
            Assert.Equal(StartupAction.BadArguments, R(A("--aplicar-atualizacao", "4242"), Down, true).Action);
            Assert.Equal(StartupAction.BadArguments, R(A("--aplicar-atualizacao", "abc"), Down, true).Action);
        }

        [Test]
        public static void ExecuteHere_NeverOffersTheInstall()
        {
            Assert.Equal(StartupAction.RunApp, R(A("--executar-aqui"), Down, false).Action);
            Assert.Equal(StartupAction.RunApp, R(A("--executar-aqui", "--autoteste"), Down, false).Action);
        }
    }

    public static class InstallPlannerTests
    {
        [Test]
        public static void FreshInstall_Steps()
        {
            List<InstallStep> s = InstallPlanner.ForInstall(false, false);
            Assert.Equal(7, s.Count);
            Assert.Equal(InstallStep.PrepareFolder, s[0]);
            Assert.Equal(InstallStep.CopyExe, s[1]);
            Assert.Equal(InstallStep.CopyConfig, s[2]);
            Assert.Equal(InstallStep.RegisterTasks, s[3]);
            Assert.Equal(InstallStep.CreateShortcuts, s[4]);
            Assert.Equal(InstallStep.WriteUninstallEntry, s[5]);
            Assert.Equal(InstallStep.StartApp, s[6]);
        }

        [Test]
        public static void Reinstall_ClosesTheRunningAppFirst()
        {
            List<InstallStep> s = InstallPlanner.ForInstall(true, false);
            Assert.Equal(InstallStep.CloseRunningApp, s[0]);
            Assert.True(s.IndexOf(InstallStep.CloseRunningApp) < s.IndexOf(InstallStep.CopyExe), "close before copy");
            Assert.True(s.IndexOf(InstallStep.CopyExe) < s.IndexOf(InstallStep.RegisterTasks), "files before tasks");
            Assert.True(s.IndexOf(InstallStep.RegisterTasks) < s.IndexOf(InstallStep.StartApp), "start last");
        }

        [Test]
        public static void RepairFromTheInstalledCopy_SkipsCopy()
        {
            List<InstallStep> s = InstallPlanner.ForInstall(false, true);
            Assert.True(!s.Contains(InstallStep.CopyExe) && !s.Contains(InstallStep.CopyConfig), "no copy");
            Assert.True(s.Contains(InstallStep.RegisterTasks), "tasks re-registered");
        }

        [Test]
        public static void Uninstall_Order()
        {
            List<UninstallStep> s = InstallPlanner.ForUninstall(false);
            Assert.Equal(UninstallStep.CloseRunningApp, s[0]);
            Assert.Equal(UninstallStep.FansToAuto, s[1]);
            Assert.True(s.IndexOf(UninstallStep.DeleteTasks) < s.IndexOf(UninstallStep.RemoveInstallDir), "tasks before files");
            Assert.True(s.IndexOf(UninstallStep.RemoveData) < s.IndexOf(UninstallStep.RemoveInstallDir), "data before the self-deleting step");
            Assert.True(s.IndexOf(UninstallStep.RemoveUpdateData) < s.IndexOf(UninstallStep.RemoveInstallDir), "update data before the self-deleting step");
            Assert.Equal(UninstallStep.RemoveInstallDir, s[s.Count - 1]);
        }

        [Test]
        public static void Uninstall_ReenablesAcerServices_AfterTheAppClosed_BeforeTheRecordsAreDeleted()
        {
            foreach (bool keep in new bool[] { false, true })
            {
                List<UninstallStep> s = InstallPlanner.ForUninstall(keep);
                int re = s.IndexOf(UninstallStep.ReenableServices);
                Assert.True(re > s.IndexOf(UninstallStep.CloseRunningApp), "after the app closed");
                Assert.True(re < s.IndexOf(UninstallStep.RemoveUpdateData), "before the undo records (state folder) are deleted");
                if (!keep) Assert.True(re < s.IndexOf(UninstallStep.RemoveData), "before config.json (with its records) is deleted");
            }
        }

        [Test]
        public static void Uninstall_KeepData_SkipsDataRemoval()
        {
            List<UninstallStep> s = InstallPlanner.ForUninstall(true);
            Assert.True(!s.Contains(UninstallStep.RemoveData), "data kept");
            Assert.True(s.Contains(UninstallStep.RemoveUpdateData), "the updater's own (admin-only) data is not user data");
            Assert.True(s.Contains(UninstallStep.RemoveUninstallEntry), "entry still removed");
        }

        [Test]
        public static void DowngradeConfirmation_OnlyWhenInstalledIsNewer()
        {
            Assert.True(InstallPlanner.NeedsDowngradeConfirmation(new UpdateVersion(0, 3, 0), new UpdateVersion(0, 2, 0)), "newer installed");
            Assert.True(!InstallPlanner.NeedsDowngradeConfirmation(new UpdateVersion(0, 2, 0), new UpdateVersion(0, 2, 0)), "same");
            Assert.True(!InstallPlanner.NeedsDowngradeConfirmation(new UpdateVersion(0, 1, 0), new UpdateVersion(0, 2, 0)), "older installed");
            Assert.True(!InstallPlanner.NeedsDowngradeConfirmation(null, new UpdateVersion(0, 2, 0)), "unknown installed version");
        }
    }

    public static class UninstallEntryTests
    {
        private static object Get(List<RegistryValue> l, string name)
        {
            foreach (RegistryValue v in l) if (v.Name == name) return v.Value;
            return null;
        }

        [Test]
        public static void Entry_HasTheAppsAndFeaturesFields()
        {
            string dir = @"C:\Program Files\SenseMonitor";
            string exe = dir + @"\SenseMonitor.exe";
            List<RegistryValue> e = UninstallEntry.Build(dir, exe, "0.2.1", 123);
            Assert.Equal("SenseMonitor", Get(e, "DisplayName"));
            Assert.Equal("0.2.1", Get(e, "DisplayVersion"));
            Assert.Equal("carlosbrito-afk", Get(e, "Publisher"));
            Assert.Equal("\"" + exe + "\" --desinstalar", Get(e, "UninstallString"));
            Assert.Equal(dir, Get(e, "InstallLocation"));
            Assert.Equal(exe, Get(e, "DisplayIcon"));
            Assert.Equal(1, Get(e, "NoModify"));
            Assert.Equal(1, Get(e, "NoRepair"));
            Assert.Equal(123, Get(e, "EstimatedSize"));
            Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SenseMonitor", UninstallEntry.KeyPath);
        }

        [Test]
        public static void Entry_SizeIsAtLeastOne()
        {
            Assert.Equal(1, Get(UninstallEntry.Build("a", "b", "0.1.0", 0), "EstimatedSize"));
        }
    }

    public static class PathSafetyTests
    {
        private const string PF = @"C:\Program Files";
        private const string AD = @"C:\Users\x\AppData\Roaming";

        [Test]
        public static void InstallDir_OnlyTheExactFolder()
        {
            Assert.True(PathSafety.IsInstallDir(@"C:\Program Files\SenseMonitor", PF), "exact");
            Assert.True(PathSafety.IsInstallDir(@"c:\program files\sensemonitor\", PF), "case + trailing slash");
            Assert.True(PathSafety.IsInstallDir(@"C:\Program Files\x\..\SenseMonitor", PF), "dot segments resolve");
            string[] bad =
            {
                @"C:\Program Files", @"C:\", @"C:\Program Files\SenseMonitor\atualizacao", @"C:\Program Files\SenseMonitor2",
                @"C:\Program Files (x86)\SenseMonitor", @"C:\claude\forja", @"C:\Program Files\SenseMonitor & calc",
                @"C:\Program Files\SenseMonitor""", @"C:\Program Files\SenseMonitor%x%", "", null,
                @"\\server\share\Program Files\SenseMonitor"
            };
            foreach (string d in bad)
            {
                Assert.True(!PathSafety.IsInstallDir(d, PF), "must reject: " + (d ?? "null"));
            }
            Assert.True(!PathSafety.IsInstallDir(@"C:\Program Files\SenseMonitor", ""), "no program files known");
            Assert.True(!PathSafety.IsInstallDir(@"C:\Program Files\SenseMonitor", @"C:\"), "root as program files");
        }

        [Test]
        public static void DataDir_OnlyTheExactFolder()
        {
            Assert.True(PathSafety.IsDataDir(AD + @"\SenseMonitor", AD), "exact");
            Assert.True(!PathSafety.IsDataDir(AD, AD), "parent");
            Assert.True(!PathSafety.IsDataDir(@"C:\Users\x", AD), "profile");
            Assert.True(!PathSafety.IsDataDir(AD + @"\SenseMonitor\sub", AD), "child");
            Assert.True(!PathSafety.IsDataDir(AD + @"\Other", AD), "sibling");
            Assert.True(!PathSafety.IsDataDir(AD + @"\SenseMonitor", ""), "no appdata known");
            Assert.True(!PathSafety.IsDataDir(null, AD), "null");
        }

        [Test]
        public static void DelayedRemoveArgs_BuiltOnlyForTheExactFolder_AndPingByItsSystem32Path()
        {
            string a = PathSafety.DelayedRemoveArgs(@"C:\Program Files\SenseMonitor", PF, @"C:\WINDOWS\System32");
            Assert.Equal(@"/d /c C:\WINDOWS\System32\ping.exe -n 4 127.0.0.1 >nul & rmdir /s /q ""C:\Program Files\SenseMonitor""", a);
            Assert.True(a.IndexOf("/c ping ") < 0, "never an unqualified ping");
            Assert.Throws<System.ArgumentException>(delegate { PathSafety.DelayedRemoveArgs(@"C:\Windows", PF, @"C:\WINDOWS\System32"); });
            Assert.Throws<System.ArgumentException>(delegate { PathSafety.DelayedRemoveArgs(@"C:\Program Files\SenseMonitor & del x", PF, @"C:\WINDOWS\System32"); });
        }

        [Test]
        public static void DelayedRemoveArgs_RefusesAnOddSystemFolder()
        {
            string[] bad = { null, "", @"C:\", @"C:\Some Dir\System32", @"C:\X & calc\System32", @"C:\%windir%\System32", @"C:""b\System32" };
            foreach (string sys in bad)
            {
                string captured = sys;
                Assert.Throws<System.ArgumentException>(delegate { PathSafety.DelayedRemoveArgs(@"C:\Program Files\SenseMonitor", PF, captured); });
            }
        }
    }
}
