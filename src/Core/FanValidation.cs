using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Forja.Core
{
    // Result for one fan: settled RPM of each phase and the verdict.
    public class FanCheck
    {
        public string Name;
        public int Baseline;   // Automático before the test
        public int Pct;        // Custom at the test percentage
        public int Max;        // Máximo (FanBehavior Max)
        public int AutoAgain;  // Automático after the test
        public bool Passed;
        public string Reason;

        public override string ToString()
        {
            return Name + ": auto " + Baseline + " / " + "pct " + Pct + " / max " + Max + " / auto " + AutoAgain + " RPM"
                + (Passed ? " — ok" : " — falhou: " + Reason);
        }
    }

    public class FanValidationReport
    {
        public int TestPct;
        public bool Passed;
        public bool Aborted;
        public string Reason;
        // Heat, an unreadable temperature or a cancel: the mode check must not run after it.
        public bool SkipModeCheck;
        public FanCheck Cpu;
        public FanCheck Gpu;
        public readonly List<string> Raw = new List<string>();

        public string Summary()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Passed ? "Ventoinhas: APROVADO" : (Aborted ? "Ventoinhas: INTERROMPIDO" : "Ventoinhas: REPROVADO"));
            sb.Append(" (teste a ").Append(TestPct).Append("%)");
            if (!string.IsNullOrEmpty(Reason)) sb.Append(" — ").Append(Reason);
            if (Cpu != null) sb.Append("; ").Append(Cpu);
            if (Gpu != null) sb.Append("; ").Append(Gpu);
            return sb.ToString();
        }

        // Stores the outcome. A failed or interrupted run revokes an earlier validation.
        public void ApplyTo(Config c, DateTime utc)
        {
            if (c == null) return;
            c.FanControlValidated = Passed;
            c.FanMaxRpmCpu = Passed && Cpu != null ? Cpu.Max : 0;
            c.FanMaxRpmGpu = Passed && Gpu != null ? Gpu.Max : 0;
            c.FanValidationUtc = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            c.FanValidationNotes = Summary() + " | amostras: " + string.Join(" ", Raw.ToArray());
            if (!Passed && c.FanMode != "Auto") c.FanMode = "Auto";
        }
    }

    public class ModeCheckReport
    {
        public bool Passed;
        public readonly List<string> Lines = new List<string>();

        public void ApplyTo(Config c, DateTime utc)
        {
            if (c == null) return;
            c.ModeCheckPassed = Passed;
            c.ModeCheckUtc = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            c.ModeCheckNotes = (Passed ? "Modos: APROVADO" : "Modos: REPROVADO") + " | " + string.Join("; ", Lines.ToArray());
        }
    }

    // Pure decisions of the fan validation.
    public static class FanValidation
    {
        public const string LockedNote = "Controle manual será liberado após um teste rápido com você";

        // Thresholds: Máximo must be >= 15 % above both other phases; the Custom phase must move
        // > 10 % from the Automático baseline toward pct% of Máximo; after Automático again the
        // fan must come back at least halfway from Máximo toward the baseline.
        public const double MaxRise = 1.15;
        public const double MinMove = 0.10;

        // baseline: all samples count; the other phases use their settled second half.
        public static FanCheck EvaluateFan(string name, List<int?> baseline, List<int?> pct, List<int?> max,
            List<int?> autoAgain, int pctValue)
        {
            FanCheck c = new FanCheck();
            c.Name = name;
            int? b = Median(baseline, false);
            int? p = Median(pct, true);
            int? m = Median(max, true);
            int? a = Median(autoAgain, true);
            c.Baseline = b ?? 0;
            c.Pct = p ?? 0;
            c.Max = m ?? 0;
            c.AutoAgain = a ?? 0;
            if (!b.HasValue || !p.HasValue || !m.HasValue || !a.HasValue || b.Value <= 0 || m.Value <= 0)
            {
                c.Reason = "sem leitura de rotação";
                return c;
            }
            if (m.Value < b.Value * MaxRise || m.Value < p.Value * MaxRise)
            {
                c.Reason = "o Máximo não acelerou a ventoinha";
                return c;
            }
            double target = pctValue * m.Value / 100.0;
            if (Math.Abs(target - b.Value) < MinMove * b.Value)
            {
                c.Reason = "inconclusivo: " + pctValue + "% fica perto do automático; repita com outra porcentagem";
                return c;
            }
            int dir = Math.Sign(target - b.Value);
            int moved = p.Value - b.Value;
            if (Math.Sign(moved) != dir || Math.Abs(moved) < MinMove * b.Value)
            {
                c.Reason = "a ventoinha não seguiu o comando de " + pctValue + "%";
                return c;
            }
            if (Math.Abs(a.Value - b.Value) > 0.5 * (m.Value - b.Value) && !SpinningDown(autoAgain, m.Value))
            {
                c.Reason = "não voltou ao automático depois do teste";
                return c;
            }
            c.Passed = true;
            return c;
        }

        // The firmware winds a fan down from Máximo gradually: after Automático it counts as
        // returning when the last reading is >= SpinDownDrop below Máximo and still falling.
        public const double SpinDownDrop = 0.20;

        public static bool SpinningDown(List<int?> autoAgain, int max)
        {
            if (autoAgain == null || max <= 0) return false;
            int? first = null, last = null;
            foreach (int? s in autoAgain)
            {
                if (!s.HasValue) continue;
                if (!first.HasValue) first = s;
                last = s;
            }
            if (!first.HasValue || last.Value >= first.Value) return false;
            return last.Value <= max * (1 - SpinDownDrop);
        }

        // Lower median of the valid (non-null) samples; settled = second half only.
        public static int? Median(List<int?> samples, bool settled)
        {
            if (samples == null || samples.Count == 0) return null;
            int start = settled ? samples.Count / 2 : 0;
            List<int> v = new List<int>();
            for (int i = start; i < samples.Count; i++)
            {
                if (samples[i].HasValue) v.Add(samples[i].Value);
            }
            if (v.Count == 0) return null;
            v.Sort();
            return v[(v.Count - 1) / 2];
        }
    }

    // The validation routine (run with the user present: SenseMonitor.exe --validar-ventoinha).
    // Fans: Automático (baseline) -> Custom pct% -> Máximo -> Automático, ~8 s each, sampling the
    // RPM every second. Any refused write, a temperature >= 90 °C or an exception stops it and
    // writes Automático at once; the routine always ends in Automático.
    // Modes: Silencioso -> Dia a dia -> Desempenho -> Dia a dia, each read back after a pause.
    public sealed class FanValidationRunner
    {
        public const int PhaseMs = 8000;
        public const int SampleMs = 1000;
        public const int SettleMs = 2000;
        public const int BaselineMs = 4000;
        public const int ModePauseMs = 1500;
        public const double AbortTempC = 90;

        private readonly IFirmware fw;
        private readonly Func<bool> setFanMax;
        private readonly Action<int> sleep;
        private readonly int pct;

        public FanValidationRunner(IFirmware fw, Func<bool> setFanMax, Action<int> sleep, int pct)
        {
            if (fw == null) throw new ArgumentNullException("fw");
            if (setFanMax == null) throw new ArgumentNullException("setFanMax");
            if (sleep == null) throw new ArgumentNullException("sleep");
            this.fw = fw;
            this.setFanMax = setFanMax;
            this.sleep = sleep;
            this.pct = pct < Codes.MinFanPct ? Codes.MinFanPct : (pct > 90 ? 90 : pct);
        }

        private class Phase
        {
            public readonly List<int?> Cpu = new List<int?>();
            public readonly List<int?> Gpu = new List<int?>();
        }

        private sealed class AbortException : Exception
        {
            public readonly bool SkipModes;
            public AbortException(string reason) : this(reason, false) { }
            public AbortException(string reason, bool skipModes) : base(reason) { SkipModes = skipModes; }
        }

        private volatile bool cancelled;

        // Any thread (app exit, crash path): stops before the next firmware write or sample;
        // the routine then writes Automático and returns.
        public void Cancel()
        {
            cancelled = true;
        }

        private void CheckCancel()
        {
            if (cancelled) throw new AbortException("teste cancelado", true);
        }

        public FanValidationReport RunFans()
        {
            FanValidationReport rep = new FanValidationReport();
            rep.TestPct = pct;
            bool autoConfirmed = false;
            Phase baseline = new Phase(), custom = new Phase(), max = new Phase(), again = new Phase();
            try
            {
                CheckCancel();
                if (!fw.SetFanAuto()) throw new AbortException("o firmware recusou o modo automático");
                sleep(SettleMs);
                Sample("auto", baseline, BaselineMs, rep);

                autoConfirmed = false;
                CheckCancel();
                if (!fw.SetFanPercent(pct, pct)) throw new AbortException("o firmware recusou a velocidade de " + pct + "%");
                Sample("pct", custom, PhaseMs, rep);

                CheckCancel();
                if (!setFanMax()) throw new AbortException("o firmware recusou o Máximo");
                Sample("max", max, PhaseMs, rep);

                autoConfirmed = fw.SetFanAuto();
                if (!autoConfirmed) throw new AbortException("o firmware recusou a volta ao automático");
                Sample("auto2", again, 2 * PhaseMs, rep); // gradual spin-down: watch it longer
            }
            catch (AbortException ex)
            {
                rep.Aborted = true;
                rep.Reason = ex.Message;
                rep.SkipModeCheck = ex.SkipModes;
            }
            catch (Exception ex)
            {
                rep.Aborted = true;
                rep.Reason = "erro " + ex.GetType().Name;
            }
            finally
            {
                if (!autoConfirmed || rep.Aborted)
                {
                    try { fw.SetFanAuto(); }
                    catch (Exception) { }
                }
            }
            if (rep.Aborted) return rep;
            rep.Cpu = FanValidation.EvaluateFan("CPU", baseline.Cpu, custom.Cpu, max.Cpu, again.Cpu, pct);
            rep.Gpu = FanValidation.EvaluateFan("GPU", baseline.Gpu, custom.Gpu, max.Gpu, again.Gpu, pct);
            rep.Passed = rep.Cpu.Passed && rep.Gpu.Passed;
            return rep;
        }

        private void Sample(string name, Phase into, int durationMs, FanValidationReport rep)
        {
            StringBuilder raw = new StringBuilder(name).Append(':');
            for (int t = 0; t < durationMs; t += SampleMs)
            {
                sleep(SampleMs);
                CheckCancel();
                SensorReading r = fw.ReadSensors();
                int? c = r == null ? null : r.CpuFanRpm;
                int? g = r == null ? null : r.GpuFanRpm;
                into.Cpu.Add(c);
                into.Gpu.Add(g);
                raw.Append(' ').Append(c.HasValue ? c.Value.ToString(CultureInfo.InvariantCulture) : "-")
                   .Append('/').Append(g.HasValue ? g.Value.ToString(CultureInfo.InvariantCulture) : "-");
                double? cpuT = Temp(r == null ? null : r.CpuTempC);
                double? gpuT = Temp(r == null ? null : r.GpuTempC);
                if (!cpuT.HasValue || !gpuT.HasValue)
                {
                    // Without a temperature nothing guards the test: never read it as 0 °C.
                    rep.Raw.Add(raw.ToString());
                    throw new AbortException("temperatura ilegível durante o teste", true);
                }
                double hot = Math.Max(cpuT.Value, gpuT.Value);
                if (hot >= AbortTempC)
                {
                    rep.Raw.Add(raw.ToString());
                    throw new AbortException("temperatura chegou a " + ((int)hot).ToString(CultureInfo.InvariantCulture) + " °C (limite 90 °C)", true);
                }
            }
            rep.Raw.Add(raw.ToString());
        }

        // null = unreadable (missing, NaN, infinite or <= 0, as in FanController).
        private static double? Temp(double? t)
        {
            if (!t.HasValue || double.IsNaN(t.Value) || double.IsInfinity(t.Value) || t.Value <= 0) return null;
            return t.Value;
        }

        public ModeCheckReport RunModes()
        {
            ModeCheckReport rep = new ModeCheckReport();
            PerfMode[] seq = { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho, PerfMode.DiaADia };
            bool all = true;
            bool lastOk = false;
            foreach (PerfMode m in seq)
            {
                bool ok = false;
                PerfMode? back = null;
                try
                {
                    ok = fw.SetMode(m);
                    sleep(ModePauseMs);
                    back = fw.GetMode();
                }
                catch (Exception ex)
                {
                    rep.Lines.Add(m + ": erro " + ex.GetType().Name);
                    all = false;
                    lastOk = false;
                    continue;
                }
                bool pass = ok && back.HasValue && back.Value == m;
                rep.Lines.Add(m + ": escrita " + (ok ? "ok" : "falhou") + ", lido " + (back.HasValue ? back.Value.ToString() : "-")
                    + (pass ? " — ok" : " — falhou"));
                all &= pass;
                lastOk = pass;
            }
            if (!lastOk)
            {
                // Never leave the machine in a mode the check could not confirm.
                try { fw.SetMode(PerfMode.DiaADia); }
                catch (Exception) { }
            }
            rep.Passed = all;
            return rep;
        }
    }
}
