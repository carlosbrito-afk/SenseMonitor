using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Live readings of this machine (counters + power plan). The GPU path is not exercised here:
    // starting nvidia-smi can wake a sleeping GPU, and the gate that decides is tested in TelemetryTests.
    public static class TelemetryProbeTests
    {
        [Test]
        public static void Probe_Cpu_FirstReadPrimesThenReal()
        {
            using (TelemetryProbe p = new TelemetryProbe())
            {
                Assert.Null(p.ReadCpu());
                Thread.Sleep(1100);
                CpuSample s = p.ReadCpu();
                Assert.True(s != null, "second read returns a sample");
                Assert.True(s.LoadPct.HasValue && s.LoadPct.Value >= 0 && s.LoadPct.Value <= 100, "load: " + s.LoadPct);
                Assert.True(s.MaxCoreLoadPct.HasValue && s.MaxCoreLoadPct.Value >= 0 && s.MaxCoreLoadPct.Value <= 100, "max core: " + s.MaxCoreLoadPct);
                // A VM or an old Windows may lack the clock counters: then the field is null, not wrong.
                if (s.EffectiveMHz.HasValue) Assert.True(s.EffectiveMHz.Value > 200 && s.EffectiveMHz.Value < 7000, "clock: " + s.EffectiveMHz);
                if (s.LimitPct.HasValue) Assert.True(s.LimitPct.Value >= 0 && s.LimitPct.Value <= 100.5, "limit: " + s.LimitPct);
                if (s.PackageWatts.HasValue) Assert.True(s.PackageWatts.Value >= 0.1 && s.PackageWatts.Value <= 500, "watts: " + s.PackageWatts);
                if (s.BusyClockMHz.HasValue) Assert.True(s.BusyClockMHz.Value > 200 && s.BusyClockMHz.Value < 7000, "busy clock: " + s.BusyClockMHz);
            }
        }

        [Test]
        public static void PowerPlan_ReadsTheActiveScheme()
        {
            PowerPlanInfo p = PowerPlanReader.Read(false);
            Assert.True(p != null && p.SchemeGuid != null && p.SchemeGuid.Length == 36, "scheme guid: " + (p == null ? "null" : p.SchemeGuid));
            Assert.True(p.Line != null && p.Line.StartsWith("Plano "), "line: " + p.Line);
            Assert.True(p.Line.EndsWith("na tomada"), "line: " + p.Line);
        }

        [Test]
        public static void Dxgi_FindsTheNvidiaAdapter_WhenNvidiaSmiIsInstalled()
        {
            uint high, low;
            string name;
            bool found = DxgiAdapters.FindNvidia(out high, out low, out name);
            if (InstallPaths.NvidiaSmiPath() == null) return; // no NVIDIA driver here: nothing to expect
            Assert.True(found, "an NVIDIA adapter is listed by DXGI");
            Assert.True(name != null && name.IndexOf("NVIDIA", System.StringComparison.OrdinalIgnoreCase) >= 0, "name: " + name);
            Assert.True(GpuEngineMath.LuidKey(high, low).StartsWith("0x"), "luid key");
        }

        [Test]
        public static void Probe_Gpu_WhenTheDGpuIsIdle_NeverStartsNvidiaSmi_AndShowsCounters()
        {
            // The first read primes the counters and never starts nvidia-smi. On the next reads
            // nvidia-smi may have been started only if the probe itself saw the NVIDIA GPU active
            // (SmiStarts is counted by the probe, so this cannot pass by returning early).
            using (TelemetryProbe p = new TelemetryProbe())
            {
                TelemetryFrame f = new TelemetryFrame();
                p.ReadGpu(f, 55.0);
                Assert.Equal(0, p.SmiStarts);
                Assert.True(f.Gpu == null && !f.GpuIdle && !f.GpuSleeping, "first read: no claim, no query");
                for (int i = 0; i < 3; i++)
                {
                    Thread.Sleep(1100);
                    f = new TelemetryFrame();
                    p.ReadGpu(f, 55.0);
                    if (p.SmiStarts > 0) Assert.True(p.LastActive, "nvidia-smi was started although the GPU showed no activity");
                    if (f.GpuIdle) Assert.True(f.Gpu == null || f.Gpu.Source == GpuSample.SourceCounters, "idle: counters only");
                }
            }
        }

        // The counters-only level (the widget) still tells a sleeping or idle dGPU apart, from fake inputs: the
        // sleep / idle checks run FIRST, because nothing is ever queried at this level.
        [Test]
        public static void DecideCounters_SleepingAndIdleAreSaid_NeverAQuery()
        {
            // firmware sensor at 0: the GPU sleeps, whatever the counters say and whether or not nvidia-smi exists
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.DecideCounters(true, true, 0.0, false));
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.DecideCounters(true, false, 0.0, true));
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.DecideCounters(true, true, -1.0, true));
            // awake sensor, NVIDIA adapter known, no activity on the counters: idle
            Assert.Equal(GpuQueryDecision.SkipIdle, GpuQueryGate.DecideCounters(true, true, 55.0, false));
            Assert.Equal(GpuQueryDecision.SkipIdle, GpuQueryGate.DecideCounters(true, true, null, false));
            // active: the counters' numbers are shown as they are (no sleep / idle claim, never Query)
            Assert.Equal(GpuQueryDecision.SkipUnavailable, GpuQueryGate.DecideCounters(true, true, 55.0, true));
            // no NVIDIA adapter found: no idle claim either (nothing says which adapter is the dGPU)
            Assert.Equal(GpuQueryDecision.SkipUnavailable, GpuQueryGate.DecideCounters(true, false, 55.0, false));
            // a missing reading is no evidence of sleep
            Assert.Equal(GpuQueryDecision.SkipUnavailable, GpuQueryGate.DecideCounters(true, true, null, true));
            Assert.Equal(GpuQueryDecision.SkipWindowHidden, GpuQueryGate.DecideCounters(false, true, 0.0, false));
            // the full gate is unchanged: it still asks nvidia-smi availability first
            Assert.Equal(GpuQueryDecision.SkipUnavailable, GpuQueryGate.Decide(true, false, 0.0, -1, false));
            foreach (bool nv in new bool[] { true, false })
            {
                foreach (bool act in new bool[] { true, false })
                {
                    Assert.True(GpuQueryGate.DecideCounters(true, nv, 55.0, act) != GpuQueryDecision.Query, "counters mode never queries");
                }
            }
        }

        [Test]
        public static void GpuWant_CountersNeverAllowNvidiaSmi_OnlyFullDoes()
        {
            Assert.True(!GpuWants.SmiPossible(GpuWant.None, true, true), "none");
            Assert.True(!GpuWants.SmiPossible(GpuWant.Counters, true, true), "the widget's level never starts nvidia-smi");
            Assert.True(GpuWants.SmiPossible(GpuWant.Full, true, true), "Monitor page");
            Assert.True(!GpuWants.SmiPossible(GpuWant.Full, false, true), "no nvidia-smi installed");
            Assert.True(!GpuWants.SmiPossible(GpuWant.Full, true, false), "no NVIDIA adapter found");
            Assert.True(!GpuWants.ReadsGpu(GpuWant.None) && GpuWants.ReadsGpu(GpuWant.Counters) && GpuWants.ReadsGpu(GpuWant.Full), "reads");
            Assert.Equal(GpuWant.Full, GpuWants.Combine(new GpuWant[] { GpuWant.None, GpuWant.Counters, GpuWant.Full }));
            Assert.Equal(GpuWant.Counters, GpuWants.Combine(new GpuWant[] { GpuWant.None, GpuWant.Counters }));
            Assert.Equal(GpuWant.None, GpuWants.Combine(new GpuWant[0]));
            Assert.Equal(GpuWant.None, GpuWants.Combine(null));
        }

        [Test]
        public static void Integration_Probe_CountersOnly_NeverStartsNvidiaSmi_AndNoneReadsNothing()
        {
            using (TelemetryProbe p = new TelemetryProbe())
            {
                TelemetryFrame f = new TelemetryFrame();
                p.ReadGpu(f, 55.0, GpuWant.None);
                Assert.True(f.Gpu == null && !f.GpuIdle && !f.GpuSleeping, "None reads nothing");
                for (int i = 0; i < 3; i++)
                {
                    f = new TelemetryFrame();
                    p.ReadGpu(f, 55.0, GpuWant.Counters);
                    Assert.Equal(0, p.SmiStarts);
                    if (f.Gpu != null) Assert.True(f.Gpu.Source == GpuSample.SourceCounters, "numbers come from the Windows counters");
                    Thread.Sleep(1100);
                }
            }
        }

        [Test]
        public static void Probe_ReadPowerPlan_IsCached()
        {
            using (TelemetryProbe p = new TelemetryProbe())
            {
                PowerPlanInfo a = p.ReadPowerPlan();
                PowerPlanInfo b = p.ReadPowerPlan();
                Assert.True(object.ReferenceEquals(a, b), "same object inside 5 s");
            }
        }
    }
}
