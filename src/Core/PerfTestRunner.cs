using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // The CPU load: Start resets Units to 0 and runs `threads` workers; Stop ends them and
    // waits (idempotent, safe from any thread). Units counts finished work units of PerfKernel.
    public interface IPerfLoad
    {
        void Start(int threads);
        void Stop();
        long Units { get; }
    }

    // Per-second readings that do not come from the firmware. null = not available.
    public interface IPerfProbe
    {
        double? ClockMHz();
        double? DischargeMw();
        // Total CPU use of the machine, percent.
        double? CpuLoadPct();
        // AC / battery and charge, read again every second.
        PerfPower Power();
    }

    // The performance test (SenseMonitor.exe --teste-desempenho). Four runs in a balanced order,
    // Dia a dia, Silencioso, Desempenho, Dia a dia (the two Dia a dia runs give the reference
    // and a measure of drift). For each: set the mode (with read-back), force the fans to
    // Automático, cool down (up to 90 s, never shortened), idle 15 s, 60 s of multi-core load,
    // 20 s of single-core load, sampling every second. >= 90 °C stops the load at once (also
    // through the independent HeatWatchdog), and so does losing the CPU temperature for 3 s.
    // Whatever happens (cancel, exception, heat, power loss) the routine ends with the mode
    // Dia a dia (read back) and the fans in Automático. Every step is journaled to disk first.
    public sealed class PerfTestRunner
    {
        public const int SampleMs = 1000;
        public const int SettleMs = 2000;
        // A heartbeat line in the journal every this many samples of a phase.
        public const int HeartbeatSamples = 10;

        public static readonly PerfMode[] Order = { PerfMode.DiaADia, PerfMode.Silencioso, PerfMode.Desempenho, PerfMode.DiaADia };

        private readonly IFirmware fw;
        private readonly IPerfLoad load;
        private readonly IPerfProbe probe;
        private readonly Action<int> sleep;
        private readonly Func<long> nowMs;
        private readonly int threads;
        private volatile bool cancelled;
        private long testStartMs;
        private bool startedOnBattery;

        // (what is happening now, estimated seconds left); called from the worker thread.
        public Action<string, int> Progress;
        // Optional independent heat guard and on-disk journal.
        public IPerfGuard Guard;
        public IPerfJournal Journal;

        public PerfTestRunner(IFirmware fw, IPerfLoad load, IPerfProbe probe, Action<int> sleep, Func<long> nowMs, int threads)
        {
            if (fw == null) throw new ArgumentNullException("fw");
            if (load == null) throw new ArgumentNullException("load");
            if (probe == null) throw new ArgumentNullException("probe");
            if (sleep == null) throw new ArgumentNullException("sleep");
            if (nowMs == null) throw new ArgumentNullException("nowMs");
            this.fw = fw;
            this.load = load;
            this.probe = probe;
            this.sleep = sleep;
            this.nowMs = nowMs;
            this.threads = threads < 1 ? 1 : threads;
        }

        // Any thread (Cancel button, exit, crash path): stops at the next sample.
        public void Cancel()
        {
            cancelled = true;
        }

        private sealed class AbortException : Exception
        {
            public readonly bool IsCancel;
            public AbortException(string reason, bool cancel) : base(reason) { IsCancel = cancel; }
        }

        private void CheckCancel()
        {
            if (cancelled) throw new AbortException("teste cancelado", true);
        }

        private double ElapsedSec()
        {
            return (nowMs() - testStartMs) / 1000.0;
        }

        private void J(string kind, string detail)
        {
            IPerfJournal j = Journal;
            if (j == null) return;
            try { j.Write(kind, detail); }
            catch (Exception) { }
        }

        public PerfTestReport Run(PerfPreconditions pre)
        {
            PerfTestReport rep = new PerfTestReport();
            rep.StartedUtc = DateTime.UtcNow;
            if (pre != null) rep.Pre = pre;
            rep.Pre.LoadThreads = threads;
            startedOnBattery = rep.Pre.OnBattery == true;
            testStartMs = nowMs();
            J(PerfJournal.Start, "ordem " + OrderText() + "; " + threads + " threads");
            try
            {
                CheckCancel();
                J("MODO", "ventoinhas: escrevendo Automático");
                if (!fw.SetFanAuto()) throw new AbortException("o firmware recusou o modo automático das ventoinhas", false);
                Report("Medindo a temperatura de repouso", Estimate(0, PerfPhase.Cooldown, 0));
                sleep(SettleMs);
                rep.BaselineCpuTempC = Baseline();
                if (!rep.BaselineCpuTempC.HasValue) throw new AbortException("temperatura da CPU ilegível antes do teste", false);

                for (int i = 0; i < Order.Length; i++)
                {
                    CheckCancel();
                    PerfModeRun run = new PerfModeRun();
                    run.Mode = Order[i];
                    run.Order = i;
                    rep.Runs.Add(run);
                    RunMode(run, i, rep);
                    run.Stats = PerfScoring.Summarize(run);
                }
            }
            catch (AbortException ex)
            {
                rep.Aborted = true;
                rep.Cancelled = ex.IsCancel;
                rep.Reason = ex.Message;
                J("ABORTO", ex.Message);
            }
            catch (Exception ex)
            {
                rep.Aborted = true;
                rep.Reason = "erro " + ex.GetType().Name;
                J("ABORTO", rep.Reason);
            }
            finally
            {
                try { load.Stop(); }
                catch (Exception) { }
                try { if (Guard != null) Guard.Disarm(); }
                catch (Exception) { }
                J("RESTAURANDO", "voltando para Dia a dia e ventoinhas em Automático");
                RestoreSafeState(rep);
                J(PerfJournal.End, (rep.Aborted ? "abortado" : "completo") + "; modo " + (rep.FinalModeOk ? "ok" : "NAO-confirmado")
                    + "; ventoinhas " + (rep.FinalFansAuto ? "ok" : "NAO-confirmadas"));
            }
            foreach (PerfModeRun r in rep.Runs)
            {
                if (r.Stats == null) r.Stats = PerfScoring.Summarize(r);
            }
            rep.TotalSec = ElapsedSec();
            return rep;
        }

        private static string OrderText()
        {
            List<string> n = new List<string>();
            foreach (PerfMode m in Order) n.Add(PerfVerdict.ModeName(m));
            return string.Join(" > ", n.ToArray());
        }

        // Dia a dia (read back) and fans Automático, with retries; never throws.
        private void RestoreSafeState(PerfTestReport rep)
        {
            rep.FinalModeOk = false;
            for (int attempt = 0; attempt < 3 && !rep.FinalModeOk; attempt++)
            {
                try
                {
                    PerfMode? back = null;
                    bool ok = fw.SetMode(PerfMode.DiaADia);
                    try { back = fw.GetMode(); }
                    catch (Exception) { back = null; }
                    rep.FinalModeOk = ok && back.HasValue && back.Value == PerfMode.DiaADia;
                }
                catch (Exception) { }
                if (!rep.FinalModeOk) { try { sleep(500); } catch (Exception) { } }
            }
            rep.FinalFansAuto = false;
            for (int attempt = 0; attempt < 3 && !rep.FinalFansAuto; attempt++)
            {
                try { rep.FinalFansAuto = fw.SetFanAuto(); }
                catch (Exception) { }
                if (!rep.FinalFansAuto) { try { sleep(500); } catch (Exception) { } }
            }
        }

        private double? Baseline()
        {
            List<double> v = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) sleep(SampleMs);
                SensorReading r = fw.ReadSensors();
                double? t = Temp(r == null ? null : r.CpuTempC);
                if (t.HasValue) v.Add(t.Value);
            }
            if (v.Count == 0) return null;
            v.Sort();
            return v[(v.Count - 1) / 2];
        }

        private int Estimate(int modeIndex, PerfPhase phase, double phaseElapsed)
        {
            return PerfPlan.RemainingSec(modeIndex, Order.Length, phase, phaseElapsed);
        }

        private void Report(string text, int remainingSec)
        {
            Action<string, int> p = Progress;
            if (p == null) return;
            try { p(text, remainingSec); }
            catch (Exception) { }
        }

        private void RunMode(PerfModeRun run, int index, PerfTestReport rep)
        {
            string name = PerfVerdict.ModeName(run.Mode);
            Report(name + ": trocando de modo", Estimate(index, PerfPhase.Cooldown, 0));
            J("MODO", name + ": escrevendo o modo");
            bool ok = fw.SetMode(run.Mode);
            sleep(1500);
            PerfMode? back = fw.GetMode();
            run.Applied = ok && back.HasValue && back.Value == run.Mode;
            run.SwitchNote = "escrita " + (ok ? "ok" : "falhou") + ", lido " + (back.HasValue ? PerfVerdict.ModeName(back.Value) : "-");
            J("MODO", name + ": " + run.SwitchNote);
            if (!run.Applied) return; // recorded; the next mode goes on
            // The mode change may touch the fan behavior: keep them in Automático.
            J("MODO", name + ": ventoinhas escrevendo Automático");
            if (!fw.SetFanAuto()) throw new AbortException("o firmware recusou manter as ventoinhas em automático", false);

            Cooldown(run, index, rep);
            if (!SafeToLoad(run)) return;

            Phase(run, PerfPhase.Idle, PerfPlan.IdleSec, 0, index, false);
            if (run.Aborted) return;
            Phase(run, PerfPhase.Multi, PerfPlan.MultiSec, threads, index, true);
            if (run.Aborted) return;
            Phase(run, PerfPhase.Single, PerfPlan.SingleSec, 1, index, true);
        }

        private bool SafeToLoad(PerfModeRun run)
        {
            double? cpu = run.StartTempC;
            if (PerfPlan.SafeToStartLoad(cpu)) return true;
            run.Aborted = true;
            run.AbortReason = cpu.HasValue
                ? "CPU a " + ((int)cpu.Value).ToString(CultureInfo.InvariantCulture) + " °C: quente demais para começar a carga"
                : "temperatura da CPU ilegível antes da carga";
            J("ABORTO", PerfVerdict.ModeName(run.Mode) + ": " + run.AbortReason);
            return false;
        }

        private void CheckPower()
        {
            PerfPower p;
            try { p = probe.Power(); }
            catch (Exception) { p = null; }
            string why;
            if (PerfPlan.PowerAbort(startedOnBattery, p, out why)) throw new AbortException(why, false);
        }

        // The wait is never shortened (reference runs included): up to 90 s, until the CPU is
        // within 4 degrees of the temperature measured before the first run.
        private void Cooldown(PerfModeRun run, int index, PerfTestReport rep)
        {
            double target = PerfPlan.CooldownTarget(rep.BaselineCpuTempC.Value);
            long start = nowMs();
            long next = start;
            bool first = true;
            PerfSample s = null;
            J("FASE", PerfVerdict.ModeName(run.Mode) + " resfriamento (meta " + ((int)Math.Ceiling(target)).ToString(CultureInfo.InvariantCulture) + " C)");
            while (true)
            {
                CheckCancel();
                CheckPower();
                s = Read(PerfPhase.Cooldown, start, 0);
                run.Samples.Add(s);
                if (first) { run.CooldownStartTempC = s.CpuTempC; first = false; }
                if (s.CpuTempC.HasValue && s.CpuTempC.Value <= target) { run.CooldownReached = true; break; }
                if (s.T >= PerfPlan.CooldownMaxSec) break;
                Report(PerfVerdict.ModeName(run.Mode) + ": esfriando (CPU " + TempText(s.CpuTempC) + ", meta " + ((int)Math.Ceiling(target)).ToString(CultureInfo.InvariantCulture) + " °C)",
                    Estimate(index, PerfPhase.Cooldown, s.T));
                next += SampleMs;
                Wait(next);
            }
            run.CooldownSec = (nowMs() - start) / 1000.0;
            run.StartTempC = s == null ? null : s.CpuTempC;
        }

        private static string TempText(double? t)
        {
            return t.HasValue ? ((int)Math.Round(t.Value)).ToString(CultureInfo.InvariantCulture) + " °C" : "?";
        }

        private void Wait(long untilMs)
        {
            long wait = untilMs - nowMs();
            if (wait > 0) sleep((int)wait);
        }

        // One timed phase. Loads count work units; every sample is checked against 90 °C, the
        // independent guard is armed while the load runs.
        private void Phase(PerfModeRun run, PerfPhase phase, int durationSec, int loadThreads, int index, bool loaded)
        {
            string name = PerfVerdict.ModeName(run.Mode);
            string what = phase == PerfPhase.Idle ? "repouso" : (phase == PerfPhase.Multi ? "carga em todos os núcleos" : "carga em um núcleo");
            long start = nowMs();
            long next = start;
            int blind = 0;
            int count = 0;
            double? lastCpu = run.StartTempC;
            J("FASE", name + " " + what + " (cpu " + TempText(lastCpu) + ")");
            try
            {
                if (loadThreads > 0)
                {
                    if (Guard != null) Guard.Arm();
                    load.Start(loadThreads);
                }
                while (true)
                {
                    next += SampleMs;
                    Wait(next);
                    CheckCancel();
                    CheckPower();
                    PerfSample s = Read(phase, start, loadThreads > 0 ? load.Units : 0);
                    run.Samples.Add(s);
                    count++;
                    if (loaded)
                    {
                        string reason = null;
                        if (Guard != null && Guard.Tripped) reason = Guard.TripReason;
                        else if (PerfPlan.MustStopLoad(s.CpuTempC, s.GpuTempC, out reason)) { }
                        else
                        {
                            blind = s.CpuTempC.HasValue ? 0 : blind + 1;
                            if (blind >= PerfPlan.BlindSamplesLimit)
                                reason = "temperatura da CPU ilegível por " + blind + " s durante a carga";
                        }
                        if (reason != null)
                        {
                            load.Stop();
                            run.Aborted = true;
                            run.AbortReason = reason;
                            J("ABORTO", name + " " + what + ": " + reason);
                            return;
                        }
                    }
                    if (s.CpuTempC.HasValue) lastCpu = s.CpuTempC;
                    if (count % HeartbeatSamples == 0)
                        J("AMOSTRA", name + " " + what + " t=" + ((int)s.T).ToString(CultureInfo.InvariantCulture) + " s cpu " + TempText(lastCpu));
                    Report(name + ": " + what + " (" + ((int)s.T).ToString(CultureInfo.InvariantCulture) + " de " + durationSec + " s, CPU " + TempText(s.CpuTempC) + ")",
                        Estimate(index, phase, s.T));
                    if (s.T >= durationSec - 0.5) break;
                }
            }
            finally
            {
                if (loadThreads > 0)
                {
                    load.Stop();
                    if (Guard != null) Guard.Disarm();
                }
            }
        }

        // Units and the clock are captured together, before the (slower) sensor calls.
        private PerfSample Read(PerfPhase phase, long phaseStartMs, long units)
        {
            double t = (nowMs() - phaseStartMs) / 1000.0;
            SensorReading r = fw.ReadSensors();
            PerfSample s = new PerfSample();
            s.Phase = phase;
            s.T = t;
            s.CpuTempC = Temp(r == null ? null : r.CpuTempC);
            s.GpuTempC = Temp(r == null ? null : r.GpuTempC);
            s.CpuRpm = r == null ? null : r.CpuFanRpm;
            s.GpuRpm = r == null ? null : r.GpuFanRpm;
            s.Units = units;
            try { s.ClockMHz = probe.ClockMHz(); }
            catch (Exception) { s.ClockMHz = null; }
            try { s.DischargeMw = probe.DischargeMw(); }
            catch (Exception) { s.DischargeMw = null; }
            try { s.CpuLoadPct = probe.CpuLoadPct(); }
            catch (Exception) { s.CpuLoadPct = null; }
            return s;
        }

        // null = unreadable (missing, NaN, infinite or <= 0).
        private static double? Temp(double? t)
        {
            if (!t.HasValue || double.IsNaN(t.Value) || double.IsInfinity(t.Value) || t.Value <= 0) return null;
            return t.Value;
        }
    }
}
