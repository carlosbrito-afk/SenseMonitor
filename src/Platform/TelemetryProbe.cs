using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Forja.Core;

namespace Forja.Platform
{
    // Reads the live CPU/GPU telemetry. Owns no thread and no timer: the StatsPoller thread calls
    // it about once per second, only while a page that shows telemetry is visible, so a hidden
    // window costs nothing. Every read is wrapped: a missing counter becomes a null field.
    //
    // CPU: one PerformanceCounterCategory.ReadCategory("Processor Information") per call, with the
    // previous read kept to compute the rate counters (actual frequency, % performance, utility).
    // Package power: "Energy Meter" \ Power for the RAPL package instance (milliwatts), when Windows
    // exposes it (Intel with RAPL; absent elsewhere, then the field stays null).
    // GPU: the "GPU Engine" / "GPU Adapter Memory" / "GPU Process Memory" counters are read first
    // (no driver call, so they cannot wake the card), and the NVIDIA adapter is matched to them by
    // LUID (DxgiAdapters). nvidia-smi starts only when GpuQueryGate allows it: window visible, not
    // more than every 2 s, firmware sensor not at 0, and the NVIDIA GPU already ACTIVE by those
    // counters (engine use above 2 %, or processes holding real memory on it). Otherwise the
    // counters supply usage and VRAM and the frame says GpuIdle / GpuSleeping ("GPU em repouso").
    public sealed class TelemetryProbe : IDisposable
    {
        private const string CpuCategory = "Processor Information";
        private const string GpuEngineCategory = "GPU Engine";
        private const string GpuMemoryCategory = "GPU Adapter Memory";
        private const string GpuProcessCategory = "GPU Process Memory";
        private const int PowerPlanEveryMs = 5000;
        private const int SmiFailureBackoffMs = 10000;
        private const int SmiCacheMs = 6000;

        private PerformanceCounterCategory cpuCat;
        private InstanceDataCollectionCollection prevCpu;
        private PerformanceCounter energy;
        private bool energyTried;
        private int energyRetryAt;
        private const int EnergyRetryMs = 30000;

        private readonly string smiPath;
        private bool smiQueried;
        private int smiTick;
        private int smiBackoffUntil;
        private bool smiBackingOff;
        private GpuSample smiCache;
        private bool smiWarm;                // the last query succeeded and the gate has not closed since
        // A killed nvidia-smi that did not exit (stuck in the driver). Static: it belongs to the
        // process, not to a page's probe, so reopening a page cannot stack another one on it.
        private static Process pendingSmi;
        private static readonly object PendingLock = new object();
        private List<string> smiFields = new List<string>(NvidiaSmiParser.DefaultFields);
        private readonly HashSet<string> reported = new HashSet<string>();
        private readonly Dictionary<int, bool> systemPids = new Dictionary<int, bool>();
        private readonly GpuActivityTracker activity = new GpuActivityTracker();
        private int gpuRetryAt;
        private const int GpuCountersRetryMs = 5000;

        private bool nvLookedUp;
        private string nvKey;   // LUID of the NVIDIA adapter as the counters spell it, or null
        private string nvName;
        private PerformanceCounterCategory gpuProcCat;

        private bool gpuCountersTried;
        private bool gpuCountersOk;
        private PerformanceCounterCategory gpuEngineCat;
        private PerformanceCounterCategory gpuMemCat;
        private InstanceDataCollection prevEngines;

        private int powerTick;
        private bool powerRead;
        private PowerPlanInfo powerCache;

        public TelemetryProbe()
        {
            smiPath = InstallPaths.NvidiaSmiPath();
        }

        public bool HasNvidiaSmi { get { return smiPath != null; } }

        // How many nvidia-smi processes this probe started, and whether the last read saw the
        // NVIDIA GPU active by the driver-free signals (for tests and diagnostics).
        public int SmiStarts { get; private set; }
        public bool LastActive { get { return activity.Active; } }

        // ---- CPU ----------------------------------------------------------------------------

