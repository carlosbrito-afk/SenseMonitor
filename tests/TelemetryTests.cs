using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Live telemetry: throttle detection with hysteresis, nvidia-smi parsing (captured sample
    // output of this machine's RTX 2050), the GPU query gate, GPU Engine aggregation and the
    // Windows power plan / power mode names.
    public static class TelemetryTests
    {
        // ---- CPU throttle detection ----

        private static CpuSample Cpu(double? limitPct, double? busyPerf, double? effective)
        {
            CpuSample s = new CpuSample();
            s.LimitPct = limitPct;
            s.SaturatedPerfPct = busyPerf;
            s.EffectiveMHz = effective;
            s.LoadPct = 90; // busy time: the CPU is loaded in these samples
            s.MaxCoreLoadPct = 100;
            return s;
        }

        private static CpuSample Idle(double? limitPct)
        {
            CpuSample s = new CpuSample();
            s.LimitPct = limitPct;
            s.EffectiveMHz = 800;
            s.LoadPct = 4;
            s.MaxCoreLoadPct = 12;
            return s;
        }

        [Test]
        public static void Throttle_LimitCounterAtIdle_IsNotThrottling()
        {
            // A Windows policy ceiling (battery, efficiency mode) lowers "% Performance Limit" with nothing running.
            ThrottleDetector d = new ThrottleDetector();
            FeedN(d, 8, Idle(60), 70.0);
            Assert.Equal(ThrottleState.Normal, d.State);
        }

        [Test]
        public static void Throttle_LimitCounterWithOneSaturatedCore_Counts()
        {
            ThrottleDetector d = new ThrottleDetector();
            CpuSample game = Idle(60);
            game.LoadPct = 30; // a game on few threads: low total, one core pegged
            game.MaxCoreLoadPct = 99;
            d.Feed(Healthy(), 60.0);
            FeedN(d, 3, game, 70.0);
            Assert.Equal(ThrottleState.PowerLimited, d.State);
        }

        [Test]
        public static void Throttle_BetweenEightyAndTheLimit_CauseIsUncertain()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            FeedN(d, 5, Cpu(70, 60, 1300), 88.0); // Unknown needs the 5 samples of leaving Normal
            Assert.Equal(ThrottleState.Unknown, d.State);
            Assert.True(d.Evidence != null && d.Evidence.Contains("energia ou calor"), "evidence: " + d.Evidence);
        }

        [Test]
        public static void Throttle_SaturationByBusyTime_CatchesACoreStuckAtLowClock()
        {
            // Real relation: a core 100 % busy (% Processor Time) held at 60 % of nominal reads
            // "% Processor Utility" of about 60, so utility would never call it saturated.
            double? perf = CpuMath.BusyWeighted(new double[] { 60, 40 }, new double[] { 100, 20 }, CpuMath.SaturatedFromPct);
            Assert.Equal(60.0, perf.Value);
            ThrottleDetector d = new ThrottleDetector();
            CpuSample s = Cpu(null, perf, 1300);
            d.Feed(Healthy(), 60.0);
            FeedN(d, 3, s, 70.0);
            Assert.Equal(ThrottleState.PowerLimited, d.State);
        }

        [Test]
        public static void CpuMath_LogicalProcessors_ExcludesTotalsAndKeepsEveryLogical()
        {
            // Names as ReadCategory spells them (lowercase), taken from this machine.
            string[] names = { "0,6", "0,7", "0,4", "0,5", "0,10", "_total", "0,_total", "0,11", "0,8", "0,9", "0,2", "0,3", "0,0", "0,1" };
            List<string> l = CpuMath.LogicalProcessors(names);
            Assert.Equal(12, l.Count);
            Assert.True(!l.Contains("_total") && !l.Contains("0,_total"), "totals are not processors");
            Assert.True(CpuMath.LogicalProcessors(null).Count == 0, "null");
            Assert.Equal(2, CpuMath.LogicalProcessors(new string[] { "1,0", "0,0", "x" }).Count);
        }

        private static CpuSample Healthy()
        {
            return Cpu(100, 150, 3200);
        }

        private static void FeedN(ThrottleDetector d, int n, CpuSample s, double? temp)
        {
            for (int i = 0; i < n; i++) d.Feed(s, temp);
        }

        [Test]
        public static void Throttle_NoCounters_IsUnknown()
        {
            ThrottleDetector d = new ThrottleDetector();
            Assert.Equal(ThrottleState.Unknown, d.Feed(new CpuSample(), 70.0));
            Assert.Equal(ThrottleState.Unknown, d.Feed(null, 70.0));
        }

        [Test]
        public static void Throttle_HealthyUnderLoad_IsNormalAtOnce()
        {
            ThrottleDetector d = new ThrottleDetector();
            Assert.Equal(ThrottleState.Normal, d.Feed(Healthy(), 80.0));
        }

        [Test]
        public static void Throttle_IdleWithClockCounters_IsNormal()
        {
            ThrottleDetector d = new ThrottleDetector();
            Assert.Equal(ThrottleState.Normal, d.Feed(Cpu(null, null, 800), 45.0));
        }

        [Test]
        public static void Throttle_LimitHotNeedsThreeSamplesToAppear()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 80.0);
            CpuSample hot = Cpu(80, 70, 1500);
            Assert.Equal(ThrottleState.Normal, d.Feed(hot, 96.0));
            Assert.Equal(ThrottleState.Normal, d.Feed(hot, 96.0));
            Assert.Equal(ThrottleState.TemperatureLimited, d.Feed(hot, 96.0));
        }

        [Test]
        public static void Throttle_LimitCold_IsPowerLimited()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            FeedN(d, 3, Cpu(60, 55, 1100), 70.0);
            Assert.Equal(ThrottleState.PowerLimited, d.State);
        }

        [Test]
        public static void Throttle_SubNominalClockUnderLoadWithoutLimitCounter_IsLimited()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Cpu(null, 150, 3000), 70.0);
            FeedN(d, 3, Cpu(null, 70, 1500), 97.0);
            Assert.Equal(ThrottleState.TemperatureLimited, d.State);
        }

        [Test]
        public static void Throttle_LimitedButNoTemperature_CauseUnknown()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            FeedN(d, 5, Cpu(70, 60, 1300), null);
            Assert.Equal(ThrottleState.Unknown, d.State);
        }

        [Test]
        public static void Throttle_LeavingNeedsFiveCleanSamples()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            FeedN(d, 3, Cpu(70, 60, 1300), 98.0);
            Assert.Equal(ThrottleState.TemperatureLimited, d.State);
            FeedN(d, 4, Healthy(), 80.0);
            Assert.Equal(ThrottleState.TemperatureLimited, d.State);
            d.Feed(Healthy(), 80.0);
            Assert.Equal(ThrottleState.Normal, d.State);
        }

        [Test]
        public static void Throttle_Flicker_DoesNotChangeTheLabel()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            for (int i = 0; i < 10; i++)
            {
                d.Feed(i % 2 == 0 ? Cpu(70, 60, 1300) : Healthy(), 98.0);
            }
            Assert.Equal(ThrottleState.Normal, d.State);
        }

        [Test]
        public static void Throttle_ThermalToPower_NeedsThreeSamplesOfTheNewCause()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            FeedN(d, 3, Cpu(70, 60, 1300), 98.0);
            Assert.Equal(ThrottleState.TemperatureLimited, d.State);
            FeedN(d, 2, Cpu(70, 60, 1300), 75.0);
            Assert.Equal(ThrottleState.TemperatureLimited, d.State);
            d.Feed(Cpu(70, 60, 1300), 75.0);
            Assert.Equal(ThrottleState.PowerLimited, d.State);
        }

        [Test]
        public static void Throttle_ResetGoesBackToUnknown()
        {
            ThrottleDetector d = new ThrottleDetector();
            d.Feed(Healthy(), 60.0);
            d.Reset();
            Assert.Equal(ThrottleState.Unknown, d.State);
        }

        [Test]
        public static void Throttle_Texts()
        {
            Assert.Equal("Normal", ThrottleText.Of(ThrottleState.Normal));
            Assert.Equal("Limitado por temperatura", ThrottleText.Of(ThrottleState.TemperatureLimited));
            Assert.Equal("Limitado por energia", ThrottleText.Of(ThrottleState.PowerLimited));
            Assert.Equal("Desconhecido", ThrottleText.Of(ThrottleState.Unknown));
            Assert.True(ThrottleText.IsLimited(ThrottleState.PowerLimited), "power is a limit");
            Assert.True(!ThrottleText.IsLimited(ThrottleState.Normal), "normal is not");
        }

        [Test]
        public static void CpuMath_BusyWeighted_IgnoresIdleProcessors()
        {
            double? v = CpuMath.BusyWeighted(new double[] { 3000, 3000, 800, 800 }, new double[] { 100, 100, 2, 1 });
            Assert.Equal(3000.0, v.Value);
            Assert.True(CpuMath.BusyWeighted(new double[] { 800, 800 }, new double[] { 5, 10 }) == null, "nobody busy");
            double? w = CpuMath.BusyWeighted(new double[] { 4000, 2000 }, new double[] { 100, 50 });
            Assert.True(Math.Abs(w.Value - 3333.3333) < 0.01, "weighted by use: " + w);
        }

        [Test]
        public static void CpuMath_Saturated_IgnoresACoreThatIsOnlyPartlyUsed()
        {
            // A core at 60 % use runs slow because Windows asks for less: not evidence of throttling.
            double? p = CpuMath.BusyWeighted(new double[] { 40, 150 }, new double[] { 60, 100 }, CpuMath.SaturatedFromPct);
            Assert.Equal(150.0, p.Value);
            Assert.True(CpuMath.BusyWeighted(new double[] { 40 }, new double[] { 60 }, CpuMath.SaturatedFromPct) == null, "no saturated core");
        }

        [Test]
        public static void Throttle_NoSaturatedCoreAndNoLimit_IsNormalNotThrottled()
        {
            ThrottleDetector d = new ThrottleDetector();
            CpuSample partial = Cpu(100, null, 1400); // moderate load: slow because of the governor
            FeedN(d, 6, partial, 97.0);
            Assert.Equal(ThrottleState.Normal, d.State);
        }

        [Test]
        public static void CpuMath_BusyPct_FromSystemTimes()
        {
            // 10 s of 12 logical processors, kernel time includes idle: 3 of 12 busy = 25 %.
            long span = 10L * 10000000 * 12;
            double? b = CpuMath.BusyPct(0, 0, 0, span * 3 / 4, span * 9 / 10, span / 10);
            Assert.Equal(25.0, Math.Round(b.Value, 6));
            Assert.True(CpuMath.BusyPct(5, 10, 10, 5, 10, 10) == null, "no time advanced");
            Assert.True(CpuMath.BusyPct(0, 0, 0, 50, 10, 10) == null, "idle above total is nonsense");
            Assert.Equal(0.0, CpuMath.BusyPct(0, 0, 0, 100, 100, 0).Value);
        }

        [Test]
        public static void CpuMath_Max_AndWatts()
        {
            Assert.Equal(90.0, CpuMath.Max(new double[] { 10, 90, 40 }).Value);
            Assert.True(CpuMath.Max(new double[0]) == null, "empty");
            Assert.Equal(53.60765, Math.Round(CpuMath.WattsFromMilliwatts(53607.65).Value, 5));
            Assert.True(CpuMath.WattsFromMilliwatts(0) == null, "zero is not a reading");
            Assert.True(CpuMath.WattsFromMilliwatts(9000000) == null, "9 kW is not a CPU");
            Assert.Equal(100.0, CpuMath.Clamp100(149.7));
        }

        // ---- nvidia-smi ----

        // Captured from this machine (RTX 2050 Laptop, idle, P8): power.limit is [N/A] there.
        private const string RealIdle = "NVIDIA GeForce RTX 2050, 0, 47.84, 0, 4096, 210, 2100, 3.17, [N/A], 0x0000000000000001, P8\r\n";

        [Test]
        public static void NvidiaSmi_RealIdleOutput()
        {
            GpuSample g = NvidiaSmiParser.Parse(RealIdle);
            Assert.True(g != null, "parsed");
            Assert.Equal("NVIDIA GeForce RTX 2050", g.Name);
            Assert.Equal(0.0, g.UsagePct.Value);
            Assert.Equal(47.84, g.TempC.Value);
            Assert.Equal(0.0, g.VramUsedMiB.Value);
            Assert.Equal(4096.0, g.VramTotalMiB.Value);
            Assert.Equal(210.0, g.ClockMHz.Value);
            Assert.Equal(2100.0, g.MaxClockMHz.Value);
            Assert.Equal(3.17, g.PowerW.Value);
            Assert.True(g.PowerLimitW == null, "[N/A] power limit");
            Assert.Equal(1L, g.ThrottleMask.Value);
            Assert.Equal("P8", g.PState);
            Assert.Equal("nvidia-smi", g.Source);
            Assert.Equal("sem limitação", GpuThrottle.Text(g.ThrottleMask));
        }

        [Test]
        public static void NvidiaSmi_LoadedPowerCapped()
        {
            GpuSample g = NvidiaSmiParser.Parse("NVIDIA GeForce RTX 2050, 98, 78, 3120, 4096, 1470, 2100, 55.20, 60.00, 0x0000000000000004, P0\n");
            Assert.Equal(98.0, g.UsagePct.Value);
            Assert.Equal(3120.0, g.VramUsedMiB.Value);
            Assert.Equal(60.0, g.PowerLimitW.Value);
            Assert.Equal(GpuLimit.Power, GpuThrottle.Decode(g.ThrottleMask));
            Assert.Equal("limitada por energia", GpuThrottle.Text(g.ThrottleMask));
        }

        [Test]
        public static void NvidiaSmi_NameWithCommaStillParses()
        {
            GpuSample g = NvidiaSmiParser.Parse("Fake, Board Rev 2, 10, 50, 100, 4096, 300, 2100, 20.0, 40.0, 0x0, P5");
            Assert.Equal("Fake, Board Rev 2", g.Name);
            Assert.Equal(10.0, g.UsagePct.Value);
            Assert.Equal("P5", g.PState);
        }

        [Test]
        public static void NvidiaSmi_Garbage_IsNull()
        {
            Assert.Null(NvidiaSmiParser.Parse(null));
            Assert.Null(NvidiaSmiParser.Parse(""));
            Assert.Null(NvidiaSmiParser.Parse("No devices were found"));
            Assert.Null(NvidiaSmiParser.Parse("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));
            Assert.Null(NvidiaSmiParser.Parse("x, [N/A], [N/A], [N/A], [N/A], [N/A], [N/A], [N/A], [N/A], [N/A], [N/A]"));
        }

        [Test]
        public static void NvidiaSmi_NotSupportedFieldsAreNull()
        {
            GpuSample g = NvidiaSmiParser.Parse("GPU, 5, 50, 100, 4096, 300, 2100, [Not Supported], [Not Supported], [Not Supported], P8");
            Assert.True(g.PowerW == null && g.PowerLimitW == null && g.ThrottleMask == null, "not supported = null");
            Assert.Null(GpuThrottle.Text(g.ThrottleMask));
        }

        [Test]
        public static void GpuThrottle_Reasons()
        {
            Assert.Equal(GpuLimit.Thermal, GpuThrottle.Decode(0x20));
            Assert.Equal(GpuLimit.Thermal, GpuThrottle.Decode(0x40 | 0x8));
            Assert.Equal(GpuLimit.Power, GpuThrottle.Decode(0x80));
            Assert.Equal(GpuLimit.Hardware, GpuThrottle.Decode(0x8));
            Assert.Equal(GpuLimit.None, GpuThrottle.Decode(0x1 | 0x2 | 0x10 | 0x100));
            Assert.Equal("limitada por temperatura e energia", GpuThrottle.Text(0x24));
            Assert.Equal("limitada pelo hardware", GpuThrottle.Text(0x8));
        }

        [Test]
        public static void NvidiaSmi_ArgumentsAskTenValues()
        {
            Assert.True(NvidiaSmiParser.Arguments.Contains("--format=csv,noheader,nounits"), "csv flags");
            Assert.Equal(11, NvidiaSmiParser.Fields.Split(',').Length);
        }

        // ---- the gate that keeps nvidia-smi from waking the GPU ----

        [Test]
        public static void Gate_HiddenWindow_NeverQueries()
        {
            Assert.Equal(GpuQueryDecision.SkipWindowHidden, GpuQueryGate.Decide(false, true, 60.0, 99999, true));
        }

        [Test]
        public static void Gate_SleepingGpu_NeverQueries()
        {
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.Decide(true, true, 0.0, 99999, true));
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.Decide(true, true, -1.0, 99999, true));
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.Decide(true, true, double.NaN, 99999, true));
        }

        [Test]
        public static void Gate_NoNvidiaSmi_Skips()
        {
            Assert.Equal(GpuQueryDecision.SkipUnavailable, GpuQueryGate.Decide(true, false, 60.0, 99999, true));
        }

        [Test]
        public static void Gate_RespectsTwoSecondSpacing()
        {
            Assert.Equal(GpuQueryDecision.SkipTooSoon, GpuQueryGate.Decide(true, true, 55.0, 1999, true));
            Assert.Equal(GpuQueryDecision.Query, GpuQueryGate.Decide(true, true, 55.0, 2000, true));
            Assert.Equal(GpuQueryDecision.Query, GpuQueryGate.Decide(true, true, 55.0, -1, true)); // never queried yet
        }

        [Test]
        public static void Gate_MissingFirmwareReading_DoesNotBlock()
        {
            Assert.Equal(GpuQueryDecision.Query, GpuQueryGate.Decide(true, true, null, 5000, true));
        }

        // ---- the driver-free "is the dGPU already active?" signal (ruling: no nvidia-smi otherwise) ----

        [Test]
        public static void Gate_DGpuIdle_NeverQueries_EvenWithAValidTemperatureAndTime()
        {
            Assert.Equal(GpuQueryDecision.SkipIdle, GpuQueryGate.Decide(true, true, 55.0, 99999, false));
            Assert.Equal(GpuQueryDecision.SkipIdle, GpuQueryGate.Decide(true, true, null, -1, false));
        }

        [Test]
        public static void Gate_DGpuActive_Queries()
        {
            Assert.Equal(GpuQueryDecision.Query, GpuQueryGate.Decide(true, true, 55.0, 99999, true));
        }

        [Test]
        public static void Gate_OtherSkipsOutrankIdle()
        {
            Assert.Equal(GpuQueryDecision.SkipWindowHidden, GpuQueryGate.Decide(false, true, 55.0, 99999, false));
            Assert.Equal(GpuQueryDecision.SkipUnavailable, GpuQueryGate.Decide(true, false, 55.0, 99999, false));
            Assert.Equal(GpuQueryDecision.SkipSleeping, GpuQueryGate.Decide(true, true, 0.0, 99999, false));
        }

        private const string Nv = "0x00000000_0x00011447";

        private static KeyValuePair<string, double> P(int pid, string luid, double bytes)
        {
            return new KeyValuePair<string, double>("pid_" + pid + "_luid_" + luid + "_phys_0", bytes);
        }

        // Real idle photograph of the RTX 2050 LUID (reviewer): System pid 4 = 4,132,864 B and three
        // processes with a D3D context at 24,576 B each. Every rule must keep the gate closed.
        private static List<KeyValuePair<string, double>> RealIdleProcesses()
        {
            List<KeyValuePair<string, double>> p = new List<KeyValuePair<string, double>>();
            p.Add(P(4, Nv, 4132864));
            p.Add(P(1220, Nv, 24576));
            p.Add(P(10556, Nv, 24576));
            p.Add(P(13648, Nv, 24576));
            p.Add(P(3700, "0x00000000_0x00010fe3", 324890624)); // the iGPU is busy: another LUID
            return p;
        }

        [Test]
        public static void Activity_RealIdleProcessMemory_StaysGated()
        {
            double? max = GpuEngineMath.MaxNonSystemLocal(RealIdleProcesses(), GpuEngineMath.LuidKey(0, 0x11447), null);
            Assert.Equal(24576.0, max.Value); // pid 4 is skipped and nothing is summed
            GpuActivityTracker t = new GpuActivityTracker();
            Assert.True(!t.Feed(0.0, max), "idle: no engine use, 24 KB processes");
            Assert.True(!t.Feed(0.0, max), "still idle");
            Assert.True(!GpuQueryGate.Decide(true, true, 55.0, 99999, t.Active).Equals(GpuQueryDecision.Query), "the gate stays shut");
        }

        [Test]
        public static void Activity_Pid4AndPid0AreNeverCounted_EvenWhenHuge()
        {
            List<KeyValuePair<string, double>> p = new List<KeyValuePair<string, double>>();
            p.Add(P(4, Nv, 500.0 * 1048576));
            p.Add(P(0, Nv, 500.0 * 1048576));
            Assert.True(GpuEngineMath.MaxNonSystemLocal(p, GpuEngineMath.LuidKey(0, 0x11447), null) == null, "system pids ignored");
        }

        [Test]
        public static void Activity_Session0SystemProcessesAreSkipped()
        {
            List<KeyValuePair<string, double>> p = new List<KeyValuePair<string, double>>();
            p.Add(P(900, Nv, 200.0 * 1048576)); // a service in session 0
            p.Add(P(5000, Nv, 10.0 * 1048576));
            double? max = GpuEngineMath.MaxNonSystemLocal(p, GpuEngineMath.LuidKey(0, 0x11447), delegate(int pid) { return pid == 900; });
            Assert.Equal(10.0 * 1048576, max.Value);
        }

        [Test]
        public static void Activity_ANonSystemProcessWithSixtyFourMiB_IsActive_NotBelow()
        {
            GpuActivityTracker t = new GpuActivityTracker();
            Assert.True(!t.Feed(0.0, 63.9 * 1048576), "just below");
            Assert.True(t.Feed(0.0, 64.0 * 1048576), "a game holds 64 MiB or more");
            Assert.True(!new GpuActivityTracker().Feed(0.0, null), "no data, no proof");
        }

        [Test]
        public static void Activity_EngineNeedsTwoSamplesInARowAboveTwoPercent()
        {
            GpuActivityTracker t = new GpuActivityTracker();
            Assert.True(!t.Feed(2.0, null), "exactly 2 % is not above 2 %");
            Assert.True(!t.Feed(5.0, null), "first sample above");
            Assert.True(t.Feed(5.0, null), "second sample in a row");
            Assert.True(!t.Feed(0.0, null), "one idle sample closes it");
            Assert.True(!t.Feed(9.0, null), "needs two again");
            Assert.True(t.Feed(9.0, null), "two again");
            t.Reset();
            Assert.True(!t.Active, "reset");
        }

        [Test]
        public static void Activity_AnyEngineTypeCounts_NotOnly3D()
        {
            List<KeyValuePair<string, double>> e = new List<KeyValuePair<string, double>>();
            e.Add(E("1", Nv, 3, "VideoDecode", 30));
            GpuAdapterLoad a = GpuEngineMath.FindByLuid(GpuEngineMath.Aggregate(e, null), GpuEngineMath.LuidKey(0, 0x11447));
            GpuActivityTracker t = new GpuActivityTracker();
            t.Feed(a.UtilAnyPct, null);
            Assert.True(t.Feed(a.UtilAnyPct, null), "video decode on the dGPU is activity");
        }

        [Test]
        public static void Activity_ParsesTheProcessId()
        {
            int pid;
            Assert.True(GpuEngineMath.TryParsePid("pid_1220_luid_0x00000000_0x00011447_phys_0", out pid), "parses");
            Assert.Equal(1220, pid);
            Assert.True(!GpuEngineMath.TryParsePid("luid_0x00000000_0x00011447_phys_0", out pid), "no pid");
        }

        [Test]
        public static void Gate_FirstQueryGetsSixSeconds_WarmOnesTwo()
        {
            Assert.Equal(6000, GpuQueryGate.TimeoutFor(false));
            Assert.Equal(2000, GpuQueryGate.TimeoutFor(true));
        }

        // ---- nvidia-smi field tolerance ----

        [Test]
        public static void NvidiaSmi_RefusedField_IsDroppedAndTheRestParses()
        {
            string refused = "Field \"clocks_throttle_reasons.active\" is not a valid field to query.";
            Assert.Equal("clocks_throttle_reasons.active", NvidiaSmiParser.InvalidField(refused));
            Assert.Null(NvidiaSmiParser.InvalidField("NVIDIA GeForce RTX 2050, 0, 47.84"));
            List<string> next = NvidiaSmiParser.NextFields(NvidiaSmiParser.DefaultFields, "clocks_throttle_reasons.active");
            Assert.True(next.Contains("clocks_event_reasons.active") && !next.Contains("clocks_throttle_reasons.active"), "renamed field is tried first");
            List<string> dropped = NvidiaSmiParser.NextFields(next, "clocks_event_reasons.active");
            Assert.True(!dropped.Contains("clocks_event_reasons.active"), "then dropped");
            GpuSample g = NvidiaSmiParser.Parse("NVIDIA GeForce RTX 2050, 98, 78, 3120, 4096, 1470, 2100, 55.20, 60.00, P0", dropped);
            Assert.Equal(98.0, g.UsagePct.Value);
            Assert.Equal(55.2, g.PowerW.Value);
            Assert.Equal("P0", g.PState);
            Assert.True(g.ThrottleMask == null, "the dropped field is null, not a total failure");
        }

        [Test]
        public static void NvidiaSmi_NextFields_RefusesWhatItCannotFix()
        {
            Assert.Null(NvidiaSmiParser.NextFields(NvidiaSmiParser.DefaultFields, "name"));
            Assert.Null(NvidiaSmiParser.NextFields(NvidiaSmiParser.DefaultFields, "not.in.the.list"));
            Assert.Null(NvidiaSmiParser.NextFields(NvidiaSmiParser.DefaultFields, null));
            Assert.Null(NvidiaSmiParser.NextFields(new string[] { "name", "pstate" }, "pstate"));
        }

        [Test]
        public static void NvidiaSmi_ArgumentsForAFieldList()
        {
            Assert.Equal("--query-gpu=name,pstate --format=csv,noheader,nounits", NvidiaSmiParser.ArgumentsFor(new string[] { "name", "pstate" }));
        }


        [Test]
        public static void Luid_KeyMatchesTheCounterInstanceNames()
        {
            Assert.Equal("0x00000000_0x00011447", GpuEngineMath.LuidKey(0, 0x11447));
            Assert.Equal("0x00000001_0x0000abcd", GpuEngineMath.LuidKey(1, 0xABCD));
            List<KeyValuePair<string, double>> e = new List<KeyValuePair<string, double>>();
            e.Add(E("1", "0x00000000_0x00011447", 0, "3D", 40));
            e.Add(E("1", "0x00000000_0x00010FE3", 0, "3D", 7));
            List<GpuAdapterLoad> a = GpuEngineMath.Aggregate(e, null);
            Assert.Equal(40.0, GpuEngineMath.FindByLuid(a, GpuEngineMath.LuidKey(0, 0x11447)).Util3DPct);
            Assert.True(GpuEngineMath.FindByLuid(a, GpuEngineMath.LuidKey(0, 0x555)) == null, "unknown LUID");
            Assert.True(GpuEngineMath.FindByLuid(null, "x") == null, "null list");
        }

        [Test]
        public static void Luid_ToSampleCarriesTheAdapterNameAndMemory()
        {
            GpuAdapterLoad a = new GpuAdapterLoad();
            a.Util3DPct = 0;
            a.DedicatedMiB = 0;
            GpuSample g = GpuEngineMath.ToSample(a, "NVIDIA GeForce RTX 2050");
            Assert.Equal("NVIDIA GeForce RTX 2050", g.Name);
            Assert.Equal(0.0, g.VramUsedMiB.Value);
            Assert.Equal("contadores do Windows", g.Source);
        }

        // ---- GPU Engine counters (generic fallback) ----

        private static KeyValuePair<string, double> E(string pid, string luid, int eng, string type, double v)
        {
            return new KeyValuePair<string, double>("pid_" + pid + "_luid_" + luid + "_phys_0_eng_" + eng + "_engtype_" + type, v);
        }

        [Test]
        public static void GpuEngine_ParsesInstanceNames()
        {
            string key, type;
            int eng;
            Assert.True(GpuEngineMath.TryParseEngine("pid_4_luid_0x00000000_0x000113B0_phys_0_eng_10_engtype_3D", out key, out eng, out type), "parses");
            Assert.Equal("0x00000000_0x000113b0_phys_0", key);
            Assert.Equal(10, eng);
            Assert.Equal("3D", type);
            Assert.True(GpuEngineMath.TryParseEngine("pid_29984_luid_0x00000000_0x00010FE3_phys_0_eng_8_engtype_", out key, out eng, out type), "empty type parses");
            Assert.Equal("", type);
            Assert.True(!GpuEngineMath.TryParseEngine("garbage", out key, out eng, out type), "garbage");
            Assert.Equal("0x00000000_0x000113b0_phys_0", GpuEngineMath.AdapterKey("luid_0x00000000_0x000113B0_phys_0"));
        }

        [Test]
        public static void GpuEngine_SumsProcessesOnTheSameEngine_MaxAcrossEngines()
        {
            List<KeyValuePair<string, double>> e = new List<KeyValuePair<string, double>>();
            e.Add(E("1", "0x00000000_0x0000AAAA", 0, "3D", 30));
            e.Add(E("2", "0x00000000_0x0000AAAA", 0, "3D", 25));   // engine 0: 55
            e.Add(E("3", "0x00000000_0x0000AAAA", 1, "3D", 10));   // engine 1: 10
            e.Add(E("3", "0x00000000_0x0000AAAA", 3, "Copy", 70)); // not 3D
            List<GpuAdapterLoad> a = GpuEngineMath.Aggregate(e, null);
            Assert.Equal(1, a.Count);
            Assert.Equal(55.0, a[0].Util3DPct);
            Assert.Equal(70.0, a[0].UtilAnyPct);
        }

        [Test]
        public static void GpuEngine_LowercaseNamesFromReadCategory_StillCount()
        {
            List<KeyValuePair<string, double>> e = new List<KeyValuePair<string, double>>();
            e.Add(new KeyValuePair<string, double>("pid_4_luid_0x00000000_0x000113b0_phys_0_eng_10_engtype_3d", 42));
            List<KeyValuePair<string, double>> m = new List<KeyValuePair<string, double>>();
            m.Add(new KeyValuePair<string, double>("luid_0x00000000_0x000113b0_phys_0", 4247552));
            List<GpuAdapterLoad> a = GpuEngineMath.Aggregate(e, m);
            Assert.Equal(1, a.Count);
            Assert.Equal(42.0, a[0].Util3DPct);
            Assert.Equal(4.0, Math.Round(a[0].DedicatedMiB.Value));
        }

        [Test]
        public static void GpuEngine_CapsAt100_AndPicksTheBusiestAdapter()
        {
            List<KeyValuePair<string, double>> e = new List<KeyValuePair<string, double>>();
            e.Add(E("1", "0x00000000_0x0000AAAA", 0, "3D", 80));
            e.Add(E("2", "0x00000000_0x0000AAAA", 0, "3D", 70));
            e.Add(E("9", "0x00000000_0x0000BBBB", 0, "3D", 5));
            List<GpuAdapterLoad> a = GpuEngineMath.Aggregate(e, null);
            Assert.Equal(2, a.Count);
            GpuAdapterLoad best = GpuEngineMath.Pick(a);
            Assert.Equal(100.0, best.Util3DPct);
            GpuSample g = GpuEngineMath.ToSample(best);
            Assert.Equal(100.0, g.UsagePct.Value);
            Assert.Equal("contadores do Windows", g.Source);
        }

        [Test]
        public static void GpuEngine_IdleTieGoesToTheAdapterWithMoreDedicatedMemory()
        {
            List<KeyValuePair<string, double>> e = new List<KeyValuePair<string, double>>();
            e.Add(E("1", "0x00000000_0x0000AAAA", 0, "3D", 0));
            e.Add(E("1", "0x00000000_0x0000BBBB", 0, "3D", 0));
            List<KeyValuePair<string, double>> m = new List<KeyValuePair<string, double>>();
            m.Add(new KeyValuePair<string, double>("luid_0x00000000_0x0000AAAA_phys_0", 120.0 * 1048576));
            m.Add(new KeyValuePair<string, double>("luid_0x00000000_0x0000BBBB_phys_0", 900.0 * 1048576));
            GpuAdapterLoad best = GpuEngineMath.Pick(GpuEngineMath.Aggregate(e, m));
            Assert.True(best.Key.Contains("bbbb"), "the dGPU holds more memory: " + best.Key);
            Assert.Equal(900.0, Math.Round(best.DedicatedMiB.Value));
        }

        [Test]
        public static void GpuEngine_Empty_IsNull()
        {
            Assert.Null(GpuEngineMath.Pick(GpuEngineMath.Aggregate(null, null)));
            Assert.Null(GpuEngineMath.ToSample(null));
        }

        // ---- power plan / power mode ----

        [Test]
        public static void PowerPlan_NamesFromGuids()
        {
            Assert.Equal("Equilibrado", PowerPlanNames.SchemeName("381B4222-F694-41F0-9685-FF5BB260DF2E"));
            Assert.Equal("Melhor desempenho", PowerPlanNames.OverlayName("3af9b8d9-7c97-431d-ad78-34a8bfea439f"));
            Assert.Equal("Melhor eficiência de energia", PowerPlanNames.OverlayName("961cc777-2547-4f9d-8174-7d86181b8a7a"));
            Assert.Null(PowerPlanNames.SchemeName("11111111-1111-1111-1111-111111111111"));
            Assert.True(PowerPlanNames.IsEfficiencyOverlay("961CC777-2547-4F9D-8174-7D86181B8A7A"), "efficiency");
            Assert.True(!PowerPlanNames.IsEfficiencyOverlay(PowerPlanNames.NoOverlay), "balanced is not");
        }

        [Test]
        public static void PowerPlan_Line()
        {
            PowerPlanInfo p = PowerPlanNames.Named("381b4222-f694-41f0-9685-ff5bb260df2e", "ded574b5-45a0-4f42-8737-46345c09c238", false);
            Assert.Equal("Plano Equilibrado · Energia: Desempenho máximo · na tomada", p.Line);
            PowerPlanInfo q = PowerPlanNames.Named("22222222-2222-2222-2222-222222222222", null, true);
            Assert.Equal("Plano personalizado · na bateria", q.Line);
            Assert.Null(new PowerPlanInfo().Line);
            Assert.Null(PowerPlanNames.Line(null));
        }
    }
}
