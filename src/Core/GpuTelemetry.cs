using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    // One GPU reading. Fields the source does not give stay null. Source says where it came from.
    public class GpuSample
    {
        public const string SourceNvidiaSmi = "nvidia-smi";
        public const string SourceCounters = "contadores do Windows";

        public string Name;
        public double? UsagePct;
        public double? TempC;
        public double? ClockMHz;
        public double? MaxClockMHz;
        public double? PowerW;
        public double? PowerLimitW;
        public double? VramUsedMiB;
        public double? VramTotalMiB;
        // NVML "clocks throttle reasons" bitmask.
        public long? ThrottleMask;
        public string PState;
        public string Source;
    }

    [Flags]
    public enum GpuLimit
    {
        None = 0,
        Thermal = 1,
        Power = 2,
        Hardware = 4
    }

    // NVML throttle reason bits (nvml.h). Idle, application clocks, sync boost and display
    // clock are not limits the user can act on and do not count.
    public static class GpuThrottle
    {
        public const long SwPowerCap = 0x4;
        public const long HwSlowdown = 0x8;
        public const long SwThermalSlowdown = 0x20;
        public const long HwThermalSlowdown = 0x40;
        public const long HwPowerBrake = 0x80;

        public static GpuLimit Decode(long? mask)
        {
            if (!mask.HasValue) return GpuLimit.None;
            long m = mask.Value;
            GpuLimit l = GpuLimit.None;
            if ((m & (SwThermalSlowdown | HwThermalSlowdown)) != 0) l |= GpuLimit.Thermal;
            if ((m & (SwPowerCap | HwPowerBrake)) != 0) l |= GpuLimit.Power;
            if ((m & HwSlowdown) != 0 && (l & (GpuLimit.Thermal | GpuLimit.Power)) == 0) l |= GpuLimit.Hardware;
            return l;
        }

        // "sem limitação", "limitada por temperatura", ... null when the mask is unknown.
        public static string Text(long? mask)
        {
            if (!mask.HasValue) return null;
            GpuLimit l = Decode(mask);
            bool t = (l & GpuLimit.Thermal) != 0;
            bool p = (l & GpuLimit.Power) != 0;
            if (t && p) return "limitada por temperatura e energia";
            if (t) return "limitada por temperatura";
            if (p) return "limitada por energia";
            if ((l & GpuLimit.Hardware) != 0) return "limitada pelo hardware";
            return "sem limitação";
        }
    }

    // Parser of `nvidia-smi --query-gpu=<NvidiaSmiParser.Fields> --format=csv,noheader,nounits`.
    // "[N/A]" / "[Not Supported]" become null. The name is the only field that can hold a comma,
    // so the other ten are read from the right.
    public static class NvidiaSmiParser
    {
        public const string Fields = "name,utilization.gpu,temperature.gpu,memory.used,memory.total,clocks.gr,clocks.max.gr,power.draw,power.limit,clocks_throttle_reasons.active,pstate";
        public const string Arguments = "--query-gpu=" + Fields + " --format=csv,noheader,nounits";

        public static GpuSample Parse(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            foreach (string raw in output.Split('\n'))
            {
                GpuSample g = ParseLine(raw);
                if (g != null) return g;
            }
            return null;
        }

        public static GpuSample ParseLine(string line)
        {
            return ParseLine(line, DefaultFields);
        }

        public static readonly string[] DefaultFields = Fields.Split(',');

        // Same output with another field list (the name stays first). A field the driver does not
        // know is dropped from the list by NextFields, so one bad field never costs the others.
        public static GpuSample Parse(string output, IList<string> fields)
        {
            if (string.IsNullOrEmpty(output)) return null;
            foreach (string raw in output.Split('\n'))
            {
                GpuSample g = ParseLine(raw, fields);
                if (g != null) return g;
            }
            return null;
        }

        public static string ArgumentsFor(IList<string> fields)
        {
            return "--query-gpu=" + string.Join(",", new List<string>(fields).ToArray()) + " --format=csv,noheader,nounits";
        }

        public static GpuSample ParseLine(string line, IList<string> fields)
        {
            if (string.IsNullOrEmpty(line) || fields == null || fields.Count < 2 || fields[0] != "name") return null;
            string[] parts = line.Trim().Split(',');
            int values = fields.Count - 1;
            if (parts.Length < values + 1) return null;
            int first = parts.Length - values;
            GpuSample g = new GpuSample();
            g.Source = GpuSample.SourceNvidiaSmi;
            g.Name = string.Join(",", parts, 0, first).Trim();
            if (g.Name.Length == 0) g.Name = null;
            for (int i = 1; i < fields.Count; i++)
            {
                string v = parts[first + i - 1];
                switch (fields[i])
                {
                    case "utilization.gpu": g.UsagePct = Num(v); break;
                    case "temperature.gpu": g.TempC = Num(v); break;
                    case "memory.used": g.VramUsedMiB = Num(v); break;
                    case "memory.total": g.VramTotalMiB = Num(v); break;
                    case "clocks.gr": g.ClockMHz = Num(v); break;
                    case "clocks.max.gr": g.MaxClockMHz = Num(v); break;
                    case "power.draw": g.PowerW = Num(v); break;
                    case "power.limit": g.PowerLimitW = Num(v); break;
                    case "clocks_throttle_reasons.active":
                    case "clocks_event_reasons.active": g.ThrottleMask = Mask(v); break;
                    case "pstate":
                        string ps = v.Trim();
                        g.PState = (ps.Length == 0 || ps.StartsWith("[", StringComparison.Ordinal)) ? null : ps;
                        break;
                }
            }
            bool any = g.UsagePct.HasValue || g.TempC.HasValue || g.VramUsedMiB.HasValue || g.ClockMHz.HasValue
                || g.PowerW.HasValue || g.ThrottleMask.HasValue;
            return any ? g : null;
        }

        private static readonly Regex InvalidFieldText = new Regex("Field \"([^\"]+)\" is not a valid field to query", RegexOptions.CultureInvariant);

        // The field nvidia-smi refused ("Field "x" is not a valid field to query."), or null.
        public static string InvalidField(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            Match m = InvalidFieldText.Match(output);
            return m.Success ? m.Groups[1].Value : null;
        }

        // The list to retry with: the renamed throttle field when that is the one refused (drivers
        // renamed clocks_throttle_reasons to clocks_event_reasons), otherwise without the bad field.
        // Null when nothing can be tried (unknown field, "name", or nothing left).
        public static List<string> NextFields(IList<string> fields, string invalid)
        {
            if (fields == null || invalid == null || invalid == "name") return null;
            int i = -1;
            for (int k = 0; k < fields.Count; k++)
            {
                if (fields[k] == invalid) { i = k; break; }
            }
            if (i < 0) return null;
            List<string> r = new List<string>(fields);
            if (invalid == "clocks_throttle_reasons.active" && !r.Contains("clocks_event_reasons.active")) r[i] = "clocks_event_reasons.active";
            else r.RemoveAt(i);
            return r.Count >= 2 ? r : null;
        }

        public static double? Num(string s)
        {
            if (s == null) return null;
            string t = s.Trim();
            if (t.Length == 0 || t.StartsWith("[", StringComparison.Ordinal) || t.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return null;
            double v;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            return v;
        }

        public static long? Mask(string s)
        {
            if (s == null) return null;
            string t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            else return null;
            long v;
            if (!long.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)) return null;
            return v;
        }
    }

    public enum GpuQueryDecision
    {
        Query,
        SkipWindowHidden,
        SkipUnavailable,
        SkipSleeping,
        // The discrete GPU shows no activity on any driver-free signal: not asked.
        SkipIdle,
        SkipTooSoon
    }

    // When nvidia-smi may be started. Every start talks to the driver and can wake an NVIDIA
    // GPU that Optimus put to sleep, so: only while the window is visible, never faster than
    // once per 2 s, and never when the firmware temperature sensor says the GPU sleeps
    // (reads 0 or less). A missing reading (null) is no evidence of sleep and does not block.
    // Above all, only when the discrete GPU is ALREADY active by a signal that needs no driver
    // call (dGpuActive, from GpuActivityTracker on the GPU Engine / GPU Process Memory counters).
    public static class GpuQueryGate
    {
        public const int MinIntervalMs = 2000;
        // Once the GPU is known awake and answering.
        public const int TimeoutMs = 2000;
        // The first query after the gate opens may have to wake NVML or the card: give it longer
        // (killing it at 2 s would only make the next try wake the card again).
        public const int FirstTimeoutMs = 6000;

        public static int TimeoutFor(bool warm)
        {
            return warm ? TimeoutMs : FirstTimeoutMs;
        }

        // The counters-only level (the widget): nothing is ever queried, but a sleeping or idle dGPU is still
        // told apart, so the sleep / idle checks come FIRST (Decide returns SkipUnavailable before them when
        // nvidia-smi cannot be used, which is always the case at this level).
        //   SkipSleeping    the firmware sensor reads 0 or less
        //   SkipIdle        an NVIDIA adapter is known and shows no activity on the driver-free counters
        //   SkipUnavailable the counters' numbers are shown as they are (no claim either way)
        public static GpuQueryDecision DecideCounters(bool windowVisible, bool nvidiaAdapterFound,
            double? firmwareGpuTempC, bool dGpuActive)
        {
            if (!windowVisible) return GpuQueryDecision.SkipWindowHidden;
            if (firmwareGpuTempC.HasValue && !InstrumentMath.IsValidTemp(firmwareGpuTempC)) return GpuQueryDecision.SkipSleeping;
            if (nvidiaAdapterFound && !dGpuActive) return GpuQueryDecision.SkipIdle;
            return GpuQueryDecision.SkipUnavailable;
        }

        public static GpuQueryDecision Decide(bool windowVisible, bool nvidiaSmiAvailable,
            double? firmwareGpuTempC, int msSinceLastQuery, bool dGpuActive)
        {
            if (!windowVisible) return GpuQueryDecision.SkipWindowHidden;
            if (!nvidiaSmiAvailable) return GpuQueryDecision.SkipUnavailable;
            if (firmwareGpuTempC.HasValue && !InstrumentMath.IsValidTemp(firmwareGpuTempC)) return GpuQueryDecision.SkipSleeping;
            if (!dGpuActive) return GpuQueryDecision.SkipIdle;
            if (msSinceLastQuery >= 0 && msSinceLastQuery < MinIntervalMs) return GpuQueryDecision.SkipTooSoon;
            return GpuQueryDecision.Query;
        }
    }

    // "Is the NVIDIA GPU already active?" from driver-free signals, fed once per sample:
    //   - engine use above 2 % on its LUID in each of the last 2 samples, OR
    //   - a non-system process holding at least 64 MiB local on that LUID (GpuEngineMath.MaxNonSystemLocal).
    public sealed class GpuActivityTracker
    {
        private int streak;

        public bool Active { get; private set; }

        public bool Feed(double? engineAnyPct, double? maxNonSystemLocalBytes)
        {
            if (engineAnyPct.HasValue && engineAnyPct.Value > GpuEngineMath.ActiveAbovePct) streak++;
            else streak = 0;
            Active = streak >= GpuEngineMath.EngineSamplesNeeded
                || (maxNonSystemLocalBytes.HasValue && maxNonSystemLocalBytes.Value >= GpuEngineMath.ProcessLocalActiveBytes);
            return Active;
        }

        public void Reset()
        {
            streak = 0;
            Active = false;
        }
    }

    // One GPU adapter as seen by the "GPU Engine" and "GPU Adapter Memory" counters.
    public class GpuAdapterLoad
    {
        public string Key;            // "0x00000000_0x00010FE3_phys_0"
        public double Util3DPct;      // busiest 3D engine (what Task Manager graphs as "3D")
        public double UtilAnyPct;     // busiest engine of any type
        public double? DedicatedMiB;  // "Dedicated Usage" of the adapter memory counter
    }

    // Generic fallback for GPUs without nvidia-smi: the "GPU Engine" counters of Windows (read
    // without touching the driver). Instance names look like
    // pid_13072_luid_0x00000000_0x00010FE3_phys_0_eng_3_engtype_3D (PerformanceCounterCategory.
    // ReadCategory lowercases them: every comparison here ignores case).
    public static class GpuEngineMath
    {
        private static readonly Regex EngineName = new Regex(
            @"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+_phys_\d+)_eng_(\d+)_engtype_(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex AdapterName = new Regex(
            @"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+_phys_\d+)", RegexOptions.CultureInvariant);

        public static bool TryParseEngine(string instance, out string adapterKey, out int engine, out string type)
        {
            adapterKey = null;
            engine = 0;
            type = null;
            if (string.IsNullOrEmpty(instance)) return false;
            Match m = EngineName.Match(instance);
            if (!m.Success) return false;
            adapterKey = NormalizeKey(m.Groups[1].Value);
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out engine)) return false;
            type = m.Groups[3].Value;
            return true;
        }

        public static string AdapterKey(string memoryInstance)
        {
            if (string.IsNullOrEmpty(memoryInstance)) return null;
            Match m = AdapterName.Match(memoryInstance);
            return m.Success ? NormalizeKey(m.Groups[1].Value) : null;
        }

        // Engine use above this (any engine type, last sample) means the GPU is in use.
        public const double ActiveAbovePct = 2.0;
        // A NON-SYSTEM process holding at least this much local (dedicated) memory on the GPU is
        // using it. Idle Windows keeps memory on every adapter for its own processes (measured on
        // the RTX 2050 LUID: System pid 4 = 4,132,864 B plus about 24 KB per process with a D3D
        // context, a total that drifts with the process count), so system processes never count
        // and the rest must hold a real working set, never a sum.
        public const double ProcessLocalActiveBytes = 64.0 * 1048576.0;
        // Engine use above ActiveAbovePct must be seen in this many consecutive samples.
        public const int EngineSamplesNeeded = 2;

        // "0x00000000_0x00011447": the LUID as the counter instance names spell it (high_low, lowercase).
        public static string LuidKey(uint high, uint low)
        {
            return "0x" + high.ToString("x8", CultureInfo.InvariantCulture) + "_0x" + low.ToString("x8", CultureInfo.InvariantCulture);
        }

        public static GpuAdapterLoad FindByLuid(IList<GpuAdapterLoad> adapters, string luidKey)
        {
            if (adapters == null || luidKey == null) return null;
            string prefix = luidKey.ToLowerInvariant() + "_phys";
            foreach (GpuAdapterLoad a in adapters)
            {
                if (a.Key != null && a.Key.StartsWith(prefix, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        private static readonly Regex PidName = new Regex(@"^pid_(\d+)_", RegexOptions.CultureInvariant);

        public static bool TryParsePid(string instance, out int pid)
        {
            pid = 0;
            if (string.IsNullOrEmpty(instance)) return false;
            Match m = PidName.Match(instance);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
        }

        // The largest "Local Usage" (bytes) held on one LUID by a single NON-SYSTEM process; null
        // when there is none. Never a sum, and pids 0 and 4 are always system; isSystem decides the
        // rest (the platform checks for session 0). Called for every instance, so keep isSystem cheap
        // (it is only asked for the biggest candidates first).
        public static double? MaxNonSystemLocal(IEnumerable<KeyValuePair<string, double>> processLocalUsage, string luidKey, Func<int, bool> isSystem)
        {
            if (processLocalUsage == null || luidKey == null) return null;
            string prefix = luidKey.ToLowerInvariant() + "_phys";
            List<KeyValuePair<int, double>> mine = new List<KeyValuePair<int, double>>();
            foreach (KeyValuePair<string, double> kv in processLocalUsage)
            {
                string key = AdapterKey(kv.Key);
                int pid;
                if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal) || !TryParsePid(kv.Key, out pid)) continue;
                if (double.IsNaN(kv.Value) || kv.Value < 0 || pid == 0 || pid == 4) continue;
                mine.Add(new KeyValuePair<int, double>(pid, kv.Value));
            }
            mine.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b) { return b.Value.CompareTo(a.Value); });
            foreach (KeyValuePair<int, double> p in mine)
            {
                if (isSystem != null && isSystem(p.Key)) continue;
                return p.Value;
            }
            return null;
        }

        private static string NormalizeKey(string k)
        {
            return k.ToLowerInvariant();
        }

        // engines: instance name -> "Utilization Percentage". memory: instance name -> bytes of
        // "Dedicated Usage" (may be null). A process's slice of an engine adds to the others on
        // the same engine; the adapter load is its busiest engine, capped at 100.
        public static List<GpuAdapterLoad> Aggregate(IEnumerable<KeyValuePair<string, double>> engines,
            IEnumerable<KeyValuePair<string, double>> memory)
        {
            Dictionary<string, Dictionary<string, double>> perEngine = new Dictionary<string, Dictionary<string, double>>();
            if (engines != null)
            {
                foreach (KeyValuePair<string, double> kv in engines)
                {
                    string key, type;
                    int eng;
                    if (!TryParseEngine(kv.Key, out key, out eng, out type)) continue;
                    double v = kv.Value;
                    if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) continue;
                    string slot = eng.ToString(CultureInfo.InvariantCulture) + "|" + (type ?? "");
                    Dictionary<string, double> d;
                    if (!perEngine.TryGetValue(key, out d))
                    {
                        d = new Dictionary<string, double>();
                        perEngine[key] = d;
                    }
                    double cur;
                    d.TryGetValue(slot, out cur);
                    d[slot] = cur + v;
                }
            }
            Dictionary<string, double> mem = new Dictionary<string, double>();
            if (memory != null)
            {
                foreach (KeyValuePair<string, double> kv in memory)
                {
                    string key = AdapterKey(kv.Key);
                    if (key == null || double.IsNaN(kv.Value) || kv.Value < 0) continue;
                    double cur;
                    mem.TryGetValue(key, out cur);
                    mem[key] = cur + kv.Value;
                }
            }
            List<GpuAdapterLoad> result = new List<GpuAdapterLoad>();
            List<string> keys = new List<string>(perEngine.Keys);
            foreach (string k in mem.Keys)
            {
                if (!keys.Contains(k)) keys.Add(k);
            }
            foreach (string k in keys)
            {
                GpuAdapterLoad a = new GpuAdapterLoad();
                a.Key = k;
                Dictionary<string, double> d;
                if (perEngine.TryGetValue(k, out d))
                {
                    foreach (KeyValuePair<string, double> e in d)
                    {
                        double v = e.Value > 100.0 ? 100.0 : e.Value;
                        if (v > a.UtilAnyPct) a.UtilAnyPct = v;
                        if (e.Key.EndsWith("|3D", StringComparison.OrdinalIgnoreCase) && v > a.Util3DPct) a.Util3DPct = v;
                    }
                }
                double bytes;
                if (mem.TryGetValue(k, out bytes)) a.DedicatedMiB = bytes / 1048576.0;
                result.Add(a);
            }
            return result;
        }

        // The adapter to show: the busiest in 3D, ties broken by dedicated memory in use (the
        // discrete GPU holds far more than the integrated one). Null for an empty list.
        public static GpuAdapterLoad Pick(IList<GpuAdapterLoad> adapters)
        {
            GpuAdapterLoad best = null;
            if (adapters == null) return null;
            foreach (GpuAdapterLoad a in adapters)
            {
                if (best == null) { best = a; continue; }
                if (a.Util3DPct > best.Util3DPct + 0.5) best = a;
                else if (Math.Abs(a.Util3DPct - best.Util3DPct) <= 0.5
                    && (a.DedicatedMiB ?? 0) > (best.DedicatedMiB ?? 0)) best = a;
            }
            return best;
        }

        public static GpuSample ToSample(GpuAdapterLoad a)
        {
            return ToSample(a, null);
        }

        public static GpuSample ToSample(GpuAdapterLoad a, string name)
        {
            if (a == null) return null;
            GpuSample g = new GpuSample();
            g.Name = name;
            g.Source = GpuSample.SourceCounters;
            g.UsagePct = a.Util3DPct;
            g.VramUsedMiB = a.DedicatedMiB;
            return g;
        }
    }
}
