using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Arguments, exit codes, plan rules, kernel, power plan and scoring of the performance test.
    public static class PerfModelTests
    {
        // ---- arguments and exit codes ----

        [Test]
        public static void Args_AutoFlag_AnyPosition_CaseInsensitive()
        {
            Assert.True(ToolArgs.Unattended(new[] { "x.exe", "--teste-desempenho", "--auto" }), "tool then --auto");
            Assert.True(ToolArgs.Unattended(new[] { "x.exe", "--AUTO", "--autoteste" }), "--auto first, upper case");
            Assert.True(!ToolArgs.Unattended(new[] { "x.exe", "--teste-desempenho" }), "no flag is interactive");
            Assert.True(!ToolArgs.Unattended(new[] { "--auto" }), "args[0] is the program, never a flag");
            Assert.True(!ToolArgs.Unattended(null), "null args");
            Assert.True(!ToolArgs.Unattended(new[] { "x.exe", "--automatico" }), "only the exact switch counts");
        }

        [Test]
        public static void Args_Has_FindsTheToolSwitch()
        {
            Assert.True(ToolArgs.Has(new[] { "x.exe", "--auto", "--autoteste" }, "--autoteste"), "autoteste");
            Assert.True(!ToolArgs.Has(new[] { "x.exe", "--auto" }, "--autoteste"), "absent");
            Assert.True(!ToolArgs.Has(new[] { "x.exe", "--auto" }, null), "null name");
        }

        [Test]
        public static void ExitCode_Perf_ZeroOnlyWhenCompletedAndRestored()
        {
            PerfTestReport r = new PerfTestReport();
            r.FinalModeOk = true;
            r.FinalFansAuto = true;
            Assert.Equal(0, ToolExit.ForPerf(r));
            r.Aborted = true;
            Assert.Equal(1, ToolExit.ForPerf(r));
            Assert.Equal(1, ToolExit.ForPerf(null));
        }

        [Test]
        public static void ExitCode_Perf_RestoreFailureHasItsOwnCode()
        {
            PerfTestReport r = new PerfTestReport();
            r.FinalModeOk = true;
            r.FinalFansAuto = false;
            Assert.Equal(ToolExit.RestoreFailed, ToolExit.ForPerf(r));
            r.FinalFansAuto = true;
            r.FinalModeOk = false;
            Assert.Equal(ToolExit.RestoreFailed, ToolExit.ForPerf(r));
            r.Aborted = true; // aborted AND not restored: still the serious one
            Assert.Equal(ToolExit.RestoreFailed, ToolExit.ForPerf(r));
            Assert.True(ToolExit.RestoreFailed != 0 && ToolExit.RestoreFailed != ToolExit.Failed && ToolExit.RestoreFailed != ToolExit.OtherInstance
                && ToolExit.RestoreFailed != ToolExit.Declined, "distinct code");
        }

        [Test]
        public static void ExitCode_Auto_ZeroOnlyWithoutFailures()
        {
            AutoTestReport r = new AutoTestReport();
            r.Checks.Add(new AutoCheck("a", AutoStatus.Ok, "ok"));
            r.Checks.Add(new AutoCheck("b", AutoStatus.Pulado, "pulou"));
            Assert.Equal(0, ToolExit.ForAuto(r));
            r.Checks.Add(new AutoCheck("x", AutoStatus.Falhou, "ruim"));
            Assert.Equal(1, ToolExit.ForAuto(r));
            AutoTestReport cut = new AutoTestReport();
            cut.Aborted = true;
            Assert.Equal(1, ToolExit.ForAuto(cut));
            Assert.Equal(1, ToolExit.ForAuto(null));
            Assert.True(ToolExit.OtherInstance != 0 && ToolExit.Declined != 0 && ToolExit.OtherInstance != ToolExit.Declined, "distinct nonzero codes");
        }

        [Test]
        public static void Args_RamCleanIsOptInAndNeverImpliedByAuto()
        {
            Assert.True(!ToolArgs.WithRamClean(new[] { "x.exe", "--autoteste" }), "off by default");
            Assert.True(!ToolArgs.WithRamClean(new[] { "x.exe", "--autoteste", "--auto" }), "--auto does not imply it");
            Assert.True(ToolArgs.WithRamClean(new[] { "x.exe", "--autoteste", "--com-limpeza-ram" }), "explicit flag");
            Assert.True(ToolArgs.WithRamClean(new[] { "x.exe", "--COM-LIMPEZA-RAM" }), "case-insensitive");
            Assert.True(!ToolArgs.WithRamClean(new[] { "--com-limpeza-ram" }), "args[0] never counts");
        }

        // ---- preflight ----

        [Test]
        public static void Preflight_QuietCpuOnAc_Allows()
        {
            PreflightResult r = PerfPlan.Preflight(false, null, 3.5, true);
            Assert.True(r.Allowed && r.Refusal == null && r.Warning == null, "clean start");
            Assert.True(PerfPlan.Preflight(false, null, 10.0, false).Allowed, "exactly 10 % is still allowed");
        }

        [Test]
        public static void Preflight_BackgroundLoadOverTenPercent_Refuses_EvenInteractive()
        {
            PreflightResult r = PerfPlan.Preflight(false, null, 10.1, false);
            Assert.True(!r.Allowed, "refused");
            Assert.True(r.Refusal.Contains("outros programas") && r.Refusal.Contains("10"), r.Refusal);
            Assert.True(!PerfPlan.Preflight(false, null, 35, true).Allowed, "also unattended");
        }

        [Test]
        public static void Preflight_Battery_Rules()
        {
            Assert.True(!PerfPlan.Preflight(true, 49, 2, false).Allowed, "below 50 % refuses");
            PreflightResult warn = PerfPlan.Preflight(true, 50, 2, false);
            Assert.True(warn.Allowed && warn.Warning != null && warn.Warning.Contains("bateria"), "50 % or more: warn and go on");
            PreflightResult auto = PerfPlan.Preflight(true, 95, 2, true);
            Assert.True(!auto.Allowed && auto.Refusal.Contains("--auto"), "--auto needs AC even at 95 %");
            Assert.True(!PerfPlan.Preflight(true, null, 2, false).Allowed, "battery with unknown charge refuses");
        }

        [Test]
        public static void Preflight_UnknownPowerOrLoad_RefusesUnattendedWarnsInteractive()
        {
            Assert.True(!PerfPlan.Preflight(null, null, 2, true).Allowed, "cannot confirm AC in --auto");
            PreflightResult a = PerfPlan.Preflight(null, null, 2, false);
            Assert.True(a.Allowed && a.Warning != null, "interactive: warn");
            Assert.True(!PerfPlan.Preflight(false, null, null, true).Allowed, "cannot measure the background in --auto");
            PreflightResult b = PerfPlan.Preflight(false, null, null, false);
            Assert.True(b.Allowed && b.Warning != null, "interactive: warn");
        }

        [Test]
        public static void PowerAbort_CableOutOrLowBattery()
        {
            string why;
            PerfPower ac = new PerfPower(); ac.OnBattery = false;
            PerfPower bat = new PerfPower(); bat.OnBattery = true; bat.Percent = 80;
            PerfPower low = new PerfPower(); low.OnBattery = true; low.Percent = 29;
            Assert.True(!PerfPlan.PowerAbort(false, ac, out why), "still on AC");
            Assert.True(PerfPlan.PowerAbort(false, bat, out why) && why.Contains("cabo"), "cable pulled: " + why);
            Assert.True(!PerfPlan.PowerAbort(true, bat, out why), "started on battery at 80 %");
            Assert.True(PerfPlan.PowerAbort(true, low, out why) && why.Contains("30"), "battery under 30 %");
            Assert.True(!PerfPlan.PowerAbort(false, null, out why), "unknown reading is no change");
        }

        [Test]
        public static void Plan_CooldownTarget_IsBaselinePlusFour()
        {
            Assert.Equal(49.0, PerfPlan.CooldownTarget(45));
        }

        [Test]
        public static void Plan_SafeToStartLoad_RejectsHotOrUnreadable()
        {
            Assert.True(PerfPlan.SafeToStartLoad(55), "cool enough");
            Assert.True(!PerfPlan.SafeToStartLoad(80), "80 is too warm");
            Assert.True(!PerfPlan.SafeToStartLoad(null), "unreadable");
        }

        [Test]
        public static void Plan_MustStopLoad_NinetyOnEitherSensor()
        {
            string why;
            Assert.True(!PerfPlan.MustStopLoad(89.9, 80, out why), "below the limit");
            Assert.True(PerfPlan.MustStopLoad(90, 60, out why), "cpu 90");
            Assert.True(why.Contains("90"), "reason names the limit");
            Assert.True(PerfPlan.MustStopLoad(70, 91, out why), "gpu 91");
            Assert.True(!PerfPlan.MustStopLoad(70, null, out why), "gpu is not loaded by the test; its absence is tolerated");
            Assert.True(!PerfPlan.MustStopLoad(null, 50, out why), "a missing cpu reading is counted by the caller (3 in a row)");
            Assert.Equal(3, PerfPlan.BlindSamplesLimit);
        }

        [Test]
        public static void Plan_CooldownIsNeverShortened_ForAnyRun()
        {
            Assert.Equal(90, PerfPlan.CooldownMaxSec);
        }

        [Test]
        public static void Plan_RemainingSec_CountsPhasesAndFutureRuns()
        {
            // start of the first run's load: 60 + 20 left here, three more runs of 45 + 95
            Assert.Equal(80 + 3 * 140, PerfPlan.RemainingSec(0, 4, PerfPhase.Multi, 0));
            // last run, 5 s into the single-core load
            Assert.Equal(15, PerfPlan.RemainingSec(3, 4, PerfPhase.Single, 5));
            // never negative
            Assert.Equal(0, PerfPlan.RemainingSec(3, 4, PerfPhase.Single, 99));
        }

        // ---- journal ----

        [Test]
        public static void Journal_Line_IsOneSanitizedLine()
        {
            string l = PerfJournal.Line(new DateTime(2026, 11, 17, 3, 0, 5, DateTimeKind.Utc), "FASE", "Silencioso | carga\nem todos");
            Assert.Equal("2026-11-17T03:00:05Z|FASE|Silencioso / carga em todos", l);
        }

        [Test]
        public static void Journal_CrashNote_NullWhenTheLastTestFinished()
        {
            string[] done = { "2026-11-17T03:00:00Z|INICIO|ordem", "2026-11-17T03:00:10Z|FASE|Silencioso repouso", "2026-11-17T03:09:00Z|FIM|completo" };
            Assert.Null(PerfJournal.CrashNote(done));
            Assert.Null(PerfJournal.CrashNote(new string[0]));
            Assert.Null(PerfJournal.CrashNote(null));
        }

        [Test]
        public static void Journal_CrashNote_NamesTheLastPhase()
        {
            string[] crashed = { "2026-11-17T03:00:00Z|INICIO|ordem", "2026-11-17T03:00:10Z|FASE|Desempenho carga em todos os núcleos (cpu 61 °C)", "2026-11-17T03:03:00Z|AMOSTRA|Desempenho carga t=40 s cpu 88 °C" };
            string note = PerfJournal.CrashNote(crashed);
            Assert.True(note != null && note.Contains("queda") && note.Contains("Desempenho carga t=40 s cpu 88"), note);
            Assert.True(note.Contains("03:03:00"), "says when the last line was written: " + note);
        }

        [Test]
        public static void Journal_CrashNote_OnlyTheLatestTestCounts()
        {
            string[] twoRuns = { "a|INICIO|x", "a|FASE|velha", "a|FIM|completo", "b|INICIO|y", "b|FASE|nova" };
            Assert.True(PerfJournal.CrashNote(twoRuns).Contains("nova"), "the open test");
            string[] crashedThenFinished = { "a|INICIO|x", "a|FASE|velha", "b|INICIO|y", "b|FIM|completo" };
            Assert.Null(PerfJournal.CrashNote(crashedThenFinished));
        }

        [Test]
        public static void Journal_Trim_KeepsTheTail()
        {
            List<string> l = new List<string> { "1", "2", "3", "4", "5" };
            Assert.Equal("4,5", string.Join(",", PerfJournal.Trim(l, 2).ToArray()));
            Assert.Equal(5, PerfJournal.Trim(l, 10).Count);
        }

        // ---- config read without side effects ----

        [Test]
        public static void ConfigTryLoad_MissingOrCorrupt_NeverWritesTheFile()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-cfg-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string path = System.IO.Path.Combine(dir, "config.json");
                Config c;
                Assert.Equal(ConfigRead.Missing, Config.TryLoad(path, out c));
                Assert.True(c == null, "no config");
                Assert.True(!System.IO.File.Exists(path), "a missing file stays missing");

                System.IO.File.WriteAllText(path, "{ \"FanMode\": \"Au");
                Assert.Equal(ConfigRead.Corrupt, Config.TryLoad(path, out c));
                Assert.Equal("{ \"FanMode\": \"Au", System.IO.File.ReadAllText(path));
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        [Test]
        public static void ConfigTryLoad_ReadsAValidFileWithoutRewritingIt()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-cfg-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string path = System.IO.Path.Combine(dir, "config.json");
                Config seed = new Config();
                seed.FanControlValidated = true;
                seed.FanMaxRpmCpu = 5000;
                seed.Save(path);
                DateTime stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                System.IO.File.SetLastWriteTimeUtc(path, stamp);
                Config c;
                Assert.Equal(ConfigRead.Ok, Config.TryLoad(path, out c));
                Assert.True(c.FanControlValidated && c.FanMaxRpmCpu == 5000, "values read");
                Assert.Equal(stamp, System.IO.File.GetLastWriteTimeUtc(path));
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        // ---- power plan ----

        [Test]
        public static void PowerScheme_Parse_EnglishAndPortuguese()
        {
            string g, n;
            Assert.True(PerfPowerScheme.Parse("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)", out g, out n), "english");
            Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", g);
            Assert.Equal("Balanced", n);
            Assert.True(PerfPowerScheme.Parse("GUID do Esquema de Energia: A1841308-3541-4FAB-BC81-F71556F20B4A  (Economia de energia)", out g, out n), "portuguese");
            Assert.Equal(PerfPowerScheme.SaverGuid, g);
            Assert.Equal("Economia de energia", n);
            Assert.True(!PerfPowerScheme.Parse("", out g, out n), "empty");
            Assert.True(!PerfPowerScheme.Parse("sem guid aqui", out g, out n), "no guid");
        }

        [Test]
        public static void PowerScheme_IsSaver_ByGuidThenByName()
        {
            Assert.True(PerfPowerScheme.IsSaver(PerfPowerScheme.SaverGuid, "Balanced"), "guid wins");
            Assert.True(PerfPowerScheme.IsSaver(null, "Economia de energia"), "name in portuguese");
            Assert.True(PerfPowerScheme.IsSaver("x", "Power saver"), "name in english");
            Assert.True(!PerfPowerScheme.IsSaver("381b4222-f694-41f0-9685-ff5bb260df2e", "Equilibrado"), "balanced is not a saver");
            Assert.True(!PerfPowerScheme.IsSaver(null, null), "unknown");
        }

        // ---- kernel ----

        [Test]
        public static void Kernel_IsDeterministicAndDependsOnState()
        {
            Assert.Equal(PerfKernel.Unit(12345UL), PerfKernel.Unit(12345UL));
            Assert.True(PerfKernel.Unit(1UL) != PerfKernel.Unit(2UL), "different input, different result");
            // chained, as the load does it
            ulong a = 7, b = 7;
            for (int i = 0; i < 5; i++) { a = PerfKernel.Unit(a); b = PerfKernel.Unit(b); }
            Assert.Equal(a, b);
        }

        [Test]
        public static void Kernel_Golden_SameOnEveryMachine()
        {
            Assert.Equal(GoldenUnit12345, PerfKernel.Unit(12345UL));
        }

        private const ulong GoldenUnit12345 = 9503594241456211647UL;

        // ---- scoring ----

        // 60 s of multi-core samples: `fast` units/s for the first 10 s, then `slow`.
        private static PerfModeRun MultiRun(double fast, double slow, int seconds)
        {
            PerfModeRun run = new PerfModeRun();
            run.Applied = true;
            double u = 0;
            for (int t = 1; t <= seconds; t++)
            {
                u += t <= 10 ? fast : slow;
                PerfSample s = new PerfSample();
                s.Phase = PerfPhase.Multi;
                s.T = t;
                s.Units = (long)u;
                s.CpuTempC = 60 + t / 10.0;
                s.GpuTempC = 50;
                s.CpuRpm = 3000;
                s.GpuRpm = 3200;
                s.ClockMHz = 3000;
                run.Samples.Add(s);
            }
            return run;
        }

        [Test]
        public static void Scoring_BurstIsTheFirstTenSeconds_SustainedTheLastFortyFive()
        {
            PerfModeRun run = MultiRun(1000, 500, 60);
            PerfStats st = PerfScoring.Summarize(run);
            Assert.True(st.MultiComplete, "60 s ran");
            Assert.Equal(1000.0, Math.Round(st.Burst.Value, 3));
            Assert.Equal(500.0, Math.Round(st.MultiSustained.Value, 3));
        }

        [Test]
        public static void Scoring_Single_AveragesTheWholeLoad()
        {
            PerfModeRun run = new PerfModeRun();
            for (int t = 1; t <= 20; t++)
            {
                PerfSample s = new PerfSample();
                s.Phase = PerfPhase.Single;
                s.T = t;
                s.Units = t * 250L;
                run.Samples.Add(s);
            }
            PerfStats st = PerfScoring.Summarize(run);
            Assert.True(st.SingleComplete, "complete");
            Assert.Equal(250.0, Math.Round(st.Single.Value, 3));
        }

        [Test]
        public static void Scoring_InterruptedLoad_IsMarkedIncomplete()
        {
            PerfStats st = PerfScoring.Summarize(MultiRun(1000, 500, 30));
            Assert.True(!st.MultiComplete, "only 30 s");
            Assert.True(st.MultiSustained.HasValue, "still has a rate over what ran");
            Assert.True(PerfScoring.Summarize(MultiRun(1000, 500, 3)).MultiSustained == null, "3 s is not enough for a score");
        }

        [Test]
        public static void Scoring_UnitsAt_InterpolatesFromZero()
        {
            List<PerfSample> s = new List<PerfSample>();
            PerfSample a = new PerfSample(); a.T = 2; a.Units = 200; s.Add(a);
            PerfSample b = new PerfSample(); b.T = 4; b.Units = 600; s.Add(b);
            Assert.Equal(100.0, PerfScoring.UnitsAt(s, 1));
            Assert.Equal(400.0, PerfScoring.UnitsAt(s, 3));
            Assert.Equal(600.0, PerfScoring.UnitsAt(s, 9));
        }

        [Test]
        public static void Scoring_Averages_PeakAndMeanRpm()
        {
            PerfStats st = PerfScoring.Summarize(MultiRun(1000, 500, 60));
            Assert.Equal(3000, st.AvgCpuRpm.Value);
            Assert.Equal(3200, st.AvgGpuRpm.Value);
            Assert.Equal(3100, st.AvgRpm.Value);
            Assert.Equal(66.0, Math.Round(st.PeakCpuTemp.Value, 3));
            Assert.Equal(50.0, st.PeakGpuTemp.Value);
            Assert.Equal(3000.0, st.AvgClockMulti.Value);
            Assert.True(st.AvgDischargeMw == null, "no battery reading stays null");
        }

        [Test]
        public static void Scoring_LateCpuTemp_IsTheMeanOfTheLastThirtySeconds()
        {
            PerfModeRun run = MultiRun(1000, 500, 60); // temp = 60 + t/10 -> last 30 s are t = 31..60
            PerfStats st = PerfScoring.Summarize(run);
            Assert.Equal(60 + 45.5 / 10, Math.Round(st.LateCpuTemp.Value, 6)); // mean t = 45.5
            Assert.True(st.LateCpuTemp.Value < st.PeakCpuTemp.Value, "late mean is below the single hottest reading");
        }

        [Test]
        public static void Scoring_IdleCpuLoad_IsTheMeanOfTheIdlePhase()
        {
            PerfModeRun run = new PerfModeRun();
            for (int t = 1; t <= 15; t++)
            {
                PerfSample s = new PerfSample();
                s.Phase = PerfPhase.Idle;
                s.T = t;
                s.CpuLoadPct = t <= 5 ? 10 : 2;
                run.Samples.Add(s);
            }
            Assert.Equal(Math.Round((5 * 10 + 10 * 2) / 15.0, 6), Math.Round(PerfScoring.Summarize(run).IdleCpuLoad.Value, 6));
        }

        [Test]
        public static void Scoring_MeanRpm_UsesTheFanThatReads()
        {
            Assert.Equal(2000, PerfScoring.MeanRpm(2000, null).Value);
            Assert.Equal(3000, PerfScoring.MeanRpm(null, 3000).Value);
            Assert.True(PerfScoring.MeanRpm(null, null) == null, "none");
        }
    }
}
