using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // Color state of one temperature readout. Never color-only on screen: Unavailable is a
    // dashed arc + dash, Sleeping a moon, Hot/Amber sit next to the hatched 90-100 zone.
    public enum TempState
    {
        Neutral,
        Amber,
        Hot,
        Unavailable,
        Sleeping
    }

    // Overall verdict for the top strip.
    public enum Verdict
    {
        Stable,
        Attention,
        Hot,
        NoReading
    }

    // Which component a history series or chart belongs to.
    public enum Hardware
    {
        Cpu,
        Gpu
    }

    public enum Metric
    {
        Temp,
        Usage
    }

    public struct GaugeTick
    {
        public double TempC;
        public double AngleDeg;
        public bool Major;
    }

    // Pure math and text for the instrument controls (gauge, component charts, verdict strip).
    // Angles are degrees clockwise from +X in screen coordinates (y down), the same
    // convention as GDI+ DrawArc and SVG.
    public static class InstrumentMath
    {
        public const double ScaleMinC = 30.0;
        public const double ScaleMaxC = 100.0;
        public const double ArcStartDeg = 135.0;
        public const double ArcSweepDeg = 270.0;
        public const double LimitC = 90.0;
        public const int AmberFromC = 80;
        public const int HotFromC = 85;
        public const int TickStepC = 5;
        public const int MajorTickStepC = 10;

        public const string Nbsp = " ";
        public const string Dash = "—";

        private static readonly NumberFormatInfo PtBr = CreatePtBr();

        private static NumberFormatInfo CreatePtBr()
        {
            // built by hand so the result never depends on the machine's culture data
            NumberFormatInfo f = new NumberFormatInfo();
            f.NumberDecimalSeparator = ",";
            f.NumberGroupSeparator = ".";
            f.NumberGroupSizes = new int[] { 3 };
            f.NegativeSign = "-";
            return f;
        }

        // ---- validity -----------------------------------------------------------------

        // The firmware answers 0 for sensors it does not have; 0 °C is never a real reading here.
        public static bool IsValidTemp(double? t)
        {
            if (!t.HasValue) return false;
            double v = t.Value;
            return !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0;
        }

        public static double? Sanitize(double? t)
        {
            return IsValidTemp(t) ? t : null;
        }

        public static double ClampToScale(double t)
        {
            if (t < ScaleMinC) return ScaleMinC;
            if (t > ScaleMaxC) return ScaleMaxC;
            return t;
        }

        public static int RoundTemp(double t)
        {
            return (int)Math.Round(t, MidpointRounding.AwayFromZero);
        }

        // ---- gauge --------------------------------------------------------------------

        public static double AngleForTemp(double t)
        {
            return ArcStartDeg + (ClampToScale(t) - ScaleMinC) / (ScaleMaxC - ScaleMinC) * ArcSweepDeg;
        }

        public static List<GaugeTick> Ticks()
        {
            List<GaugeTick> list = new List<GaugeTick>();
            for (int t = (int)ScaleMinC; t <= (int)ScaleMaxC; t += TickStepC)
            {
                GaugeTick k = new GaugeTick();
                k.TempC = t;
                k.AngleDeg = AngleForTemp(t);
                k.Major = t % MajorTickStepC == 0;
                list.Add(k);
            }
            return list;
        }

        // State follows the rounded number shown on screen, so "80" is always amber and "85" always hot.
        public static TempState StateFor(double? t, bool sleeping)
        {
            if (sleeping) return TempState.Sleeping;
            if (!IsValidTemp(t)) return TempState.Unavailable;
            int r = RoundTemp(t.Value);
            if (r >= HotFromC) return TempState.Hot;
            if (r >= AmberFromC) return TempState.Amber;
            return TempState.Neutral;
        }

        // ---- verdict ------------------------------------------------------------------

        // Heat outranks missing readings; "Stable" requires every sensor to read normally
        // (a sleeping GPU counts as normal).
        public static Verdict VerdictFor(double? cpu, double? gpu, bool gpuSleeping)
        {
            TempState c = StateFor(cpu, false);
            TempState g = StateFor(gpu, gpuSleeping);
            if (c == TempState.Hot || g == TempState.Hot) return Verdict.Hot;
            if (c == TempState.Amber || g == TempState.Amber) return Verdict.Attention;
            if (c == TempState.Unavailable || g == TempState.Unavailable) return Verdict.NoReading;
            return Verdict.Stable;
        }

        public static string VerdictLabel(Verdict v)
        {
            switch (v)
            {
                case Verdict.Stable: return "Estável";
                case Verdict.Attention: return "Atenção";
                case Verdict.Hot: return "Quente";
                default: return "Sem leitura";
            }
        }


        // ---- component charts (shared 0-100 scale for °C and %) ------------------------

        public const double ChartMin = 0.0;
        public const double ChartMax = 100.0;

        // hairlines with unit-free labels
        public static int[] ChartGrid
        {
            get { return new int[] { 25, 50, 75 }; }
        }

        public static double ChartY(double v, double top, double height)
        {
            double c = v < ChartMin ? ChartMin : (v > ChartMax ? ChartMax : v);
            return top + (ChartMax - c) / (ChartMax - ChartMin) * height;
        }

        // ageSamples 0 = newest (right edge); capacity-1 = oldest slot (left edge).
        public static double ChartX(int ageSamples, int capacity, double left, double width)
        {
            if (capacity < 2) return left + width;
            int age = ageSamples < 0 ? 0 : (ageSamples > capacity - 1 ? capacity - 1 : ageSamples);
            return left + width - (double)age / (capacity - 1) * width;
        }

        // Places two end labels (desired centers yA, yB) at least minGap apart, keeping their
        // vertical order (A on top when equal) and both inside [top, bottom].
        public static void SpreadLabels(double yA, double yB, double minGap, double top, double bottom,
            out double a, out double b)
        {
            bool aOnTop = yA <= yB;
            double up = aOnTop ? yA : yB;
            double down = aOnTop ? yB : yA;
            if (down - up < minGap)
            {
                double mid = (up + down) / 2.0;
                up = mid - minGap / 2.0;
                down = mid + minGap / 2.0;
            }
            if (up < top)
            {
                down += top - up;
                up = top;
            }
            if (down > bottom)
            {
                up -= down - bottom;
                down = bottom;
                if (up < top) up = top;
            }
            a = aOnTop ? up : down;
            b = aOnTop ? down : up;
        }

        public static string HardwareName(Hardware h)
        {
            return h == Hardware.Cpu ? "CPU" : "GPU";
        }

        // ---- usage --------------------------------------------------------------------

        // 0 % is a real reading; above 100 is rounding noise and is clamped; negative/NaN/Inf = gap.
        public static double? SanitizeUsage(double? pct)
        {
            if (!pct.HasValue) return null;
            double v = pct.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0.0) return null;
            return v > 100.0 ? 100.0 : v;
        }

        public static string FormatUsage(double? pct)
        {
            double? v = SanitizeUsage(pct);
            if (!v.HasValue) return Dash;
            return WithUnit(((int)Math.Round(v.Value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture), "%");
        }

        // ---- pt-BR text ---------------------------------------------------------------

        public static string FormatGrouped(long n)
        {
            return n.ToString("#,0", PtBr);
        }

        public static string FormatDecimal1(double v)
        {
            return Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("#,0.0", PtBr);
        }

        public static string FormatTemp(double? t)
        {
            if (!IsValidTemp(t)) return Dash;
            return RoundTemp(t.Value).ToString(CultureInfo.InvariantCulture);
        }

        public static string WithUnit(string number, string unit)
        {
            if (string.IsNullOrEmpty(unit)) return number;
            return number + Nbsp + unit;
        }

        public static string Days(int n)
        {
            if (n < 0) n = 0;
            return n == 1 ? "1 dia" : n.ToString(CultureInfo.InvariantCulture) + " dias";
        }

        // ---- stability ("sem tela azul") ---------------------------------------------

        public static int WholeDaysBetween(DateTime since, DateTime now)
        {
            double d = (now - since).TotalDays;
            return d <= 0 ? 0 : (int)Math.Floor(d);
        }

        // Detail text for the verdict strip.
        public static string NoCrashSummary(int? days)
        {
            if (!days.HasValue) return "Sem dados de tela azul";
            if (days.Value <= 0) return "Sem tela azul há menos de 1 dia";
            return "Sem tela azul há " + Days(days.Value);
        }

        // ---- accessibility ------------------------------------------------------------

        public static string GaugeAccessibleText(string name, double? value, double? peak, bool sleeping)
        {
            TempState s = StateFor(value, sleeping);
            if (s == TempState.Sleeping) return name + " em repouso";
            if (s == TempState.Unavailable) return name + " sem leitura";
            string text = name + " " + FormatTemp(value) + " graus";
            if (s == TempState.Amber) text += ", atenção";
            else if (s == TempState.Hot) text += ", quente";
            if (IsValidTemp(peak)) text += ", pico " + FormatTemp(peak);
            return text;
        }

        public static string ComponentChartText(TempHistory h, Hardware part)
        {
            int minutes = h == null ? TempHistory.DefaultCapacity / 60 : Math.Max(1, h.Capacity / 60);
            return HardwareName(part) + ": temperatura " + Range(h, part, Metric.Temp, "°C")
                + ", uso " + Range(h, part, Metric.Usage, "%")
                + " nos últimos " + (minutes == 1 ? "1 minuto" : minutes + " minutos")
                + "; limite " + WithUnit(FormatTemp(LimitC), "°C");
        }

        private static string Range(TempHistory h, Hardware part, Metric m, string unit)
        {
            double? lo = h == null ? null : h.Min(part, m);
            double? hi = h == null ? null : h.Peak(part, m);
            if (!lo.HasValue || !hi.HasValue) return "sem leitura";
            return "entre " + Round(lo.Value) + " e " + WithUnit(Round(hi.Value), unit);
        }

        private static string Round(double v)
        {
            return ((int)Math.Round(v, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        }
    }

    // Fixed-size ring buffer of per-second samples (default 600 = 10 min at 1 Hz): temperature
    // and usage % for CPU and GPU. Invalid readings are stored as gaps, never as 0 °C; a 0 %
    // usage is a real value.
    public sealed class TempHistory
    {
        public const int DefaultCapacity = 600;

        private readonly double[] _cpuTemp;
        private readonly double[] _gpuTemp;
        private readonly double[] _cpuUse;
        private readonly double[] _gpuUse;
        private int _start;
        private int _count;
        private int _version;

        public TempHistory() : this(DefaultCapacity)
        {
        }

        public TempHistory(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException("capacity", "capacity must be at least 2");
            _cpuTemp = new double[capacity];
            _gpuTemp = new double[capacity];
            _cpuUse = new double[capacity];
            _gpuUse = new double[capacity];
        }

        public int Capacity { get { return _cpuTemp.Length; } }
        public int Count { get { return _count; } }

        // Changes on every Add/Clear; controls can compare it to skip redundant repaints.
        public int Version { get { return _version; } }

        public void Add(double? cpuTemp, double? gpuTemp, double? cpuUse, double? gpuUse)
        {
            int slot;
            if (_count < _cpuTemp.Length)
            {
                slot = (_start + _count) % _cpuTemp.Length;
                _count++;
            }
            else
            {
                slot = _start;
                _start = (_start + 1) % _cpuTemp.Length;
            }
            _cpuTemp[slot] = InstrumentMath.IsValidTemp(cpuTemp) ? cpuTemp.Value : double.NaN;
            _gpuTemp[slot] = InstrumentMath.IsValidTemp(gpuTemp) ? gpuTemp.Value : double.NaN;
            double? cu = InstrumentMath.SanitizeUsage(cpuUse);
            double? gu = InstrumentMath.SanitizeUsage(gpuUse);
            _cpuUse[slot] = cu.HasValue ? cu.Value : double.NaN;
            _gpuUse[slot] = gu.HasValue ? gu.Value : double.NaN;
            _version++;
        }

        // temperatures only; usage is stored as a gap
        public void Add(double? cpuTemp, double? gpuTemp)
        {
            Add(cpuTemp, gpuTemp, null, null);
        }

        public void Add(SensorReading r)
        {
            Add(r, null, null);
        }

        public void Add(SensorReading r, double? cpuUse, double? gpuUse)
        {
            if (r == null) Add(null, null, cpuUse, gpuUse);
            else Add(r.CpuTempC, r.GpuTempC, cpuUse, gpuUse);
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
            _version++;
        }

        // index 0 = oldest sample, Count-1 = newest; null = gap.
        public double? At(Hardware h, Metric m, int index)
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException("index");
            double v = Buffer(h, m)[(_start + index) % _cpuTemp.Length];
            if (double.IsNaN(v)) return null;
            return v;
        }

        public double? Latest(Hardware h, Metric m)
        {
            return _count == 0 ? null : At(h, m, _count - 1);
        }

        public double? Peak(Hardware h, Metric m)
        {
            return Extreme(Buffer(h, m), true);
        }

        public double? Min(Hardware h, Metric m)
        {
            return Extreme(Buffer(h, m), false);
        }

        private double? Extreme(double[] buf, bool max)
        {
            bool any = false;
            double best = 0.0;
            for (int i = 0; i < _count; i++)
            {
                double v = buf[(_start + i) % buf.Length];
                if (double.IsNaN(v)) continue;
                if (!any || (max ? v > best : v < best))
                {
                    best = v;
                    any = true;
                }
            }
            if (!any) return null;
            return best;
        }

        private double[] Buffer(Hardware h, Metric m)
        {
            if (h == Hardware.Cpu) return m == Metric.Temp ? _cpuTemp : _cpuUse;
            return m == Metric.Temp ? _gpuTemp : _gpuUse;
        }
    }
}
