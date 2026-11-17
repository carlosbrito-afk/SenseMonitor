using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class ElevationGuardTests
    {
        private static StartupRoute R(StartupAction a)
        {
            return new StartupRoute(a);
        }

        private static readonly List<string> None = new List<string>();

        [Test]
        public static void EveryRouteThatWouldElevateAnExeFromElsewhere_IsRefusedOnCompanions()
        {
            List<string> bad = new List<string>(new string[] { "x.dll" });
            // N1: not just installs. A normal run, a tool switch, an uninstall, "--executar-aqui" (all RunApp),
            // the install and the offer: each elevates a copy that sits in a user-writable folder.
            StartupAction[] routes = { StartupAction.RunApp, StartupAction.Install, StartupAction.Uninstall, StartupAction.OfferInstall };
            foreach (StartupAction a in routes)
            {
                Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(R(a), false, false, bad));
            }
        }

        [Test]
        public static void TheSameRoutes_FromTheInstalledFolder_AreNotAffected()
        {
            List<string> own = new List<string>(new string[] { "SenseMonitor.exe.config" });
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.RunApp), false, true, own));
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.Uninstall), false, true, own));
        }

        [Test]
        public static void CleanFolder_ElevatesAsBefore()
        {
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.RunApp), false, false, None));
            Assert.Equal(FirstStageAction.ShowInstaller, FirstStage.Decide(R(StartupAction.OfferInstall), false, false, None));
        }

        [Test]
        public static void MustRefuseElevation_IsOneRule()
        {
            List<string> bad = new List<string>(new string[] { "a.config" });
            Assert.True(FirstStage.MustRefuseElevation(false, bad), "elsewhere + companion");
            Assert.True(!FirstStage.MustRefuseElevation(true, bad), "installed copy");
            Assert.True(!FirstStage.MustRefuseElevation(false, new List<string>()), "clean folder");
            Assert.True(!FirstStage.MustRefuseElevation(false, null), "no list given: the runner turns an unreadable folder into a companion entry");
        }

        [Test]
        public static void InstalledAndDoubleClickedFromElsewhere_HandsOffInsteadOfElevating()
        {
            // not elevated: first the question "open the installed one / update with this file", with or
            // without companions (the hand-off elevates nothing of the downloaded exe)
            Assert.Equal(FirstStageAction.HandOffOrReinstall, FirstStage.Decide(R(StartupAction.OfferHandOff), false, false, None));
            Assert.Equal(FirstStageAction.HandOffOrReinstall, FirstStage.Decide(R(StartupAction.OfferHandOff), false, false, new List<string>(new string[] { "x.dll" })));
            // already elevated (an administrator prompt): the install tool asks the same question
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.OfferHandOff), true, false, None));
        }

        [Test]
        public static void ElevatedProcesses_AreNeverBlockedExceptWhenCopyingIntoProgramFiles()
        {
            List<string> bad = new List<string>(new string[] { "x.dll" });
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.RunApp), true, false, bad));
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(R(StartupAction.Install), true, false, bad));
        }

        [Test]
        public static void Router_InstalledPlusDoubleClick_IsAHandOff_NotAnInstallOffer()
        {
            string inst = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";
            string down = @"C:\Users\x\Downloads\SenseMonitor.exe";
            Assert.Equal(StartupAction.OfferHandOff, StartupRouter.Decide(new string[] { down }, down, inst, true).Action);
            Assert.Equal(StartupAction.OfferInstall, StartupRouter.Decide(new string[] { down }, down, inst, false).Action);
            Assert.Equal(StartupAction.RunApp, StartupRouter.Decide(new string[] { inst }, inst, inst, true).Action);
            Assert.Equal(StartupAction.RunApp, StartupRouter.Decide(new string[] { down, "--executar-aqui" }, down, inst, true).Action);
            Assert.Equal(StartupAction.RunApp, StartupRouter.Decide(new string[] { down, "--elevado" }, down, inst, true).Action);
        }
    }

    public static class CommandLineTests
    {
        [Test]
        public static void Split_ProgramAndPlainArguments()
        {
            List<string> t = CommandLine.Split(@"C:\x\a.exe --one two");
            Assert.Equal(3, t.Count);
            Assert.Equal(@"C:\x\a.exe", t[0]);
            Assert.Equal("two", t[2]);
        }

        [Test]
        public static void Split_BackslashQuoteRules()
        {
            // "a b\\" -> a b\ : an even run of backslashes before a quote halves, and that quote closes the string
            List<string> t = CommandLine.Split("p.exe \"a b\\\\\" c \\\"lit d\\e");
            Assert.Equal(5, t.Count);
            Assert.Equal("a b\\", t[1]);
            Assert.Equal("c", t[2]);
            Assert.Equal("\"lit", t[3]);   // \"lit : an odd run gives a literal quote
            Assert.Equal("d\\e", t[4]);    // backslashes not before a quote stay
        }

        [Test]
        public static void Split_QuotedProgramHasNoEscapes()
        {
            List<string> t = CommandLine.Split("\"C:\\Program Files\\x\\\" --a");
            Assert.Equal(@"C:\Program Files\x\", t[0]);
            Assert.Equal("--a", t[1]);
        }

        [Test]
        public static void Quote_ThenSplit_RoundTripsAwkwardArguments()
        {
            string[] args = { "plain", "has space", @"C:\Program Files\SenseMonitor\", "say \"hi\"", @"\\server\share dir\", "end\\\\", "\"", "", "a\\\"b c\\" };
            foreach (string a in args)
            {
                List<string> back = CommandLine.Split("prog.exe " + CommandLine.Quote(a));
                Assert.Equal(2, back.Count);
                Assert.Equal(a, back[1]);
            }
        }

        [Test]
        public static void Quote_OnlyDoublesBackslashesWhereNeeded()
        {
            Assert.Equal(@"C:\Temp\x", CommandLine.Quote(@"C:\Temp\x"));                  // nothing to quote
            Assert.Equal("\"C:\\My Dir\\sub\"", CommandLine.Quote(@"C:\My Dir\sub"));      // inner backslashes untouched (N9)
            Assert.Equal("\"C:\\My Dir\\\\\"", CommandLine.Quote(@"C:\My Dir\"));          // doubled only before the closing quote
        }

        [Test]
        public static void Classify_FollowsTheSameParsing()
        {
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify("C:\\x\\SenseMonitor.exe \"--sentinela x\""));
            Assert.Equal(AppProcessKind.Sentinel, ProcessKinds.Classify("C:\\x\\SenseMonitor.exe  --sentinela"));
        }

        [Test]
        public static void IsTray_OnlyThePlainAppWithItsMarkers()
        {
            string exe = "\"C:\\Program Files\\SenseMonitor\\SenseMonitor.exe\"";
            Assert.True(ProcessKinds.IsTray(exe), "plain");
            Assert.True(ProcessKinds.IsTray(exe + " --elevado"), "after the relaunch");
            Assert.True(ProcessKinds.IsTray(exe + " --executar-aqui --elevado"), "markers only");
            Assert.True(!ProcessKinds.IsTray(exe + " --autoteste"), "a CLI tool is not the tray");
            Assert.True(!ProcessKinds.IsTray(exe + " --validar-ventoinha 50"), "validation tool");
            Assert.True(!ProcessKinds.IsTray(exe + " --sentinela"), "the sentinel");
            Assert.True(!ProcessKinds.IsTray(null), "unknown");
        }
    }

    public static class ApplyBackoffTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public static void ApplyDeferral_HoldsBackUntilTheMoment()
        {
            UpdateState s = new UpdateState();
            Assert.True(!s.ApplyDeferred(T0), "nothing deferred");
            s.DeferApply(T0, TimeSpan.FromHours(1));
            Assert.True(s.ApplyDeferred(T0.AddMinutes(59)), "held");
            Assert.True(!s.ApplyDeferred(T0.AddMinutes(61)), "released");
        }

        [Test]
        public static void ApplyDeferral_NeverShortens_AndSurvivesARoundTrip()
        {
            UpdateState s = new UpdateState();
            s.DeferApply(T0, TimeSpan.FromHours(6));
            s.DeferApply(T0, TimeSpan.FromHours(1));
            UpdateState back = UpdateState.Parse(s.ToBytes());
            Assert.True(back.ApplyDeferred(T0.AddHours(5)), "still the 6 h one");
            Assert.True(!back.ApplyDeferred(T0.AddHours(7)), "over");
        }

        [Test]
        public static void ApplyDeferral_UnreadableFailsClosed_FarFutureIsIgnored()
        {
            UpdateState s = new UpdateState();
            s.AplicarDepoisDe = "amanhã";
            Assert.True(s.ApplyDeferred(T0), "unreadable: held");
            s.AplicarDepoisDe = UpdateState.FormatUtc(T0.AddDays(400));
            Assert.True(!s.ApplyDeferred(T0), "a date months away (clock games) cannot freeze updates for good");
        }
    }

    public static class ApplierRound2Tests
    {
        private const string Cur = @"P\SenseMonitor.exe";
        private const string Ant = @"P\SenseMonitor.anterior.exe";
        private const string Nov = @"P\atualizacao\SenseMonitor.novo.exe";
        private const string Fal = @"P\atualizacao\SenseMonitor.falhou.exe";

        private static FakeApplyEnv Env()
        {
            FakeApplyEnv e = new FakeApplyEnv();
            e.Files[Cur] = "old";
            e.Files[Nov] = "new";
            return e;
        }

        private static ApplyResult Run(FakeApplyEnv e)
        {
            ApplyPaths p = new ApplyPaths();
            p.Current = Cur; p.Anterior = Ant; p.Novo = Nov; p.Falhou = Fal;
            return new UpdateApplier(p, new ApplyTimings()).Run(e, 1234, "0.2.1");
        }

        [Test]
        public static void ReplaceFailsHalfway_TheOldExeIsPutBackAtOnce()
        {
            FakeApplyEnv e = Env();
            e.ReplaceFailsHalfway = true;
            Assert.Equal(ApplyResult.NotApplied, Run(e));
            Assert.Equal("old", e.Files[Cur]);          // the install name is back, with the old exe
            Assert.Equal("new", e.Files[Nov]);          // the staged release is untouched
            Assert.True(!e.Files.ContainsKey(Ant), "the backup name was handed back");
            Assert.True(e.Logs.Exists(delegate(string l) { return l.Contains("recolocada"); }), "logged");
            Assert.Equal(1, e.Starts);                  // and the app is started again
        }

        [Test]
        public static void ReplaceFailsHalfway_AndTheRepairFails_IsReportedLoudly()
        {
            FakeApplyEnv e = Env();
            e.ReplaceFailsHalfway = true;
            e.MoveFails = true;
            Assert.Equal(ApplyResult.RollbackFailed, Run(e));
            Assert.True(e.Logs.Exists(delegate(string l) { return l.Contains("ERRO GRAVE") && l.Contains("SenseMonitor.anterior.exe"); }), "tells the user what to copy");
        }

        [Test]
        public static void EveryNotApplied_AsksForABackoff()
        {
            FakeApplyEnv a = Env(); a.OldExits = false; a.PreexistingApp = true; Run(a);
            Assert.True(a.Deferred.Count == 1 && a.Deferred[0].StartsWith("1h"), "old still running: short");
            FakeApplyEnv b = Env(); b.OthersGone = false; b.PreexistingApp = true; Run(b);
            Assert.True(b.Deferred.Count == 1 && b.Deferred[0].StartsWith("1h"), "other instance: short");
            FakeApplyEnv c = Env(); c.FailReplaceInto = Cur; Run(c);
            Assert.True(c.Deferred.Count == 1 && c.Deferred[0].StartsWith("6h"), "swap failed: long");
            FakeApplyEnv d = Env(); Run(d);
            Assert.Equal(0, d.Deferred.Count);          // success: nothing deferred
        }

        [Test]
        public static void ARunningTool_IsNotTheTray_SoTheTrayIsStarted()
        {
            FakeApplyEnv e = Env();
            e.OldExits = false;       // a refusal path
            e.PreexistingTool = true; // a CLI tool is alive, no tray
            Assert.Equal(ApplyResult.OldStillRunning, Run(e));
            Assert.Equal(1, e.Starts); // a duplicate tray is turned away by its mutex; a missing one is not acceptable
        }
    }
}
