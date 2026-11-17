using System;
using Forja.Core;

namespace Forja.Tests
{
    // The per-mode numbers of the last teste-desempenho.json, for the Modes page.
    public static class PerfScoreboardTests
    {
        private static PerfModeRun Run(PerfMode m, double sustained, double clock, double lateTemp, int rpm)
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
            s.AvgClockMulti = clock;
            s.LateCpuTemp = lateTemp;
            s.AvgCpuTemp = lateTemp;
            s.PeakCpuTemp = lateTemp + 3;
            s.AvgCpuRpm = rpm;
            s.AvgGpuRpm = rpm;
            r.Stats = s;
            return r;
        }

        // The real writer (PerfReport.Json) feeds the real reader: if the file format changes,
        // this breaks.
        private static string FileJson()
        {
            PerfTestReport rep = new PerfTestReport();
            rep.StartedUtc = new DateTime(2026, 11, 16, 17, 5, 0, DateTimeKind.Utc);
            rep.Runs.Add(Run(PerfMode.Silencioso, 100, 2400, 70, 2800));
            rep.Runs.Add(Run(PerfMode.Desempenho, 142, 3300, 86, 3900));
            rep.Runs.Add(Run(PerfMode.DiaADia, 118, 3000, 80, 3200));
            rep.Runs.Add(Run(PerfMode.DiaADia, 122, 3000, 82, 3200));
            return PerfReport.Json(rep, PerfVerdict.Build(rep));
        }

        [Test]
        public static void RoundTrip_ModeLinesCarryTheNumbers()
        {
            PerfScoreboard b = PerfScoreboard.Parse(FileJson());
            Assert.True(b.Loaded, "loaded");
            Assert.True(b.Reliable, "reliable");
            ModeScore dia = b.Get(PerfMode.DiaADia);
            Assert.Equal(2, dia.Runs);
            Assert.Equal(120.0, dia.Score.Value);
            Assert.Equal("Pontuação 120,0 (referência) · clock 3.000 MHz · CPU 81 °C · 3.200 rpm", dia.Line);
            ModeScore perf = b.Get(PerfMode.Desempenho);
            Assert.Equal("Pontuação 142,0 (+18 % sobre Dia a dia) · clock 3.300 MHz · CPU 86 °C · 3.900 rpm", perf.Line);
            Assert.Equal(18.0, Math.Round(perf.ScoreVsDiaPct.Value));
            ModeScore sil = b.Get(PerfMode.Silencioso);
            Assert.Equal("Pontuação 100,0 (−17 % sobre Dia a dia) · clock 2.400 MHz · CPU 70 °C · 2.800 rpm", sil.Line);
        }

        [Test]
        public static void RoundTrip_DateIsLocalTime()
        {
            PerfScoreboard b = PerfScoreboard.Parse(FileJson());
            DateTime local = new DateTime(2026, 11, 16, 17, 5, 0, DateTimeKind.Utc).ToLocalTime();
            Assert.Equal("medido em " + local.ToString("dd/MM/yyyy HH:mm", new System.Globalization.CultureInfo("pt-BR")), b.DateText);
        }

        [Test]
        public static void MissingFileOrGarbage_IsNoMeasurement()
        {
            PerfScoreboard a = PerfScoreboard.FromFile(@"C:\this\does\not\exist.json");
            Assert.True(!a.Loaded, "not loaded");
            Assert.Equal("sem medição", a.Get(PerfMode.Desempenho).Line);
            Assert.Equal("sem medição", a.DateText);
            Assert.Equal("sem medição", PerfScoreboard.Parse("{not json").Get(PerfMode.DiaADia).Line);
            Assert.Equal("sem medição", PerfScoreboard.Parse("").Get(PerfMode.Silencioso).Line);
            Assert.Equal(3, a.Modes.Count);
        }

        [Test]
        public static void ModeThatDidNotRun_IsNoMeasurement()
        {
            PerfTestReport rep = new PerfTestReport();
            rep.StartedUtc = DateTime.UtcNow;
            rep.Runs.Add(Run(PerfMode.DiaADia, 118, 3000, 80, 3200));
            PerfModeRun notApplied = Run(PerfMode.Desempenho, 142, 3300, 86, 3900);
            notApplied.Applied = false;
            rep.Runs.Add(notApplied);
            PerfScoreboard b = PerfScoreboard.Parse(PerfReport.Json(rep, PerfVerdict.Build(rep)));
            Assert.Equal("sem medição", b.Get(PerfMode.Desempenho).Line);
            Assert.Equal("sem medição", b.Get(PerfMode.Silencioso).Line);
            Assert.True(b.Get(PerfMode.DiaADia).HasData, "reference ran");
        }

