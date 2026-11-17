using System;
using Forja.Core;

namespace Forja.Tests
{
    // Final-review safety rules: manual control gated by a hardware validation, RPM-response
    // guard after Custom writes, watchdog hand-back, adaptive firmware polling, startup mode.
    public static class FanSafetyRulesTests
    {
        private static SensorReading R(double? cpu, double? gpu, int? cpuRpm, int? gpuRpm)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            r.CpuFanRpm = cpuRpm;
            r.GpuFanRpm = gpuRpm;
            return r;
        }

        private static SensorReading R(double? cpu) { return R(cpu, null, null, null); }

        // ---- gate: Manual/Curva only after the validation ----

        [Test]
        public static void Unvalidated_ManualAndCurveAreRefused_StaysAutoWithNote()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = new FanController(fw);
            Assert.True(!c.CustomWritesValidated, "safe default: not validated");
            c.Tick(R(40));
            c.Mode = FanUserMode.Manual;
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal(FanValidation.LockedNote, c.Status);
            c.Mode = FanUserMode.Curve;
            Assert.Equal(FanUserMode.Auto, c.Mode);
            c.Tick(R(95));
            c.Tick(R(95));
            foreach (string call in fw.Calls) Assert.True(!call.StartsWith("pct:"), "no Custom write while unvalidated: " + call);
        }

        [Test]
        public static void Unvalidated_HotReading_ProtectionIsTheFirmwareAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(40));
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            Assert.Equal("pct:30,30", fw.Last());
            // validation revoked while Manual (e.g. a failed re-run): back to the firmware at once
            c.CustomWritesValidated = false;
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("auto", fw.Last());
            fw.Calls.Clear();
            c.Tick(R(96));
            c.Tick(R(97));
            foreach (string call in fw.Calls) Assert.True(!call.StartsWith("pct:"), "never Custom 100% while unvalidated: " + call);
        }

        [Test]
        public static void Validated_ThermalProtectionStillUsesCustom100()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(40));
            c.Mode = FanUserMode.Manual;
            c.Tick(R(92));
            Assert.Equal("pct:100,100", fw.Last());
        }

        [Test]
        public static void Config_ManualFanAllowed_NeedsValidationAndMaxRpm()
        {
            Config cfg = new Config();
            Assert.True(!cfg.ManualFanAllowed, "default locked");
            cfg.FanControlValidated = true;
            Assert.True(!cfg.ManualFanAllowed, "no RPM reference yet");
            cfg.FanMaxRpmCpu = 5200;
            cfg.FanMaxRpmGpu = 5000;
            Assert.True(cfg.ManualFanAllowed, "validated with reference");
        }

        // ---- RPM-response guard ----

        [Test]
        public static void RpmGuard_InBandAndTowardRules()
        {
            // 50% of 6000 = 3000 expected; band 2250..3750
            Assert.True(RpmGuard.InBand(3000, 50, 6000), "centre");
            Assert.True(!RpmGuard.InBand(2000, 50, 6000), "below band");
            Assert.True(!RpmGuard.InBand(null, 50, 6000), "no reading");
            // from 2000 toward 3000: needs > 10 % change upwards
            Assert.True(RpmGuard.Responded(2000, 2250, 50, 6000), "moved up toward the band");
            Assert.True(!RpmGuard.Responded(2000, 2100, 50, 6000), "5 % is noise");
            Assert.True(!RpmGuard.Responded(2000, 1700, 50, 6000), "moved the wrong way");
            Assert.True(RpmGuard.Responded(5800, 4000, 50, 6000), "moved down toward the band");
            Assert.True(!RpmGuard.Responded(2000, null, 50, 6000), "lost reading is not a response");
        }

        [Test]
        public static void RpmGuard_FansIgnoreCustomWrite_FallsBackAfterThreeTicks()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetRpmReference(6000, 6000);
            c.Tick(R(50, 45, 1800, 1700));
            c.ManualCpuPct = 90;
            c.ManualGpuPct = 90;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45, 1800, 1700));      // write 90 %
            Assert.Equal("pct:90,90", fw.Last());
            c.Tick(R(50, 45, 1810, 1690));
            c.Tick(R(50, 45, 1790, 1700));
            Assert.Equal(FanUserMode.Manual, c.Mode);
            c.Tick(R(50, 45, 1800, 1710));      // third tick without response
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("Ventoinha não respondeu ao comando — automático", c.Status);
            Assert.True(c.StatusIsWarning, "warning");
            Assert.Equal("auto", fw.Last());
        }

        [Test]
        public static void RpmGuard_OneFanIgnoresTheWrite_FallsBack()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetRpmReference(6000, 6000);
            c.Tick(R(50, 45, 1800, 1700));
            c.ManualCpuPct = 90;
            c.ManualGpuPct = 90;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45, 1800, 1700));
            c.Tick(R(50, 45, 4000, 1700));
            c.Tick(R(50, 45, 5200, 1700));
            c.Tick(R(50, 45, 5400, 1700));
            Assert.Equal(FanUserMode.Auto, c.Mode);
        }

        [Test]
        public static void RpmGuard_FansRespond_StaysManual()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetRpmReference(6000, 6000);
            c.Tick(R(50, 45, 1800, 1700));
            c.ManualCpuPct = 90;
            c.ManualGpuPct = 90;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45, 1800, 1700));
            c.Tick(R(50, 45, 2600, 2500));
            for (int i = 0; i < 6; i++) c.Tick(R(50, 45, 5300, 5200));
            Assert.Equal(FanUserMode.Manual, c.Mode);
            Assert.Equal(1, fw.Count("pct:90,90"));
        }

        [Test]
        public static void RpmGuard_AlreadyInBandAtWrite_NeedsNoMovement()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetRpmReference(6000, 6000);
            c.Tick(R(50, 45, 3000, 3000));
            c.ManualCpuPct = 50;
            c.ManualGpuPct = 50;
            c.Mode = FanUserMode.Manual;
            for (int i = 0; i < 6; i++) c.Tick(R(50, 45, 3000, 3000));
            Assert.Equal(FanUserMode.Manual, c.Mode);
        }

        [Test]
        public static void RpmGuard_ThermalProtectionWriteIsGuardedToo()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetRpmReference(6000, 6000);
            c.Tick(R(50, 45, 3000, 3000));
            c.Mode = FanUserMode.Manual;            // 30 %: 1800 expected, from 3000 down
            c.Tick(R(50, 45, 3000, 3000));
            c.Tick(R(60, 45, 2000, 2000));
            c.Tick(R(92, 45, 2000, 2000));          // protection: 100 %
            Assert.Equal("pct:100,100", fw.Last());
            c.Tick(R(92, 45, 2000, 2000));
            c.Tick(R(92, 45, 2000, 2000));
            c.Tick(R(92, 45, 2000, 2000));
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("auto", fw.Last());
        }

        [Test]
        public static void RpmGuard_WithoutReference_IsOff()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50));
            c.Mode = FanUserMode.Manual;
            for (int i = 0; i < 6; i++) c.Tick(R(50));
            Assert.Equal(FanUserMode.Manual, c.Mode);
        }

        // ---- watchdog ----

        [Test]
        public static void Watchdog_FiresOnlyWhenRiskyAndStalled_OncePerStall()
        {
            FanWatchdog w = new FanWatchdog();
            Assert.True(!w.Check(50000), "never fires before the first beat");
            w.Beat(0, false);
            Assert.True(!w.Check(30000), "Automático: no action");
            w.Beat(30000, true);
            Assert.True(!w.Check(39000), "9 s is fine");
            Assert.True(w.Check(40500), "10 s without a tick while Manual/Curva/Máximo");
            Assert.True(!w.Check(41500), "once per stall");
            w.ReportResult(true, 41600);
            Assert.True(w.TakePendingNotify(), "UI must notify the controller");
            Assert.True(!w.TakePendingNotify(), "taken once");
            Assert.True(!w.Check(80000), "still the same stall");
            w.Beat(81000, true);
            Assert.True(w.Check(91500), "a new stall fires again");
        }

        [Test]
        public static void Watchdog_FailedWrite_RetriesEveryFiveSeconds()
        {
            FanWatchdog w = new FanWatchdog();
            w.Beat(0, true);
            Assert.True(w.Check(10000), "fires");
            w.ReportResult(false, 10000);
            Assert.True(!w.TakePendingNotify(), "nothing to notify");
            Assert.True(!w.Check(12000), "no hammering");
            Assert.True(w.Check(15000), "retry after 5 s");
        }

        [Test]
        public static void Watchdog_PausedWhileSuspended()
        {
            FanWatchdog w = new FanWatchdog();
            w.Beat(0, true);
            w.Pause();
            Assert.True(!w.Check(60000), "suspend is not a stall");
            w.Beat(61000, true);
            Assert.True(w.Check(72000), "armed again after the next tick");
        }

        [Test]
        public static void Watchdog_TickCountWrap()
        {
            FanWatchdog w = new FanWatchdog();
            w.Beat(int.MaxValue - 1000, true);
            Assert.True(!w.Check(unchecked(int.MaxValue + 5000)), "6 s");
            Assert.True(w.Check(unchecked(int.MaxValue + 9500)), "10.5 s");
        }

        [Test]
        public static void NotifyWatchdogAuto_LeavesManualWithNotice()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50));
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            int writes = c.AutoWriteCount;
            fw.Calls.Clear();
            c.NotifyWatchdogAuto();
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal(writes + 1, c.AutoWriteCount);
            Assert.True(c.StatusIsWarning, "explained");
            Assert.Equal(0, fw.Calls.Count);
        }

        // A SetFanPercent stuck on the hung UI thread may land AFTER the isolated Auto: the next
        // UI tick must write Auto again from the UI thread.
        [Test]
        public static void NotifyWatchdogAuto_NextTickReSendsAutoFromTheUiThread()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50));
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            c.NotifyWatchdogAuto();
            Assert.True(!c.FirmwareAutoConfirmed, "not trusted: a late Custom write may have landed");
            fw.Calls.Clear();
            c.Tick(R(50));
            Assert.Equal("auto", fw.Last());
            Assert.Equal(1, fw.Calls.Count);
            Assert.True(c.FirmwareAutoConfirmed, "confirmed by the UI thread's own write");
            c.Tick(R(50));
            Assert.Equal(1, fw.Calls.Count);
        }

        [Test]
        public static void Watchdog_UserPickBeat_ArmsItImmediately()
        {
            FanWatchdog w = new FanWatchdog();
            w.Beat(0, false);              // hidden, Automático, 12 s ticks
            w.Beat(3000, true);            // user picks Manual/Curva/Máximo (page or tray)
            Assert.True(!w.Check(12000), "9 s after the pick");
            Assert.True(w.Check(13500), "10.5 s after the pick without a tick");
        }

        // ---- polling ----

        [Test]
        public static void PollPolicy_SlowOnlyWhenAutoAndHidden()
        {
            Assert.Equal(12000, PollPolicy.FirmwareIntervalMs(FanUserMode.Auto, false, false, true));
            Assert.Equal(2000, PollPolicy.FirmwareIntervalMs(FanUserMode.Auto, false, true, true));
            Assert.Equal(2000, PollPolicy.FirmwareIntervalMs(FanUserMode.Manual, false, false, true));
            Assert.Equal(2000, PollPolicy.FirmwareIntervalMs(FanUserMode.Curve, false, false, true));
            Assert.Equal(2000, PollPolicy.FirmwareIntervalMs(FanUserMode.Auto, true, false, true));
            Assert.Equal(2000, PollPolicy.FirmwareIntervalMs(FanUserMode.Auto, false, false, false));
        }

        [Test]
        public static void SamplePacer_TwelveSecondTicks_StillOneSamplePerSecond()
        {
            SamplePacer p = new SamplePacer();
            p.Due(0);
            int total = 0;
            for (int t = 12000; t <= 600000; t += 12000) total += p.Due(t);
            Assert.Equal(600, total);
        }

        // ---- startup mode ----

        [Test]
        public static void StartupMode_SetsDiaADiaWithReadBack()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Desempenho;
            string logged = null;
            PerfMode? m = StartupMode.Apply(fw, delegate(string s) { logged = s; });
            Assert.Equal((PerfMode?)PerfMode.DiaADia, m);
            Assert.Equal("mode:DiaADia", fw.Last());
            Assert.Null(logged);
        }

        [Test]
        public static void StartupMode_Failure_IsLoggedAndReportsTheRealMode()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Silencioso;
            fw.IgnoredModes.Add(PerfMode.DiaADia);
            string logged = null;
            PerfMode? m = StartupMode.Apply(fw, delegate(string s) { logged = s; });
            Assert.Equal((PerfMode?)PerfMode.Silencioso, m);
            Assert.True(logged != null && logged.Contains("Dia a dia"), "logged: " + logged);
        }

        [Test]
        public static void StartupMode_NeverThrows()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ThrowOnMode = true;
            string logged = null;
            PerfMode? m = StartupMode.Apply(fw, delegate(string s) { logged = s; });
            Assert.Null(m);
            Assert.True(logged != null, "failure logged");
        }
    }
}
