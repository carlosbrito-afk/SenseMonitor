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

        [Test]
        public static void Stability_NoEvents()
        {
            Assert.Equal("Nenhuma tela azul registrada recentemente",
                Stability.LastCrashLine(true, new List<DateTime>(), Start, TimeZoneInfo.Utc));
        }

        [Test]
        public static void Stability_LatestEventWins()
        {
            DateTime crash = new DateTime(2026, 11, 14, 22, 13, 0, DateTimeKind.Utc);
            List<DateTime> events = new List<DateTime> { crash.AddDays(-3), crash, crash.AddDays(-1) }; // unsorted
            Assert.Equal(crash, Stability.LastCrash(events).Value);
            Assert.Equal("Última tela azul: 14/11 às 22:13 (há 1 dia)",
                Stability.LastCrashLine(true, events, Start, TimeZoneInfo.Utc));               // 16/11 18:00
            Assert.Equal("Última tela azul: 14/11 às 22:13 (há 2 dias)",
                Stability.LastCrashLine(true, events, crash.AddDays(2.5), TimeZoneInfo.Utc));
        }

        [Test]
        public static void Stability_LocalTimeZone()
        {
            TimeZoneInfo brt = TimeZoneInfo.CreateCustomTimeZone("BRT-test", TimeSpan.FromHours(-3), "BRT", "BRT");
            DateTime crashUtc = new DateTime(2026, 11, 15, 1, 13, 0, DateTimeKind.Utc); // 14/11 22:13 local
            Assert.Equal("Última tela azul: 14/11 às 22:13 (há menos de 1 dia)",
                Stability.LastCrashLine(true, new List<DateTime> { crashUtc }, crashUtc.AddHours(5), brt));
        }

        [Test]
        public static void Stability_UnreadableLog()
        {
            Assert.Equal("Não foi possível ler o registro de erros", Stability.LastCrashLine(false, null, Start, TimeZoneInfo.Utc));
            Assert.Equal("Não foi possível ler o registro de erros", Stability.LastCrashLine(true, null, Start, TimeZoneInfo.Utc));
            Assert.Equal("Não foi possível ler o registro de erros",
                Stability.LastCrashLine(false, new List<DateTime> { Start }, Start, TimeZoneInfo.Utc));
        }

        // Detail text of the window's verdict strip.
        [Test]
        public static void Stability_VerdictDetail()
        {
            DateTime crash = new DateTime(2026, 11, 14, 22, 13, 0, DateTimeKind.Utc);
            List<DateTime> events = new List<DateTime> { crash.AddDays(-3), crash };
            Assert.Equal("Sem tela azul há 2 dias", Stability.VerdictDetail(true, events, crash.AddDays(2.5), 90));
            Assert.Equal("Sem tela azul há 1 dia", Stability.VerdictDetail(true, events, crash.AddDays(1.2), 90));
            Assert.Equal("Sem tela azul há menos de 1 dia", Stability.VerdictDetail(true, events, crash.AddHours(3), 90));
            Assert.Equal("Nenhuma tela azul nos últimos 90 dias", Stability.VerdictDetail(true, new List<DateTime>(), Start, 90));
            Assert.Equal("Sem dados de tela azul", Stability.VerdictDetail(false, null, Start, 90));
            Assert.Equal("Sem dados de tela azul", Stability.VerdictDetail(true, null, Start, 90));
        }

        // Same classification as the Saúde page: only entries with a bugcheck code are blue
        // screens; an unexpected shutdown without a code (power loss, hard reset) is not.
        [Test]
        public static void Stability_BlueScreens_IgnoreShutdownsWithoutCode()
        {
            DateTime blueLocal = new DateTime(2026, 11, 14, 22, 13, 0, DateTimeKind.Local);
            List<CrashEntry> entries = new List<CrashEntry>();
            CrashEntry blue = new CrashEntry();
            blue.When = blueLocal;
            blue.Code = 0x1C7;
            CrashEntry shutdown = new CrashEntry();
            shutdown.When = new DateTime(2026, 11, 16, 14, 28, 0, DateTimeKind.Local);
            entries.Add(shutdown);
            entries.Add(blue);

            List<DateTime> times = Stability.BlueScreenTimesUtc(entries);
            Assert.Equal(1, times.Count);
            Assert.Equal(blueLocal.ToUniversalTime(), times[0]);
            Assert.Equal(DateTimeKind.Utc, times[0].Kind);
            DateTime now = blueLocal.ToUniversalTime().AddDays(2.2);
            Assert.Equal("Sem tela azul há 2 dias", Stability.VerdictDetail(true, times, now, 90));

            List<CrashEntry> onlyShutdowns = new List<CrashEntry> { shutdown };
            Assert.Equal("Nenhuma tela azul nos últimos 90 dias",
                Stability.VerdictDetail(true, Stability.BlueScreenTimesUtc(onlyShutdowns), now, 90));
            Assert.Null(Stability.BlueScreenTimesUtc(null));
        }

        [Test]
        public static void Stability_Ago()
        {
            Assert.Equal("há 2 dias", Stability.Ago(Start, Start.AddDays(2.5)));
            Assert.Equal("há 1 dia", Stability.Ago(Start, Start.AddDays(1.2)));
            Assert.Equal("há menos de 1 dia", Stability.Ago(Start, Start.AddHours(3)));
            Assert.Equal("há menos de 1 dia", Stability.Ago(Start, Start.AddHours(-1))); // clock skew
        }
    }
}