        [Test]
        public static void AbortedRun_IsNotUsed()
        {
            PerfTestReport rep = new PerfTestReport();
            rep.StartedUtc = DateTime.UtcNow;
            rep.Runs.Add(Run(PerfMode.DiaADia, 118, 3000, 80, 3200));
            PerfModeRun ab = Run(PerfMode.Desempenho, 142, 3300, 86, 3900);
            ab.Aborted = true;
            rep.Runs.Add(ab);
            PerfScoreboard b = PerfScoreboard.Parse(PerfReport.Json(rep, PerfVerdict.Build(rep)));
            Assert.Equal("sem medição", b.Get(PerfMode.Desempenho).Line);
        }

        [Test]
        public static void NoReference_NoPercentClaim()
        {
            PerfTestReport rep = new PerfTestReport();
            rep.StartedUtc = DateTime.UtcNow;
            rep.Runs.Add(Run(PerfMode.Desempenho, 142, 3300, 86, 3900));
            PerfScoreboard b = PerfScoreboard.Parse(PerfReport.Json(rep, PerfVerdict.Build(rep)));
            ModeScore p = b.Get(PerfMode.Desempenho);
            Assert.True(p.ScoreVsDiaPct == null, "no Dia a dia to compare with");
            Assert.Equal("Pontuação 142,0 · clock 3.300 MHz · CPU 86 °C · 3.900 rpm", p.Line);
        }

        [Test]
        public static void InsideTheBand_SaysEqual()
        {
            PerfTestReport rep = new PerfTestReport();
            rep.StartedUtc = DateTime.UtcNow;
            rep.Runs.Add(Run(PerfMode.Desempenho, 121, 3000, 80, 3200));
            rep.Runs.Add(Run(PerfMode.DiaADia, 120, 3000, 80, 3200));
            PerfScoreboard b = PerfScoreboard.Parse(PerfReport.Json(rep, PerfVerdict.Build(rep)));
            Assert.True(b.Get(PerfMode.Desempenho).Line.Contains("igual a Dia a dia"), b.Get(PerfMode.Desempenho).Line);
        }

        [Test]
        public static void Unreliable_ShowsNoNumbersAndSaysWhy()
        {
            string json = "{\"startedUtc\":\"2026-11-16T17:05:00Z\",\"aborted\":false,\"preconditions\":{\"onBattery\":false,\"powerSchemeName\":\"Equilibrado\"},"
                + "\"modes\":[{\"mode\":\"Dia a dia\",\"applied\":true,\"aborted\":false,\"multiSustained\":118.5}],"
                + "\"verdict\":{\"reliable\":false,\"bandPct\":3,\"lines\":[]}}";
            PerfScoreboard b = PerfScoreboard.Parse(json);
            Assert.True(!b.Reliable, "unreliable");
            Assert.Equal("medição não confiável", b.Get(PerfMode.DiaADia).Line);
            Assert.True(b.Note.Contains("outros programas"), b.Note);
        }

        [Test]
        public static void TheCanary_PreviousCrashFromTheJournal_IsShown()
        {
            string json = "{\"startedUtc\":\"2026-11-16T17:05:00Z\",\"aborted\":false,\"previousCrash\":\"o teste anterior parou durante o modo Desempenho\","
                + "\"preconditions\":{\"onBattery\":false},\"modes\":[],\"verdict\":{\"reliable\":true,\"bandPct\":3}}";
            PerfScoreboard b = PerfScoreboard.Parse(json);
            Assert.True(b.Note.Contains("queda registrada: o teste anterior parou durante o modo Desempenho"), b.Note);
        }

        [Test]
        public static void TryFromFile_ReadFailureIsNotCachedAsNoMeasurement()
        {
            string path = System.IO.Path.GetTempFileName();
            try
            {
                PerfScoreboard b;
                Assert.True(!PerfScoreboard.TryFromFile(path, delegate(string p) { throw new System.IO.IOException("being written"); }, out b), "I/O error: not a result");
                Assert.True(!PerfScoreboard.TryFromFile(path, delegate(string p) { return "{\"startedUtc\":"; }, out b), "half-written JSON: not a result");
                Assert.True(PerfScoreboard.TryFromFile(path, delegate(string p) { return FileJson(); }, out b), "a good read");
                Assert.True(b.Loaded, "loaded");
                Assert.True(PerfScoreboard.TryFromFile(path + ".missing", null, out b), "a missing file is a real 'sem medição'");
                Assert.Equal("sem medição", b.Get(PerfMode.DiaADia).Line);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public static void BatteryAndAbort_AreNoted()
        {
            string json = "{\"startedUtc\":\"2026-11-16T17:05:00Z\",\"aborted\":true,\"preconditions\":{\"onBattery\":true},"
                + "\"modes\":[],\"verdict\":{\"reliable\":true,\"bandPct\":3}}";
            PerfScoreboard b = PerfScoreboard.Parse(json);
            Assert.Equal("o teste foi interrompido; medido na bateria", b.Note);
        }
    }
}
