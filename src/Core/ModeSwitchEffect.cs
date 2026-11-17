using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // One aligned reading used to judge a mode switch (the hub builds it from the firmware tick
    // and, while the window is visible, the CPU counters). Tick = Environment.TickCount. A field
    // that was not measured is null: nothing is carried over from an earlier reading.
    public class EffectSample
    {
        public int Tick;
        // Total CPU busy time (0-100): from the counters when sampling, from GetSystemTimes on the
        // firmware tick otherwise. Null = unknown.
        public double? LoadPct;
        public double? MaxCoreLoadPct;
        // Clock of the busy cores when there are any, else the average (MHz). Null while hidden.
        public double? ClockMHz;
        public double? PackageWatts;
        public double? CpuTempC;
        public double? GpuTempC;
        public int? CpuFanRpm;
        public int? GpuFanRpm;
        // Unknown while hidden: the detector is not fed then, its old state is not stamped on samples.
        public ThrottleState Throttle = ThrottleState.Unknown;

        // Mean of the fans that read (one if only one does).
        public double? FanRpm
        {
            get
            {
                if (CpuFanRpm.HasValue && GpuFanRpm.HasValue) return (CpuFanRpm.Value + GpuFanRpm.Value) / 2.0;
                if (CpuFanRpm.HasValue) return CpuFanRpm.Value;
                if (GpuFanRpm.HasValue) return GpuFanRpm.Value;
                return null;
            }
        }
    }

    // Builds the sample the hub feeds the tracker. Without the CPU counters (window hidden) the
    // sample carries only what the firmware tick and GetSystemTimes measured: no clock, no power,
    // and an Unknown throttle state (the detector is not fed then, so its last state is stale).
    public static class EffectSampleBuilder
    {
        public static EffectSample Make(int tick, SensorReading r, CpuSample cpu, double? fallbackLoadPct, ThrottleState detectorState)
        {
            EffectSample e = new EffectSample();
            e.Tick = tick;
            if (r != null)
            {
                e.CpuTempC = InstrumentMath.Sanitize(r.CpuTempC);
                e.GpuTempC = InstrumentMath.Sanitize(r.GpuTempC);
                e.CpuFanRpm = r.CpuFanRpm;
                e.GpuFanRpm = r.GpuFanRpm;
            }
            e.LoadPct = fallbackLoadPct;
            if (cpu != null)
            {
                if (cpu.LoadPct.HasValue) e.LoadPct = cpu.LoadPct;
                e.MaxCoreLoadPct = cpu.MaxCoreLoadPct;
                e.ClockMHz = cpu.BusyClockMHz ?? cpu.EffectiveMHz;
                e.PackageWatts = cpu.PackageWatts;
                e.Throttle = detectorState;
            }
            return e;
        }
    }

    public enum EffectStatus
    {
        // No switch yet.
        None,
        // Waiting for the 30-60 s window after the switch to complete.
        Measuring,
        // At least one difference was measured; Verdict says which (or that there is none).
        Ready,
        // The loads of the two windows are unknown or not comparable (or the PC was idle and the
        // clock cannot be compared): nothing is attributed to the mode.
        NoLoad,
        // Nothing measurable in the windows (sensors silent).
        NoData,
        // The load is known and similar, but a window has less than 30 s of valid samples.
        TooFewSamples
    }

    // What the last mode switch really did: the verdict line plus the numbers behind it. Plain
    // data for any UI. Named ModeSwitchEffect because "ModeEffect" is already the perf-test verdict.
    public class ModeSwitchEffect
    {
        public EffectStatus Status = EffectStatus.None;
        public PerfMode From;
        // False when the mode before the switch could not be read.
        public bool FromKnown = true;
        public PerfMode To;
        public string Verdict;
        public int SecondsLeft;
        public double? ClockBeforeMHz;
        public double? ClockAfterMHz;
        public double? ClockDeltaPct;
        public double? FanDeltaRpm;
        public double? CpuTempDeltaC;
        public double? PowerDeltaW;
        public ThrottleState ThrottleBefore = ThrottleState.Unknown;
        public ThrottleState ThrottleAfter = ThrottleState.Unknown;
        // Why the clock was not compared ("sem carga para comparar", ...); null when it was.
        public string ClockNote;
        public int BeforeSamples;
        public int AfterSamples;
        public int BeforeLoaded;
        public int AfterLoaded;
        // Mean busy time of the two windows (null when unknown).
        public double? LoadBefore;
        public double? LoadAfter;
    }

    // The comparison rules. Window: 60 s before the switch against 30-60 s after it (the first
    // 30 s are settling: fans ramp, temperatures lag). Nothing is attributed to the mode unless
    // the load is KNOWN on both sides and COMPARABLE.
    public static class EffectRules
    {
        public const int BeforeMs = 60000;
        public const int AfterFromMs = 30000;
        public const int AfterToMs = 60000;
        // The verdict is computed and frozen when the after window is complete.
        public const int ReadyAfterMs = AfterToMs;
        public const int KeepMs = 180000;
        public const int MaxSamples = 400;
        // A sample is "loaded" from this total busy time, or when one logical processor is this busy.
        public const double LoadedPct = 50.0;
        public const double LoadedCorePct = 90.0;
        public const int MinLoadedSamples = 3;
        public const int MinSamples = 2;
        // Valid (load known) samples must cover at least this long in EACH window.
        public const int MinCoverageMs = 30000;
        // The gap between two samples counts for at most this much coverage (a 12 s tick is fine,
        // a long pause is not).
        public const int MaxGapMs = 15000;
        // The mean busy time of the two windows may differ this much (percentage points).
        public const double LoadTolerancePts = 15.0;
        // Fans, temperature and power need this many readings in each window.
        public const int MinNoiseSamples = 4;
        public const double ClockBandPct = PerfVerdict.BandPct;
        public const double FanBandRpm = 150.0;
        public const double TempBandC = PerfVerdict.TempBandC;
        public const double PowerBandW = 2.0;
        // A difference counts only if it exceeds this many standard deviations of the "before" window.
        public const double NoiseSigmas = 2.0;
        public const double LimitedShare = 0.5;

        // Why the clock was not compared (ModeSwitchEffect.ClockNote).
        public const string NoteUnavailable = "indisponível";
        public const string NoteNoLoad = "sem carga para comparar";
        // The whole comparison refused (verdict text).
        public const string NotComparable = "sem carga comparável para medir";
        public const string NotEnoughSamples = "amostras insuficientes para medir";
        // The firmware tick stays at 2 s for this long after a switch (NeedsFastTick).
        public const int FastTickMs = 75000;

        public static bool IsLoaded(EffectSample s)
        {
            if (s.LoadPct.HasValue && s.LoadPct.Value >= LoadedPct) return true;
            return s.MaxCoreLoadPct.HasValue && s.MaxCoreLoadPct.Value >= LoadedCorePct;
        }

        // Time covered by the samples: each gap to the next sample counts up to MaxGapMs.
        public static int CoverageMs(IList<EffectSample> l)
        {
            long total = 0;
            for (int i = 0; i + 1 < l.Count; i++)
            {
                int gap = unchecked(l[i + 1].Tick - l[i].Tick);
                if (gap < 0) continue;
                total += Math.Min(gap, MaxGapMs);
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        public static ModeSwitchEffect Compute(PerfMode? from, PerfMode to, IList<EffectSample> before, IList<EffectSample> after)
        {
            ModeSwitchEffect e = new ModeSwitchEffect();
            e.From = from.HasValue ? from.Value : to;
            e.FromKnown = from.HasValue;
            e.To = to;
            e.BeforeSamples = before.Count;
            e.AfterSamples = after.Count;
            if (before.Count < MinSamples || after.Count < MinSamples)
            {
                e.Status = EffectStatus.NoData;
                e.Verdict = before.Count < MinSamples
                    ? "sem dados de antes da troca para comparar"
                    : "sem dados suficientes depois da troca";
                return e;
            }

            // 1. The load must be known on both sides, cover 30 s each, and match within 15 points.
            List<EffectSample> vb = Where(before, delegate(EffectSample s) { return s.LoadPct.HasValue; });
            List<EffectSample> va = Where(after, delegate(EffectSample s) { return s.LoadPct.HasValue; });
            e.LoadBefore = Mean(vb, delegate(EffectSample s) { return s.LoadPct; }, null, 1);
            e.LoadAfter = Mean(va, delegate(EffectSample s) { return s.LoadPct; }, null, 1);
            if (!e.LoadBefore.HasValue || !e.LoadAfter.HasValue)
            {
                // The load is unknown on a side (no sample carries one): not comparable.
                e.Status = EffectStatus.NoLoad;
                e.Verdict = NotComparable;
                return e;
            }
            if (Math.Abs(e.LoadBefore.Value - e.LoadAfter.Value) > LoadTolerancePts)
            {
                e.Status = EffectStatus.NoLoad;
                e.Verdict = NotComparable;
                return e;
            }
            if (CoverageMs(vb) < MinCoverageMs || CoverageMs(va) < MinCoverageMs)
            {
                // The loads match but there are too few samples: say so, not "different load".
                e.Status = EffectStatus.TooFewSamples;
                e.Verdict = NotEnoughSamples;
                return e;
            }

            // 2. Clock under comparable load (loaded samples only).
            Func<EffectSample, bool> loadedWithClock = delegate(EffectSample s) { return s.ClockMHz.HasValue && IsLoaded(s); };
            List<EffectSample> lb = Where(vb, loadedWithClock);
            List<EffectSample> la = Where(va, loadedWithClock);
            e.BeforeLoaded = lb.Count;
            e.AfterLoaded = la.Count;
            if (!AnyClock(before) && !AnyClock(after)) e.ClockNote = NoteUnavailable;
            else if (lb.Count < MinLoadedSamples || la.Count < MinLoadedSamples) e.ClockNote = NoteNoLoad;
            else
            {
                double? ulb = Mean(lb, delegate(EffectSample s) { return s.LoadPct; }, null, 1);
                double? ula = Mean(la, delegate(EffectSample s) { return s.LoadPct; }, null, 1);
                if (ulb.HasValue && ula.HasValue && Math.Abs(ulb.Value - ula.Value) > LoadTolerancePts) e.ClockNote = NoteNoLoad;
                else
                {
                    e.ClockBeforeMHz = Mean(lb, delegate(EffectSample s) { return s.ClockMHz; }, null, 1);
                    e.ClockAfterMHz = Mean(la, delegate(EffectSample s) { return s.ClockMHz; }, null, 1);
                    e.ClockDeltaPct = PerfVerdict.Diff(e.ClockAfterMHz, e.ClockBeforeMHz);
                    if (!e.ClockDeltaPct.HasValue) e.ClockNote = NoteUnavailable;
                }
            }

            // 3. Fans, temperature and power: enough readings, and a difference beyond the noise.
            double clockBand = ClockBandPct;
            if (e.ClockDeltaPct.HasValue)
            {
                double? sd = StdDev(lb, delegate(EffectSample s) { return s.ClockMHz; });
                if (sd.HasValue && e.ClockBeforeMHz.Value > 0) clockBand = Math.Max(ClockBandPct, NoiseSigmas * sd.Value / e.ClockBeforeMHz.Value * 100.0);
            }
            double fanBand, tempBand, powerBand;
            e.FanDeltaRpm = Delta(vb, va, delegate(EffectSample s) { return s.FanRpm; }, null, FanBandRpm, out fanBand);
            e.CpuTempDeltaC = Delta(vb, va, delegate(EffectSample s) { return s.CpuTempC; }, ValidTemp, TempBandC, out tempBand);
            e.PowerDeltaW = Delta(vb, va, delegate(EffectSample s) { return s.PackageWatts; }, null, PowerBandW, out powerBand);

            e.ThrottleBefore = Dominant(vb);
            e.ThrottleAfter = Dominant(va);

            List<string> parts = new List<string>();
            if (e.ClockDeltaPct.HasValue)
            {
                if (Math.Abs(e.ClockDeltaPct.Value) >= clockBand) parts.Add(PerfVerdict.Pct(e.ClockDeltaPct.Value) + " de clock sob carga");
                else parts.Add("clock igual sob carga");
            }
            if (e.FanDeltaRpm.HasValue && Math.Abs(e.FanDeltaRpm.Value) >= fanBand)
                parts.Add("ventoinha " + Signed(e.FanDeltaRpm.Value) + " rpm");
            if (e.CpuTempDeltaC.HasValue && Math.Abs(e.CpuTempDeltaC.Value) >= tempBand)
                parts.Add("CPU " + Signed(e.CpuTempDeltaC.Value) + " °C");
            if (e.PowerDeltaW.HasValue && Math.Abs(e.PowerDeltaW.Value) >= powerBand)
                parts.Add(Signed(e.PowerDeltaW.Value) + " W do pacote");
            string limit = ThrottleChange(e.ThrottleBefore, e.ThrottleAfter);
            if (limit != null) parts.Add(limit);

            string name = PerfVerdict.ModeName(to);
            bool measured = e.ClockDeltaPct.HasValue || e.FanDeltaRpm.HasValue || e.CpuTempDeltaC.HasValue || e.PowerDeltaW.HasValue;
            if (parts.Count > 0)
            {
                e.Status = EffectStatus.Ready;
                e.Verdict = name + ": " + string.Join(", ", parts.ToArray());
                if (!e.ClockDeltaPct.HasValue && e.ClockNote != null) e.Verdict += " (clock: " + e.ClockNote + ")";
            }
            else if (e.ClockDeltaPct.HasValue || (measured && e.ClockNote == null))
            {
                e.Status = EffectStatus.Ready;
                e.Verdict = name + ": sem diferença mensurável";
            }
            else if (e.ClockNote == NoteNoLoad)
            {
                e.Status = EffectStatus.NoLoad;
                e.Verdict = NoteNoLoad;
            }
            else if (measured)
            {
                e.Status = EffectStatus.Ready;
                e.Verdict = name + ": sem diferença mensurável (clock indisponível)";
            }
            else
            {
                e.Status = EffectStatus.NoData;
                e.Verdict = "sem dados para comparar";
            }
            return e;
        }

        // "+900", "−1.200" (real minus, pt-BR grouping), whole numbers.
        public static string Signed(double v)
        {
            double r = Math.Round(v, MidpointRounding.AwayFromZero);
            if (r == 0) return "0";
            return (r > 0 ? "+" : "−") + InstrumentMath.FormatGrouped((long)Math.Abs(r));
        }

        private static bool ValidTemp(double v)
        {
            return InstrumentMath.IsValidTemp(v);
        }

        private static bool AnyClock(IList<EffectSample> l)
        {
            foreach (EffectSample s in l)
            {
                if (s.ClockMHz.HasValue) return true;
            }
            return false;
        }

        private static List<EffectSample> Where(IList<EffectSample> l, Func<EffectSample, bool> f)
        {
            List<EffectSample> r = new List<EffectSample>();
            foreach (EffectSample s in l)
            {
                if (f(s)) r.Add(s);
            }
            return r;
        }

        private static double? Mean(IList<EffectSample> l, Func<EffectSample, double?> pick, Func<double, bool> valid, int minCount)
        {
            double sum = 0;
            int n = 0;
            foreach (EffectSample s in l)
            {
                double? v = pick(s);
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) continue;
                if (valid != null && !valid(v.Value)) continue;
                sum += v.Value;
                n++;
            }
            return n < minCount ? (double?)null : sum / n;
        }

        private static double? StdDev(IList<EffectSample> l, Func<EffectSample, double?> pick)
        {
            double? mean = Mean(l, pick, null, 2);
            if (!mean.HasValue) return null;
            double ss = 0;
            int n = 0;
            foreach (EffectSample s in l)
            {
                double? v = pick(s);
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) continue;
                ss += (v.Value - mean.Value) * (v.Value - mean.Value);
                n++;
            }
            return n < 2 ? (double?)null : Math.Sqrt(ss / (n - 1));
        }

        // After minus before, from at least MinNoiseSamples readings in each window; band = the fixed
        // band or NoiseSigmas standard deviations of the "before" window, whichever is larger.
        private static double? Delta(IList<EffectSample> before, IList<EffectSample> after, Func<EffectSample, double?> pick,
            Func<double, bool> valid, double fixedBand, out double band)
        {
            band = fixedBand;
            double? b = Mean(before, pick, valid, MinNoiseSamples);
            double? a = Mean(after, pick, valid, MinNoiseSamples);
            if (!b.HasValue || !a.HasValue) return null;
            double? sd = StdDev(before, pick);
            if (sd.HasValue) band = Math.Max(fixedBand, NoiseSigmas * sd.Value);
            return a.Value - b.Value;
        }

        // The limited state in at least half of the readings that know their state; Normal when
        // none is limited; Unknown when no reading knows.
        private static ThrottleState Dominant(IList<EffectSample> l)
        {
            int known = 0, temp = 0, power = 0;
            foreach (EffectSample s in l)
            {
                if (s.Throttle == ThrottleState.Unknown) continue;
                known++;
                if (s.Throttle == ThrottleState.TemperatureLimited) temp++;
                else if (s.Throttle == ThrottleState.PowerLimited) power++;
            }
            if (known == 0) return ThrottleState.Unknown;
            if (temp >= known * LimitedShare && temp >= power) return ThrottleState.TemperatureLimited;
            if (power >= known * LimitedShare) return ThrottleState.PowerLimited;
            return ThrottleState.Normal;
        }

        private static string ThrottleChange(ThrottleState before, ThrottleState after)
        {
            if (before == ThrottleState.Unknown || after == ThrottleState.Unknown || before == after) return null;
            if (ThrottleText.IsLimited(after) && !ThrottleText.IsLimited(before))
                return "passou a ser limitado por " + (after == ThrottleState.TemperatureLimited ? "temperatura" : "energia");
            if (ThrottleText.IsLimited(before) && !ThrottleText.IsLimited(after))
                return "deixou de ser limitado por " + (before == ThrottleState.TemperatureLimited ? "temperatura" : "energia");
            return "limitação mudou de " + (before == ThrottleState.TemperatureLimited ? "temperatura" : "energia")
                + " para " + (after == ThrottleState.TemperatureLimited ? "temperatura" : "energia");
        }
    }

    // Keeps the last minutes of samples and judges the most recent mode switch. The window before
    // the switch is copied at MarkSwitch and the window after is collected as samples arrive, so
    // the verdict is computed and FROZEN by the sampling side (Add/Evaluate with the sampling
    // clock) when the 60 s mark passes, whether or not any UI ever asks. Pure: the caller passes
    // Environment.TickCount values. Not thread-safe (UI thread only).
    public sealed class ModeSwitchTracker
    {
        private readonly List<EffectSample> samples = new List<EffectSample>();
        private readonly List<EffectSample> before = new List<EffectSample>();
        private readonly List<EffectSample> after = new List<EffectSample>();
        private bool hasSwitch;
        private PerfMode? from;
        private PerfMode to;
        private int switchTick;
        private ModeSwitchEffect frozen;

        public ModeSwitchEffect Last { get; private set; }

        public int SampleCount { get { return samples.Count; } }

        public void Add(EffectSample s)
        {
            if (s == null) return;
            samples.Add(s);
            while (samples.Count > 0 && (samples.Count > EffectRules.MaxSamples
                || unchecked(s.Tick - samples[0].Tick) > EffectRules.KeepMs))
            {
                samples.RemoveAt(0);
            }
            if (hasSwitch && frozen == null)
            {
                int rel = unchecked(s.Tick - switchTick);
                if (rel >= EffectRules.AfterFromMs && rel <= EffectRules.AfterToMs) after.Add(s);
                else if (rel > EffectRules.AfterToMs) Freeze();
            }
        }

        // A confirmed mode switch (read-back done). The previous pending measurement is dropped;
        // switching to the mode already active is not a switch. fromMode = null: it could not be read.
        public void MarkSwitch(PerfMode? fromMode, PerfMode toMode, int tick)
        {
            if (fromMode.HasValue && fromMode.Value == toMode) return;
            hasSwitch = true;
            from = fromMode;
            to = toMode;
            switchTick = tick;
            frozen = null;
            Last = null;
            before.Clear();
            after.Clear();
            foreach (EffectSample s in samples)
            {
                int rel = unchecked(s.Tick - tick);
                if (rel < 0 && rel >= -EffectRules.BeforeMs) before.Add(s);
            }
        }

        private void Freeze()
        {
            frozen = EffectRules.Compute(from, to, before, after);
            Last = frozen;
            before.Clear();
            after.Clear();
        }

        // True for 75 s after a switch: the firmware tick must stay at 2 s even with the window
        // hidden (PollPolicy), so the 30-60 s window gets the 30 s of coverage the proof requires.
        public bool NeedsFastTick(int nowTick)
        {
            if (!hasSwitch) return false;
            int age = unchecked(nowTick - switchTick);
            return age >= 0 && age <= EffectRules.FastTickMs;
        }

        // Called by the sampling side on every tick as well as by any UI: cheap while measuring.
        public ModeSwitchEffect Evaluate(int nowTick)
        {
            if (!hasSwitch)
            {
                ModeSwitchEffect none = new ModeSwitchEffect();
                Last = none;
                return none;
            }
            if (frozen != null) return frozen;
            int age = unchecked(nowTick - switchTick);
            if (age < EffectRules.ReadyAfterMs)
            {
                ModeSwitchEffect m = new ModeSwitchEffect();
                m.Status = EffectStatus.Measuring;
                m.From = from.HasValue ? from.Value : to;
                m.FromKnown = from.HasValue;
                m.To = to;
                m.SecondsLeft = Math.Max(1, (int)Math.Ceiling((EffectRules.ReadyAfterMs - Math.Max(0, age)) / 1000.0));
                m.Verdict = "ainda medindo (faltam " + m.SecondsLeft.ToString(CultureInfo.InvariantCulture) + " s)";
                Last = m;
                return m;
            }
            Freeze();
            return frozen;
        }
    }
}