        // Null on the first call (it primes the counters) or when the category does not exist.
        public CpuSample ReadCpu()
        {
            try
            {
                if (cpuCat == null) cpuCat = new PerformanceCounterCategory(CpuCategory);
                InstanceDataCollectionCollection cur = cpuCat.ReadCategory();
                InstanceDataCollectionCollection prev = prevCpu;
                prevCpu = cur;
                if (prev == null || cur == null) return null;

                // Busy TIME decides "loaded" and "saturated": "% Processor Utility" is normalized by
                // the clock, so a core stuck at low clock reads low there exactly when it matters.
                string utilName = Has(cur, "% processor time") ? "% processor time" : "% processor utility";
                List<string> cores = LogicalInstances(cur, utilName);
                List<double> util = new List<double>();
                List<double> perf = new List<double>();
                List<double> freq = new List<double>();
                double? nominal = Rate(prev, cur, "processor frequency", "_total");
                bool haveFreq = Has(cur, "actual frequency");
                foreach (string core in cores)
                {
                    double? u = Rate(prev, cur, utilName, core);
                    double? p = Rate(prev, cur, "% processor performance", core);
                    double? fr = haveFreq ? Rate(prev, cur, "actual frequency", core) : null;
                    if (!fr.HasValue && p.HasValue && nominal.HasValue && nominal.Value > 0) fr = p.Value * nominal.Value / 100.0;
                    util.Add(u.HasValue ? CpuMath.Clamp100(u.Value) : double.NaN);
                    perf.Add(p.HasValue ? p.Value : double.NaN);
                    freq.Add(fr.HasValue ? fr.Value : double.NaN);
                }

                CpuSample s = new CpuSample();
                double? total = Rate(prev, cur, utilName, "_total");
                if (total.HasValue) s.LoadPct = CpuMath.Clamp100(total.Value);
                s.MaxCoreLoadPct = CpuMath.Max(util);
                double? eff = haveFreq ? Rate(prev, cur, "actual frequency", "_total") : null;
                if (!eff.HasValue)
                {
                    double? p = Rate(prev, cur, "% processor performance", "_total");
                    if (p.HasValue && nominal.HasValue && nominal.Value > 0) eff = p.Value * nominal.Value / 100.0;
                }
                if (eff.HasValue && eff.Value > 0) s.EffectiveMHz = eff;
                s.BusyClockMHz = CpuMath.BusyWeighted(freq, util);
                s.SaturatedPerfPct = CpuMath.BusyWeighted(perf, util, CpuMath.SaturatedFromPct);
                s.LimitPct = Rate(prev, cur, "% performance limit", "_total");
                s.PackageWatts = ReadPackageWatts();
                return s;
            }
            catch (Exception)
            {
                // A broken counter set: forget it and rebuild on the next call.
                cpuCat = null;
                prevCpu = null;
                return null;
            }
        }

        private static bool Has(InstanceDataCollectionCollection c, string counter)
        {
            return c != null && c.Contains(counter);
        }

        private static List<string> LogicalInstances(InstanceDataCollectionCollection c, string counter)
        {
            List<string> names = new List<string>();
            if (!Has(c, counter)) return names;
            foreach (object k in c[counter].Keys)
            {
                string name = k as string;
                if (name != null) names.Add(name);
            }
            return CpuMath.LogicalProcessors(names);
        }

