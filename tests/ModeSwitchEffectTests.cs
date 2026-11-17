using System;
using Forja.Core;

namespace Forja.Tests
{
    // "Proof that the mode changed": 60 s before against 30-60 s after the switch. Nothing is
    // attributed to the mode unless the load is known on both sides and comparable.
    public static class ModeSwitchEffectTests
    {
        private const int T0 = 1000000; // the switch

        private static EffectSample S(int tick, double? load, double? clock, int? fan, double? temp)
        {
            EffectSample s = new EffectSample();
            s.Tick = tick;
            s.LoadPct = load;
            s.MaxCoreLoadPct = load;
            s.ClockMHz = clock;
            s.CpuFanRpm = fan;
            s.GpuFanRpm = fan;
            s.CpuTempC = temp;
            return s;
        }

        // 1 s samples: 60 s before the switch, and 1-60 s after it (30-60 s counts).
        private static ModeSwitchTracker Fill(double load, double clockBefore, double clockAfter, int fanBefore, int fanAfter,
            double tempBefore, double tempAfter, int baseTick)
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(unchecked(baseTick + s * 1000), load, clockBefore, fanBefore, tempBefore));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, baseTick);
            for (int s = 1; s <= 60; s++) t.Add(S(unchecked(baseTick + s * 1000), load, clockAfter, fanAfter, tempAfter));
            return t;
        }

        [Test]
        public static void NoSwitch_NothingToShow()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            Assert.Equal(EffectStatus.None, t.Evaluate(T0).Status);
            Assert.Null(t.Evaluate(T0).Verdict);
        }

        [Test]
        public static void Measuring_UntilTheWindowIsComplete()
        {
            ModeSwitchTracker t = Fill(90, 2800, 3300, 3000, 3900, 80, 80, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 20000);
            Assert.Equal(EffectStatus.Measuring, e.Status);
            Assert.Equal(40, e.SecondsLeft);
            Assert.Equal("ainda medindo (faltam 40 s)", e.Verdict);
            Assert.Equal(EffectStatus.Measuring, t.Evaluate(T0 + 45000).Status);
            Assert.Equal(EffectStatus.Ready, t.Evaluate(T0 + 60000).Status);
        }

        [Test]
        public static void Performance_ClockAndFanVerdict()
        {
            ModeSwitchTracker t = Fill(90, 2800, 3304, 3000, 3900, 80, 80, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.Ready, e.Status);
            Assert.Equal("Desempenho: +18 % de clock sob carga, ventoinha +900 rpm", e.Verdict);
            Assert.Equal(900.0, Math.Round(e.FanDeltaRpm.Value));
            Assert.Equal(18.0, Math.Round(e.ClockDeltaPct.Value));
            Assert.Null(e.ClockNote);
        }

        [Test]
        public static void Silent_NegativeNumbersUseARealMinusAndGrouping()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 90, 3500, 4500, 85));
            t.MarkSwitch(PerfMode.Desempenho, PerfMode.Silencioso, T0);
            for (int s = 1; s <= 60; s++) t.Add(S(T0 + s * 1000, 90, 2800, 3300, 78));
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal("Silencioso: −20 % de clock sob carga, ventoinha −1.200 rpm, CPU −7 °C", e.Verdict);
        }

        [Test]
        public static void Idle_SaysThereIsNoLoadToCompare()
        {
            ModeSwitchTracker t = Fill(4, 800, 800, 2000, 2000, 45, 45, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.NoLoad, e.Status);
            Assert.Equal("sem carga para comparar", e.Verdict);
        }

        [Test]
        public static void Idle_ButFanMoved_StillShowsTheFan_BecauseTheLoadIsKnownAndEqual()
        {
            ModeSwitchTracker t = Fill(4, 800, 800, 2000, 3000, 45, 45, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.Ready, e.Status);
            Assert.Equal("Desempenho: ventoinha +1.000 rpm (clock: sem carga para comparar)", e.Verdict);
        }

        [Test]
        public static void LoadedBothSides_NoChange_SaysNoMeasurableDifference()
        {
            ModeSwitchTracker t = Fill(95, 3000, 3030, 3000, 3050, 80, 80.5, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.Ready, e.Status);
            Assert.Equal("Desempenho: clock igual sob carga", e.Verdict);
        }

        [Test]
        public static void HiddenWindowWithSystemLoad_FansAndTempAreComparedWithoutClock()
        {
            // Firmware ticks of 2 s with the load read from GetSystemTimes: known and equal, no clock.
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s += 2) t.Add(S(T0 + s * 1000, 6, null, 3000, 70));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 2; s <= 60; s += 2) t.Add(S(T0 + s * 1000, 5, null, 4200, 74));
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.Ready, e.Status);
            Assert.Equal("Desempenho: ventoinha +1.200 rpm, CPU +4 °C (clock: indisponível)", e.Verdict);
        }

        [Test]
        public static void UnknownLoadAfter_IsNeverBlamedOnTheMode()
        {
            // The real flow: switch in Modos (window open, idle load known), close the window and start
            // a game: the "after" samples are firmware ticks with no load.
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 5, 800, 2000, 45));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 30; s <= 60; s += 12) t.Add(S(T0 + s * 1000, null, null, 4500, 85));
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.NoLoad, e.Status);
            Assert.Equal("sem carga comparável para medir", e.Verdict);
            Assert.True(e.FanDeltaRpm == null && e.CpuTempDeltaC == null && e.PowerDeltaW == null, "no fan/temp/power without a load");
        }

        [Test]
        public static void UnknownLoadBefore_IsNeverBlamedEither()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, null, null, 2000, 45));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++) t.Add(S(T0 + s * 1000, 5, 800, 4500, 85));
            Assert.Equal("sem carga comparável para medir", t.Evaluate(T0 + 60000).Verdict);
        }

        [Test]
        public static void LoadChangedALot_IsNotBlamedOnTheMode()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 5, 800, 2000, 45));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++) t.Add(S(T0 + s * 1000, 95, 3200, 4500, 85)); // a game started
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.NoLoad, e.Status);
            Assert.Equal("sem carga comparável para medir", e.Verdict);
            Assert.True(e.FanDeltaRpm == null, "fans not compared across different loads");
        }

        [Test]
        public static void LoadsWithinFifteenPoints_AreComparable_BeyondAreNot()
        {
            ModeSwitchTracker ok = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) ok.Add(S(T0 + s * 1000, 60, 2800, 3000, 80));
            ok.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++) ok.Add(S(T0 + s * 1000, 74, 3300, 3000, 80)); // 14 points
            Assert.Equal(EffectStatus.Ready, ok.Evaluate(T0 + 60000).Status);

            ModeSwitchTracker no = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) no.Add(S(T0 + s * 1000, 60, 2800, 3000, 80));
            no.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++) no.Add(S(T0 + s * 1000, 76, 3300, 3000, 80)); // 16 points
            ModeSwitchEffect e = no.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.NoLoad, e.Status);
            Assert.True(e.ClockDeltaPct == null, "no clock delta");
        }

        [Test]
        public static void ThirtySecondsOfValidSamplesNeededOnEachSide()
        {
            // "After" at 12 s ticks inside 30-60 s: 30, 42, 54 = 24 s of coverage: not enough.
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 90, 2800, 3000, 80));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 30; s <= 60; s += 12) t.Add(S(T0 + s * 1000, 90, 3300, 3900, 80));
            Assert.Equal("amostras insuficientes para medir", t.Evaluate(T0 + 60000).Verdict);
        }

        [Test]
        public static void TooFewSamples_IsSaidAsSuch_NotAsADifferentLoad()
        {
            // Same load on both sides, but the hidden 12 s tick leaves 24 s of coverage after the switch.
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 90, 2800, 3000, 80));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 30; s <= 60; s += 12) t.Add(S(T0 + s * 1000, 90, 3300, 3900, 80));
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.Equal(EffectStatus.TooFewSamples, e.Status);
            Assert.Equal("amostras insuficientes para medir", e.Verdict);
        }

        [Test]
        public static void UnknownOrDifferentLoad_StaysNotComparable_NotTooFewSamples()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 5, 800, 2000, 45));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 30; s <= 60; s += 12) t.Add(S(T0 + s * 1000, null, null, 4500, 85)); // no load at all
            Assert.Equal("sem carga comparável para medir", t.Evaluate(T0 + 60000).Verdict);
        }

        [Test]
        public static void ProofWindow_KeepsTheTickFastForSeventyFiveSeconds()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            Assert.True(!t.NeedsFastTick(T0), "no switch, no need");
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            Assert.True(t.NeedsFastTick(T0), "right after the switch");
            Assert.True(t.NeedsFastTick(T0 + 74999), "still inside 75 s");
            Assert.True(!t.NeedsFastTick(T0 + 75001), "after 75 s the tick may slow down again");
            Assert.True(!t.NeedsFastTick(T0 - 1000), "a clock before the switch is not inside it");
        }

        [Test]
        public static void ProofWindow_SurvivesTheTickCountWrap()
        {
            int nearWrap = int.MaxValue - 20000;
            ModeSwitchTracker t = new ModeSwitchTracker();
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, nearWrap);
            Assert.True(t.NeedsFastTick(unchecked(nearWrap + 60000)), "60 s later, past the wrap");
            Assert.True(!t.NeedsFastTick(unchecked(nearWrap + 80000)), "80 s later");
        }

        [Test]
        public static void NoSamplesAfter_NoData()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 90, 3000, 3000, 80));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 70000);
            Assert.Equal(EffectStatus.NoData, e.Status);
            Assert.True(e.Verdict.Contains("sem dados"), e.Verdict);
        }

        [Test]
        public static void SwitchWithoutHistory_NoData()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 30; s <= 60; s++) t.Add(S(T0 + s * 1000, 90, 3000, 3000, 80));
            Assert.Equal(EffectStatus.NoData, t.Evaluate(T0 + 60000).Status);
        }

        [Test]
        public static void FanNoise_AFluctuationInsideTwoSigmasIsNotAChange()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 90, 3000, s % 2 == 0 ? 2800 : 3400, 80)); // sd about 300
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++) t.Add(S(T0 + s * 1000, 90, 3000, 3350, 80)); // +250 rpm: above 150, inside 2 sigma
            Assert.Equal("Desempenho: clock igual sob carga", t.Evaluate(T0 + 60000).Verdict);
        }

        [Test]
        public static void FansTempPower_NeedFourReadingsPerWindow()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 4, null, 2000, 45));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 30; s <= 60; s++)
            {
                // Fan reads in only three of the after samples.
                t.Add(S(T0 + s * 1000, 4, null, s <= 32 ? (int?)3500 : null, null));
            }
            ModeSwitchEffect e = t.Evaluate(T0 + 60000);
            Assert.True(e.FanDeltaRpm == null, "three readings are not enough");
        }

        [Test]
        public static void ThrottleAppearing_IsReported()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++)
            {
                EffectSample x = S(T0 + s * 1000, 95, 3000, 3000, 80);
                x.Throttle = ThrottleState.Normal;
                t.Add(x);
            }
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++)
            {
                EffectSample x = S(T0 + s * 1000, 95, 3000, 3000, 80);
                x.Throttle = ThrottleState.TemperatureLimited;
                t.Add(x);
            }
            Assert.Equal("Desempenho: clock igual sob carga, passou a ser limitado por temperatura", t.Evaluate(T0 + 60000).Verdict);
        }

        [Test]
        public static void PowerDrawChange_IsReportedAsThePackage()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++)
            {
                EffectSample x = S(T0 + s * 1000, 95, 3000, 3000, 80);
                x.PackageWatts = 35;
                t.Add(x);
            }
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 60; s++)
            {
                EffectSample x = S(T0 + s * 1000, 95, 3000, 3000, 80);
                x.PackageWatts = 41;
                t.Add(x);
            }
            Assert.Equal("Desempenho: clock igual sob carga, +6 W do pacote", t.Evaluate(T0 + 60000).Verdict);
        }

        [Test]
        public static void TickCountWrapAround_StillWorks()
        {
            int nearWrap = int.MaxValue - 20000; // TickCount wraps 20 s after the switch
            ModeSwitchTracker t = Fill(90, 2800, 3304, 3000, 3900, 80, 80, nearWrap);
            ModeSwitchEffect e = t.Evaluate(unchecked(nearWrap + 60000));
            Assert.Equal("Desempenho: +18 % de clock sob carga, ventoinha +900 rpm", e.Verdict);
        }

        [Test]
        public static void Verdict_IsFrozenBySamplingAlone_NoUiNeeded()
        {
            ModeSwitchTracker t = Fill(90, 2800, 3304, 3000, 3900, 80, 80, T0);
            t.Add(S(T0 + 61000, 90, 3304, 3900, 80)); // the next sample past the mark closes the window
            Assert.True(t.Last != null && t.Last.Status == EffectStatus.Ready, "frozen without any Evaluate call");
            string first = t.Last.Verdict;
            for (int s = 62; s < 120; s++) t.Add(S(T0 + s * 1000, 90, 1000, 1000, 50));
            Assert.Equal(first, t.Evaluate(T0 + 120000).Verdict);
        }

        [Test]
        public static void FirstLookTwoHundredSecondsLater_StillHasTheBeforeWindow()
        {
            // Switch in Modos, window closed for minutes: the "before" samples are older than the
            // 180 s kept, but they were copied at the switch.
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = -60; s < 0; s++) t.Add(S(T0 + s * 1000, 90, 2800, 3000, 80));
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.Desempenho, T0);
            for (int s = 1; s <= 200; s++) t.Add(S(T0 + s * 1000, 90, 3304, 3900, 80));
            Assert.Equal("Desempenho: +18 % de clock sob carga, ventoinha +900 rpm", t.Evaluate(T0 + 200000).Verdict);
        }

        [Test]
        public static void NewSwitch_ReplacesThePendingMeasurement()
        {
            ModeSwitchTracker t = Fill(90, 2800, 3304, 3000, 3900, 80, 80, T0);
            t.MarkSwitch(PerfMode.Desempenho, PerfMode.Silencioso, T0 + 70000);
            ModeSwitchEffect e = t.Evaluate(T0 + 80000);
            Assert.Equal(EffectStatus.Measuring, e.Status);
            Assert.Equal(PerfMode.Silencioso, e.To);
        }

        [Test]
        public static void SwitchToTheSameMode_IsIgnored()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            t.MarkSwitch(PerfMode.DiaADia, PerfMode.DiaADia, T0);
            Assert.Equal(EffectStatus.None, t.Evaluate(T0 + 1000).Status);
        }

        [Test]
        public static void UnreadableModeBefore_IsStillASwitch()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            t.MarkSwitch(null, PerfMode.Desempenho, T0);
            ModeSwitchEffect e = t.Evaluate(T0 + 1000);
            Assert.Equal(EffectStatus.Measuring, e.Status);
            Assert.True(!e.FromKnown, "the old mode is not invented");
        }

        [Test]
        public static void OldSamplesAreDropped()
        {
            ModeSwitchTracker t = new ModeSwitchTracker();
            for (int s = 0; s < 1000; s++) t.Add(S(T0 + s * 1000, 90, 3000, 3000, 80));
            Assert.True(t.SampleCount <= EffectRules.KeepMs / 1000 + 1, "kept " + t.SampleCount);
        }

        // ---- the sample the hub builds ----

        [Test]
        public static void SampleBuilder_HiddenSample_CarriesNoStaleState()
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = 70;
            r.CpuFanRpm = 3000;
            EffectSample s = EffectSampleBuilder.Make(5, r, null, 22.5, ThrottleState.PowerLimited);
            Assert.True(s.ClockMHz == null && s.PackageWatts == null, "no counters while hidden");
            Assert.Equal(ThrottleState.Unknown, s.Throttle);
            Assert.Equal(22.5, s.LoadPct.Value);
            Assert.Equal(3000, s.CpuFanRpm.Value);
        }

        [Test]
        public static void SampleBuilder_Sampling_UsesCountersAndTheDetector()
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = 0; // the firmware answers 0 for a sensor it does not have
            CpuSample c = new CpuSample();
            c.LoadPct = 80;
            c.MaxCoreLoadPct = 95;
            c.BusyClockMHz = 4200;
            c.EffectiveMHz = 2500;
            c.PackageWatts = 40;
            EffectSample s = EffectSampleBuilder.Make(9, r, c, 11.0, ThrottleState.TemperatureLimited);
            Assert.Equal(80.0, s.LoadPct.Value); // the counters win over the fallback load
            Assert.Equal(4200.0, s.ClockMHz.Value);
            Assert.Equal(40.0, s.PackageWatts.Value);
            Assert.Equal(ThrottleState.TemperatureLimited, s.Throttle);
            Assert.True(s.CpuTempC == null, "0 °C is not a reading");
        }
    }
}
