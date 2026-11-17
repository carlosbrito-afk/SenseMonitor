using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class InstrumentMathTests
    {
        private static void Near(double expected, double actual, string what)
        {
            Assert.True(Math.Abs(expected - actual) < 1e-6, what + ": expected " + expected + " but was " + actual);
        }

        // ---- validity -------------------------------------------------------------------

        [Test]
        public static void IsValidTemp_RejectsNullZeroNegativeNaNInfinity()
        {
            Assert.True(InstrumentMath.IsValidTemp(64.0), "64 is valid");
            Assert.True(InstrumentMath.IsValidTemp(0.5), "0.5 is valid");
            Assert.True(!InstrumentMath.IsValidTemp(null), "null is invalid");
            Assert.True(!InstrumentMath.IsValidTemp(0.0), "firmware 0 is invalid");
            Assert.True(!InstrumentMath.IsValidTemp(-5.0), "negative is invalid");
            Assert.True(!InstrumentMath.IsValidTemp(double.NaN), "NaN is invalid");
            Assert.True(!InstrumentMath.IsValidTemp(double.PositiveInfinity), "+Inf is invalid");
            Assert.True(!InstrumentMath.IsValidTemp(double.NegativeInfinity), "-Inf is invalid");
        }

        [Test]
        public static void Sanitize_MapsInvalidToNull()
        {
            Assert.Null(InstrumentMath.Sanitize(0.0));
            Assert.Null(InstrumentMath.Sanitize(double.NaN));
            Assert.Null(InstrumentMath.Sanitize(null));
            Assert.Equal((double?)71.5, InstrumentMath.Sanitize(71.5));
        }

        // ---- gauge angle and ticks ------------------------------------------------------

        [Test]
        public static void AngleForTemp_MapsScaleOnto270DegreeArcFrom135()
        {
            Near(135.0, InstrumentMath.AngleForTemp(30.0), "30");
            Near(270.0, InstrumentMath.AngleForTemp(65.0), "65");
            Near(405.0, InstrumentMath.AngleForTemp(100.0), "100");
            Near(135.0 + 60.0 / 70.0 * 270.0, InstrumentMath.AngleForTemp(90.0), "90");
        }

        [Test]
        public static void AngleForTemp_ClampsOutsideScale()
        {
            Near(135.0, InstrumentMath.AngleForTemp(12.0), "below");
            Near(405.0, InstrumentMath.AngleForTemp(130.0), "above");
        }

        [Test]
        public static void Ticks_MinorEvery5MajorEvery10()
        {
            List<GaugeTick> ticks = InstrumentMath.Ticks();
            Assert.Equal(15, ticks.Count);
            int majors = 0;
            foreach (GaugeTick t in ticks)
            {
                if (t.Major) majors++;
                Assert.Equal(((int)t.TempC) % 10 == 0, t.Major);
                Near(InstrumentMath.AngleForTemp(t.TempC), t.AngleDeg, "tick angle " + t.TempC);
            }
            Assert.Equal(8, majors);
            Near(30.0, ticks[0].TempC, "first");
            Near(35.0, ticks[1].TempC, "second");
            Near(100.0, ticks[14].TempC, "last");
        }

        // ---- color state ----------------------------------------------------------------

        [Test]
        public static void StateFor_Thresholds()
        {
            Assert.Equal(TempState.Neutral, InstrumentMath.StateFor(64.0, false));
            Assert.Equal(TempState.Neutral, InstrumentMath.StateFor(79.0, false));
            Assert.Equal(TempState.Amber, InstrumentMath.StateFor(80.0, false));
            Assert.Equal(TempState.Amber, InstrumentMath.StateFor(84.0, false));
            Assert.Equal(TempState.Hot, InstrumentMath.StateFor(85.0, false));
            Assert.Equal(TempState.Hot, InstrumentMath.StateFor(99.0, false));
        }

        [Test]
        public static void StateFor_FollowsTheDisplayedRoundedNumber()
        {
            // the number on screen and its color must agree: 79.5 shows "80" -> amber, 84.5 shows "85" -> hot
            Assert.Equal(TempState.Neutral, InstrumentMath.StateFor(79.4, false));
            Assert.Equal(TempState.Amber, InstrumentMath.StateFor(79.5, false));
            Assert.Equal(TempState.Amber, InstrumentMath.StateFor(84.4, false));
            Assert.Equal(TempState.Hot, InstrumentMath.StateFor(84.5, false));
        }

        [Test]
        public static void StateFor_UnavailableAndSleeping()
        {
            Assert.Equal(TempState.Unavailable, InstrumentMath.StateFor(null, false));
            Assert.Equal(TempState.Unavailable, InstrumentMath.StateFor(0.0, false));
            Assert.Equal(TempState.Unavailable, InstrumentMath.StateFor(double.NaN, false));
            Assert.Equal(TempState.Unavailable, InstrumentMath.StateFor(double.PositiveInfinity, false));
            Assert.Equal(TempState.Sleeping, InstrumentMath.StateFor(40.0, true));
            Assert.Equal(TempState.Sleeping, InstrumentMath.StateFor(null, true));
        }

        [Test]
        public static void RoundTemp_AwayFromZero()
        {
            Assert.Equal(64, InstrumentMath.RoundTemp(63.5));
            Assert.Equal(64, InstrumentMath.RoundTemp(64.4));
            Assert.Equal(65, InstrumentMath.RoundTemp(64.5));
        }

        // ---- verdict --------------------------------------------------------------------

        [Test]
        public static void VerdictFor_StableOnlyWhenEverySensorReadsNormal()
        {
            Assert.Equal(Verdict.Stable, InstrumentMath.VerdictFor(64.0, 50.0, false));
            Assert.Equal(Verdict.Stable, InstrumentMath.VerdictFor(64.0, null, true));
            Assert.Equal(Verdict.Stable, InstrumentMath.VerdictFor(64.0, 0.0, true));
        }

        [Test]
        public static void VerdictFor_NeverStableWithAnUnavailableSensor()
        {
            Assert.Equal(Verdict.NoReading, InstrumentMath.VerdictFor(null, 50.0, false));
            Assert.Equal(Verdict.NoReading, InstrumentMath.VerdictFor(64.0, null, false));
            Assert.Equal(Verdict.NoReading, InstrumentMath.VerdictFor(0.0, 50.0, false));
            Assert.Equal(Verdict.NoReading, InstrumentMath.VerdictFor(64.0, double.NaN, false));
            Assert.Equal(Verdict.NoReading, InstrumentMath.VerdictFor(null, null, false));
        }

        [Test]
        public static void VerdictFor_HeatOutranksMissingReadings()
        {
            Assert.Equal(Verdict.Attention, InstrumentMath.VerdictFor(82.0, 50.0, false));
            Assert.Equal(Verdict.Attention, InstrumentMath.VerdictFor(64.0, 80.0, false));
            Assert.Equal(Verdict.Hot, InstrumentMath.VerdictFor(64.0, 87.0, false));
            Assert.Equal(Verdict.Hot, InstrumentMath.VerdictFor(86.0, 82.0, false));
            Assert.Equal(Verdict.Hot, InstrumentMath.VerdictFor(87.0, null, false));
            Assert.Equal(Verdict.Attention, InstrumentMath.VerdictFor(null, 83.0, false));
        }

        [Test]
        public static void VerdictLabel_PtBr()
        {
            Assert.Equal("Estável", InstrumentMath.VerdictLabel(Verdict.Stable));
            Assert.Equal("Atenção", InstrumentMath.VerdictLabel(Verdict.Attention));
            Assert.Equal("Quente", InstrumentMath.VerdictLabel(Verdict.Hot));
            Assert.Equal("Sem leitura", InstrumentMath.VerdictLabel(Verdict.NoReading));
        }

        // ---- chart mapping ---------------------------------------------------------------

        [Test]
        public static void ChartY_SharedZeroTo100ScaleAndClamps()
        {
            // one 0-100 scale for °C and % in the component charts
            Near(10.0, InstrumentMath.ChartY(100.0, 10.0, 140.0), "100 at top");
            Near(150.0, InstrumentMath.ChartY(0.0, 10.0, 140.0), "0 at bottom");
            Near(80.0, InstrumentMath.ChartY(50.0, 10.0, 140.0), "50 mid");
            Near(24.0, InstrumentMath.ChartY(90.0, 10.0, 140.0), "90 limit");
            Near(115.0, InstrumentMath.ChartY(25.0, 10.0, 140.0), "25");
            Near(10.0, InstrumentMath.ChartY(120.0, 10.0, 140.0), "clamp high");
            Near(150.0, InstrumentMath.ChartY(-5.0, 10.0, 140.0), "clamp low");
        }

        [Test]
        public static void ChartGrid_Is25_50_75()
        {
            Assert.Equal(3, InstrumentMath.ChartGrid.Length);
            Assert.Equal(25, InstrumentMath.ChartGrid[0]);
            Assert.Equal(50, InstrumentMath.ChartGrid[1]);
            Assert.Equal(75, InstrumentMath.ChartGrid[2]);
        }

        // ---- usage ----------------------------------------------------------------------

        [Test]
        public static void SanitizeUsage_KeepsZeroClampsTo100RejectsJunk()
        {
            Assert.Equal((double?)0.0, InstrumentMath.SanitizeUsage(0.0));
            Assert.Equal((double?)18.0, InstrumentMath.SanitizeUsage(18.0));
            Assert.Equal((double?)100.0, InstrumentMath.SanitizeUsage(100.4));
            Assert.Null(InstrumentMath.SanitizeUsage(-1.0));
            Assert.Null(InstrumentMath.SanitizeUsage(double.NaN));
            Assert.Null(InstrumentMath.SanitizeUsage(double.PositiveInfinity));
            Assert.Null(InstrumentMath.SanitizeUsage(null));
        }

        [Test]
        public static void FormatUsage_RoundedPercentOrDash()
        {
            Assert.Equal("18 %", InstrumentMath.FormatUsage(17.6));
            Assert.Equal("0 %", InstrumentMath.FormatUsage(0.0));
            Assert.Equal("—", InstrumentMath.FormatUsage(null));
            Assert.Equal("—", InstrumentMath.FormatUsage(-3.0));
        }

        [Test]
        public static void ChartX_NewestAtRightEdgeOldestAtLeft()
        {
            Near(582.0, InstrumentMath.ChartX(0, 600, 34.0, 548.0), "now");
            Near(34.0, InstrumentMath.ChartX(599, 600, 34.0, 548.0), "oldest slot");
            Near(34.0 + 548.0 / 2.0, InstrumentMath.ChartX(1, 3, 34.0, 548.0), "middle of 3");
            Near(34.0, InstrumentMath.ChartX(900, 600, 34.0, 548.0), "clamped");
        }

        [Test]
        public static void SpreadLabels_LeavesDistantLabelsAlone()
        {
            double a, b;
            InstrumentMath.SpreadLabels(40.0, 80.0, 14.0, 10.0, 150.0, out a, out b);
            Near(40.0, a, "a");
            Near(80.0, b, "b");
        }

        [Test]
        public static void SpreadLabels_PushesCloseLabelsApartSymmetrically()
        {
            double a, b;
            InstrumentMath.SpreadLabels(60.0, 64.0, 14.0, 10.0, 150.0, out a, out b);
            Near(55.0, a, "a moves up");
            Near(69.0, b, "b moves down");
            // order is kept when A is the lower one
            InstrumentMath.SpreadLabels(64.0, 60.0, 14.0, 10.0, 150.0, out a, out b);
            Near(69.0, a, "a below");
            Near(55.0, b, "b above");
            // identical: A (CPU) goes on top
            InstrumentMath.SpreadLabels(70.0, 70.0, 14.0, 10.0, 150.0, out a, out b);
            Near(63.0, a, "a on top");
            Near(77.0, b, "b below");
        }

        [Test]
        public static void SpreadLabels_StaysInsideBounds()
        {
            double a, b;
            InstrumentMath.SpreadLabels(12.0, 12.0, 14.0, 10.0, 150.0, out a, out b);
            Near(10.0, a, "top clamp a");
            Near(24.0, b, "top clamp b");
            InstrumentMath.SpreadLabels(150.0, 148.0, 14.0, 10.0, 150.0, out a, out b);
            Near(150.0, a, "bottom clamp a");
            Near(136.0, b, "bottom clamp b");
        }

        // ---- formatting -----------------------------------------------------------------

        [Test]
        public static void FormatGrouped_UsesDotThousands()
        {
            Assert.Equal("2.961", InstrumentMath.FormatGrouped(2961));
            Assert.Equal("999", InstrumentMath.FormatGrouped(999));
            Assert.Equal("12.345", InstrumentMath.FormatGrouped(12345));
            Assert.Equal("0", InstrumentMath.FormatGrouped(0));
        }

        [Test]
        public static void FormatDecimal1_UsesCommaDecimal()
        {
            Assert.Equal("12,4", InstrumentMath.FormatDecimal1(12.44));
            Assert.Equal("31,7", InstrumentMath.FormatDecimal1(31.66));
            Assert.Equal("1,8", InstrumentMath.FormatDecimal1(1.8));
            Assert.Equal("0,0", InstrumentMath.FormatDecimal1(0.0));
            Assert.Equal("1.024,5", InstrumentMath.FormatDecimal1(1024.5));
        }

        [Test]
        public static void FormatTemp_RoundedOrDash()
        {
            Assert.Equal("64", InstrumentMath.FormatTemp(63.6));
            Assert.Equal("—", InstrumentMath.FormatTemp(null));
            Assert.Equal("—", InstrumentMath.FormatTemp(0.0));
            Assert.Equal("—", InstrumentMath.FormatTemp(double.NaN));
        }

        [Test]
        public static void WithUnit_JoinsWithNonBreakingSpace()
        {
            Assert.Equal("2.961 rpm", InstrumentMath.WithUnit("2.961", "rpm"));
            Assert.Equal("71 °C", InstrumentMath.WithUnit("71", "°C"));
            Assert.Equal("71", InstrumentMath.WithUnit("71", null));
            Assert.Equal("71", InstrumentMath.WithUnit("71", ""));
        }

        [Test]
        public static void Days_SingularAndPlural()
        {
            Assert.Equal("1 dia", InstrumentMath.Days(1));
            Assert.Equal("2 dias", InstrumentMath.Days(2));
            Assert.Equal("0 dias", InstrumentMath.Days(0));
            Assert.Equal("0 dias", InstrumentMath.Days(-3));
        }

        // ---- whole days ---------------------------------------------------------------------

        [Test]
        public static void WholeDaysBetween_FloorsAndNeverNegative()
        {
            DateTime since = new DateTime(2026, 11, 14, 22, 13, 0);
            Assert.Equal(2, InstrumentMath.WholeDaysBetween(since, new DateTime(2026, 11, 16, 22, 13, 0)));
            Assert.Equal(1, InstrumentMath.WholeDaysBetween(since, new DateTime(2026, 11, 16, 22, 12, 59)));
            Assert.Equal(0, InstrumentMath.WholeDaysBetween(since, new DateTime(2026, 11, 15, 8, 0, 0)));
            Assert.Equal(0, InstrumentMath.WholeDaysBetween(since, new DateTime(2026, 11, 13, 8, 0, 0)));
        }

        // ---- accessibility text ----------------------------------------------------------

        [Test]
        public static void GaugeAccessibleText_ValuePeakAndStateInWords()
        {
            Assert.Equal("CPU 64 graus, pico 71", InstrumentMath.GaugeAccessibleText("CPU", 64.0, 71.0, false));
            Assert.Equal("CPU 64 graus", InstrumentMath.GaugeAccessibleText("CPU", 64.0, null, false));
            Assert.Equal("CPU 82 graus, atenção, pico 84", InstrumentMath.GaugeAccessibleText("CPU", 82.0, 84.0, false));
            Assert.Equal("GPU 87 graus, quente", InstrumentMath.GaugeAccessibleText("GPU", 87.0, 0.0, false));
            Assert.Equal("CPU sem leitura", InstrumentMath.GaugeAccessibleText("CPU", null, 71.0, false));
            Assert.Equal("CPU sem leitura", InstrumentMath.GaugeAccessibleText("CPU", 0.0, null, false));
            Assert.Equal("GPU em repouso", InstrumentMath.GaugeAccessibleText("GPU", 40.0, 53.0, true));
        }

        [Test]
        public static void ComponentChartText_RangesOfTemperatureAndUsage()
        {
            TempHistory h = new TempHistory();
            h.Add(58.0, 47.0, 10.0, 5.0);
            h.Add(71.2, null, 40.4, null);
            h.Add(64.0, 53.0, 18.0, 7.0);
            Assert.Equal("CPU: temperatura entre 58 e 71 °C, uso entre 10 e 40 % nos últimos 10 minutos; limite 90 °C",
                InstrumentMath.ComponentChartText(h, Hardware.Cpu));
            Assert.Equal("GPU: temperatura entre 47 e 53 °C, uso entre 5 e 7 % nos últimos 10 minutos; limite 90 °C",
                InstrumentMath.ComponentChartText(h, Hardware.Gpu));
            Assert.Equal("CPU: temperatura sem leitura, uso sem leitura nos últimos 10 minutos; limite 90 °C",
                InstrumentMath.ComponentChartText(new TempHistory(), Hardware.Cpu));
            Assert.Equal("GPU: temperatura sem leitura, uso sem leitura nos últimos 10 minutos; limite 90 °C",
                InstrumentMath.ComponentChartText(null, Hardware.Gpu));
        }

        [Test]
        public static void HardwareName_CpuGpu()
        {
            Assert.Equal("CPU", InstrumentMath.HardwareName(Hardware.Cpu));
            Assert.Equal("GPU", InstrumentMath.HardwareName(Hardware.Gpu));
        }

        // ---- history ring buffer -----------------------------------------------------------

        [Test]
        public static void TempHistory_DefaultsTo600Samples()
        {
            TempHistory h = new TempHistory();
            Assert.Equal(600, h.Capacity);
            Assert.Equal(0, h.Count);
            Assert.Null(h.Latest(Hardware.Cpu, Metric.Temp));
            Assert.Null(h.Peak(Hardware.Cpu, Metric.Temp));
            Assert.Null(h.Min(Hardware.Gpu, Metric.Temp));
        }

        [Test]
        public static void TempHistory_RejectsTinyCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(delegate { new TempHistory(1); });
        }

        [Test]
        public static void TempHistory_OrdersOldestFirstAndDropsOldestWhenFull()
        {
            TempHistory h = new TempHistory(3);
            h.Add(50.0, 40.0);
            h.Add(51.0, 41.0);
            h.Add(52.0, 42.0);
            h.Add(53.0, 43.0);
            Assert.Equal(3, h.Count);
            Assert.Equal((double?)51.0, h.At(Hardware.Cpu, Metric.Temp, 0));
            Assert.Equal((double?)53.0, h.At(Hardware.Cpu, Metric.Temp, 2));
            Assert.Equal((double?)41.0, h.At(Hardware.Gpu, Metric.Temp, 0));
            Assert.Equal((double?)53.0, h.Latest(Hardware.Cpu, Metric.Temp));
            Assert.Throws<ArgumentOutOfRangeException>(delegate { h.At(Hardware.Cpu, Metric.Temp, 3); });
        }

        [Test]
        public static void TempHistory_StoresInvalidReadingsAsGaps()
        {
            TempHistory h = new TempHistory(5);
            h.Add(0.0, double.NaN);
            h.Add(-1.0, double.PositiveInfinity);
            h.Add(null, 50.0);
            Assert.Equal(3, h.Count);
            Assert.Null(h.At(Hardware.Cpu, Metric.Temp, 0));
            Assert.Null(h.At(Hardware.Gpu, Metric.Temp, 1));
            Assert.Null(h.Latest(Hardware.Cpu, Metric.Temp));
            Assert.Equal((double?)50.0, h.Latest(Hardware.Gpu, Metric.Temp));
        }

        [Test]
        public static void TempHistory_PeakAndMinIgnoreGapsAndExpiredSamples()
        {
            TempHistory h = new TempHistory(4);
            h.Add(90.0, null);
            h.Add(60.0, null);
            h.Add(null, 48.0);
            h.Add(71.0, 53.0);
            Assert.Equal((double?)90.0, h.Peak(Hardware.Cpu, Metric.Temp));
            Assert.Equal((double?)60.0, h.Min(Hardware.Cpu, Metric.Temp));
            Assert.Equal((double?)53.0, h.Peak(Hardware.Gpu, Metric.Temp));
            h.Add(64.0, 50.0); // the 90 drops out of the window
            Assert.Equal((double?)71.0, h.Peak(Hardware.Cpu, Metric.Temp));
            Assert.Equal((double?)48.0, h.Min(Hardware.Gpu, Metric.Temp));
        }

        [Test]
        public static void TempHistory_AddReadingVersionAndClear()
        {
            TempHistory h = new TempHistory(10);
            int v0 = h.Version;
            SensorReading r = new SensorReading();
            r.CpuTempC = 64.0;
            r.GpuTempC = 0.0;
            h.Add(r);
            h.Add((SensorReading)null);
            Assert.True(h.Version == v0 + 2, "version should advance on each Add");
            Assert.Equal(2, h.Count);
            Assert.Equal((double?)64.0, h.At(Hardware.Cpu, Metric.Temp, 0));
            Assert.Null(h.At(Hardware.Gpu, Metric.Temp, 0));
            Assert.Null(h.At(Hardware.Cpu, Metric.Temp, 1));
            h.Clear();
            Assert.Equal(0, h.Count);
            Assert.True(h.Version == v0 + 3, "version should advance on Clear");
        }

        [Test]
        public static void TempHistory_StoresUsagePerComponent()
        {
            TempHistory h = new TempHistory(5);
            h.Add(64.0, 50.0, 18.0, 7.0);
            Assert.Equal((double?)18.0, h.At(Hardware.Cpu, Metric.Usage, 0));
            Assert.Equal((double?)7.0, h.At(Hardware.Gpu, Metric.Usage, 0));
            Assert.Equal((double?)64.0, h.At(Hardware.Cpu, Metric.Temp, 0));
            Assert.Equal((double?)50.0, h.Latest(Hardware.Gpu, Metric.Temp));
        }

        [Test]
        public static void TempHistory_TwoArgumentAddLeavesUsageAsGaps()
        {
            TempHistory h = new TempHistory(5);
            h.Add(64.0, 50.0);
            Assert.Null(h.At(Hardware.Cpu, Metric.Usage, 0));
            Assert.Null(h.Latest(Hardware.Gpu, Metric.Usage));
            SensorReading r = new SensorReading();
            r.CpuTempC = 61.0;
            h.Add(r, 25.0, null);
            Assert.Equal((double?)25.0, h.Latest(Hardware.Cpu, Metric.Usage));
            Assert.Null(h.Latest(Hardware.Gpu, Metric.Usage));
            Assert.Equal((double?)61.0, h.Latest(Hardware.Cpu, Metric.Temp));
        }

        [Test]
        public static void TempHistory_UsageKeepsZeroClampsAndBreaksOnJunk()
        {
            TempHistory h = new TempHistory(5);
            h.Add(null, null, 0.0, 100.4);
            h.Add(null, null, double.NaN, -2.0);
            Assert.Equal((double?)0.0, h.At(Hardware.Cpu, Metric.Usage, 0));
            Assert.Equal((double?)100.0, h.At(Hardware.Gpu, Metric.Usage, 0));
            Assert.Null(h.At(Hardware.Cpu, Metric.Usage, 1));
            Assert.Null(h.At(Hardware.Gpu, Metric.Usage, 1));
        }

        [Test]
        public static void TempHistory_UsagePeakAndMinIgnoreGaps()
        {
            TempHistory h = new TempHistory(3);
            h.Add(60.0, 50.0, 90.0, 5.0);
            h.Add(60.0, 50.0, null, null);
            h.Add(60.0, 50.0, 30.0, 9.0);
            Assert.Equal((double?)90.0, h.Peak(Hardware.Cpu, Metric.Usage));
            Assert.Equal((double?)30.0, h.Min(Hardware.Cpu, Metric.Usage));
            Assert.Equal((double?)9.0, h.Peak(Hardware.Gpu, Metric.Usage));
            h.Add(60.0, 50.0, 20.0, 4.0); // the 90 % sample expires
            Assert.Equal((double?)30.0, h.Peak(Hardware.Cpu, Metric.Usage));
            Assert.Equal((double?)4.0, h.Min(Hardware.Gpu, Metric.Usage));
        }
    }
}