        // Value of a counter instance between two reads; null when it did not exist in both.
        private static double? Rate(InstanceDataCollectionCollection prev, InstanceDataCollectionCollection cur, string counter, string instance)
        {
            try
            {
                if (!Has(prev, counter) || !Has(cur, counter)) return null;
                InstanceDataCollection a = prev[counter];
                InstanceDataCollection b = cur[counter];
                if (!a.Contains(instance) || !b.Contains(instance)) return null;
                float v = CounterSample.Calculate(a[instance].Sample, b[instance].Sample);
                if (float.IsNaN(v) || float.IsInfinity(v) || v < 0) return null;
                return v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private double? ReadPackageWatts()
        {
            try
            {
                if (energy == null)
                {
                    // A failure is retried after 30 s (a counter rebuild is transient); a machine that
                    // simply has no Energy Meter is asked again that rarely too, which costs nothing.
                    if (energyTried && unchecked(Environment.TickCount - energyRetryAt) < 0) return null;
                    energyTried = true;
                    energyRetryAt = unchecked(Environment.TickCount + EnergyRetryMs);
                    if (!PerformanceCounterCategory.Exists("Energy Meter")) return null;
                    string inst = null;
                    foreach (string n in new PerformanceCounterCategory("Energy Meter").GetInstanceNames())
                    {
                        if (n.IndexOf("PKG", StringComparison.OrdinalIgnoreCase) >= 0) { inst = n; break; }
                    }
                    if (inst == null) return null;
                    energy = new PerformanceCounter("Energy Meter", "Power", inst, true);
                    energy.NextValue();
                    return null;
                }
                return CpuMath.WattsFromMilliwatts(energy.NextValue());
            }
            catch (Exception)
            {
                if (energy != null) { energy.Dispose(); energy = null; }
                return null;
            }
        }

        // ---- power plan -----------------------------------------------------------------------

        // Cached for 5 s: the plan changes only when the user changes it.
        public PowerPlanInfo ReadPowerPlan()
        {
            int now = Environment.TickCount;
            if (!powerRead || unchecked(now - powerTick) >= PowerPlanEveryMs)
            {
                powerCache = PowerPlanReader.Read(PerfProbe.ReadPower().OnBattery);
                powerTick = now;
                powerRead = true;
            }
            return powerCache;
        }

        // ---- GPU ------------------------------------------------------------------------------

        // Optional diagnostics sink (the app points it at its log); the first problem of each kind is reported.
        public Action<string> Log;

        // Counters first (they never touch the driver); nvidia-smi only when GpuQueryGate allows it,
        // which needs the NVIDIA GPU to be ALREADY active by those counters (GpuActivityTracker:
        // engine use above 2 % twice in a row, or a non-system process with 64 MiB on its LUID).
        public void ReadGpu(TelemetryFrame f, double? firmwareGpuTempC)
        {
            ReadGpu(f, firmwareGpuTempC, GpuWant.Full);
        }

        // want = Counters: only the driver-free counters, nvidia-smi is never started (the widget).
        public void ReadGpu(TelemetryFrame f, double? firmwareGpuTempC, GpuWant want)
        {
            if (!GpuWants.ReadsGpu(want)) return;
            int now = Environment.TickCount;
            LocateNvidia();
            CounterRead cr = ReadGpuCounters(nvKey != null);
            if (cr == null)
            {
                // The counters need two reads (or failed and wait to be retried): nothing proves the
                // GPU is in use and nothing is claimed about it either; the view says "indisponível".
                smiCache = null;
                activity.Reset();
                return;
            }
            GpuAdapterLoad nv = null;
            GpuSample counters;
            bool active = false;
            bool smiPossible = GpuWants.SmiPossible(want, smiPath != null, nvKey != null);
            if (want != GpuWant.Full) smiCache = null; // an earlier Full read must not answer a counters-only one
            if (nvKey != null)
            {
                nv = GpuEngineMath.FindByLuid(cr.Adapters, nvKey);
                counters = GpuEngineMath.ToSample(nv, nvName);
                active = activity.Feed(nv == null ? (double?)null : nv.UtilAnyPct,
                    GpuEngineMath.MaxNonSystemLocal(cr.ProcessLocal, nvKey, IsSystemProcess));
            }
            else counters = GpuEngineMath.ToSample(GpuEngineMath.Pick(cr.Adapters));
            int sinceSmi = smiQueried ? unchecked(now - smiTick) : -1;
            GpuQueryDecision d = want == GpuWant.Full
                ? GpuQueryGate.Decide(true, smiPossible, firmwareGpuTempC, sinceSmi, active)
                : GpuQueryGate.DecideCounters(true, nvKey != null, firmwareGpuTempC, active);
            if (d == GpuQueryDecision.SkipSleeping || d == GpuQueryDecision.SkipIdle)
            {
                if (d == GpuQueryDecision.SkipSleeping) f.GpuSleeping = true;
                else f.GpuIdle = true;
                smiCache = null;
                smiWarm = false; // the next query after the gate reopens may have to wake things: 6 s
                f.Gpu = counters; // usage / VRAM from the counters; nvidia-smi is not started
                return;
            }
            if (d == GpuQueryDecision.Query)
            {
                if (smiBackingOff && unchecked(now - smiBackoffUntil) < 0) d = GpuQueryDecision.SkipTooSoon;
                else if (SmiStillRunning()) d = GpuQueryDecision.SkipTooSoon; // never stack a stuck one
                else
                {
                    smiBackingOff = false;
                    GpuSample g = QuerySmi();
                    smiQueried = true;
                    smiTick = Environment.TickCount;
                    if (g != null)
                    {
                        smiCache = g;
                        smiWarm = true;
                    }
                    else
                    {
                        smiCache = null;
                        smiBackingOff = true;
                        smiBackoffUntil = unchecked(Environment.TickCount + SmiFailureBackoffMs);
                    }
                }
            }
            if (smiCache != null && smiQueried && unchecked(Environment.TickCount - smiTick) < SmiCacheMs)
            {
                f.Gpu = smiCache;
                return;
            }
            f.Gpu = counters;
        }

        // The NVIDIA adapter's LUID, once (it cannot change within a boot). Only looked up when
        // nvidia-smi exists: without it there is nothing to gate.
        private void LocateNvidia()
        {
            if (nvLookedUp) return;
            nvLookedUp = true;
            if (smiPath == null) return;
            string key, name;
            if (DxgiAdapters.CachedNvidia(out key, out name)) // enumerated once per process
            {
                nvKey = key;
                nvName = name;
            }
        }

        // System processes never count as "using the GPU": pids 0 and 4, session 0 (services), and
        // anything that is gone or cannot be inspected. Cached per pid.
        private bool IsSystemProcess(int pid)
        {
            if (pid <= 4) return true;
            bool r;
            if (systemPids.TryGetValue(pid, out r)) return r;
            try
            {
                using (Process p = Process.GetProcessById(pid)) r = p.SessionId == 0;
            }
            catch (Exception)
            {
                r = true;
            }
            if (systemPids.Count > 512) systemPids.Clear();
            systemPids[pid] = r;
            return r;
        }

        // Process-wide: probes are recreated on every page open, a stuck nvidia-smi must outlive them.
        private static bool SmiStillRunning()
        {
            lock (PendingLock)
            {
                if (pendingSmi == null) return false;
                try
                {
                    if (!pendingSmi.HasExited) return true;
                    pendingSmi.Dispose();
                }
                catch (Exception)
                {
                }
                pendingSmi = null;
                return false;
            }
        }

        private sealed class CounterRead
        {
            public List<GpuAdapterLoad> Adapters;
            public List<KeyValuePair<string, double>> ProcessLocal;
        }

        // One nvidia-smi query. A field the driver refuses (renamed or unsupported) is replaced or
        // dropped and the query repeated, so one bad field never costs the others. The timeout is 6 s
        // for the first query after the gate opened and 2 s once the GPU answered. A process that
        // does not die after Kill is remembered (SmiStillRunning) and never joined by a second one.
        private GpuSample QuerySmi()
        {
            try
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    string output;
                    int code;
                    if (!RunSmi(NvidiaSmiParser.ArgumentsFor(smiFields), GpuQueryGate.TimeoutFor(smiWarm), out output, out code))
                    {
                        Report("nvidia-smi não respondeu no tempo limite");
                        return null;
                    }
                    GpuSample g = NvidiaSmiParser.Parse(output, smiFields);
                    if (g != null) return g;
                    string bad = NvidiaSmiParser.InvalidField(output);
                    List<string> next = NvidiaSmiParser.NextFields(smiFields, bad);
                    if (next == null)
                    {
                        Report("nvidia-smi sem dados (código " + code + ")");
                        return null;
                    }
                    Report("nvidia-smi recusou o campo " + bad + ": " + (next.Count == smiFields.Count ? "trocado" : "retirado"));
                    smiFields = next;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private bool RunSmi(string arguments, int timeoutMs, out string output, out int exitCode)
        {
            output = null;
            exitCode = -1;
            SmiStarts++;
            ProcessStartInfo psi = new ProcessStartInfo(smiPath, arguments);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); }
                catch (Exception) { }
                bool dead = false;
                try { dead = p.WaitForExit(500); }
                catch (Exception) { }
                if (dead) p.Dispose();
                else lock (PendingLock) pendingSmi = p; // stuck in the driver: do not start another until it is gone
                return false;
            }
            output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            exitCode = p.ExitCode;
            p.Dispose();
            return true;
        }

