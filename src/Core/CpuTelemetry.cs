using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // One reading of the CPU counters of Windows ("Processor Information" and "Energy Meter").
    // Every field may be null: a counter that does not exist on a machine stays null and the
    // rest still works. Plain data, no behavior, so any UI can bind to it.
    public class CpuSample
    {
        // Average clock of all logical processors (MHz).
        public double? EffectiveMHz;
        // Clock of the busy cores only (weighted by their use); the number that says whether
        // the CPU boosts under a game. Null when no core is busy.
        public double? BusyClockMHz;
        // "% Processor Performance" of the SATURATED cores only (use of 85 % or more; 100 = nominal
        // clock, above 100 = turbo). A core that is not saturated runs slow because Windows asks
        // for less, which is no evidence of a limit. Null when no core is saturated.
        public double? SaturatedPerfPct;
        // "% Performance Limit": 100 = nothing limits the CPU, lower = a limit is active.
        public double? LimitPct;
        // Total CPU busy time (0-100, "% Processor Time": time busy, whatever the clock; not the
        // clock-normalized "% Processor Utility", which reads low exactly when a core is throttled).
        public double? LoadPct;
        // Busy time of the busiest logical processor (0-100).
        public double? MaxCoreLoadPct;
        // Package power in watts from the "Energy Meter" counter (RAPL). Null when Windows
        // does not expose it on this machine.
        public double? PackageWatts;
    }

    public enum ThrottleState
    {
        Unknown,
        Normal,
        TemperatureLimited,
        PowerLimited
    }

    // Decides whether the CPU is being throttled from counters + CPU temperature, with
    // hysteresis so the label does not flicker. Pure and deterministic: one Feed per sample.
    //
    // Evidence of a limit (either one):
    //   - "% Performance Limit" below 98 % WHILE the CPU is loaded (50 % total or a core at 85 %):
    //     Windows/firmware say the CPU is held back;
    //   - a saturated core (busy time 85 % or more) runs below the nominal clock (under 90 %): a CPU
    //     that is not held back boosts above nominal when it is asked for everything.
    // Cause: CPU temperature at or above NearLimitC means "temperatura", below 80 °C "energia",
    // 80 to NearLimitC cannot be told (chassis limits sit there): Unknown; no valid temperature: Unknown.
    // Hysteresis: a limited state needs EnterSamples samples in a row to appear, Normal needs
    // ExitSamples in a row to come back; the first state after Unknown takes one sample.
    public sealed class ThrottleDetector
    {
        public const double LimitedBelowPct = 98.0;
        public const double SubNominalPct = 90.0;
        public const double DefaultNearLimitC = 93.0;
        // From here to NearLimitC the cause of a limit is ambiguous.
        public const double AmbiguousFromC = 80.0;
        public const double LoadedPct = 50.0;
        public const double LoadedCorePct = 85.0;
        public const int DefaultEnterSamples = 3;
        public const int DefaultExitSamples = 5;

        private readonly double nearLimitC;
        private readonly int enterSamples;
        private readonly int exitSamples;
        private ThrottleState state = ThrottleState.Unknown;
        private ThrottleState candidate = ThrottleState.Unknown;
        private int run;

        public ThrottleDetector() : this(DefaultNearLimitC, DefaultEnterSamples, DefaultExitSamples)
        {
        }

        public ThrottleDetector(double nearLimitC, int enterSamples, int exitSamples)
        {
            this.nearLimitC = nearLimitC;
            this.enterSamples = Math.Max(1, enterSamples);
            this.exitSamples = Math.Max(1, exitSamples);
        }

        public ThrottleState State { get { return state; } }

        // Short statement of the evidence behind the current candidate (for a tooltip / log).
        public string Evidence { get; private set; }

        public void Reset()
        {
            state = ThrottleState.Unknown;
            candidate = ThrottleState.Unknown;
            run = 0;
            Evidence = null;
        }

        public static ThrottleState Classify(CpuSample s, double? cpuTempC, double nearLimitC, out string evidence)
        {
            evidence = null;
            if (s == null) return ThrottleState.Unknown;
            bool haveLimit = s.LimitPct.HasValue;
            double? perf = s.SaturatedPerfPct;
            if (!haveLimit && !perf.HasValue)
            {
                // No limit counter and no busy core. If the clock counters work, an idle CPU is
                // not being held back; if they do not exist there is nothing to judge by.
                return s.EffectiveMHz.HasValue ? ThrottleState.Normal : ThrottleState.Unknown;
            }
            // "% Performance Limit" also reflects Windows policy ceilings (battery, efficiency mode,
            // maximum processor state) that are not throttling, and an idle CPU has nothing to lose:
            // it counts only while the CPU is loaded.
            bool limitCounter = haveLimit && s.LimitPct.Value < LimitedBelowPct && IsLoaded(s);
            bool subNominal = perf.HasValue && perf.Value < SubNominalPct;
            if (!limitCounter && !subNominal) return ThrottleState.Normal;

            evidence = limitCounter
                ? "limite de desempenho em " + Math.Round(s.LimitPct.Value, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %"
                : "núcleo ocupado abaixo do clock nominal";
            if (!InstrumentMath.IsValidTemp(cpuTempC)) return ThrottleState.Unknown;
            if (cpuTempC.Value >= nearLimitC) return ThrottleState.TemperatureLimited;
            if (cpuTempC.Value >= AmbiguousFromC)
            {
                // Chassis/EC thermal limits sit below the CPU's own: heat or power cannot be told apart.
                evidence += "; causa incerta (energia ou calor)";
                return ThrottleState.Unknown;
            }
            return ThrottleState.PowerLimited;
        }

        // Loaded enough for a limit to matter: total use of 50 % or more, or one logical processor
        // at 85 % or more (a game with few busy threads). Measured by busy TIME (% Processor Time).
        public static bool IsLoaded(CpuSample s)
        {
            if (s.LoadPct.HasValue && s.LoadPct.Value >= LoadedPct) return true;
            return s.MaxCoreLoadPct.HasValue && s.MaxCoreLoadPct.Value >= LoadedCorePct;
        }

        public ThrottleState Feed(CpuSample sample, double? cpuTempC)
        {
            string ev;
            ThrottleState c = Classify(sample, cpuTempC, nearLimitC, out ev);
            if (c == state)
            {
                candidate = c;
                run = 0;
                Evidence = ev;
                return state;
            }
            if (c == candidate) run++;
            else
            {
                candidate = c;
                run = 1;
            }
            int need;
            if (state == ThrottleState.Unknown) need = 1;
            else if (c == ThrottleState.Normal || c == ThrottleState.Unknown) need = exitSamples;
            else need = enterSamples;
            if (run >= need)
            {
                state = c;
                run = 0;
                Evidence = ev;
            }
            return state;
        }
    }

    public static class ThrottleText
    {
        public static string Of(ThrottleState s)
        {
            if (s == ThrottleState.Normal) return "Normal";
            if (s == ThrottleState.TemperatureLimited) return "Limitado por temperatura";
            if (s == ThrottleState.PowerLimited) return "Limitado por energia";
            return "Desconhecido";
        }

        public static bool IsLimited(ThrottleState s)
        {
            return s == ThrottleState.TemperatureLimited || s == ThrottleState.PowerLimited;
        }
    }

    // Arithmetic on the per-processor counters (pure; the Platform reader feeds it).
    public static class CpuMath
    {
        // A logical processor counts as "busy" from this use on.
        public const double BusyFromPct = 30.0;
        // ... and as "saturated" (asked for everything) from this use on: only then does a clock
        // below nominal mean the CPU is held back.
        public const double SaturatedFromPct = 85.0;

        // Mean of the values weighted by use, over the processors that are busy. Null when none is.
        public static double? BusyWeighted(IList<double> values, IList<double> loadPct)
        {
            return BusyWeighted(values, loadPct, BusyFromPct);
        }

        public static double? BusyWeighted(IList<double> values, IList<double> loadPct, double fromPct)
        {
            if (values == null || loadPct == null) return null;
            int n = Math.Min(values.Count, loadPct.Count);
            double sum = 0, weight = 0;
            for (int i = 0; i < n; i++)
            {
                double l = loadPct[i];
                double v = values[i];
                if (double.IsNaN(l) || double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (l < fromPct) continue;
                if (l > 100.0) l = 100.0;
                sum += v * l;
                weight += l;
            }
            return weight <= 0 ? (double?)null : sum / weight;
        }

        public static double? Max(IList<double> values)
        {
            if (values == null || values.Count == 0) return null;
            double m = double.NaN;
            foreach (double v in values)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (double.IsNaN(m) || v > m) m = v;
            }
            return double.IsNaN(m) ? (double?)null : m;
        }

        private static readonly System.Text.RegularExpressions.Regex LogicalName =
            new System.Text.RegularExpressions.Regex(@"^\d+,\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // "Processor Information" instances that are ONE logical processor ("0,11"), in a stable
        // order; "_total" and "0,_total" (group/node totals) are not.
        public static List<string> LogicalProcessors(IEnumerable<string> instances)
        {
            List<string> r = new List<string>();
            if (instances == null) return r;
            foreach (string n in instances)
            {
                if (n != null && LogicalName.IsMatch(n)) r.Add(n);
            }
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        // Total busy percentage between two GetSystemTimes readings (100 ns ticks; kernel time
        // includes idle time). Null when time did not advance or a counter went backwards.
        public static double? BusyPct(long idle0, long kernel0, long user0, long idle1, long kernel1, long user1)
        {
            long total = (kernel1 - kernel0) + (user1 - user0);
            long idle = idle1 - idle0;
            if (total <= 0 || idle < 0 || idle > total) return null;
            return (total - idle) * 100.0 / total;
        }

        public static double Clamp100(double v)
        {
            if (v < 0) return 0;
            return v > 100.0 ? 100.0 : v;
        }

        // Energy Meter "Power" is in milliwatts; anything outside 0.1-500 W is a bad reading.
        public static double? WattsFromMilliwatts(double mw)
        {
            if (double.IsNaN(mw) || double.IsInfinity(mw)) return null;
            double w = mw / 1000.0;
            if (w < 0.1 || w > 500.0) return null;
            return w;
        }
    }
}
