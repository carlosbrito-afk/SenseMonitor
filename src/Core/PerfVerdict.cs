using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    public enum ModeEffect
    {
        NotEvaluated,   // mode not applied, interrupted, no reference, or the test is unreliable
        Works,          // behaves as the mode promises
        NoEffect,       // no measurable difference from Dia a dia
        Contradiction   // measurably the opposite of the promise
    }

    public class PerfVerdictResult
    {
        public ModeEffect Desempenho = ModeEffect.NotEvaluated;
        public ModeEffect Silencioso = ModeEffect.NotEvaluated;
        // False when background load or other conditions make the comparison meaningless.
        public bool Reliable = true;
        // Difference between the two Dia a dia runs (percent of their mean); null with fewer than two.
        public double? DriftPct;
        // The smallest difference that is stated: max(3 %, drift).
        public double BandPct = PerfVerdict.BandPct;
        public readonly List<string> Lines = new List<string>();
    }

    // Compares each mode against the Dia a dia reference (the mean of the two Dia a dia runs).
    // Differences inside the band are "sem diferença mensurável"; the band is 3 %, widened to
    // the drift between the two reference runs when that is larger (a difference is only
    // stated when it exceeds the spread of the same mode). Temperature counts from 2 degrees,
    // and only when the runs started at similar temperatures. Pure: Stats are computed here
    // when missing.
    public static class PerfVerdict
    {
        public const double BandPct = 3.0;
        public const double TempBandC = 2.0;

        // Signed whole percent with a real minus sign: "+5 %", "−1 %", "0 %".
        public static string Pct(double d)
        {
            double r = Math.Round(d, MidpointRounding.AwayFromZero);
            if (r == 0) return "0 %";
            return (r > 0 ? "+" : "−") + Math.Abs(r).ToString("0", CultureInfo.InvariantCulture) + " %";
        }

        // (value - reference) / reference * 100; null when either is missing or the reference is 0.
        public static double? Diff(double? value, double? reference)
        {
            if (!value.HasValue || !reference.HasValue || reference.Value <= 0) return null;
            return (value.Value - reference.Value) / reference.Value * 100.0;
        }

        public static string ModeName(PerfMode m)
        {
            return ModeCatalog.Name(m);
        }

        // "Dia a dia (1ª)" / "(2ª)" when the mode ran twice.
        public static string RunLabel(PerfTestReport rep, PerfModeRun run)
        {
            string n = ModeName(run.Mode);
            List<PerfModeRun> all = rep.FindAll(run.Mode);
            if (all.Count < 2) return n;
            return n + " (" + (all.IndexOf(run) + 1) + "ª)";
        }

        public static bool Comparable(PerfModeRun r)
        {
            return r != null && r.Applied && !r.Aborted && r.Stats != null
                && r.Stats.MultiSustained.HasValue && r.Stats.MultiComplete;
        }

        // The Dia a dia reference: the mean of the comparable Dia a dia runs (a single run if
        // only one is valid); null when none is.
        public static PerfModeRun Reference(PerfTestReport rep)
        {
            List<PerfModeRun> good = new List<PerfModeRun>();
            foreach (PerfModeRun r in rep.FindAll(PerfMode.DiaADia))
            {
                if (r.Stats == null) r.Stats = PerfScoring.Summarize(r);
                if (Comparable(r)) good.Add(r);
            }
            if (good.Count == 0) return null;
            if (good.Count == 1) return good[0];
            PerfModeRun m = new PerfModeRun();
            m.Mode = PerfMode.DiaADia;
            m.Applied = true;
            PerfStats s = new PerfStats();
            s.MultiComplete = true;
            s.SingleComplete = true;
            s.MultiSustained = Avg(good, delegate(PerfStats x) { return x.MultiSustained; });
            s.Burst = Avg(good, delegate(PerfStats x) { return x.Burst; });
            s.Single = Avg(good, delegate(PerfStats x) { return x.Single; });
            s.AvgCpuTemp = Avg(good, delegate(PerfStats x) { return x.AvgCpuTemp; });
            s.LateCpuTemp = Avg(good, delegate(PerfStats x) { return x.LateCpuTemp; });
            s.PeakCpuTemp = Avg(good, delegate(PerfStats x) { return x.PeakCpuTemp; });
            s.PeakGpuTemp = Avg(good, delegate(PerfStats x) { return x.PeakGpuTemp; });
            s.AvgClockMulti = Avg(good, delegate(PerfStats x) { return x.AvgClockMulti; });
            s.AvgClockSingle = Avg(good, delegate(PerfStats x) { return x.AvgClockSingle; });
            s.AvgCpuRpm = ToInt(Avg(good, delegate(PerfStats x) { return x.AvgCpuRpm.HasValue ? (double?)x.AvgCpuRpm.Value : null; }));
            s.AvgGpuRpm = ToInt(Avg(good, delegate(PerfStats x) { return x.AvgGpuRpm.HasValue ? (double?)x.AvgGpuRpm.Value : null; }));
            s.IdleCpuRpm = ToInt(Avg(good, delegate(PerfStats x) { return x.IdleCpuRpm.HasValue ? (double?)x.IdleCpuRpm.Value : null; }));
            s.IdleGpuRpm = ToInt(Avg(good, delegate(PerfStats x) { return x.IdleGpuRpm.HasValue ? (double?)x.IdleGpuRpm.Value : null; }));
            m.Stats = s;
            return m;
        }

        private static double? Avg(List<PerfModeRun> runs, Func<PerfStats, double?> pick)
        {
            double sum = 0;
            int n = 0;
            foreach (PerfModeRun r in runs)
            {
                double? v = pick(r.Stats);
                if (v.HasValue) { sum += v.Value; n++; }
            }
            return n == 0 ? (double?)null : sum / n;
        }

        private static int? ToInt(double? v)
        {
            return v.HasValue ? (int)Math.Round(v.Value, MidpointRounding.AwayFromZero) : (int?)null;
        }

        // Percent difference between the two Dia a dia runs, relative to their mean.
        public static double? Drift(PerfTestReport rep)
        {
            List<PerfModeRun> good = new List<PerfModeRun>();
            foreach (PerfModeRun r in rep.FindAll(PerfMode.DiaADia))
            {
                if (r.Stats == null) r.Stats = PerfScoring.Summarize(r);
                if (Comparable(r)) good.Add(r);
            }
            if (good.Count < 2) return null;
            double a = good[0].Stats.MultiSustained.Value;
            double b = good[1].Stats.MultiSustained.Value;
            double mean = (a + b) / 2.0;
            if (mean <= 0) return null;
            return Math.Abs(a - b) / mean * 100.0;
        }

        // Range of the start temperatures of the runs that began a load (null when fewer than two).
        public static double? StartSpread(PerfTestReport rep, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            int n = 0;
            foreach (PerfModeRun r in rep.Runs)
            {
                if (!r.Applied || !r.StartTempC.HasValue || r.Samples.Count == 0) continue;
                if (r.StartTempC.Value < min) min = r.StartTempC.Value;
                if (r.StartTempC.Value > max) max = r.StartTempC.Value;
                n++;
            }
            return n < 2 ? (double?)null : max - min;
        }

        public static PerfVerdictResult Build(PerfTestReport rep)
        {
            PerfVerdictResult v = new PerfVerdictResult();
            if (rep == null) return v;
            foreach (PerfModeRun run in rep.Runs)
            {
                if (run.Stats == null) run.Stats = PerfScoring.Summarize(run);
            }

            if (PerfPowerScheme.IsSaver(rep.Pre.PowerSchemeGuid, rep.Pre.PowerSchemeName))
            {
                v.Lines.Add("O plano de energia do Windows (" + (rep.Pre.PowerSchemeName ?? "economia") + ") é de economia: ele pode estar limitando todos os modos.");
            }
            if (rep.Pre.OnBattery == true)
            {
                v.Lines.Add("O teste rodou na bateria, onde o notebook costuma limitar o desempenho; repita na tomada para uma comparação justa.");
            }
            // Background load while idle makes the numbers meaningless.
            foreach (PerfModeRun run in rep.Runs)
            {
                if (run.Stats.IdleCpuLoad.HasValue && run.Stats.IdleCpuLoad.Value > PerfPlan.IdleLoadMaxPct)
                {
                    v.Reliable = false;
                    v.Lines.Add(RunLabel(rep, run) + ": havia outros programas usando a CPU no repouso (" + ((int)Math.Round(run.Stats.IdleCpuLoad.Value)).ToString(CultureInfo.InvariantCulture)
                        + " %, limite " + ((int)PerfPlan.IdleLoadMaxPct).ToString(CultureInfo.InvariantCulture) + " %): as medidas não são confiáveis.");
                }
            }
            if (!v.Reliable)
            {
                v.Lines.Add("Veredito invalidado pela carga de fundo: nenhum modo foi comparado; repita sem outros programas abertos.");
                return v;
            }

            double? drift = Drift(rep);
            v.DriftPct = drift;
            if (drift.HasValue && drift.Value > v.BandPct) v.BandPct = drift.Value;
            if (drift.HasValue && drift.Value > BandPct)
            {
                v.Lines.Add("As duas medidas de Dia a dia diferem " + ((int)Math.Round(drift.Value)).ToString(CultureInfo.InvariantCulture)
                    + " % entre si (calor acumulado ou ruído): só diferenças maiores que isso são afirmadas.");
            }

            double min, max;
            double? spread = StartSpread(rep, out min, out max);
            bool tempReliable = true;
            if (spread.HasValue && spread.Value > PerfPlan.StartSpreadMaxC)
            {
                tempReliable = false;
                v.Lines.Add("Os testes começaram com temperaturas diferentes (de " + ((int)Math.Round(min)).ToString(CultureInfo.InvariantCulture) + " a "
                    + ((int)Math.Round(max)).ToString(CultureInfo.InvariantCulture) + " °C): comparações de temperatura não foram usadas.");
            }

            PerfModeRun reference = Reference(rep);
            if (reference == null)
            {
                v.Lines.Add("Sem medida completa do Dia a dia" + Why(rep.Find(PerfMode.DiaADia)) + ": não dá para comparar os outros modos.");
                return v;
            }
            if (rep.FindAll(PerfMode.DiaADia).Count >= 2 && !drift.HasValue)
            {
                v.Lines.Add("Só uma das duas medidas de Dia a dia ficou completa: não há como medir a variação entre execuções.");
            }
            v.Desempenho = CompareDesempenho(rep.Find(PerfMode.Desempenho), reference, v.BandPct, tempReliable, v.Lines);
            v.Silencioso = CompareSilencioso(rep.Find(PerfMode.Silencioso), reference, v.BandPct, tempReliable, v.Lines);
            return v;
        }

        private static string Why(PerfModeRun r)
        {
            if (r == null) return " (não foi testado)";
            if (!r.Applied) return " (o notebook não confirmou a troca de modo)";
            if (r.Aborted) return " (" + r.AbortReason + ")";
            return "";
        }

        // null = fine to compare; otherwise the plain-language line to show.
        private static string NotComparableLine(PerfMode m, PerfModeRun r)
        {
            if (Comparable(r)) return null;
            return ModeName(m) + ": não comparado" + Why(r) + ".";
        }

        private static string Extras(PerfModeRun run, PerfModeRun reference)
        {
            List<string> parts = new List<string>();
            double? s = Diff(run.Stats.Single, reference.Stats.Single);
            if (s.HasValue) parts.Add("1 núcleo: " + Pct(s.Value));
            double? b = Diff(run.Stats.Burst, reference.Stats.Burst);
            if (b.HasValue) parts.Add("pico de 10 s: " + Pct(b.Value));
            return parts.Count == 0 ? "" : " (" + string.Join("; ", parts.ToArray()) + ")";
        }

        private static double? Late(PerfStats s)
        {
            return s.LateCpuTemp ?? s.PeakCpuTemp;
        }

        // positive = the mode ran cooler than Dia a dia (mean of the last 30 s of the load)
        private static double? TempGap(PerfModeRun run, PerfModeRun reference)
        {
            double? a = Late(run.Stats), b = Late(reference.Stats);
            if (!a.HasValue || !b.HasValue) return null;
            return b.Value - a.Value;
        }

        private static string Tag(double band)
        {
            return band > BandPct + 0.05 ? " (faixa de " + ((int)Math.Round(band)).ToString(CultureInfo.InvariantCulture) + " %)" : "";
        }

        private static ModeEffect CompareDesempenho(PerfModeRun run, PerfModeRun reference, double band, bool tempReliable, List<string> lines)
        {
            string skip = NotComparableLine(PerfMode.Desempenho, run);
            if (skip != null) { lines.Add(skip); return ModeEffect.NotEvaluated; }
            double d = Diff(run.Stats.MultiSustained, reference.Stats.MultiSustained).Value;
            string extras = Extras(run, reference);
            if (d >= band)
            {
                lines.Add("Desempenho rendeu " + Pct(d) + " em relação ao Dia a dia na carga de 60 s" + extras + ": o modo está fazendo efeito.");
                return ModeEffect.Works;
            }
            if (d > -band)
            {
                double? cooler = TempGap(run, reference);
                if (tempReliable && cooler.HasValue && cooler.Value >= TempBandC)
                {
                    lines.Add("Desempenho rendeu o mesmo que o Dia a dia (" + Pct(d) + ", sem diferença mensurável" + Tag(band) + ") mas ficou "
                        + ((int)Math.Round(cooler.Value)).ToString(CultureInfo.InvariantCulture) + " °C mais frio: mais folga térmica, o modo parece estar atuando.");
                    return ModeEffect.Works;
                }
                lines.Add("Desempenho não rendeu mais que Dia a dia (" + Pct(d) + "): sem diferença mensurável" + Tag(band) + "; o modo não está fazendo efeito neste notebook / plano de energia limitando.");
                return ModeEffect.NoEffect;
            }
            lines.Add("Desempenho rendeu MENOS que o Dia a dia (" + Pct(d) + "): contradição; o modo não está ajudando (calor acumulado ou limite do plano de energia podem explicar).");
            return ModeEffect.Contradiction;
        }

        private static ModeEffect CompareSilencioso(PerfModeRun run, PerfModeRun reference, double band, bool tempReliable, List<string> lines)
        {
            string skip = NotComparableLine(PerfMode.Silencioso, run);
            if (skip != null) { lines.Add(skip); return ModeEffect.NotEvaluated; }
            double d = Diff(run.Stats.MultiSustained, reference.Stats.MultiSustained).Value;
            double? rpmLoad = Diff(run.Stats.AvgRpm, reference.Stats.AvgRpm);
            double? rpmIdle = Diff(run.Stats.IdleRpm, reference.Stats.IdleRpm);
            bool quieter = (rpmLoad.HasValue && rpmLoad.Value <= -BandPct) || (rpmIdle.HasValue && rpmIdle.Value <= -BandPct);
            double? cooler = TempGap(run, reference);
            bool colder = tempReliable && cooler.HasValue && cooler.Value >= TempBandC;
            string rpmText = quieter ? "ventoinhas mais lentas (" + Pct(rpmLoad.HasValue && rpmLoad.Value <= -BandPct ? rpmLoad.Value : rpmIdle.Value) + " de RPM)" : "ventoinhas na mesma rotação";

            if (d >= band)
            {
                lines.Add("Silencioso rendeu " + Pct(d) + " MAIS que o Dia a dia: contradição; o esperado era igual ou menor.");
                return ModeEffect.Contradiction;
            }
            if (d <= -band)
            {
                if (quieter)
                {
                    lines.Add("Silencioso rendeu " + Pct(d) + " em relação ao Dia a dia (esperado para um modo silencioso) com " + rpmText + ": o modo está fazendo efeito.");
                    return ModeEffect.Works;
                }
                lines.Add("Silencioso rendeu " + Pct(d) + " em relação ao Dia a dia, mas não baixou a rotação das ventoinhas: limita o desempenho sem dar mais silêncio, então a promessa do modo não se confirmou.");
                return ModeEffect.NoEffect;
            }
            if (quieter || colder)
            {
                lines.Add("Silencioso rendeu o mesmo que o Dia a dia (" + Pct(d) + ") com " + (quieter ? rpmText : "temperatura "
                    + ((int)Math.Round(cooler.Value)).ToString(CultureInfo.InvariantCulture) + " °C menor") + ": o modo está fazendo efeito.");
                return ModeEffect.Works;
            }
            lines.Add("Silencioso não mudou nada que dê para medir (rendimento " + Pct(d) + Tag(band) + ", ventoinhas e temperatura iguais): o modo não está fazendo efeito neste notebook / plano de energia limitando.");
            return ModeEffect.NoEffect;
        }
    }
}