        private void Report(string message)
        {
            if (Log == null || reported.Contains(message)) return;
            reported.Add(message);
            try { Log(message); }
            catch (Exception) { }
        }

        // Windows "GPU Engine" / "GPU Adapter Memory" / "GPU Process Memory" counters: no driver call.
        // Needs two reads for the rate: null the first time.
        private CounterRead ReadGpuCounters(bool wantProcessMemory)
        {
            try
            {
                if (gpuCountersTried && !gpuCountersOk && unchecked(Environment.TickCount - gpuRetryAt) >= 0)
                {
                    // A transient failure (counter rebuild, driver change): start over after 5 s.
                    gpuCountersTried = false;
                    prevEngines = null;
                    gpuEngineCat = null;
                    gpuMemCat = null;
                    gpuProcCat = null;
                }
                if (!gpuCountersTried)
                {
                    gpuCountersTried = true;
                    gpuCountersOk = PerformanceCounterCategory.Exists(GpuEngineCategory);
                    if (!gpuCountersOk) gpuRetryAt = unchecked(Environment.TickCount + 30000); // no such counters: ask rarely
                    if (gpuCountersOk)
                    {
                        gpuEngineCat = new PerformanceCounterCategory(GpuEngineCategory);
                        if (PerformanceCounterCategory.Exists(GpuMemoryCategory)) gpuMemCat = new PerformanceCounterCategory(GpuMemoryCategory);
                    }
                }
                if (!gpuCountersOk) return null;
                InstanceDataCollectionCollection cur = gpuEngineCat.ReadCategory();
                if (!Has(cur, "utilization percentage")) return null;
                InstanceDataCollection now = cur["utilization percentage"];
                InstanceDataCollection prev = prevEngines;
                prevEngines = now;
                if (prev == null) return null;
                List<KeyValuePair<string, double>> engines = new List<KeyValuePair<string, double>>();
                foreach (object k in now.Keys)
                {
                    string name = k as string;
                    if (name == null || !prev.Contains(name)) continue;
                    float v = CounterSample.Calculate(prev[name].Sample, now[name].Sample);
                    if (float.IsNaN(v) || float.IsInfinity(v) || v < 0) continue;
                    engines.Add(new KeyValuePair<string, double>(name, v));
                }
                List<KeyValuePair<string, double>> mem = new List<KeyValuePair<string, double>>();
                if (gpuMemCat != null)
                {
                    InstanceDataCollectionCollection m = gpuMemCat.ReadCategory();
                    if (Has(m, "dedicated usage"))
                    {
                        InstanceDataCollection d = m["dedicated usage"];
                        foreach (object k in d.Keys)
                        {
                            string name = k as string;
                            if (name != null) mem.Add(new KeyValuePair<string, double>(name, d[name].Sample.RawValue));
                        }
                    }
                }
                CounterRead r = new CounterRead();
                r.Adapters = GpuEngineMath.Aggregate(engines, mem);
                r.ProcessLocal = new List<KeyValuePair<string, double>>();
                if (wantProcessMemory)
                {
                    if (gpuProcCat == null && PerformanceCounterCategory.Exists(GpuProcessCategory)) gpuProcCat = new PerformanceCounterCategory(GpuProcessCategory);
                    if (gpuProcCat != null)
                    {
                        InstanceDataCollectionCollection pm = gpuProcCat.ReadCategory();
                        if (Has(pm, "local usage"))
                        {
                            InstanceDataCollection lu = pm["local usage"];
                            foreach (object k in lu.Keys)
                            {
                                string name = k as string;
                                if (name != null) r.ProcessLocal.Add(new KeyValuePair<string, double>(name, lu[name].Sample.RawValue));
                            }
                        }
                    }
                }
                return r;
            }
            catch (Exception)
            {
                gpuCountersOk = false;
                gpuRetryAt = unchecked(Environment.TickCount + GpuCountersRetryMs);
                return null;
            }
        }

        public void Dispose()
        {
            // pendingSmi is process-wide and stays: the next probe must still see a stuck nvidia-smi.
            if (energy != null) { energy.Dispose(); energy = null; }
            prevCpu = null;
            prevEngines = null;
            cpuCat = null;
            gpuEngineCat = null;
            gpuMemCat = null;
            gpuProcCat = null;
        }
    }
}
