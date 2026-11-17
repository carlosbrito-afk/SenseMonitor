using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The unified installer: where it resumes after the elevation (a step token, nothing else), which first-stage
    // action opens it, the install steps, their failure/rollback rules and the Start Menu shortcuts.
    public static class InstallerRouteTests
    {
        private const string Inst = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";
        private const string Down = @"C:\Users\x\Downloads\SenseMonitor.exe";

        private static StartupRoute R(params string[] a)
        {
            List<string> l = new List<string>();
            l.Add(Down);
            l.AddRange(a);
            return StartupRouter.Decide(l.ToArray(), Down, Inst, false);
        }

        [Test]
        public static void Install_WithoutAToken_StartsAtTheWelcome()
        {
            StartupRoute r = R("--instalar");
            Assert.Equal(StartupAction.Install, r.Action);
            Assert.Equal(InstallerStage.Welcome, r.Stage);
        }

        [Test]
        public static void Install_WithTheDetectToken_ResumesAtTheSecondStep()
        {
            StartupRoute r = R("--instalar", "--passo=reconhecer", "--elevado");
            Assert.Equal(StartupAction.Install, r.Action);
            Assert.Equal(InstallerStage.Detect, r.Stage);
            Assert.Equal(InstallerStage.Detect, R("--PASSO=RECONHECER", "--instalar").Stage);
        }

        [Test]
        public static void AnyOtherToken_IsRefused_NoStateIsTrusted()
        {
            string[] bad = { "--passo=", "--passo=instalando", "--passo=pronto", "--passo=2", "--passo=reconhecer;x", "--passo=..\\x", "--passo=reconhecer ", "--passo" };
            foreach (string b in bad)
            {
                StartupRoute r = R("--instalar", b);
                // "--passo" alone is not the token form: it is an unknown switch (ignored, like every unknown switch)
                if (b == "--passo") { Assert.Equal(StartupAction.Install, r.Action); Assert.Equal(InstallerStage.Welcome, r.Stage); continue; }
                Assert.Equal(StartupAction.BadArguments, r.Action);
            }
        }

        [Test]
        public static void TheToken_NeedsInstall_AndIsGivenOnce()
        {
            Assert.Equal(StartupAction.BadArguments, R("--passo=reconhecer").Action);
            Assert.Equal(StartupAction.BadArguments, StartupRouter.Decide(new string[] { Inst, "--desinstalar", "--passo=reconhecer" }, Inst, Inst, true).Action);
            Assert.Equal(StartupAction.BadArguments, R("--instalar", "--passo=reconhecer", "--passo=reconhecer").Action);
        }

        [Test]
        public static void NoArguments_FromElsewhere_StillOffersTheInstall()
        {
            StartupRoute r = StartupRouter.Decide(new string[] { Down }, Down, Inst, false);
            Assert.Equal(StartupAction.OfferInstall, r.Action);
            Assert.Equal(InstallerStage.Welcome, r.Stage);
        }
    }

    public static class InstallerFirstStageTests
    {
        private static readonly List<string> None = new List<string>();

        [Test]
        public static void TheDoubleClick_NotElevated_OpensTheInstallerWindow_NotAQuestionBox()
        {
            Assert.Equal(FirstStageAction.ShowInstaller, FirstStage.Decide(new StartupRoute(StartupAction.OfferInstall), false, false, None));
        }

        [Test]
        public static void ThePlantedFileCheckStillComesFirst_NoWindowNoElevation()
        {
            List<string> bad = new List<string>(new string[] { "version.dll" });
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(new StartupRoute(StartupAction.OfferInstall), false, false, bad));
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(new StartupRoute(StartupAction.Install), false, false, bad));
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(new StartupRoute(StartupAction.Install), true, false, bad));
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(new StartupRoute(StartupAction.OfferInstall), true, false, bad));
        }

        [Test]
        public static void AlreadyElevated_NothingMoreIsAsked_TheWizardRunsHere()
        {
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(new StartupRoute(StartupAction.OfferInstall), true, false, None));
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(new StartupRoute(StartupAction.Install), true, false, None));
        }

        [Test]
        public static void HandOff_IsUnchanged()
        {
            Assert.Equal(FirstStageAction.HandOffOrReinstall, FirstStage.Decide(new StartupRoute(StartupAction.OfferHandOff), false, false, None));
        }

        [Test]
        public static void Mode_WizardOnlyForAFreshInstall_TheOthersKeepTheQuickPath()
        {
            Assert.Equal(InstallMode.Wizard, InstallerFlow.Mode(false, false));
            Assert.Equal(InstallMode.Replace, InstallerFlow.Mode(true, false));
            Assert.Equal(InstallMode.Repair, InstallerFlow.Mode(true, true));
            Assert.Equal(InstallMode.Repair, InstallerFlow.Mode(false, true));
        }
    }

    public static class InstallerPlanTests
    {
        [Test]
        public static void WizardInstall_HasNoStartStep_TheAppOpensFromTheLastScreen()
        {
            List<InstallStep> s = InstallPlanner.ForWizardInstall(false);
            Assert.True(!s.Contains(InstallStep.StartApp), "starting is the user's button at the end");
            Assert.Equal(InstallStep.PrepareFolder, s[0]);
            Assert.Equal(InstallStep.CopyExe, s[1]);
            Assert.True(s.IndexOf(InstallStep.RegisterTasks) < s.IndexOf(InstallStep.CreateShortcuts), "tasks, then shortcuts");
            Assert.True(s.IndexOf(InstallStep.CreateShortcuts) < s.IndexOf(InstallStep.WriteUninstallEntry), "entry last");
            Assert.Equal(InstallStep.WriteUninstallEntry, s[s.Count - 1]);
        }

        [Test]
        public static void WizardInstall_ClosesARunningAppFirst()
        {
            List<InstallStep> s = InstallPlanner.ForWizardInstall(true);
            Assert.Equal(InstallStep.CloseRunningApp, s[0]);
        }

        [Test]
        public static void QuickInstall_AlsoGetsFoldersAndShortcuts()
        {
            List<InstallStep> s = InstallPlanner.ForInstall(false, false);
            Assert.True(s.Contains(InstallStep.PrepareFolder) && s.Contains(InstallStep.CreateShortcuts), "folder and shortcuts");
            Assert.Equal(InstallStep.StartApp, s[s.Count - 1]);
            List<InstallStep> repair = InstallPlanner.ForInstall(false, true);
            Assert.True(!repair.Contains(InstallStep.PrepareFolder), "a repair from the installed copy has its folder");
            Assert.True(repair.Contains(InstallStep.CreateShortcuts), "shortcuts are recreated");
        }

        [Test]
        public static void FatalSteps_AreTheOnesThatMakeTheInstallUnusable()
        {
            Assert.True(InstallPlanner.IsFatal(InstallStep.CloseRunningApp), "close");
            Assert.True(InstallPlanner.IsFatal(InstallStep.PrepareFolder), "folder");
            Assert.True(InstallPlanner.IsFatal(InstallStep.CopyExe), "exe");
            Assert.True(InstallPlanner.IsFatal(InstallStep.RegisterTasks), "tasks: the sentinel and the autostart");
            Assert.True(InstallPlanner.IsFatal(InstallStep.WriteUninstallEntry), "Apps and features");
            Assert.True(!InstallPlanner.IsFatal(InstallStep.CopyConfig), "the config is rewritten at every start");
            Assert.True(!InstallPlanner.IsFatal(InstallStep.CreateShortcuts), "shortcuts are a convenience");
            Assert.True(!InstallPlanner.IsFatal(InstallStep.StartApp), "starting");
        }

        [Test]
        public static void EveryStep_HasALineTheUserCanRead()
        {
            foreach (InstallStep s in (InstallStep[])Enum.GetValues(typeof(InstallStep)))
            {
                string t = InstallerText.Line(s);
                Assert.True(!string.IsNullOrEmpty(t) && t.Length > 12 && t.Length < 100, "line for " + s + ": " + t);
            }
        }

        [Test]
        public static void Uninstall_RemovesTheShortcuts_WithTheEntry_BeforeTheFiles()
        {
            foreach (bool keep in new bool[] { false, true })
            {
                List<UninstallStep> s = InstallPlanner.ForUninstall(keep);
                int sc = s.IndexOf(UninstallStep.RemoveShortcuts);
                Assert.True(sc >= 0, "shortcuts are removed");
                Assert.True(sc > s.IndexOf(UninstallStep.DeleteTasks), "after the tasks");
                Assert.True(sc < s.IndexOf(UninstallStep.RemoveInstallDir), "before the self-deleting step");
            }
        }
    }

    public static class InstallRollbackTests
    {
        private static List<InstallStep> Done(params InstallStep[] s)
        {
            return new List<InstallStep>(s);
        }

        [Test]
        public static void FreshInstall_FailingAtTheEntry_UndoesEverythingInReverse()
        {
            List<RollbackStep> p = InstallRollback.Plan(
                Done(InstallStep.PrepareFolder, InstallStep.CopyExe, InstallStep.CopyConfig, InstallStep.RegisterTasks, InstallStep.CreateShortcuts),
                InstallStep.WriteUninstallEntry, false, true, false);
            Assert.Equal(6, p.Count);
            Assert.Equal(RollbackStep.RemoveUninstallEntry, p[0]);
            Assert.Equal(RollbackStep.RemoveShortcuts, p[1]);
            Assert.Equal(RollbackStep.DeleteTasks, p[2]);
            Assert.Equal(RollbackStep.RemoveConfigFile, p[3]);
            Assert.Equal(RollbackStep.RemoveInstalledExe, p[4]);
            Assert.Equal(RollbackStep.RemoveInstallFolder, p[5]);
        }

        [Test]
        public static void FailureAtTheCopy_RemovesOnlyTheFolderWeMade()
        {
            List<RollbackStep> p = InstallRollback.Plan(Done(InstallStep.PrepareFolder), InstallStep.CopyExe, false, true, false);
            Assert.Equal(1, p.Count);
            Assert.Equal(RollbackStep.RemoveInstallFolder, p[0]);
        }

        [Test]
        public static void AFolderThatWasThereBefore_IsNeverRemoved()
        {
            List<RollbackStep> p = InstallRollback.Plan(Done(InstallStep.PrepareFolder, InstallStep.CopyExe), InstallStep.CopyConfig, false, false, false);
            Assert.True(!p.Contains(RollbackStep.RemoveInstallFolder), "not ours");
            Assert.True(p.Contains(RollbackStep.RemoveInstalledExe), "the exe we copied goes");
        }

        [Test]
        public static void NothingDone_NothingToUndo()
        {
            Assert.Equal(0, InstallRollback.Plan(new List<InstallStep>(), InstallStep.PrepareFolder, false, false, false).Count);
            Assert.Equal(0, InstallRollback.Plan(new List<InstallStep>(), null, false, false, false).Count);
        }

        [Test]
        public static void Cancel_HasNoFailedStep_JustTheCompletedOnes()
        {
            List<RollbackStep> p = InstallRollback.Plan(Done(InstallStep.PrepareFolder, InstallStep.CopyExe, InstallStep.CopyConfig), null, false, true, false);
            Assert.Equal(RollbackStep.RemoveConfigFile, p[0]);
            Assert.Equal(RollbackStep.RemoveInstalledExe, p[1]);
            Assert.Equal(RollbackStep.RemoveInstallFolder, p[2]);
        }

        [Test]
        public static void Reinstall_RestoresThePreviousExe_AndLeavesTasksEntryAndShortcutsAlone()
        {
            List<RollbackStep> p = InstallRollback.Plan(
                Done(InstallStep.CloseRunningApp, InstallStep.PrepareFolder, InstallStep.CopyExe, InstallStep.CopyConfig, InstallStep.RegisterTasks),
                InstallStep.CreateShortcuts, true, false, true);
            Assert.True(p.Contains(RollbackStep.RestorePreviousExe), "the old exe comes back");
            Assert.True(!p.Contains(RollbackStep.RemoveInstalledExe), "never deleted on a reinstall");
            Assert.True(!p.Contains(RollbackStep.DeleteTasks) && !p.Contains(RollbackStep.RemoveUninstallEntry) && !p.Contains(RollbackStep.RemoveShortcuts), "they existed before");
            Assert.True(!p.Contains(RollbackStep.RemoveInstallFolder), "the folder existed");
            Assert.Equal(RollbackStep.RestartPreviousApp, p[p.Count - 1]);
        }

        [Test]
        public static void Reinstall_FailedCopy_NeverRestoresAnOldAnteriorOverTheWorkingExe()
        {
            // CopyExe cleans up after itself when it fails: the .anterior.exe of an EARLIER update must stay where it is.
            List<RollbackStep> p = InstallRollback.Plan(Done(InstallStep.CloseRunningApp, InstallStep.PrepareFolder), InstallStep.CopyExe, true, false, true);
            Assert.True(!p.Contains(RollbackStep.RestorePreviousExe), "copy did not complete");
            Assert.True(p.Contains(RollbackStep.RestartPreviousApp), "the app that was open is opened again");
        }

        [Test]
        public static void Reinstall_AppThatWasNotRunning_IsNotStarted()
        {
            List<RollbackStep> p = InstallRollback.Plan(Done(InstallStep.PrepareFolder, InstallStep.CopyExe), InstallStep.RegisterTasks, true, false, false);
            Assert.True(!p.Contains(RollbackStep.RestartPreviousApp), "it was closed before");
        }
    }

    public static class StartMenuShortcutTests
    {
        private const string Programs = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
        private const string Exe = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";

        [Test]
        public static void TwoShortcuts_OpenAndUninstall()
        {
            List<ShortcutSpec> l = StartMenuShortcuts.Specs(Exe);
            Assert.Equal(2, l.Count);
            Assert.Equal("SenseMonitor.lnk", l[0].FileName);
            Assert.Equal(Exe, l[0].Target);
            Assert.Equal("", l[0].Arguments);
            Assert.Equal("Desinstalar SenseMonitor.lnk", l[1].FileName);
            Assert.Equal(Exe, l[1].Target);
            Assert.Equal("--desinstalar", l[1].Arguments);
            Assert.True(l[0].Description.Length > 5 && l[1].Description.Length > 5, "described");
        }

        [Test]
        public static void TheFolder_IsExactlyUnderTheCommonProgramsFolder()
        {
            Assert.Equal(Programs + @"\SenseMonitor", StartMenuShortcuts.FolderPath(Programs));
            Assert.True(StartMenuShortcuts.IsOurFolder(Programs + @"\SenseMonitor", Programs), "ours");
            Assert.True(StartMenuShortcuts.IsOurFolder(Programs + @"\SenseMonitor\", Programs), "trailing slash");
            Assert.True(!StartMenuShortcuts.IsOurFolder(Programs, Programs), "never the parent");
            Assert.True(!StartMenuShortcuts.IsOurFolder(Programs + @"\SenseMonitor2", Programs), "never a sibling");
            Assert.True(!StartMenuShortcuts.IsOurFolder(Programs + @"\SenseMonitor\..\Accessories", Programs), "no dot-dot");
            Assert.True(!StartMenuShortcuts.IsOurFolder(@"C:\Users\x\Desktop\SenseMonitor", Programs), "another place");
            Assert.True(!StartMenuShortcuts.IsOurFolder(null, Programs) && !StartMenuShortcuts.IsOurFolder(Programs + @"\SenseMonitor", null), "nulls");
            Assert.True(!StartMenuShortcuts.IsOurFolder(@"C:\SenseMonitor", @"C:\"), "a root is not a Start Menu");
        }

        [Test]
        public static void OnlyTheTwoKnownNames_AreEverDeleted()
        {
            Assert.True(StartMenuShortcuts.IsOurFile("SenseMonitor.lnk"), "open");
            Assert.True(StartMenuShortcuts.IsOurFile("desinstalar sensemonitor.LNK"), "case");
            Assert.True(!StartMenuShortcuts.IsOurFile("Outro.lnk"), "other");
            Assert.True(!StartMenuShortcuts.IsOurFile("..\\SenseMonitor.lnk"), "path");
            Assert.True(!StartMenuShortcuts.IsOurFile(null), "null");
        }
    }

    public static class UninstallSummaryTests
    {
        [Test]
        public static void Lists_WhatIsRemoved()
        {
            UninstallSummary s = UninstallSummary.Build(@"C:\Program Files\SenseMonitor", false);
            string all = string.Join("\n", s.RemovedLines().ToArray());
            Assert.True(all.IndexOf(@"C:\Program Files\SenseMonitor", StringComparison.Ordinal) >= 0, "the folder");
            Assert.True(all.IndexOf("Menu Iniciar", StringComparison.Ordinal) >= 0, "shortcuts");
            Assert.True(all.IndexOf("Apps e recursos", StringComparison.Ordinal) >= 0, "the Apps entry");
            Assert.True(all.IndexOf("tarefas", StringComparison.OrdinalIgnoreCase) >= 0, "tasks");
            Assert.True(s.Question.IndexOf("Manter suas configurações?", StringComparison.Ordinal) >= 0, "the question");
        }

        [Test]
        public static void TheConfigLine_FollowsTheAnswer()
        {
            UninstallSummary s = UninstallSummary.Build(@"C:\Program Files\SenseMonitor", false);
            Assert.True(s.DataLine(true).IndexOf("mantid", StringComparison.OrdinalIgnoreCase) >= 0, "kept");
            Assert.True(s.DataLine(false).IndexOf("apagad", StringComparison.OrdinalIgnoreCase) >= 0, "erased");
        }

        [Test]
        public static void Notes_FansAndAcerServices_OnlyWhenThereIsSomethingToRestore()
        {
            List<string> without = UninstallSummary.Build(@"C:\Program Files\SenseMonitor", false).Notes();
            List<string> with = UninstallSummary.Build(@"C:\Program Files\SenseMonitor", true).Notes();
            Assert.True(without.Count == 1 && without[0].IndexOf("automático", StringComparison.OrdinalIgnoreCase) >= 0, "fans always");
            Assert.True(with.Count == 2 && with[1].IndexOf("Acer", StringComparison.Ordinal) >= 0, "Acer services only when disabled by the test");
        }
    }

    public static class InstallEngineTests
    {
        private sealed class Ops : IInstallOps
        {
            public readonly List<string> Log = new List<string>();
            public string FailAt;           // step name that returns an error
            public bool FailRollback;       // every rollback action fails
            public bool CloseFails;
            public bool Created = true;
            public bool FolderCreatedByUs { get { return Created; } }

            private string Do(string name)
            {
                Log.Add(name);
                return FailAt == name ? "falhou " + name : null;
            }

            public string CloseRunningApp() { Log.Add("CloseRunningApp"); return CloseFails ? "não fechou" : null; }
            public string PrepareFolder() { return Do("PrepareFolder"); }
            public string CopyExe() { return Do("CopyExe"); }
            public string CopyConfig() { return Do("CopyConfig"); }
            public string RegisterTasks() { return Do("RegisterTasks"); }
            public string CreateShortcuts() { return Do("CreateShortcuts"); }
            public string WriteUninstallEntry() { return Do("WriteUninstallEntry"); }
            public string StartApp() { return Do("StartApp"); }
            public string Undo(RollbackStep step) { Log.Add("undo:" + step); return FailRollback ? "não deu" : null; }
        }

        private static InstallOutcome Run(Ops ops, List<InstallStep> steps, bool had, bool appWasRunning, Func<bool> cancelled, List<InstallProgress> seen)
        {
            return InstallEngine.Run(ops, steps, had, appWasRunning, cancelled ?? delegate { return false; }, delegate(InstallProgress p) { if (seen != null) seen.Add(p); });
        }

        [Test]
        public static void AllSteps_RunInOrder_AndReportRunningThenDone()
        {
            Ops ops = new Ops();
            List<InstallProgress> seen = new List<InstallProgress>();
            InstallOutcome o = Run(ops, InstallPlanner.ForWizardInstall(false), false, false, null, seen);
            Assert.True(o.Ok, "ok");
            Assert.Equal("PrepareFolder,CopyExe,CopyConfig,RegisterTasks,CreateShortcuts,WriteUninstallEntry", string.Join(",", ops.Log.ToArray()));
            Assert.Equal(12, seen.Count);
            Assert.Equal(InstallStepState.Running, seen[0].State);
            Assert.Equal(InstallStepState.Done, seen[1].State);
            Assert.Equal(InstallStep.PrepareFolder, seen[0].Step);
        }

        [Test]
        public static void AFatalFailure_StopsAtOnce_AndRollsBackWhatWasDone()
        {
            Ops ops = new Ops();
            ops.FailAt = "RegisterTasks";
            InstallOutcome o = Run(ops, InstallPlanner.ForWizardInstall(false), false, false, null, null);
            Assert.True(!o.Ok && !o.Cancelled, "failed");
            Assert.Equal(InstallStep.RegisterTasks, o.FailedStep.Value);
            Assert.Equal("falhou RegisterTasks", o.Error);
            Assert.True(!ops.Log.Contains("CreateShortcuts") && !ops.Log.Contains("WriteUninstallEntry"), "nothing after the failure");
            int i = ops.Log.IndexOf("undo:" + RollbackStep.DeleteTasks);
            Assert.True(i > ops.Log.IndexOf("RegisterTasks"), "the failed step itself is undone too (it may be half done)");
            Assert.True(ops.Log.Contains("undo:" + RollbackStep.RemoveInstalledExe) && ops.Log.Contains("undo:" + RollbackStep.RemoveInstallFolder), "exe and folder go");
            Assert.True(o.RollbackClean, "clean");
        }

        [Test]
        public static void ANonFatalFailure_IsAWarning_TheInstallGoesOn()
        {
            Ops ops = new Ops();
            ops.FailAt = "CreateShortcuts";
            InstallOutcome o = Run(ops, InstallPlanner.ForWizardInstall(false), false, false, null, null);
            Assert.True(o.Ok, "still installed");
            Assert.Equal(1, o.Warnings.Count);
            Assert.True(ops.Log.Contains("WriteUninstallEntry"), "went on");
            Assert.True(!ops.Log.Exists(delegate(string l) { return l.StartsWith("undo:"); }), "nothing rolled back");
        }

        [Test]
        public static void ACancel_BetweenSteps_RollsBack_AndSaysCancelled()
        {
            Ops ops = new Ops();
            int polls = 0;
            InstallOutcome o = Run(ops, InstallPlanner.ForWizardInstall(false), false, false, delegate { polls++; return polls > 2; }, null);
            Assert.True(o.Cancelled && !o.Ok, "cancelled");
            Assert.True(ops.Log.Contains("undo:" + RollbackStep.RemoveInstalledExe), "the exe copied so far is removed");
            Assert.True(!ops.Log.Contains("RegisterTasks"), "stopped before the tasks");
        }

        [Test]
        public static void ACancelAfterTheLastStep_IsTooLate_TheInstallStays()
        {
            Ops ops = new Ops();
            bool last = false;
            InstallOutcome o = InstallEngine.Run(ops, InstallPlanner.ForWizardInstall(false), false, false, delegate { return last; },
                delegate(InstallProgress p) { if (p.Step == InstallStep.WriteUninstallEntry && p.State == InstallStepState.Done) last = true; });
            Assert.True(o.Ok && !o.Cancelled, "installed");
        }

        [Test]
        public static void ACloseThatFails_IsFatal_AndNothingIsTouched()
        {
            Ops ops = new Ops();
            ops.CloseFails = true;
            InstallOutcome o = Run(ops, InstallPlanner.ForWizardInstall(true), true, true, null, null);
            Assert.True(!o.Ok, "failed");
            Assert.Equal(InstallStep.CloseRunningApp, o.FailedStep.Value);
            Assert.True(!ops.Log.Contains("PrepareFolder") && !ops.Log.Contains("CopyExe"), "nothing was changed");
        }

        [Test]
        public static void ARollbackThatFails_IsReported_AndTheRestStillRuns()
        {
            Ops ops = new Ops();
            ops.FailAt = "WriteUninstallEntry";
            ops.FailRollback = true;
            InstallOutcome o = Run(ops, InstallPlanner.ForWizardInstall(false), false, false, null, null);
            Assert.True(!o.Ok, "failed");
            Assert.True(!o.RollbackClean, "not clean");
            Assert.True(o.RollbackProblems.Count >= 5, "every action tried and reported");
            Assert.True(ops.Log.Contains("undo:" + RollbackStep.RemoveInstallFolder), "the last action still ran");
        }

        [Test]
        public static void TheFolderWeDidNotCreate_IsNotUndone()
        {
            Ops ops = new Ops();
            ops.Created = false;
            ops.FailAt = "CopyExe";
            Run(ops, InstallPlanner.ForWizardInstall(false), false, false, null, null);
            Assert.True(!ops.Log.Contains("undo:" + RollbackStep.RemoveInstallFolder), "existing folder stays");
        }
    }
}
