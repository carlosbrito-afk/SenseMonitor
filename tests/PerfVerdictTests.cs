using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The verdict: each mode against Dia a dia, within +-3 % = no measurable difference.
    public static class PerfVerdictTests
    {
        private static PerfModeRun Run(PerfMode m, double sustained, int rpm, double peakTemp)
        {
            PerfModeRun r = new PerfModeRun();
            r.Mode = m;
            r.Applied = true;
            r.CooldownReached = true;
            PerfStats s = new PerfStats();
            s.MultiComplete = true;
            s.SingleComplete = true;
            s.MultiSustained = sustained;
            s.Burst = sustained;
            s.Single = sustained / 4;
            s.AvgCpuRpm = rpm;
            s.AvgGpuRpm = rpm;
            s.IdleCpuRpm = rpm / 2;
            s.IdleGpuRpm = rpm / 2;
            s.PeakCpuTemp = peakTemp;
            r.Stats = s;
            return r;
        }

        private static PerfTestReport Report(PerfModeRun sil, PerfModeRun des, PerfModeRun dia)
        {
            PerfTestReport rep = new PerfTestReport();
            if (sil != null) rep.Runs.Add(sil);
            if (des != null) rep.Runs.Add(des);
            if (dia != null) rep.Runs.Add(dia);
            return rep;
        }

        private static string All(PerfVerdictResult v)
        {
            return string.Join("\n", v.Lines.ToArray());
        }

        [Test]
        public static void Verdict_Pct_UsesARealMinusAndWholeNumbers()
        {
            Assert.Equal("+5 %", PerfVerdict.Pct(5.2));
            Assert.Equal("−1 %", PerfVerdict.Pct(-1.2));
            Assert.Equal("0 %", PerfVerdict.Pct(0.3));
            Assert.Equal("0 %", PerfVerdict.Pct(-0.4));
        }

        [Test]
        public static void Verdict_Diff_NullWhenNothingToCompare()
        {
            Assert.Equal(10.0, Math.Round(PerfVerdict.Diff(110, 100).Value, 6));
            Assert.True(PerfVerdict.Diff(null, 100) == null, "no value");
            Assert.True(PerfVerdict.Diff(100, 0) == null, "zero reference");
        }

        [Test]
        public static void Verdict_Desempenho_Faster_Works()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(null, Run(PerfMode.Desempenho, 1100, 4500, 85), Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Works, v.Desempenho);
            Assert.True(All(v).Contains("+10 %"), "says how much faster: " + All(v));
        }

        [Test]
        public static void Verdict_Desempenho_BandEdges()
        {
            PerfVerdictResult inside = PerfVerdict.Build(Report(null, Run(PerfMode.Desempenho, 1029, 4000, 80), Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.NoEffect, inside.Desempenho);
            PerfVerdictResult edge = PerfVerdict.Build(Report(null, Run(PerfMode.Desempenho, 1030, 4000, 80), Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Works, edge.Desempenho);
        }

        [Test]
        public static void Verdict_Desempenho_NoGain_FlagsThePlainContradiction()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(null, Run(PerfMode.Desempenho, 990, 4000, 80), Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.NoEffect, v.Desempenho);
            string t = All(v);
            Assert.True(t.Contains("Desempenho não rendeu mais que Dia a dia (−1 %)"), t);
            Assert.True(t.Contains("sem diferença mensurável"), t);
            Assert.True(t.Contains("o modo não está fazendo efeito neste notebook / plano de energia limitando"), t);
        }

        [Test]
        public static void Verdict_Desempenho_SameScoreButCooler_CountsAsHeadroom()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(null, Run(PerfMode.Desempenho, 1000, 4000, 77), Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Works, v.Desempenho);
            Assert.True(All(v).Contains("mais folga térmica"), All(v));
        }

        [Test]
        public static void Verdict_Desempenho_Slower_IsAContradiction()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(null, Run(PerfMode.Desempenho, 920, 4000, 80), Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Contradiction, v.Desempenho);
            Assert.True(All(v).Contains("MENOS") && All(v).Contains("−8 %"), All(v));
        }

        [Test]
        public static void Verdict_Silencioso_LowerScoreAndLowerRpm_Works()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(Run(PerfMode.Silencioso, 850, 3000, 75), null, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Works, v.Silencioso);
            Assert.True(All(v).Contains("ventoinhas mais lentas"), All(v));
        }

        [Test]
        public static void Verdict_Silencioso_SameScoreQuieter_Works()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(Run(PerfMode.Silencioso, 1000, 3500, 80), null, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Works, v.Silencioso);
        }

        [Test]
        public static void Verdict_Silencioso_NothingChanged_HasNoEffect()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(Run(PerfMode.Silencioso, 1010, 4000, 80), null, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.NoEffect, v.Silencioso);
            Assert.True(All(v).Contains("não mudou nada"), All(v));
        }

        [Test]
        public static void Verdict_Silencioso_FasterThanDiaADia_IsAContradiction()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(Run(PerfMode.Silencioso, 1100, 3000, 70), null, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.Contradiction, v.Silencioso);
        }

        [Test]
        public static void Verdict_Silencioso_SlowerButFansUnchanged_IsNoEffect()
        {
            // the promise of the mode is silence: slower without quieter fans is not "works"
            PerfVerdictResult v = PerfVerdict.Build(Report(Run(PerfMode.Silencioso, 800, 4000, 80), null, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.NoEffect, v.Silencioso);
            Assert.True(All(v).Contains("não baixou a rotação"), All(v));
        }

        // ---- balanced order: two Dia a dia runs ----

        private static PerfTestReport Balanced(double dia1, double dia2, double sil, double des)
        {
            PerfModeRun d1 = Run(PerfMode.DiaADia, dia1, 4000, 80);
            d1.Order = 0;
            PerfModeRun s = Run(PerfMode.Silencioso, sil, 3000, 78);
            s.Order = 1;
            PerfModeRun d = Run(PerfMode.Desempenho, des, 4500, 82);
            d.Order = 2;
            PerfModeRun d2 = Run(PerfMode.DiaADia, dia2, 4000, 80);
            d2.Order = 3;
            PerfTestReport rep = new PerfTestReport();
            rep.Runs.Add(d1);
            rep.Runs.Add(s);
            rep.Runs.Add(d);
            rep.Runs.Add(d2);
            return rep;
        }

        [Test]
        public static void Verdict_Reference_IsTheMeanOfTheTwoDiaADiaRuns()
        {
            PerfTestReport rep = Balanced(1000, 960, 800, 1200);
            PerfModeRun reference = PerfVerdict.Reference(rep);
            Assert.Equal(980.0, Math.Round(reference.Stats.MultiSustained.Value, 6));
            // 1200 vs the mean 980 = +22 %
            PerfVerdictResult v = PerfVerdict.Build(rep);
            Assert.Equal(ModeEffect.Works, v.Desempenho);
            Assert.True(All(v).Contains("+22 %"), All(v));
        }

        [Test]
        public static void Verdict_Reference_FallsBackToTheOneValidRun()
        {
            PerfTestReport rep = Balanced(1000, 960, 800, 1200);
            rep.Runs[3].Aborted = true;
            rep.Runs[3].AbortReason = "temperatura chegou a 92 °C (limite 90 °C)";
            PerfModeRun reference = PerfVerdict.Reference(rep);
            Assert.Equal(1000.0, reference.Stats.MultiSustained.Value);
            Assert.True(PerfVerdict.Drift(rep) == null, "no dispersion with one valid run");
            Assert.True(All(PerfVerdict.Build(rep)).Contains("Só uma das duas medidas"), All(PerfVerdict.Build(rep)));
        }

        [Test]
        public static void Verdict_Drift_IsTheGapBetweenTheTwoDiaADiaRuns()
        {
            PerfTestReport rep = Balanced(1000, 940, 800, 1200);
            Assert.Equal(6.2, Math.Round(PerfVerdict.Drift(rep).Value, 1));
            PerfVerdictResult v = PerfVerdict.Build(rep);
            Assert.True(v.BandPct > 6.1 && v.BandPct < 6.3, "band widened to the drift: " + v.BandPct);
            Assert.True(All(v).Contains("diferem 6 %"), All(v));
        }

        [Test]
        public static void Verdict_DifferenceInsideTheDrift_IsNotAffirmed()
        {
            // drift 10 %: a +5 % Desempenho is inside the spread of the same mode
            PerfTestReport rep = Balanced(1050, 950, 800, 1050);
            PerfVerdictResult v = PerfVerdict.Build(rep);
            Assert.Equal(ModeEffect.NoEffect, v.Desempenho);
            Assert.True(All(v).Contains("faixa de 10 %"), All(v));
            // and a difference beyond it is
            PerfTestReport big = Balanced(1050, 950, 800, 1250);
            Assert.Equal(ModeEffect.Works, PerfVerdict.Build(big).Desempenho);
        }

        [Test]
        public static void Verdict_SmallDrift_KeepsTheThreePercentBand()
        {
            PerfVerdictResult v = PerfVerdict.Build(Balanced(1000, 990, 800, 1200));
            Assert.Equal(3.0, v.BandPct);
            Assert.True(!All(v).Contains("diferem"), "no drift warning under 3 %");
        }

        [Test]
        public static void Verdict_DifferentStartTemperatures_DisableTemperatureClaims()
        {
            PerfTestReport rep = Balanced(1000, 1000, 800, 1000);
            // Desempenho runs 5 degrees cooler (would be "more headroom"), but started 6 degrees colder too
            rep.Runs[2].Stats.LateCpuTemp = 70;
            rep.Runs[0].Stats.LateCpuTemp = 80;
            rep.Runs[3].Stats.LateCpuTemp = 80;
            rep.Runs[0].StartTempC = 55;
            rep.Runs[2].StartTempC = 49;
            foreach (PerfModeRun r in rep.Runs) { if (r.Samples.Count == 0) r.Samples.Add(new PerfSample()); if (!r.StartTempC.HasValue) r.StartTempC = 52; }
            PerfVerdictResult v = PerfVerdict.Build(rep);
            Assert.Equal(ModeEffect.NoEffect, v.Desempenho);
            Assert.True(All(v).Contains("temperaturas diferentes"), All(v));
        }

        [Test]
        public static void Verdict_BackgroundLoadWhileIdle_InvalidatesEverything()
        {
            PerfTestReport rep = Balanced(1000, 1000, 800, 1200);
            rep.Runs[1].Stats.IdleCpuLoad = 40;
            PerfVerdictResult v = PerfVerdict.Build(rep);
            Assert.True(!v.Reliable, "unreliable");
            Assert.Equal(ModeEffect.NotEvaluated, v.Desempenho);
            Assert.Equal(ModeEffect.NotEvaluated, v.Silencioso);
            Assert.True(All(v).Contains("Veredito invalidado") && All(v).Contains("Silencioso"), All(v));
        }

        [Test]
        public static void Verdict_RunLabels_NumberTheTwoDiaADiaRuns()
        {
            PerfTestReport rep = Balanced(1000, 1000, 800, 1200);
            Assert.Equal("Dia a dia (1ª)", PerfVerdict.RunLabel(rep, rep.Runs[0]));
            Assert.Equal("Dia a dia (2ª)", PerfVerdict.RunLabel(rep, rep.Runs[3]));
            Assert.Equal("Silencioso", PerfVerdict.RunLabel(rep, rep.Runs[1]));
        }

        [Test]
        public static void Verdict_TemperatureComparison_UsesTheLateMeanNotTheSinglePeak()
        {
            // peaks say "3 degrees cooler" but the late mean says it is the same: not headroom
            PerfTestReport rep = Report(null, Run(PerfMode.Desempenho, 1000, 4000, 77), Run(PerfMode.DiaADia, 1000, 4000, 80));
            rep.Runs[0].Stats.LateCpuTemp = 79.5;
            rep.Find(PerfMode.DiaADia).Stats.LateCpuTemp = 80;
            Assert.Equal(ModeEffect.NoEffect, PerfVerdict.Build(rep).Desempenho);
        }

        [Test]
        public static void Verdict_ModeNotApplied_IsNotComparedAndSaysWhy()
        {
            PerfModeRun des = Run(PerfMode.Desempenho, 1500, 4000, 80);
            des.Applied = false;
            PerfVerdictResult v = PerfVerdict.Build(Report(null, des, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.NotEvaluated, v.Desempenho);
            Assert.True(All(v).Contains("não confirmou a troca de modo"), All(v));
        }

        [Test]
        public static void Verdict_AbortedMode_IsNotCompared()
        {
            PerfModeRun des = Run(PerfMode.Desempenho, 1500, 4000, 92);
            des.Aborted = true;
            des.AbortReason = "temperatura chegou a 92 °C (limite 90 °C)";
            PerfVerdictResult v = PerfVerdict.Build(Report(null, des, Run(PerfMode.DiaADia, 1000, 4000, 80)));
            Assert.Equal(ModeEffect.NotEvaluated, v.Desempenho);
            Assert.True(All(v).Contains("92 °C"), All(v));
        }

        [Test]
        public static void Verdict_NoDiaADiaReference_NothingIsCompared()
        {
            PerfVerdictResult v = PerfVerdict.Build(Report(Run(PerfMode.Silencioso, 800, 3000, 70), Run(PerfMode.Desempenho, 1200, 5000, 85), null));
            Assert.Equal(ModeEffect.NotEvaluated, v.Desempenho);
            Assert.Equal(ModeEffect.NotEvaluated, v.Silencioso);
            Assert.True(All(v).Contains("Sem medida completa do Dia a dia"), All(v));
        }

        [Test]
        public static void Verdict_PowerSaverPlan_IsCalledOut()
        {
            PerfTestReport rep = Report(null, Run(PerfMode.Desempenho, 1000, 4000, 80), Run(PerfMode.DiaADia, 1000, 4000, 80));
            rep.Pre.PowerSchemeGuid = PerfPowerScheme.SaverGuid;
            rep.Pre.PowerSchemeName = "Economia de energia";
            Assert.True(All(PerfVerdict.Build(rep)).Contains("plano de energia") && All(PerfVerdict.Build(rep)).Contains("limitando todos os modos"), All(PerfVerdict.Build(rep)));
        }

        [Test]
        public static void Verdict_OnBattery_IsWarnedAbout()
        {
            PerfTestReport rep = Report(null, Run(PerfMode.Desempenho, 1000, 4000, 80), Run(PerfMode.DiaADia, 1000, 4000, 80));
            rep.Pre.OnBattery = true;
            Assert.True(All(PerfVerdict.Build(rep)).Contains("bateria"), All(PerfVerdict.Build(rep)));
            rep.Pre.OnBattery = false;
            Assert.True(!All(PerfVerdict.Build(rep)).Contains("bateria"), "on AC: no battery line");
        }

        // ---- report text and JSON ----

        private static PerfTestReport Sample()
        {
            PerfTestReport rep = Report(Run(PerfMode.Silencioso, 850, 3000, 75), Run(PerfMode.Desempenho, 1100, 4500, 85), Run(PerfMode.DiaADia, 1000, 4000, 80));
            rep.StartedUtc = new DateTime(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc);
            rep.TotalSec = 400;
            rep.FinalModeOk = true;
            rep.FinalFansAuto = true;
            rep.Pre.CpuName = "Intel \"Core\" i7";
            rep.Pre.PowerSource = "tomada";
            rep.Pre.OnBattery = false;
            rep.Pre.PowerSchemeName = "Equilibrado";
            PerfSample s = new PerfSample();
            s.Phase = PerfPhase.Multi;
            s.T = 1;
            s.Units = 1000;
            s.CpuTempC = 70.5;
            rep.Runs[0].Samples.Add(s);
            return rep;
        }

        [Test]
        public static void Report_Table_ListsEveryModeAndTheScores()
        {
            PerfTestReport rep = Sample();
            string t = PerfReport.Table(rep);
            Assert.True(t.Contains("Silencioso") && t.Contains("Desempenho") && t.Contains("Dia a dia"), t);
            Assert.True(t.Contains("1100,0"), "pt-BR decimal comma: " + t);
            Assert.True(t.Contains("Multi 60 s") && t.Contains("turbo"), "honest label for the 60 s window: " + t);
            Assert.True(t.Contains("Clock aprox."), "clock is labelled approximate: " + t);
        }

        [Test]
        public static void Report_Table_ShowsBothDiaADiaRunsAndTheirMean()
        {
            PerfTestReport rep = Balanced(1000, 960, 800, 1200);
            string t = PerfReport.Table(rep);
            Assert.True(t.Contains("Dia a dia (1ª)") && t.Contains("Dia a dia (2ª)") && t.Contains("Dia a dia (média)"), t);
            Assert.True(t.Contains("980,0"), "the mean: " + t);
        }

        [Test]
        public static void Report_Summary_HasBackgroundLoadStartTempsAndAnEarlierCrash()
        {
            PerfTestReport rep = Sample();
            rep.Pre.BackgroundCpuPct = 4.2;
            rep.Pre.BatteryPercent = 88;
            rep.Pre.AppVersion = "1.2.3.4";
            rep.PreviousCrashNote = "o último teste terminou numa queda na fase \"Desempenho carga\"";
            rep.Runs[0].StartTempC = 51;
            string t = PerfReport.Summary(rep, PerfVerdict.Build(rep));
            Assert.True(t.Contains("4,2 %"), "background load: " + t);
            Assert.True(t.Contains("ATENÇÃO") && t.Contains("queda"), t);
            Assert.True(t.Contains("1.2.3.4"), t);
            Assert.True(t.Contains("CPU ao começar a carga 51"), t);
        }

        [Test]
        public static void Report_Json_HasConditionsAndPerRunStartTemperature()
        {
            PerfTestReport rep = Sample();
            rep.Pre.BackgroundCpuPct = 4.2;
            rep.Pre.AppVersion = "1.2.3.4";
            rep.Runs[0].StartTempC = 51;
            rep.Runs[0].CooldownStartTempC = 66;
            string j = PerfReport.Json(rep, PerfVerdict.Build(rep));
            Assert.True(j.Contains("\"backgroundCpuPct\":4.2") && j.Contains("\"appVersion\":\"1.2.3.4\""), j);
            Assert.True(j.Contains("\"startTempC\":51") && j.Contains("\"cooldownStartTempC\":66"), j);
            Assert.True(j.Contains("\"restoreOk\":true") && j.Contains("\"reliable\":true"), j);
        }

        [Test]
        public static void Report_Summary_HasPreconditionsVerdictAndGpuNote()
        {
            PerfTestReport rep = Sample();
            string t = PerfReport.Summary(rep, PerfVerdict.Build(rep));
            Assert.True(t.Contains("GPU: sem carga de teste (só temperatura)"), t);
            Assert.True(t.Contains("Plano de energia: Equilibrado"), t);
            Assert.True(t.Contains("Uso de CPU de fundo antes do teste: não medido"), t);
            Assert.True(t.Contains("Veredito:"), t);
            Assert.True(t.Contains("modo Dia a dia confirmado"), t);
            Assert.True(t.Contains("Automático"), t);
        }

        [Test]
        public static void Report_FullText_AppendsRawSamples()
        {
            PerfTestReport rep = Sample();
            string t = PerfReport.FullText(rep, PerfVerdict.Build(rep));
            Assert.True(t.Contains("Amostras por segundo"), t);
            Assert.True(t.Contains("Silencioso;Multi;1.00;70.5;;;;;;;1000"), "invariant numbers, empty for null: " + t);
        }

        [Test]
        public static void Report_Json_EscapesAndUsesNull()
        {
            PerfTestReport rep = Sample();
            string j = PerfReport.Json(rep, PerfVerdict.Build(rep));
            Assert.True(j.StartsWith("{") && j.EndsWith("}"), "object");
            Assert.True(j.Contains("\"cpuName\":\"Intel \\\"Core\\\" i7\""), "quotes escaped: " + j);
            Assert.True(j.Contains("\"avgDischargeMw\":null"), "missing = null");
            Assert.True(j.Contains("\"multiSustained\":850"), "invariant number: " + j);
            Assert.True(j.Contains("\"finalModeOk\":true"), j);
            Assert.True(j.Contains("\"mode\":\"Desempenho\""), j);
        }

        [Test]
        public static void Report_JsonStr_EscapesControlCharacters()
        {
            Assert.Equal("\"a\\nb\\\\c\\u0001\"", PerfReport.Str("a\nb\\c\u0001"));
            Assert.Equal("null", PerfReport.Str(null));
            Assert.Equal("null", PerfReport.Num((double?)double.NaN));
        }
    }
}
