using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class DisplayTests
    {
        private const long GiB = 1024L * 1024L * 1024L;
        private static readonly DateTime Start = new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc);

        [Test]
        public static void Temp_RoundsToWholeDegrees()
        {
            Assert.Equal("64 °C", Display.Temp(64.0));
            Assert.Equal("65 °C", Display.Temp(64.5));
            Assert.Equal("49 °C", Display.Temp(49.2));
        }

        [Test]
        public static void Temp_InvalidIsUnavailable()
        {
            Assert.Equal("indisponível", Display.Temp(null));
            Assert.Equal("indisponível", Display.Temp(0.0));
            Assert.Equal("indisponível", Display.Temp(-5.0));
            Assert.Equal("indisponível", Display.Temp(double.NaN));
            Assert.Equal("indisponível", Display.Temp(double.PositiveInfinity));
        }

        [Test]
        public static void Percent_Formats()
        {
            Assert.Equal("37%", Display.Percent(37.4));
            Assert.Equal("0%", Display.Percent(0.0));
            Assert.Equal("100%", Display.Percent(130.0));
            Assert.Equal("indisponível", Display.Percent(null));
            Assert.Equal("indisponível", Display.Percent(double.NaN));
            Assert.Equal("indisponível", Display.Percent(-1.0));
        }

        [Test]
        public static void Rpm_Formats()
        {
            Assert.Equal("2961 RPM", Display.Rpm(2961));
            Assert.Equal("0 RPM", Display.Rpm(0));
            Assert.Equal("indisponível", Display.Rpm(null));
            Assert.Equal("indisponível", Display.Rpm(-1));
        }

        [Test]
        public static void Ram_UsesGibAndComma()
        {
            // 34,049,220,608 bytes = 31.7 GiB (this laptop)
            Assert.Equal("12,4 / 31,7 GB", Display.Ram((long)(12.4 * GiB), 34049220608L));
            Assert.Equal("0,0 / 1,0 GB", Display.Ram(0, GiB));
            Assert.Equal("indisponível", Display.Ram(0, 0));
            Assert.Equal("indisponível", Display.Ram(-1, GiB));
            Assert.Equal("indisponível", Display.Ram(2 * GiB, GiB));
        }

        [Test]
        public static void RamFraction_ClampsAndRejects()
        {
            Assert.True(Math.Abs(Display.RamFraction(GiB, 4 * GiB).Value - 0.25) < 1e-9, "quarter");
            Assert.Null(Display.RamFraction(1, 0));
            Assert.Null(Display.RamFraction(5 * GiB, 4 * GiB));
        }

        [Test]
        public static void Truncate_RespectsLimit()
        {
            Assert.Equal("abc", Display.Truncate("abc", 63));
            Assert.Equal(63, Display.Truncate(new string('x', 100), 63).Length);
            Assert.Equal("", Display.Truncate(null, 63));
        }

        [Test]
        public static void TrayTooltip_Formats()
        {
            Assert.Equal("SenseMonitor — CPU 64 °C", Display.TrayTooltip(64.0));
            Assert.Equal("SenseMonitor — CPU indisponível", Display.TrayTooltip(null));
            Assert.True(Display.TrayTooltip(64.0).Length <= Display.TooltipMax, "<= 63 chars");
        }
    }
}
