using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // The wizard's manual fan test: the user picks the fan(s) and a speed, the app writes Custom through the guarded firmware
    // (marker first, sentinel, read-only mode, 90 °C), shows the RPM live, and the user says whether the sound changed.
    public enum ManualFanTarget { Cpu, Gpu, Both }

    public sealed class ManualFanProgress
    {
        public ManualFanTarget Target;
        public int Pct;
        public int HeldMs;
        public int LimitMs;
        public int? CpuRpm;
        public int? GpuRpm;
        public double? CpuTempC;
        public double? GpuTempC;
        // The readings agree (the fans reached the speed): a good moment to answer.
        public bool Settled;
        public string Text;
    }

    // How one fan answered one manual write.
    public sealed class ManualFanVerdict
    {
        public string Name;
        public int Baseline;
        public int Settled;
        // Moved at least 10 % of the baseline toward the requested speed.
        public bool Moved;
        // The requested speed is too close to what Automático already does: nothing can be concluded, try another speed.
        public bool Inconclusive;
        public string Reason;
        // Máximo implied by this reading (settled * 100 / pct): the RPM guard's reference when the test passes.
        public int EstimatedMax;
    }

    public sealed class ManualFanResult
    {
        public ManualFanTarget Target;
        public int Pct;
        // The firmware accepted the Custom write.
        public bool Applied;
        public ManualFanVerdict Cpu;
        public ManualFanVerdict Gpu;
        // null = the user took the fans back to Automático; otherwise why it stopped (heat, unreadable temperature, cancel, time).
        public string StopReason;
        // Heat, an unreadable temperature or a cancel: no further manual attempt in this run.
        public bool SkipFurther;
        // Automático was written AND read back.
        public bool AutoConfirmed;
        public int HeldMs;
        public int Readings;
        public string Error;
    }

    // One apply plus what the user said about it.
    public sealed class ManualFanAttempt
    {
        public ManualFanResult Result;
        // true = "Ouvi a ventoinha mudar", false = "Não mudou", null = no answer.
        public bool? Heard;
    }

    public static class ManualFanJudge
    {
        public const int MinPct = Codes.MinFanPct;
        public const int MaxPct = 100;
        public const int DefaultPct = 70;
        // When no Máximo was ever measured here: what a gaming-laptop fan typically reaches, to tell which way a speed should move the fan.
        public const int DefaultMaxRpm = 6500;

        public static int ClampPct(int pct)
        {
            return pct < MinPct ? MinPct : (pct > MaxPct ? MaxPct : pct);
        }

        // The fans of the notebook the choice addresses (null = the notebook has none of the chosen).
        public static FanLayout Sub(FanLayout notebook, ManualFanTarget t)
        {
            if (notebook == null) return null;
            bool cpu = notebook.Cpu && (t == ManualFanTarget.Cpu || t == ManualFanTarget.Both);
            bool gpu = notebook.Gpu && (t == ManualFanTarget.Gpu || t == ManualFanTarget.Both);
            return FanLayout.From(cpu, gpu);
        }

        // baseline: Automático before the write; settled: the speed the fan reached; refMax: a Máximo measured here, or 0.
        public static ManualFanVerdict Judge(string name, int? baseline, int? settled, int pct, int refMax)
        {
            ManualFanVerdict v = new ManualFanVerdict();
            v.Name = name;
            v.Baseline = baseline ?? 0;
            v.Settled = settled ?? 0;
            if (!baseline.HasValue || !settled.HasValue || baseline.Value <= 0 || settled.Value <= 0)
            {
                v.Reason = "sem leitura de rotação";
                return v;
            }
            int max = refMax > 0 ? refMax : DefaultMaxRpm;
            double target = pct * max / 100.0;
            double b = baseline.Value;
            if (Math.Abs(target - b) < FanValidation.MinMove * b)
            {
                v.Inconclusive = true;
                v.Reason = "inconclusivo: " + pct.ToString(CultureInfo.InvariantCulture) + "% fica perto do automático; tente uma velocidade bem diferente";
                return v;
            }
            int dir = Math.Sign(target - b);
            double moved = settled.Value - b;
            if (Math.Sign(moved) != dir || Math.Abs(moved) < FanValidation.MinMove * b)
            {
                v.Reason = "a rotação lida não mudou com o comando de " + pct.ToString(CultureInfo.InvariantCulture) + "%";
                return v;
            }
            v.Moved = true;
            v.EstimatedMax = (int)Math.Round(settled.Value * 100.0 / pct);
            return v;
        }
    }

    // What the manual attempts add up to, in the shape the capability builder reads.
    public static class ManualFanOutcome
    {
        public const string Method = "manual";
        // The user heard the fans change but the measurement did not show it (or the RPM could not be read) for at least one
        // fan: the user's word wins and unlocks the fan control. Recorded as such; the RPM-response guard has no reference
        // for that fan (Config.ManualFanAllowed accepts exactly this method without one).
        public const string MethodUserConfirmed = "manual (confirmado pelo usuário, sem medição)";

        public static bool IsUserConfirmed(string method)
        {
            return string.Equals(method, MethodUserConfirmed, StringComparison.Ordinal);
        }

        public static FanTestResult Decide(FanLayout layout, List<ManualFanAttempt> attempts)
        {
            FanTestResult r = new FanTestResult();
            r.Method = Method;
            if (layout == null) layout = FanLayout.Both;
            if (attempts == null) attempts = new List<ManualFanAttempt>();
            bool cpuOk = false, gpuOk = false;
            int cpuMax = 0, gpuMax = 0;
            // fans confirmed by the user's word alone, with what the measurement showed for them
            List<string> byUser = new List<string>();
            ManualFanAttempt last = null;
            foreach (ManualFanAttempt a in attempts)
            {
                if (a == null || a.Result == null) continue;
                ManualFanResult m = a.Result;
                last = a;
                if (!m.Applied)
                {
                    r.Status = StepStatus.Skipped;
                    r.Reason = m.Error ?? m.StopReason ?? "o firmware não aceitou o comando";
                    continue;
                }
                if (m.SkipFurther)
                {
                    r.Status = StepStatus.Skipped;
                    r.Reason = m.StopReason;
                    return Finish(r, layout, cpuOk, gpuOk, cpuMax, gpuMax, false);
                }
                if (a.Heard == false)
                {
                    r.Status = StepStatus.Failed;
                    r.Reason = "você respondeu que o som da ventoinha não mudou";
                    return Finish(r, layout, cpuOk, gpuOk, cpuMax, gpuMax, true);
                }
                if (a.Heard != true) continue;
                // The user heard it: that settles each fan that was addressed. A measured change also gives the RPM guard its
                // reference; without one (small change, inconclusive, unreadable RPM) the user's word alone confirms the fan.
                ManualFanVerdict[] vs = { m.Cpu, m.Gpu };
                foreach (ManualFanVerdict v in vs)
                {
                    if (v == null) continue;
                    bool measured = v.Moved;
                    int max = measured ? v.EstimatedMax : 0;
                    if (v.Name == "CPU") { cpuOk = true; cpuMax = max; }
                    else { gpuOk = true; gpuMax = max; }
                    string entry = v.Name + ": " + Measured(v, m.Pct);
                    if (!measured) byUser.Add(entry);
                }
            }
            bool all = (!layout.Cpu || cpuOk) && (!layout.Gpu || gpuOk);
            bool autoBack = last != null && last.Result.AutoConfirmed;
            if (all && autoBack)
            {
                r.Status = StepStatus.Passed;
                r.CpuOk = layout.Cpu && cpuOk;
                r.GpuOk = layout.Gpu && gpuOk;
                r.MaxRpmCpu = layout.Cpu ? cpuMax : 0;
                r.MaxRpmGpu = layout.Gpu ? gpuMax : 0;
                bool unmeasured = (layout.Cpu && cpuMax <= 0) || (layout.Gpu && gpuMax <= 0);
                if (unmeasured)
                {
                    r.Method = MethodUserConfirmed;
                    r.Reason = "você ouviu a mudança, e isso vale mais que a medição. Medido: " + string.Join("; ", byUser.ToArray());
                }
                return r;
            }
            return Finish(r, layout, cpuOk, gpuOk, cpuMax, gpuMax, false);
        }

        // What the measurement showed for a fan, in words (kept in the result when the user's word overrode it).
        private static string Measured(ManualFanVerdict v, int pct)
        {
            if (v.Baseline <= 0 || v.Settled <= 0) return "sem leitura de rotação";
            string s = "automático " + v.Baseline.ToString(CultureInfo.InvariantCulture) + " RPM, a " + pct.ToString(CultureInfo.InvariantCulture)
                + "% " + v.Settled.ToString(CultureInfo.InvariantCulture) + " RPM";
            double change = (v.Settled - v.Baseline) * 100.0 / v.Baseline;
            string pctText = Math.Abs(Math.Round(change)).ToString("0", CultureInfo.InvariantCulture);
            string how = v.Inconclusive ? "inconclusivo (velocidade perto do automático)"
                : Math.Abs(change) < FanValidation.MinMove * 100 ? "variação de " + pctText + "%, abaixo dos 10% que a medição pede"
                : "variação de " + pctText + "% em outra direção que a esperada";
            return s + " — " + how;
        }

        // Not passed. failed = a real "no"; otherwise nothing proved (locked, can be redone).
        private static FanTestResult Finish(FanTestResult r, FanLayout layout, bool cpuOk, bool gpuOk, int cpuMax, int gpuMax, bool failed)
        {
            r.CpuOk = cpuOk;
            r.GpuOk = gpuOk;
            if (failed) return r;
            if (r.Status != StepStatus.Skipped) r.Status = StepStatus.Skipped;
            if (string.IsNullOrEmpty(r.Reason))
            {
                List<string> missing = new List<string>();
                if (layout.Cpu && !cpuOk) missing.Add("CPU");
                if (layout.Gpu && !gpuOk) missing.Add("GPU");
                r.Reason = missing.Count > 0
                    ? "faltou testar e confirmar a ventoinha " + string.Join(" e ", missing.ToArray())
                    : "o automático não foi confirmado no fim do teste";
            }
            return r;
        }
    }

    // A stop or a cancel asked for before the runner exists (the firmware is still being opened) is remembered and applied the
    // moment the runner is attached: it must never be lost, or the fans would get Custom and hold it for the whole limit.
    public sealed class ManualFanControl
    {
        private readonly object gate = new object();
        private ManualFanRunner runner;
        private bool stop;
        private bool cancel;

        public void RequestStop()
        {
            ManualFanRunner r;
            lock (gate) { stop = true; r = runner; }
            if (r != null) r.RequestStop();
        }

        // The wizard is closing: stays asked for (a runner that appears later ends at once).
        public void Cancel()
        {
            ManualFanRunner r;
            lock (gate) { cancel = true; stop = true; r = runner; }
            if (r != null) r.Cancel();
        }

        public void Attach(ManualFanRunner r)
        {
            bool s, c;
            lock (gate) { runner = r; s = stop; c = cancel; }
            if (r == null) return;
            if (c) r.Cancel();
            else if (s) r.RequestStop();
        }

        // The run ended: the next one starts without the old stop (a cancel stays).
        public void Detach()
        {
            lock (gate) { runner = null; stop = false; }
        }
    }

    // Writes Custom to the chosen fan(s) and holds it, reading the sensors every second, until the user takes the fans back
    // to Automático (RequestStop), 90 °C, an unreadable temperature, a refused write, a cancel or HoldLimitMs. Always ends by
    // writing Automático for the notebook's fans and reading it back.
    public sealed class ManualFanRunner
    {
        public const int SampleMs = 1000;
        public const int BaselineReadings = 2;
        public const int HoldLimitMs = 120000;
        public const int AutoTries = 3;
        public const double AbortTempC = FanValidationRunner.AbortTempC;

        private readonly IFirmware fw;
        private readonly Action<int> sleep;
        private readonly FanLayout notebook;
        private volatile bool stop;
        private volatile bool cancelled;

        // Which fans the firmware commands address from now on (the guarded firmware's SetLayout). May be null.
        public Action<FanLayout> SetLayout;
        // true = the firmware reports Automático on every fan (the behavior read-back). null/false = not confirmed.
        public Func<bool?> AutoReadBack;
        // A Máximo measured on this notebook (0 = none): which way a speed should move the fan.
        public int RefMaxCpu;
        public int RefMaxGpu;
        public Action<ManualFanProgress> Progress;

        public ManualFanRunner(IFirmware fw, Action<int> sleep, FanLayout notebook)
        {
            if (fw == null) throw new ArgumentNullException("fw");
            if (sleep == null) throw new ArgumentNullException("sleep");
            this.fw = fw;
            this.sleep = sleep;
            this.notebook = notebook ?? FanLayout.Both;
        }

        // The user took the fans back to Automático (or answered "Não mudou").
        public void RequestStop() { stop = true; }

        // Any thread (cancel, exit, crash): stops now; the routine then writes Automático.
        public void Cancel()
        {
            cancelled = true;
            stop = true;
        }

        // Automático counts as confirmed only when the write was accepted AND the firmware's own read-back says Automático.
        // An unreadable answer is "not confirmed": the marker stays and the hand-back is retried by the sentinel and the next start.
        public static bool ConfirmedByReadBack(bool accepted, bool? readBack)
        {
            return accepted && readBack == true;
        }

        public ManualFanResult Run(ManualFanTarget target, int pct)
        {
            ManualFanResult res = new ManualFanResult();
            res.Target = target;
            pct = ManualFanJudge.ClampPct(pct);
            res.Pct = pct;
            FanLayout sel = ManualFanJudge.Sub(notebook, target);
            if (sel == null)
            {
                res.Error = "este notebook não tem essa ventoinha";
                res.AutoConfirmed = true;
                return res;
            }
            List<int?> cpu = new List<int?>(), gpu = new List<int?>();
            List<int?> baseCpu = new List<int?>(), baseGpu = new List<int?>();
            // Once Run starts, Automático is always written on the way out through the guarded firmware (see the finally below):
            // a stop or a cancel asked for early still ends with the fans handed back and read back.
            try
            {
                if (cancelled) throw new Stop("teste cancelado", true);
                if (stop) throw new Stop("você voltou ao automático antes de o teste aplicar a velocidade", false);
                // Automático before the write: the baseline each fan is judged against, and the temperature check.
                for (int i = 0; i < BaselineReadings; i++)
                {
                    if (i > 0) sleep(SampleMs);
                    if (cancelled) throw new Stop("teste cancelado", true);
                    if (stop) throw new Stop("você voltou ao automático antes de o teste aplicar a velocidade", false);
                    SensorReading r = fw.ReadSensors();
                    Guard(r, sel, res);
                    baseCpu.Add(r == null ? null : r.CpuFanRpm);
                    baseGpu.Add(r == null ? null : r.GpuFanRpm);
                }
                if (cancelled) throw new Stop("teste cancelado", true);
                if (stop) throw new Stop("você voltou ao automático antes de o teste aplicar a velocidade", false);
                if (SetLayout != null) SetLayout(sel);
                if (!fw.SetFanPercent(pct, pct))
                {
                    res.Error = "o firmware recusou a velocidade de " + pct.ToString(CultureInfo.InvariantCulture) + "%";
                    throw new Stop(res.Error, false);
                }
                res.Applied = true;
                while (!stop && res.HeldMs < HoldLimitMs)
                {
                    sleep(SampleMs);
                    res.HeldMs += SampleMs;
                    if (cancelled) throw new Stop("teste cancelado", true);
                    SensorReading r = fw.ReadSensors();
                    Guard(r, sel, res);
                    int? c = r == null ? null : r.CpuFanRpm;
                    int? g = r == null ? null : r.GpuFanRpm;
                    cpu.Add(c);
                    gpu.Add(g);
                    res.Readings++;
                    Report(target, pct, res.HeldMs, c, g, r, (!sel.Cpu || FanValidation.Settled(cpu, null)) && (!sel.Gpu || FanValidation.Settled(gpu, null)));
                }
                if (!stop) res.StopReason = "o tempo do teste manual acabou (" + (HoldLimitMs / 1000).ToString(CultureInfo.InvariantCulture) + " s): voltei ao automático";
            }
            catch (Stop ex)
            {
                res.StopReason = ex.Message;
                res.SkipFurther = ex.SkipFurther;
            }
            catch (Exception ex)
            {
                res.StopReason = "erro " + ex.GetType().Name;
                res.SkipFurther = true;
            }
            finally
            {
                res.AutoConfirmed = BackToAuto();
            }
            if (res.Applied && cpu.Count >= FanValidation.SettleSamples)
            {
                if (sel.Cpu) res.Cpu = ManualFanJudge.Judge("CPU", FanValidation.Median(baseCpu, false), FanValidation.Median(cpu, true), pct, RefMaxCpu);
                if (sel.Gpu) res.Gpu = ManualFanJudge.Judge("GPU", FanValidation.Median(baseGpu, false), FanValidation.Median(gpu, true), pct, RefMaxGpu);
            }
            else if (res.Applied)
            {
                string why = "leituras de menos para julgar (" + res.Readings.ToString(CultureInfo.InvariantCulture) + ")";
                if (sel.Cpu) { res.Cpu = new ManualFanVerdict(); res.Cpu.Name = "CPU"; res.Cpu.Inconclusive = true; res.Cpu.Reason = why; }
                if (sel.Gpu) { res.Gpu = new ManualFanVerdict(); res.Gpu.Name = "GPU"; res.Gpu.Inconclusive = true; res.Gpu.Reason = why; }
            }
            return res;
        }

        // The notebook's fans back to Automático, read back; a few tries, because this is the step that matters most.
        private bool BackToAuto()
        {
            for (int i = 0; i < AutoTries; i++)
            {
                try
                {
                    if (SetLayout != null) SetLayout(notebook);
                    bool ok = fw.SetFanAuto();
                    if (ok)
                    {
                        bool? rb = null;
                        try { rb = AutoReadBack == null ? (bool?)true : AutoReadBack(); }
                        catch (Exception) { rb = null; }
                        if (ConfirmedByReadBack(ok, rb)) return true;
                    }
                }
                catch (Exception)
                {
                }
                if (i < AutoTries - 1) sleep(500);
            }
            return false;
        }

        private sealed class Stop : Exception
        {
            public readonly bool SkipFurther;
            public Stop(string reason, bool skipFurther) : base(reason) { SkipFurther = skipFurther; }
        }

        // The same guard as the automatic test: no trusted temperature, no Custom; 90 °C ends it.
        private static void Guard(SensorReading r, FanLayout sel, ManualFanResult res)
        {
            double? cpuT = Temp(r == null ? null : r.CpuTempC);
            double? gpuT = Temp(r == null ? null : r.GpuTempC);
            if ((sel.Cpu && !cpuT.HasValue) || (sel.Gpu && !gpuT.HasValue) || (!cpuT.HasValue && !gpuT.HasValue))
                throw new Stop("temperatura ilegível durante o teste", true);
            double hot = Math.Max(cpuT ?? 0, gpuT ?? 0);
            if (hot >= AbortTempC)
                throw new Stop("temperatura chegou a " + ((int)hot).ToString(CultureInfo.InvariantCulture) + " °C (limite 90 °C)", true);
        }

        private static double? Temp(double? t)
        {
            if (!t.HasValue || double.IsNaN(t.Value) || double.IsInfinity(t.Value) || t.Value <= 0) return null;
            return t.Value;
        }

        private void Report(ManualFanTarget target, int pct, int held, int? c, int? g, SensorReading r, bool settled)
        {
            Action<ManualFanProgress> cb = Progress;
            if (cb == null) return;
            ManualFanProgress p = new ManualFanProgress();
            p.Target = target;
            p.Pct = pct;
            p.HeldMs = held;
            p.LimitMs = HoldLimitMs;
            p.CpuRpm = c;
            p.GpuRpm = g;
            p.CpuTempC = r == null ? null : r.CpuTempC;
            p.GpuTempC = r == null ? null : r.GpuTempC;
            p.Settled = settled;
            try { cb(p); }
            catch (Exception) { }
        }
    }
}
