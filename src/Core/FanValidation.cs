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
            // The environment profile (if any) follows: an interrupted run proves nothing (locked), a failed one is a no.
            if (c.Env != null)
            {
                c.Env.Set(Cap.FanControl, Passed ? CapState.Confirmed : (Aborted ? CapState.Untested : CapState.Unavailable),
                    Passed ? "As ventoinhas seguiram os comandos no teste." : (Reason ?? "As ventoinhas não seguiram os comandos no teste."));
            }
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
            // The environment profile (if any) follows for the three modes this check covers.
            if (c.Env != null)
            {
                PerfMode[] covered = { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };
                foreach (PerfMode m in covered)
                {
                    Cap cap = ModeCatalog.CapOf(m);
                    if (c.Env.State(cap) == CapState.Unavailable) continue;
                    c.Env.Set(cap, Passed ? CapState.Confirmed : CapState.Untested,
                        Passed ? "O modo foi trocado e confirmado na leitura de volta." : "O teste de modos não passou: bloqueado até um novo teste.");
                }
            }
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
            if (!ReturnedToAuto(autoAgain, b.Value, m.Value))
            {
                c.Reason = "não voltou ao automático depois do teste";
                return c;
            }
            c.Passed = true;
            return c;
        }

        // A phase is "settled" when the last SettleSamples readings agree within SettleTolerance (the fan reached its speed)
        // and, when the phase is expected to move the fan away from reference, the speed really left it by MinMove.
        // A fan that has not reacted yet (same readings as before the command) is not settled: the phase runs to its limit.
        public const int SettleSamples = 3;
        public const double SettleTolerance = 0.03;

        public static bool Settled(List<int?> samples, int? reference)
        {
            if (samples == null || samples.Count < SettleSamples) return false;
            int lo = int.MaxValue, hi = 0;
            for (int i = samples.Count - SettleSamples; i < samples.Count; i++)
            {
                if (!samples[i].HasValue || samples[i].Value <= 0) return false;
                lo = Math.Min(lo, samples[i].Value);
                hi = Math.Max(hi, samples[i].Value);
            }
            if (hi - lo > SettleTolerance * hi) return false;
            if (reference.HasValue && reference.Value > 0)
            {
                if (Math.Abs(samples[samples.Count - 1].Value - reference.Value) < MinMove * reference.Value) return false;
            }
            return true;
        }

        // After Automático the fan counts as returned when the TWO latest consecutive readings (both real, above 0) are back at
        // least halfway from Máximo to the baseline, or the firmware is winding it down: both under SpinDownDrop below Máximo
        // and falling. One spurious reading (a transient 0 from WMI) can never pass a fan stuck at Máximo.
        public const int ReturnReadings = 2;

        public static bool ReturnedToAuto(List<int?> autoAgain, int baseline, int max)
        {
            if (autoAgain == null || autoAgain.Count < ReturnReadings) return false;
            int a = autoAgain.Count - ReturnReadings;
            int[] v = new int[ReturnReadings];
            for (int i = 0; i < ReturnReadings; i++)
            {
                int? s = autoAgain[a + i];
                if (!s.HasValue || s.Value <= 0) return false;
                v[i] = s.Value;
            }
            bool halfway = true, under = true;
            foreach (int x in v)
            {
                if (Math.Abs(x - baseline) > 0.5 * (max - baseline)) halfway = false;
                if (x > max * (1 - SpinDownDrop)) under = false;
            }
            if (halfway) return true;
            return under && SpinningDown(autoAgain, max);
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

    // One progress report of the fan test.
    public sealed class FanProgress
    {
        // "auto" (baseline), "pct" (Custom), "max", "auto2" (back to Automático).
        public string Phase;
        public int ElapsedMs;
        public int TotalMs;
        public int Percent;
        public int? CpuRpm;
        public int? GpuRpm;
        public double? CpuTempC;
        public double? GpuTempC;
        public string Text;
        // The step the user sees: "Fase 2 de 4", its name, and the longest it can still take (a phase ends early once the fans settle).
        public int PhaseNumber;
        public int PhaseCount;
        public string PhaseName;
        public int PhaseElapsedMs;
        public int PhaseMaxMs;

        public int PhaseLeftMs { get { return Math.Max(0, PhaseMaxMs - PhaseElapsedMs); } }

        public static int NumberOf(string phase)
        {
            switch (phase)
            {
                case "auto": return 1;
                case "pct": return 2;
                case "max": return 3;
                default: return 4;
            }
        }

        public static string NameFor(string phase, int pct)
        {
            switch (phase)
            {
                case "auto": return "Medindo o automático";
                case "pct": return "Ventoinhas a " + pct.ToString(CultureInfo.InvariantCulture) + "%";
                case "max": return "Ventoinhas no máximo";
                default: return "Voltando ao automático";
            }
        }

        public static string TextFor(string phase, int pct)
        {
            switch (phase)
            {
                case "auto": return "Medindo o ruído normal das ventoinhas, no automático. Escute: é o som de referência.";
                case "pct": return "Ventoinhas a " + pct.ToString(CultureInfo.InvariantCulture) + "%. Escute: o som deve mudar em relação ao automático.";
                case "max": return "Ventoinhas no máximo. Escute: o barulho deve subir bastante.";
                default: return "Voltando ao automático. Escute: o barulho deve ir baixando aos poucos.";
            }
        }
    }

    // The validation routine (run with the user present: SenseMonitor.exe --validar-ventoinha, and the wizard's fan step).
    // Fans: Automático (baseline) -> Custom pct% -> Máximo -> Automático, sampling the RPM every second. Each phase ends
    // as soon as the fans settle (three readings within 3 %), up to its limit; the way back ends as soon as the fan is
    // winding down or halfway back. Worst case 25 s, typically about 15 s. Any refused write, a temperature >= 90 °C or an
    // exception stops it and writes Automático at once; the routine always ends in Automático.
    // Modes: Silencioso -> Dia a dia -> Desempenho -> Dia a dia, each read back after a pause.
    public sealed class FanValidationRunner
    {
        public const int SampleMs = 1000;
        public const int BaselineMs = 3000;
        public const int PctMaxMs = 6000;
        public const int MaxPhaseMs = 6000;
        public const int ReturnMaxMs = 10000;
        public const int ModePauseMs = 1500;
        public const double AbortTempC = 90;

        // Worst case of the sleeps of RunFans (the progress bar's scale): baseline + custom + max + way back.
        public const int TotalMs = BaselineMs + PctMaxMs + MaxPhaseMs + ReturnMaxMs;

        private readonly IFirmware fw;
        private readonly Func<bool> setFanMax;
        private readonly Action<int> sleep;
        private readonly int pct;
        private readonly FanLayout layout;
        private int elapsedMs;
        private int phaseBaseMs;

        // Optional: told about every sample (the wizard's progress panel). A callback that throws never breaks the test.
        public Action<FanProgress> Progress;

        public FanValidationRunner(IFirmware fw, Func<bool> setFanMax, Action<int> sleep, int pct)
            : this(fw, setFanMax, sleep, pct, FanLayout.Both)
        {
        }

        // layout: the fans this notebook has. Only those are judged and need a temperature.
        public FanValidationRunner(IFirmware fw, Func<bool> setFanMax, Action<int> sleep, int pct, FanLayout layout)
        {
            if (fw == null) throw new ArgumentNullException("fw");
            if (setFanMax == null) throw new ArgumentNullException("setFanMax");
            if (sleep == null) throw new ArgumentNullException("sleep");
            this.fw = fw;
            this.setFanMax = setFanMax;
            this.sleep = sleep;
            this.pct = pct < Codes.MinFanPct ? Codes.MinFanPct : (pct > 90 ? 90 : pct);
            this.layout = layout ?? FanLayout.Both;
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
            elapsedMs = 0;
            phaseBaseMs = 0;
            bool autoConfirmed = false;
            Phase baseline = new Phase(), custom = new Phase(), max = new Phase(), again = new Phase();
            try
            {
                CheckCancel();
                if (!fw.SetFanAuto()) throw new AbortException("o firmware recusou o modo automático");
                Sample("auto", baseline, BaselineMs, rep, null);
                int? bCpu = FanValidation.Median(baseline.Cpu, false);
                int? bGpu = FanValidation.Median(baseline.Gpu, false);

                autoConfirmed = false;
                CheckCancel();
                if (!fw.SetFanPercent(pct, pct)) throw new AbortException("o firmware recusou a velocidade de " + pct + "%");
                Sample("pct", custom, PctMaxMs, rep, delegate(Phase p) { return Both(p, bCpu, bGpu); });
                int? pCpu = FanValidation.Median(custom.Cpu, true);
                int? pGpu = FanValidation.Median(custom.Gpu, true);

                CheckCancel();
                if (!setFanMax()) throw new AbortException("o firmware recusou o Máximo");
                Sample("max", max, MaxPhaseMs, rep, delegate(Phase p) { return Both(p, pCpu, pGpu); });
                int? mCpu = FanValidation.Median(max.Cpu, true);
                int? mGpu = FanValidation.Median(max.Gpu, true);

                autoConfirmed = fw.SetFanAuto();
                if (!autoConfirmed) throw new AbortException("o firmware recusou a volta ao automático");
                // gradual spin-down: it ends as soon as the fans are winding down or halfway back
                Sample("auto2", again, ReturnMaxMs, rep, delegate(Phase p) { return Returned(p, bCpu, bGpu, mCpu, mGpu); });
                ReportDone();
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
            if (layout.Cpu) rep.Cpu = FanValidation.EvaluateFan("CPU", baseline.Cpu, custom.Cpu, max.Cpu, again.Cpu, pct);
            if (layout.Gpu) rep.Gpu = FanValidation.EvaluateFan("GPU", baseline.Gpu, custom.Gpu, max.Gpu, again.Gpu, pct);
            rep.Passed = (rep.Cpu == null || rep.Cpu.Passed) && (rep.Gpu == null || rep.Gpu.Passed);
            return rep;
        }

        // Every fan of the layout settled (and moved away from its reference).
        private bool Both(Phase p, int? cpuRef, int? gpuRef)
        {
            return (!layout.Cpu || FanValidation.Settled(p.Cpu, cpuRef)) && (!layout.Gpu || FanValidation.Settled(p.Gpu, gpuRef));
        }

        // Every fan of the layout came back (halfway to the baseline or winding down from Máximo).
        private bool Returned(Phase p, int? bCpu, int? bGpu, int? mCpu, int? mGpu)
        {
            return (!layout.Cpu || (bCpu.HasValue && mCpu.HasValue && FanValidation.ReturnedToAuto(p.Cpu, bCpu.Value, mCpu.Value)))
                && (!layout.Gpu || (bGpu.HasValue && mGpu.HasValue && FanValidation.ReturnedToAuto(p.Gpu, bGpu.Value, mGpu.Value)));
        }

        // Samples every SampleMs up to limitMs; stops early when done(phase) says so (null = the whole time).
        // The progress bar counts the phase's full limit once it ends, early or not.
        private void Sample(string name, Phase into, int limitMs, FanValidationReport rep, Func<Phase, bool> done)
        {
            StringBuilder raw = new StringBuilder(name).Append(':');
            for (int t = 0; t < limitMs; t += SampleMs)
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
                // Without a temperature nothing guards the test: never read it as 0 °C. Each fan the
                // notebook has needs the temperature of its side; the other one is used when it is there.
                if ((layout.Cpu && !cpuT.HasValue) || (layout.Gpu && !gpuT.HasValue) || (!cpuT.HasValue && !gpuT.HasValue))
                {
                    rep.Raw.Add(raw.ToString());
                    throw new AbortException("temperatura ilegível durante o teste", true);
                }
                double hot = Math.Max(cpuT ?? 0, gpuT ?? 0);
                if (hot >= AbortTempC)
                {
                    rep.Raw.Add(raw.ToString());
                    throw new AbortException("temperatura chegou a " + ((int)hot).ToString(CultureInfo.InvariantCulture) + " °C (limite 90 °C)", true);
                }
                elapsedMs = phaseBaseMs + t + SampleMs;
                Report(name, c, g, cpuT, gpuT, t + SampleMs, limitMs);
                if (done != null && done(into)) break;
            }
            rep.Raw.Add(raw.ToString());
            phaseBaseMs += limitMs;
            elapsedMs = phaseBaseMs;
        }

        // The routine ended: the bar is full whatever the early stops saved.
        private void ReportDone()
        {
            elapsedMs = TotalMs;
            Report("auto2", lastCpu, lastGpu, lastCpuT, lastGpuT, ReturnMaxMs, ReturnMaxMs);
        }

        private int? lastCpu, lastGpu;
        private double? lastCpuT, lastGpuT;

        private void Report(string phase, int? cpuRpm, int? gpuRpm, double? cpuT, double? gpuT, int phaseElapsedMs, int phaseMaxMs)
        {
            lastCpu = cpuRpm;
            lastGpu = gpuRpm;
            lastCpuT = cpuT;
            lastGpuT = gpuT;
            Action<FanProgress> cb = Progress;
            if (cb == null) return;
            FanProgress p = new FanProgress();
            p.Phase = phase;
            p.ElapsedMs = elapsedMs;
            p.TotalMs = TotalMs;
            p.Percent = Math.Min(100, (int)Math.Round(100.0 * p.ElapsedMs / TotalMs));
            p.CpuRpm = cpuRpm;
            p.GpuRpm = gpuRpm;
            p.CpuTempC = cpuT;
            p.GpuTempC = gpuT;
            p.Text = FanProgress.TextFor(phase, pct);
            p.PhaseNumber = FanProgress.NumberOf(phase);
            p.PhaseCount = 4;
            p.PhaseName = FanProgress.NameFor(phase, pct);
            p.PhaseElapsedMs = phaseElapsedMs;
            p.PhaseMaxMs = phaseMaxMs;
            try { cb(p); }
            catch (Exception) { }
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
