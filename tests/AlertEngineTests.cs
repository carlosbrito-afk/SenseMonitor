using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class AlertEngineTests
    {
        private static readonly DateTime Day = new DateTime(2026, 11, 17, 21, 0, 0);

        private static AlertEngine Engine()
        {
            return new AlertEngine(new AlertSettings(), 95, 87);
        }

        private static AlertSample S(long ms, double? cpu, double? gpu)
        {
            AlertSample s = new AlertSample();
            s.NowMs = ms;
            s.Local = Day.AddMilliseconds(ms);
            s.CpuC = cpu;
            s.GpuC = gpu;
            return s;
        }

        private static AlertSample P(long ms, bool protect)
        {
            AlertSample s = S(ms, 60, 50);
            s.ThermalProtection = protect;
            s.FanStatus = protect ? "Proteção térmica: 100%" : "Automático";
            return s;
        }

        private static AlertContext Game()
        {
            AlertContext c = new AlertContext();
            c.Fullscreen = true;
            return c;
        }

        private static AlertContext Clear()
        {
            return AlertContext.Clear();
        }

        // Feeds a steady temperature every 2 s from 'from' to 'to' (ms), returns balloons collected.
        private static List<Alert> Feed(AlertEngine e, long from, long to, double cpu, AlertContext ctx)
        {
            List<Alert> list = new List<Alert>();
            for (long t = from; t <= to; t += 2000)
            {
                list.AddRange(e.Tick(S(t, cpu, 50), ctx).Balloons);
            }
            return list;
        }

        // ---- heat ----------------------------------------------------------------------------

        [Test]
        public static void Heat_NeedsTheFullSustainTime_ThenOneBalloonAndAnAmberIcon()
        {
            AlertEngine e = Engine();
            List<Alert> early = Feed(e, 0, 28000, 96, Clear());
            Assert.Equal(0, early.Count);
            Assert.Equal(TrayAlertState.Normal, e.IconState(28000));
            List<Alert> at30 = Feed(e, 30000, 30000, 96, Clear());
            Assert.Equal(1, at30.Count);
            Assert.Equal(AlertKind.CpuHot, at30[0].Kind);
            Assert.True(at30[0].Text.Contains("96 °C") && at30[0].Text.Contains("95 °C") && at30[0].Text.Contains("30 s"), at30[0].Text);
            Assert.Equal(TrayAlertState.Warning, e.IconState(30000));
            Assert.True(e.IconText(30000).StartsWith("SenseMonitor — CPU quente"), e.IconText(30000));
            // still hot: no second balloon
            Assert.Equal(0, Feed(e, 32000, 120000, 97, Clear()).Count);
        }

        [Test]
        public static void Heat_BelowTheLimit85IsNotAnAlert()
        {
            AlertEngine e = Engine();
            Assert.Equal(0, Feed(e, 0, 600000, 88, Clear()).Count);
            Assert.Equal(TrayAlertState.Normal, e.IconState(600000));
        }

        [Test]
        public static void Heat_ADipClearlyUnderTheBandRestartsTheCount()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 20000, 96, Clear());
            Feed(e, 22000, 22000, 88, Clear());       // clearly cool (below limit - 3)
            Assert.Equal(0, Feed(e, 24000, 48000, 96, Clear()).Count); // only 24 s since the dip
            Assert.Equal(1, Feed(e, 50000, 56000, 96, Clear()).Count); // 30 s reached at 54 s
        }

        [Test]
        public static void Heat_WobbleInsideTheBand_DoesNotRestartTheCount()
        {
            // 94 <-> 96 around a 95 limit is still the throttling zone
            AlertEngine e = Engine();
            List<Alert> all = new List<Alert>();
            for (long t = 0; t <= 40000; t += 2000) all.AddRange(e.Tick(S(t, (t / 2000) % 2 == 0 ? 96 : 94, 50), Clear()).Balloons);
            Assert.Equal(1, all.Count);
        }

        [Test]
        public static void Heat_OneBadReading_DoesNotDropTheActiveState_NorTheCount()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 30000, 97, Clear());
            Assert.True(e.NeedsAttention(30000), "hot");
            // the EC drops out: null, then garbage
            e.Tick(S(32000, null, 50), Clear());
            Assert.True(e.NeedsAttention(32000), "held through a null");
            e.Tick(S(34000, 255, 50), Clear());
            Assert.True(e.NeedsAttention(34000), "held through garbage");
            AlertOutput back = e.Tick(S(36000, 97, 50), Clear());
            Assert.Equal(0, back.Balloons.Count);                      // no new balloon
            Assert.Equal("Monitor", e.AttentionPage(36000));
            Assert.Equal(TrayAlertState.Warning, e.IconState(36000));
        }

        [Test]
        public static void Heat_BadReadingDuringTheCount_DoesNotRestartIt()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 20000, 96, Clear());
            e.Tick(S(22000, null, 50), Clear());
            e.Tick(S(24000, null, 50), Clear());
            List<Alert> rest = Feed(e, 26000, 32000, 96, Clear());
            Assert.Equal(1, rest.Count);   // 30 s since the first hot sample, bad readings in between included
        }

        [Test]
        public static void Heat_LongStretchOfBadReadings_ClearsHeat_AndTheStripSaysNoReading()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 30000, 97, Clear());
            for (long t = 32000; t <= 50000; t += 2000) e.Tick(S(t, null, 50), Clear());
            Assert.True(e.NeedsAttention(50000), "never 'Estável' without a CPU reading");
            Assert.Equal("Sem leitura de temperatura", e.AttentionReason(50000));
            e.Tick(S(52000, 60, 50), Clear());
            Assert.True(!e.NeedsAttention(52000), "reading back and cool");
        }

        [Test]
        public static void Heat_GpuNullForALongTime_IsNotAnAttention()
        {
            AlertEngine e = Engine();
            for (long t = 0; t <= 60000; t += 2000) e.Tick(S(t, 60, null), Clear());
            Assert.True(!e.NeedsAttention(60000), "a sleeping GPU is normal");
        }

        [Test]
        public static void Heat_ALongPauseBetweenSamples_IsNotSustainedHeat()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 20000, 96, Clear());
            // the PC slept: the next sample comes 10 minutes later and is hot again
            Assert.Equal(0, e.Tick(S(620000, 96, 50), Clear()).Balloons.Count);
            Assert.Equal(1, Feed(e, 622000, 650000, 96, Clear()).Count);
        }

        [Test]
        public static void Heat_SlowPollingStillCounts()
        {
            // hidden window: one sample every 12 s
            AlertEngine e = Engine();
            List<Alert> all = new List<Alert>();
            for (long t = 0; t <= 48000; t += 12000) all.AddRange(e.Tick(S(t, 97, 50), Clear()).Balloons);
            Assert.Equal(1, all.Count); // fires at 36 s, the first sample at or after 30 s
        }

        [Test]
        public static void Heat_Hysteresis_AndRateLimitedSecondEpisode()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 30000, 96, Clear());
            Assert.Equal(TrayAlertState.Warning, e.IconState(30000));
            Feed(e, 32000, 34000, 93, Clear());       // 93 is inside the 3 degree band: icon stays
            Assert.Equal(TrayAlertState.Warning, e.IconState(34000));
            Feed(e, 36000, 38000, 91, Clear());       // 91 < 95 - 3: cleared
            Assert.Equal(TrayAlertState.Normal, e.IconState(38000));
            // hot again 1 minute later: the icon follows at once, the balloon waits for the 5 min gap
            List<Alert> second = Feed(e, 100000, 140000, 96, Clear());
            Assert.Equal(0, second.Count);
            Assert.Equal(TrayAlertState.Warning, e.IconState(140000));
            // cool, then a third episode after 5 minutes: the balloon is allowed again
            Feed(e, 142000, 144000, 80, Clear());
            List<Alert> third = Feed(e, 340000, 380000, 96, Clear());
            Assert.Equal(1, third.Count);
        }

        [Test]
        public static void Heat_GpuHasItsOwnLimit()
        {
            AlertEngine e = Engine();
            List<Alert> list = new List<Alert>();
            for (long t = 0; t <= 30000; t += 2000) list.AddRange(e.Tick(S(t, 70, 88), Clear()).Balloons);
            Assert.Equal(1, list.Count);
            Assert.Equal(AlertKind.GpuHot, list[0].Kind);
            Assert.True(list[0].Text.Contains("87 °C"), list[0].Text);
        }

        [Test]
        public static void Heat_InvalidReadings_AreNeverHeat()
        {
            AlertEngine e = Engine();
            List<Alert> all = new List<Alert>();
            for (long t = 0; t <= 40000; t += 2000) all.AddRange(e.Tick(S(t, 255, 255), Clear()).Balloons);
            // 255 is firmware garbage: not heat (it is "no reading", which is its own alert after a minute)
            Assert.Equal(0, all.Count);
            Assert.True(e.IconText(40000) == null || !e.IconText(40000).Contains("quente"), "no heat alert for garbage");
        }

        // ---- thermal protection, sensor ------------------------------------------------------

        [Test]
        public static void Protection_RisingEdge_IsCriticalOnBothChannels_AndClears()
        {
            AlertEngine e = Engine();
            Assert.Equal(0, e.Tick(P(0, false), Clear()).Balloons.Count);
            AlertOutput o = e.Tick(P(2000, true), Clear());
            Assert.Equal(1, o.Balloons.Count);
            Assert.Equal(AlertKind.ThermalProtection, o.Balloons[0].Kind);
            Assert.Equal(AlertSeverity.Critical, o.Balloons[0].Severity);
            Assert.True(o.Balloons[0].Text.Contains("100%"), o.Balloons[0].Text);
            Assert.Equal(TrayAlertState.Critical, e.IconState(2000));
            Assert.Equal(0, e.Tick(P(4000, true), Clear()).Balloons.Count);
            e.Tick(P(6000, false), Clear());
            Assert.Equal(TrayAlertState.Normal, e.IconState(6000));
        }

        [Test]
        public static void Protection_CriticalGapIsOneMinute_NotFive()
        {
            AlertEngine e = Engine();
            e.Tick(P(0, true), Clear());
            e.Tick(P(2000, false), Clear());
            Assert.Equal(0, e.Tick(P(30000, true), Clear()).Balloons.Count);  // 30 s later: still limited
            e.Tick(P(32000, false), Clear());
            Assert.Equal(1, e.Tick(P(70000, true), Clear()).Balloons.Count);  // a minute passed
        }

        [Test]
        public static void SensorLost_AfterAMinuteWithoutCpuReading()
        {
            AlertEngine e = Engine();
            List<Alert> list = new List<Alert>();
            for (long t = 0; t <= 58000; t += 2000) list.AddRange(e.Tick(S(t, null, null), Clear()).Balloons);
            Assert.Equal(0, list.Count);
            list.AddRange(e.Tick(S(60000, null, null), Clear()).Balloons);
            Assert.Equal(1, list.Count);
            Assert.Equal(AlertKind.SensorLost, list[0].Kind);
            Assert.Equal(TrayAlertState.Warning, e.IconState(60000));
            e.Tick(S(62000, 60, 50), Clear());
            Assert.Equal(TrayAlertState.Normal, e.IconState(62000));
        }

        // ---- one-off events ------------------------------------------------------------------

        private static CrashEntry Crash(DateTime down, long? code, FailureKind kind)
        {
            CrashEntry c = new CrashEntry();
            c.When = down.AddMinutes(3);
            c.DownAt = down;
            c.Code = code;
            c.Kind = kind;
            return c;
        }

        [Test]
        public static void Restart_TextAndBothChannels_FollowsTheUnseenSet()
        {
            AlertEngine e = Engine();
            List<CrashEntry> fresh = new List<CrashEntry>();
            fresh.Add(Crash(new DateTime(2026, 11, 17, 1, 35, 24), 0xEF, FailureKind.BlueScreen));
            Alert a = AlertText.UnexpectedRestart(fresh, new DateTime(2026, 11, 17, 1, 40, 0));
            Assert.Equal("O Windows reiniciou inesperadamente. Ver detalhes", a.Text);
            Assert.Equal("Reinício inesperado", a.Title);
            Assert.Equal("Saude", a.Page);
            AlertOutput o = e.Raise(a, 0, a.Local, Clear());
            Assert.Equal(1, o.Balloons.Count);
            Assert.Equal(TrayAlertState.Normal, e.IconState(0));          // the balloon alone does not colour the icon
            e.SetUnseenRestarts(1);
            Assert.Equal(TrayAlertState.Warning, e.IconState(0));
            Assert.Equal(TrayAlertState.Warning, e.IconState(24L * 3600 * 1000)); // a day later, still unseen
            e.Acknowledge();                                               // clicking elsewhere does not mark it seen
            Assert.Equal(TrayAlertState.Warning, e.IconState(1000));
            e.SetUnseenRestarts(0);
            Assert.Equal(TrayAlertState.Normal, e.IconState(1000));
            Assert.Null(e.IconText(1000));
        }

        [Test]
        public static void Restart_SeveralInOneBalloon_AndNoTechnicalWordsInTheAlert()
        {
            List<CrashEntry> two = new List<CrashEntry>();
            two.Add(Crash(new DateTime(2026, 11, 16, 14, 6, 52), null, FailureKind.HangPowerButton));
            two.Add(Crash(new DateTime(2026, 11, 16, 20, 0, 0), 0x1C7, FailureKind.BlueScreen));
            Alert a = AlertText.UnexpectedRestart(two, new DateTime(2026, 11, 17, 9, 0, 0));
            Assert.Equal("O Windows reiniciou inesperadamente (2 vezes). Ver detalhes", a.Text);
            Assert.Equal(AlertSeverity.Info, a.Severity);
            Assert.True(!a.Text.Contains("0x") && !a.Title.ToLowerInvariant().Contains("tela azul"), "gentle");
        }

        [Test]
        public static void FanEvent_IconExpiresAfterFifteenMinutes()
        {
            AlertEngine e = Engine();
            Alert a = AlertText.Watchdog(Day);
            Assert.Equal(1, e.Raise(a, 0, Day, Clear()).Balloons.Count);
            Assert.Equal(TrayAlertState.Warning, e.IconState(10 * 60 * 1000));
            Assert.Equal(TrayAlertState.Normal, e.IconState(16 * 60 * 1000));
        }

        [Test]
        public static void SameEventTwiceInFiveMinutes_OneBalloon()
        {
            AlertEngine e = Engine();
            Assert.Equal(1, e.Raise(AlertText.FanNoResponse(Day), 0, Day, Clear()).Balloons.Count);
            Assert.Equal(0, e.Raise(AlertText.FanNoResponse(Day), 60000, Day, Clear()).Balloons.Count);
            Assert.Equal(1, e.Raise(AlertText.FanNoResponse(Day), 6 * 60000, Day, Clear()).Balloons.Count);
        }

        // ---- game mode and quiet hours -------------------------------------------------------

        [Test]
        public static void FullScreen_NoBalloon_IconStillChanges_ThenOneBalloonAfterTheGame()
        {
            AlertEngine e = Engine();
            List<Alert> during = Feed(e, 0, 40000, 96, Game());
            Assert.Equal(0, during.Count);
            Assert.Equal(TrayAlertState.Warning, e.IconState(40000));
            // game over, still hot: the held alert comes out once, stamped with its time
            AlertOutput after = e.Tick(S(42000, 96, 50), Clear());
            Assert.Equal(1, after.Balloons.Count);
            Assert.Equal(AlertKind.CpuHot, after.Balloons[0].Kind);
            Assert.True(after.Balloons[0].Text.StartsWith("(21:00) "), after.Balloons[0].Text);
            Assert.Equal(0, e.Tick(S(44000, 96, 50), Clear()).Balloons.Count);
        }

        [Test]
        public static void FullScreen_HoldsEvenCritical_AndSeveralBecomeOneSummary()
        {
            AlertEngine e = Engine();
            e.Tick(P(0, true), Game());
            e.Raise(AlertText.FanNoResponse(Day), 1000, Day, Game());
            Alert crash = AlertText.UnexpectedRestart(new List<CrashEntry>(new CrashEntry[] { Crash(Day.AddMinutes(-10), 0xA, FailureKind.BlueScreen) }), Day);
            AlertOutput held = e.Raise(crash, 2000, Day, Game());
            Assert.Equal(0, held.Balloons.Count);
            Assert.Equal(TrayAlertState.Critical, e.IconState(2000));
            AlertOutput after = e.Tick(P(60000, true), Clear());
            Assert.Equal(1, after.Balloons.Count);
            Alert sum = after.Balloons[0];
            Assert.True(sum.Summary, "summary");
            Assert.Equal(AlertSeverity.Critical, sum.Severity);
            Assert.Equal("Saude", sum.Page);
            Assert.True(sum.Text.Contains("Proteção térmica ativada") && sum.Text.Contains("Reinício inesperado") && sum.Text.Contains("A ventoinha não respondeu"), sum.Text);
            Assert.True(sum.Text.Length <= AlertText.MaxText, "fits a balloon");
        }

        [Test]
        public static void QuietHours_HoldWarnings_NotCritical_AndDeliverAfter()
        {
            AlertSettings s = new AlertSettings();
            s.QuietHoursEnabled = true;
            s.QuietStartHour = 20;
            s.QuietEndHour = 22;   // Day is 21:00
            AlertEngine e = new AlertEngine(s, 95, 87);
            Assert.Equal(0, e.Raise(AlertText.FanNoResponse(Day), 0, Day, Clear()).Balloons.Count);
            Assert.Equal(1, e.Tick(P(2000, true), Clear()).Balloons.Count); // critical goes through
            // 22:05: the window ended, the held warning is delivered
            AlertSample later = P(65 * 60 * 1000, false);
            AlertOutput o = e.Tick(later, Clear());
            Assert.Equal(1, o.Balloons.Count);
            Assert.Equal(AlertKind.FanNoResponse, o.Balloons[0].Kind);
        }

        [Test]
        public static void OsFocusAssist_HoldsWarnings()
        {
            AlertEngine e = Engine();
            AlertContext quiet = new AlertContext();
            quiet.OsQuiet = true;
            Assert.Equal(0, e.Raise(AlertText.Watchdog(Day), 0, Day, quiet).Balloons.Count);
            Assert.Equal(TrayAlertState.Warning, e.IconState(0));
            Assert.Equal(1, e.Tick(S(2000, 60, 50), Clear()).Balloons.Count);
        }

        [Test]
        public static void HeldAlertsOlderThanTwelveHours_AreDropped()
        {
            AlertEngine e = Engine();
            e.Raise(AlertText.Watchdog(Day), 0, Day, Game());
            AlertOutput o = e.Tick(S(AlertEngine.DeferKeepMs + 1000, 60, 50), Clear());
            Assert.Equal(0, o.Balloons.Count);
        }

        // ---- settings ------------------------------------------------------------------------

        [Test]
        public static void Settings_MasterOff_NothingAtAll()
        {
            AlertSettings s = new AlertSettings();
            s.Enabled = false;
            AlertEngine e = new AlertEngine(s, 95, 87);
            Assert.Equal(0, Feed(e, 0, 60000, 99, Clear()).Count);
            Assert.True(e.NeedsAttention(60000), "the strip still sees sustained heat");
            Assert.Equal(0, e.Tick(P(62000, true), Clear()).Balloons.Count);
            Assert.Equal(0, e.Raise(AlertText.Watchdog(Day), 0, Day, Clear()).Balloons.Count);
            Assert.Equal(TrayAlertState.Normal, e.IconState(62000));
            // the strip is about the PC, not about what the user wants to be told: protection still counts
            Assert.True(e.NeedsAttention(62000), "strip still sees the protection");
        }

        [Test]
        public static void Settings_EachKindCanBeTurnedOff()
        {
            AlertSettings s = new AlertSettings();
            s.TempAlerts = false;
            s.FanAlerts = false;
            AlertEngine e = new AlertEngine(s, 95, 87);
            Assert.Equal(0, Feed(e, 0, 60000, 99, Clear()).Count);
            Assert.Equal(TrayAlertState.Normal, e.IconState(60000));
            Assert.Equal(0, e.Raise(AlertText.FanNoResponse(Day), 0, Day, Clear()).Balloons.Count);
            Assert.Equal(1, e.Tick(P(62000, true), Clear()).Balloons.Count); // protection still on
        }

        [Test]
        public static void Settings_CustomLimitAndSustain_AndReconfigure()
        {
            AlertSettings s = new AlertSettings();
            s.SustainSeconds = 10;
            AlertEngine e = new AlertEngine(s, 80, 87);
            Assert.Equal(1, Feed(e, 0, 12000, 82, Clear()).Count);
            e.Configure(s, 99, 87);
            Feed(e, 14000, 16000, 70, Clear());
            Assert.Equal(0, Feed(e, 18000, 60000, 82, Clear()).Count);
        }

        [Test]
        public static void Settings_Defaults_Normalize_Allows()
        {
            AlertSettings d = new AlertSettings();
            Assert.True(d.Enabled && d.TempAlerts && d.ProtectionAlerts && d.RestartAlerts && d.FanAlerts && d.SensorAlerts, "all on");
            Assert.Equal(0, d.CpuLimitC);
            Assert.Equal(87, d.GpuLimitC);
            Assert.Equal(30, d.SustainSeconds);
            Assert.Equal(5, d.BalloonGapMinutes);
            AlertSettings wild = new AlertSettings();
            wild.CpuLimitC = 500;
            wild.GpuLimitC = -3;
            wild.SustainSeconds = 1;
            wild.BalloonGapMinutes = 9999;
            wild.QuietStartHour = 99;
            wild.Normalize();
            Assert.Equal(105, wild.CpuLimitC);
            Assert.Equal(87, wild.GpuLimitC);
            Assert.Equal(10, wild.SustainSeconds);
            Assert.Equal(120, wild.BalloonGapMinutes);
            Assert.Equal(23, wild.QuietStartHour);
        }

        [Test]
        public static void ThermalLimits_PerCpuDefault_AndOverride()
        {
            Assert.Equal(95, ThermalLimits.CpuDefault("13th Gen Intel(R) Core(TM) i5-13420H"));
            Assert.Equal(90, ThermalLimits.CpuDefault("AMD Ryzen 7 5800H with Radeon Graphics"));
            Assert.Equal(95, ThermalLimits.CpuDefault(null));
            Assert.Equal(95, ThermalLimits.CpuDefault("Mystery CPU"));
            AlertSettings s = new AlertSettings();
            Assert.Equal(95, ThermalLimits.EffectiveCpu(s, "13th Gen Intel(R) Core(TM) i5-13420H"));
            s.CpuLimitC = 92;
            Assert.Equal(92, ThermalLimits.EffectiveCpu(s, "13th Gen Intel(R) Core(TM) i5-13420H"));
            Assert.Equal(87, ThermalLimits.EffectiveGpu(new AlertSettings()));
        }

        [Test]
        public static void QuietHours_WrapsMidnight()
        {
            DateTime at23 = new DateTime(2026, 11, 17, 23, 30, 0);
            DateTime at03 = new DateTime(2026, 11, 17, 3, 0, 0);
            DateTime at12 = new DateTime(2026, 11, 17, 12, 0, 0);
            Assert.True(QuietHours.IsQuiet(at23, true, 23, 7), "23:30");
            Assert.True(QuietHours.IsQuiet(at03, true, 23, 7), "03:00");
            Assert.True(!QuietHours.IsQuiet(at12, true, 23, 7), "12:00");
            Assert.True(!QuietHours.IsQuiet(at23, false, 23, 7), "disabled");
            Assert.True(!QuietHours.IsQuiet(at23, true, 5, 5), "empty window");
        }

        [Test]
        public static void Texts_FitBalloonLimits()
        {
            DateTime n = Day;
            Alert[] all = new Alert[]
            {
                AlertText.Hot(AlertKind.CpuHot, 101.4, 95, 30, n), AlertText.Hot(AlertKind.GpuHot, 90, 87, 30, n),
                AlertText.Protection("Proteção térmica: automático", n), AlertText.Watchdog(n), AlertText.FanNoResponse(n),
                AlertText.FanUnconfirmed(n), AlertText.SensorLost(60, n), AlertText.SensorLost(300, n)
            };
            foreach (Alert a in all)
            {
                Assert.True(a.Title.Length <= AlertText.MaxTitle && a.Title.Length > 0, a.Title);
                Assert.True(a.Text.Length <= AlertText.MaxText && a.Text.Length > 0, a.Text);
            }
            Assert.True(AlertText.Protection("Proteção térmica: automático", n).Text.Contains("automático"), "auto wording");
            Assert.True(AlertText.SensorLost(300, n).Text.Contains("5 min"), "minutes");
        }

        // ---- the top strip -----------------------------------------------------------------------

        [Test]
        public static void Strip_StableUntilSomethingIsActionable()
        {
            AlertEngine e = Engine();
            e.SetRestartLogKnown(true, 0);
            Feed(e, 0, 20000, 78, Clear());              // 78 C in a game is not an alert
            Assert.True(!e.NeedsAttention(20000), "stable");
            Assert.Null(e.AttentionPage(20000));
            Assert.Null(e.AttentionReason(20000));
            Assert.Equal(StripState.Stable, e.State(20000));
            Assert.Equal("Estável", StripText.Label(e.State(20000), e.ReadingGrace(20000)));
        }

        [Test]
        public static void Strip_SustainedHeat_PointsToMonitor_AndEndsWithTheHeat()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 30000, 96, Clear());
            Assert.True(e.NeedsAttention(30000), "attention");
            Assert.Equal("Monitor", e.AttentionPage(30000));
            Assert.Equal(StripState.Attention, e.State(30000));
            Assert.Equal("Requer sua atenção", StripText.Label(e.State(30000), e.ReadingGrace(30000)));
            Assert.True(!e.AttentionIsCritical(30000), "amber");
            Feed(e, 32000, 34000, 70, Clear());
            Assert.True(!e.NeedsAttention(34000), "back to stable");
        }

        [Test]
        public static void Strip_ThermalProtection_IsCritical_AndPointsToVentoinha()
        {
            AlertEngine e = Engine();
            e.Tick(P(0, true), Clear());
            Assert.True(e.AttentionIsCritical(0), "critical");
            Assert.Equal("Ventoinha", e.AttentionPage(0));
        }

        [Test]
        public static void Strip_SensorLost_AfterTenSeconds_NeverStableWithoutAReading()
        {
            AlertEngine e = Engine();
            for (long t = 0; t <= 8000; t += 2000) e.Tick(S(t, null, null), Clear());
            Assert.True(!e.NeedsAttention(8000), "a blip is not a lost sensor");
            e.Tick(S(10000, null, null), Clear());
            Assert.True(e.NeedsAttention(10000), "no reading for 10 s");
            Assert.Equal("Monitor", e.AttentionPage(10000));
            e.Tick(S(12000, 60, 50), Clear());
            Assert.True(!e.NeedsAttention(12000), "reading is back");
        }

        [Test]
        public static void Strip_RestartNotYetSeen_PointsToSaude_UntilTheCallerSaysSeen()
        {
            AlertEngine e = Engine();
            e.SetUnseenRestarts(2);
            Assert.True(e.NeedsAttention(5000), "attention");
            Assert.Equal("Saude", e.AttentionPage(5000));
            e.Acknowledge("Ventoinha");
            e.Acknowledge("Saude");
            Assert.True(e.NeedsAttention(5000), "page acknowledgements are for one-off events, not restarts");
            e.SetUnseenRestarts(0);
            Assert.True(!e.NeedsAttention(5000), "seen");
        }

        [Test]
        public static void Strip_UnseenRestart_IgnoresTheAlertToggle_ButTheIconHonoursIt()
        {
            AlertSettings s = new AlertSettings();
            s.RestartAlerts = false;
            AlertEngine e = new AlertEngine(s, 95, 87);
            e.SetUnseenRestarts(1);
            Assert.True(e.NeedsAttention(0), "strip");
            Assert.Equal(TrayAlertState.Normal, e.IconState(0));
        }

        [Test]
        public static void CommandFailed_BalloonAndIcon_AndTheTextNamesTheCommand()
        {
            AlertEngine e = Engine();
            Alert a = AlertText.CommandFailed("trocar para o modo Desempenho", "Modos", Day);
            Assert.Equal("O comando não funcionou", a.Title);
            Assert.True(a.Text.Contains("trocar para o modo Desempenho"), a.Text);
            Assert.Equal(1, e.Raise(a, 0, Day, Clear()).Balloons.Count);
            Assert.Equal(TrayAlertState.Warning, e.IconState(1000));
            Assert.Equal("Modos", e.AttentionPage(1000));
            AlertSettings off = new AlertSettings();
            off.FanAlerts = false;
            Assert.Equal(0, new AlertEngine(off, 95, 87).Raise(a, 0, Day, Clear()).Balloons.Count);
        }

        [Test]
        public static void HeldCritical_IsDeliveredWhenTheGameEnds_EvenInsideQuietHours()
        {
            AlertSettings s = new AlertSettings();
            s.QuietHoursEnabled = true;
            s.QuietStartHour = 20;
            s.QuietEndHour = 23;   // Day is 21:00: inside the window
            AlertEngine e = new AlertEngine(s, 95, 87);
            e.Tick(P(0, true), Game());                                 // critical, held by the game
            e.Raise(AlertText.FanNoResponse(Day), 1000, Day, Game());   // warning, held too
            AlertOutput after = e.Tick(P(60000, true), Clear());        // game over, still quiet hours
            Assert.Equal(1, after.Balloons.Count);
            Assert.Equal(AlertKind.ThermalProtection, after.Balloons[0].Kind);
            // the warning waits for the end of the quiet hours
            AlertSample late = P(2 * 3600 * 1000 + 5 * 60 * 1000, false); // 23:05
            AlertOutput later = e.Tick(late, Clear());
            Assert.Equal(1, later.Balloons.Count);
            Assert.Equal(AlertKind.FanNoResponse, later.Balloons[0].Kind);
        }

        [Test]
        public static void HeldCritical_AlsoOutOfFocusAssist()
        {
            AlertEngine e = Engine();
            e.Tick(P(0, true), Game());
            AlertContext quiet = new AlertContext();
            quiet.OsQuiet = true;
            Assert.Equal(1, e.Tick(P(2000, true), quiet).Balloons.Count);
        }

        [Test]
        public static void RestartIncidents_StableIds_72HourWindow_SeenSetPersistsAndIsCapped()
        {
            DateTime now = new DateTime(2026, 11, 17, 9, 0, 0);
            CrashEntry recent = Crash(new DateTime(2026, 11, 17, 1, 35, 0), 0xEF, FailureKind.BlueScreen);
            CrashEntry twoDays = Crash(new DateTime(2026, 11, 15, 12, 0, 0), null, FailureKind.PowerLoss);
            CrashEntry old = Crash(new DateTime(2026, 11, 10, 12, 0, 0), 0xA, FailureKind.BlueScreen);
            List<CrashEntry> all = new List<CrashEntry>(new CrashEntry[] { recent, twoDays, old });
            Assert.Equal(RestartIncidents.IdOf(recent), RestartIncidents.IdOf(Crash(new DateTime(2026, 11, 17, 1, 35, 0), 0xEF, FailureKind.BlueScreen)));
            Assert.True(RestartIncidents.IdOf(recent) != RestartIncidents.IdOf(twoDays), "different incidents");
            List<string> seen = new List<string>();
            Assert.Equal(2, RestartIncidents.Unseen(all, seen, now).Count);       // the old one is outside 72 h
            Assert.True(RestartIncidents.MarkSeen(seen, RestartIncidents.Unseen(all, seen, now)), "changed");
            Assert.Equal(0, RestartIncidents.Unseen(all, seen, now).Count);
            Assert.True(!RestartIncidents.MarkSeen(seen, all.GetRange(0, 2)), "no change when already seen");
            // a new restart after those: unseen again
            CrashEntry fresh = Crash(new DateTime(2026, 11, 17, 8, 0, 0), 0x1C7, FailureKind.BlueScreen);
            all.Insert(0, fresh);
            Assert.Equal(1, RestartIncidents.Unseen(all, seen, now).Count);
            // the list never grows without bound
            List<string> many = new List<string>();
            List<CrashEntry> lots = new List<CrashEntry>();
            for (int i = 0; i < RestartIncidents.MaxSeen + 50; i++) lots.Add(Crash(now.AddMinutes(-i - 5), null, FailureKind.PowerLoss));
            RestartIncidents.MarkSeen(many, lots);
            Assert.Equal(RestartIncidents.MaxSeen, many.Count);
        }

        [Test]
        public static void AlertState_SeenIncidents_Persist()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forja-st-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string path = System.IO.Path.Combine(dir, "alertas-estado.json");
                AlertState st = new AlertState();
                st.SeenIncidents.Add("20261117013827-BlueScreen");
                Assert.True(Forja.Platform.AlertStore.SaveState(path, st), "saved");
                AlertState back = Forja.Platform.AlertStore.LoadState(path);
                Assert.Equal(1, back.SeenIncidents.Count);
                Assert.Equal("20261117013827-BlueScreen", back.SeenIncidents[0]);
                System.IO.File.WriteAllText(path, "{\"LastCrashTicks\":5}");
                Assert.True(Forja.Platform.AlertStore.LoadState(path).SeenIncidents != null, "older file without the list");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        [Test]
        public static void Strip_FanEvent_PointsToVentoinha()
        {
            AlertEngine e = Engine();
            e.Raise(AlertText.Watchdog(Day), 0, Day, Clear());
            Assert.Equal("Ventoinha", e.AttentionPage(1000));
            Assert.True(!e.NeedsAttention(AlertEngine.StickyMs + 1), "expired");
        }

        [Test]
        public static void Strip_Words_AreOnlyTwo()
        {
            Assert.Equal("Estável", StripText.Label(StripState.Stable, false));
            Assert.Equal("Requer sua atenção", StripText.Label(StripState.Attention, false));
            Assert.Equal("Lendo…", StripText.Label(StripState.Reading, true));
            Assert.Equal("", StripText.Label(StripState.Reading, false));
        }

        // ---- "Estável" only when every input is known-good ----------------------------------------

        [Test]
        public static void Strip_BeforeAnyReading_IsReading_NotStable()
        {
            AlertEngine e = Engine();
            Assert.Equal(StripState.Reading, e.State(0));
            Assert.True(!e.NeedsAttention(0), "nothing actionable yet, but nothing known either");
            e.SetRestartLogKnown(true, 0);
            Assert.Equal(StripState.Reading, e.State(1000));   // log known, no temperature reading yet
        }

        [Test]
        public static void Strip_ReadingWord_ShowsOnlyForTheFirstFiveSeconds()
        {
            AlertEngine e = Engine();
            e.SetRestartLogKnown(true, 1000);
            Assert.True(e.ReadingGrace(1000), "at start");
            Assert.True(e.ReadingGrace(5999), "just under 5 s");
            Assert.True(!e.ReadingGrace(6001), "after 5 s the word is gone");
            Assert.Equal("Lendo…", StripText.Label(e.State(2000), e.ReadingGrace(2000)));
            Assert.Equal("", StripText.Label(e.State(7000), e.ReadingGrace(7000)));   // still unknown: neutral LED, no word
        }

        [Test]
        public static void Strip_UnreadableRestartLog_NeverBecomesStableByAssumption()
        {
            AlertEngine e = Engine();
            Feed(e, 0, 20000, 60, Clear());                    // temperatures fine
            e.SetRestartLogKnown(false, 20000);                // the log could not be read
            Assert.Equal(StripState.Reading, e.State(20000));
            Assert.True(!e.NeedsAttention(20000), "an unreadable log does not add attention");
            e.SetRestartLogKnown(true, 21000);
            Assert.Equal(StripState.Stable, e.State(21000));
            e.SetRestartLogKnown(false, 22000);                // a later failed read: back to unknown, not to "Estável"
            Assert.Equal(StripState.Reading, e.State(22000));
        }

        [Test]
        public static void Strip_Attention_WinsOverUnknown()
        {
            AlertEngine e = Engine();
            e.Tick(P(0, true), Clear());                       // protection engaged before the log was ever read
            Assert.Equal(StripState.Attention, e.State(0));
        }

        [Test]
        public static void Strip_OnlyAValidCpuReadingCounts_GarbageDoesNot()
        {
            AlertEngine e = Engine();
            e.SetRestartLogKnown(true, 0);
            e.Tick(S(0, null, null), Clear());
            e.Tick(S(2000, 255, 50), Clear());
            Assert.Equal(StripState.Reading, e.State(2000));
            e.Tick(S(4000, 60, null), Clear());
            Assert.Equal(StripState.Stable, e.State(4000));
        }

        [Test]
        public static void Strip_AfterTheSensorIsLost_ItIsAttention_NotAGoBackToReading()
        {
            AlertEngine e = Engine();
            e.SetRestartLogKnown(true, 0);
            Feed(e, 0, 10000, 60, Clear());
            for (long t = 12000; t <= 30000; t += 2000) e.Tick(S(t, null, null), Clear());
            Assert.Equal(StripState.Attention, e.State(30000));
        }

        // ---- view-model for a future settings screen ---------------------------------------------

        [Test]
        public static void SettingsModel_ExposesLimitsRangesAndTexts()
        {
            AlertSettingsModel m = new AlertSettingsModel(new AlertSettings(), "13th Gen Intel(R) Core(TM) i5-13420H");
            Assert.True(m.CpuLimitAutomatic, "automatic by default");
            Assert.Equal(95, m.CpuLimitC);
            Assert.Equal(95, m.CpuAutomaticLimitC);
            Assert.Equal("95 °C (automático para este processador)", m.CpuLimitText);
            Assert.Equal("CPU acima de 95 °C ou GPU acima de 87 °C por mais de 30 s.", m.Summary());
            Assert.Equal("desligado", m.QuietHoursText);
            m.CpuLimitAutomatic = false;
            m.CpuManualLimitC = 92;
            m.SustainSeconds = 45;
            m.QuietHoursEnabled = true;
            Assert.Equal("92 °C (definido por você)", m.CpuLimitText);
            Assert.Equal("CPU acima de 92 °C ou GPU acima de 87 °C por mais de 45 s.", m.Summary());
            Assert.Equal("23:00 às 07:00", m.QuietHoursText);
            AlertSettings s = m.ToSettings();
            Assert.Equal(92, s.CpuLimitC);
            Assert.Equal(45, s.SustainSeconds);
            Assert.True(s.QuietHoursEnabled, "quiet hours");
        }

        [Test]
        public static void SettingsModel_OutOfRangeValuesAreBroughtIntoRange_AutomaticWritesZero()
        {
            AlertSettingsModel m = new AlertSettingsModel(new AlertSettings(), "AMD Ryzen 7 5800H");
            Assert.Equal(90, m.CpuLimitC);
            m.CpuLimitAutomatic = false;
            m.CpuManualLimitC = 400;
            m.GpuLimitC = 5;
            m.SustainSeconds = 1;
            m.BalloonGapMinutes = 9999;
            AlertSettings s = m.ToSettings();
            Assert.Equal(AlertSettingsModel.CpuMaxC, s.CpuLimitC);
            Assert.Equal(AlertSettingsModel.GpuMinC, s.GpuLimitC);
            Assert.Equal(AlertSettingsModel.SustainMinSeconds, s.SustainSeconds);
            Assert.Equal(AlertSettingsModel.GapMaxMinutes, s.BalloonGapMinutes);
            m.CpuLimitAutomatic = true;
            Assert.Equal(0, m.ToSettings().CpuLimitC);
            // a manual limit saved earlier comes back as manual
            AlertSettings saved = new AlertSettings();
            saved.CpuLimitC = 88;
            AlertSettingsModel back = new AlertSettingsModel(saved, null);
            Assert.True(!back.CpuLimitAutomatic && back.CpuLimitC == 88, "manual kept");
        }

        [Test]
        public static void SettingsModel_RoundTripsEveryToggle()
        {
            AlertSettings a = new AlertSettings();
            a.Enabled = false;
            a.TempAlerts = false;
            a.ProtectionAlerts = false;
            a.RestartAlerts = false;
            a.FanAlerts = false;
            a.SensorAlerts = false;
            AlertSettings b = new AlertSettingsModel(a, null).ToSettings();
            Assert.True(!b.Enabled && !b.TempAlerts && !b.ProtectionAlerts && !b.RestartAlerts && !b.FanAlerts && !b.SensorAlerts, "all off survive");
        }

        [Test]
        public static void ScrubMachineName_WholeWordOnly()
        {
            Assert.Equal("falha em %COMPUTERNAME% às 01:35", LogText.ScrubMachineName("falha em DESKTOP-ABC123 às 01:35", "desktop-abc123"));
            Assert.Equal("DESKTOP-ABC1234 fica", LogText.ScrubMachineName("DESKTOP-ABC1234 fica", "DESKTOP-ABC123"));
            Assert.Equal("texto", LogText.ScrubMachineName("texto", null));
            Assert.Equal("ab", LogText.ScrubMachineName("ab", "ab"));
        }

        // ---- sources -------------------------------------------------------------------------

        [Test]
        public static void FanStatus_ClassifiedFromTheRealControllerTexts()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            SensorReading r = new SensorReading();
            r.CpuTempC = 40;
            c.Tick(r);
            Assert.Equal(FanCondition.None, FanStatusClassifier.Classify(c.Status));
            c.Mode = FanUserMode.Manual;
            r = new SensorReading();
            r.CpuTempC = 95;
            c.Tick(r);
            Assert.Equal(FanCondition.ThermalProtection, FanStatusClassifier.Classify(c.Status));
            c.Mode = FanUserMode.Manual;
            c.NotifyWatchdogAuto();
            Assert.Equal(FanCondition.Watchdog, FanStatusClassifier.Classify(c.Status));
            Assert.Equal(FanCondition.NoResponse, FanStatusClassifier.Classify("Ventoinha não respondeu ao comando — automático"));
            Assert.Equal(FanCondition.ThermalProtection, FanStatusClassifier.Classify("Proteção térmica: automático"));
            Assert.Equal(FanCondition.None, FanStatusClassifier.Classify(null));
        }

        private static JournalEntry J(string source, string kind, string text)
        {
            JournalEntry e = new JournalEntry();
            e.Time = Day;
            e.Source = source;
            e.Kind = kind;
            e.Text = text;
            return e;
        }

        [Test]
        public static void Journal_FanLinesBecomeAlerts_OthersDoNot()
        {
            Assert.Equal(AlertKind.FanWatchdog, JournalAlertClassifier.Classify(J("app", JournalKind.State, "o vigia interno devolveu a ventoinha ao automático (nenhuma leitura por 10 s)")).Kind);
            Assert.Equal(AlertKind.FanWatchdog, JournalAlertClassifier.Classify(J("app", JournalKind.State, "o guardião devolveu a ventoinha ao automático (o app ficou sem responder)")).Kind);
            Assert.Equal(AlertKind.FanWatchdog, JournalAlertClassifier.Classify(J("sentinela", JournalKind.Sentinel, "o app parou de responder (sem sinal há 31 s) com a ventoinha fora do automático: voltando ao automático (tentativa 1)")).Kind);
            Assert.Equal(AlertKind.FanWatchdog, JournalAlertClassifier.Classify(J("sentinela", JournalKind.Sentinel, "nenhum app aberto e a ventoinha fora do automático: voltando ao automático (tentativa 1)")).Kind);
            Assert.Equal(AlertKind.FanWatchdog, JournalAlertClassifier.Classify(J("app", JournalKind.Recovery, "automático confirmado (marcador de ventoinha manual presente)")).Kind);
            Assert.True(JournalAlertClassifier.Classify(J("app", JournalKind.Recovery, "automático NÃO confirmado (x)")).Title.Contains("sem confirmação"), "unconfirmed wording");
            Assert.Null(JournalAlertClassifier.Classify(J("app", JournalKind.State, "modo=Dia a dia; ventoinha=Automático; automático confirmado=sim")));
            Assert.Null(JournalAlertClassifier.Classify(J("app", JournalKind.WriteBefore, "x")));
            Assert.Null(JournalAlertClassifier.Classify(J("sentinela", JournalKind.Sentinel, "guardião iniciado")));
            Assert.Null(JournalAlertClassifier.Classify(J("sentinela", JournalKind.Sentinel, "guardião encerrado: nenhum app aberto e a ventoinha está em automático")));
            Assert.Null(JournalAlertClassifier.Classify(null));
        }

        [Test]
        public static void Journal_Select_OnlyAfterTheLastSeenTime_OldestFirst()
        {
            List<JournalEntry> lines = new List<JournalEntry>();
            JournalEntry a = J("app", JournalKind.State, "o vigia interno devolveu a ventoinha ao automático");
            a.Time = Day.AddMinutes(-30);
            JournalEntry b = J("app", JournalKind.State, "o guardião devolveu a ventoinha ao automático");
            b.Time = Day.AddMinutes(-5);
            JournalEntry c = J("app", JournalKind.State, "modo=x");
            c.Time = Day;
            lines.Add(b);
            lines.Add(a);
            lines.Add(c);
            List<Alert> all = JournalAlertClassifier.Select(lines, Day.AddHours(-1));
            Assert.Equal(2, all.Count);
            Assert.Equal(Day.AddMinutes(-30), all[0].Local);
            Assert.Equal(1, JournalAlertClassifier.Select(lines, Day.AddMinutes(-10)).Count);
            Assert.Equal(0, JournalAlertClassifier.Select(lines, Day).Count);
        }

        [Test]
        public static void NewCrashDetector_FirstRunOnlyRecent_ThenOnlyNewerThanLastSeen()
        {
            DateTime now = new DateTime(2026, 11, 17, 9, 0, 0);
            CrashEntry old = Crash(new DateTime(2026, 11, 10, 12, 0, 0), 0xA, FailureKind.BlueScreen);
            CrashEntry mid = Crash(new DateTime(2026, 11, 16, 14, 6, 0), null, FailureKind.HangPowerButton);
            CrashEntry recent = Crash(new DateTime(2026, 11, 17, 1, 35, 0), 0xEF, FailureKind.BlueScreen);
            List<CrashEntry> all = new List<CrashEntry>(new CrashEntry[] { recent, mid, old });
            List<CrashEntry> first = NewCrashDetector.Select(all, 0, now);
            Assert.Equal(1, first.Count);
            Assert.Equal(0xEFL, first[0].Code.Value);
            long seen = NewCrashDetector.NewestTicks(all, 0);
            Assert.Equal(recent.When.Ticks, seen);
            Assert.Equal(0, NewCrashDetector.Select(all, seen, now).Count);
            Assert.Equal(1, NewCrashDetector.Select(all, mid.When.Ticks, now).Count);
            Assert.Equal(seen, NewCrashDetector.NewestTicks(new List<CrashEntry>(), seen));
            Assert.Equal(0, NewCrashDetector.Select(null, 0, now).Count);
        }
    }
}
