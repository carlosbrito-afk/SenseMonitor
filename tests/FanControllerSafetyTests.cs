using System;
using Forja.Core;

namespace Forja.Tests
{
    // The controller never writes Auto to a model whose fan control is not confirmed (unless this app made the
    // fans leave Auto), backs off after failed writes, alerts once, and can be paused while the environment
    // wizard owns the firmware.
    public static class FanControllerSafetyTests
    {
        private static SensorReading R(double cpu, double gpu)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            r.CpuFanRpm = 2000;
            r.GpuFanRpm = 2000;
            return r;
        }

        // ---- no Auto write without confirmed fan control ----

        [Test]
        public static void Unconfirmed_StartupTicks_WriteNothing()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = new FanController(fw);
            c.AutoAssertEnabled = false;
            for (int i = 0; i < 5; i++) c.Tick(R(50, 45));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(c.FirmwareAutoConfirmed, "the firmware is in charge: nothing to confirm");
            Assert.True(!c.StatusIsWarning, "no warning for a notebook we never wrote to");
        }

        [Test]
        public static void Unconfirmed_ExitSuspendResume_WriteNothing()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = new FanController(fw);
            c.AutoAssertEnabled = false;
            c.Tick(R(50, 45));
            c.OnSuspend();
            c.OnResume();
            c.Tick(R(50, 45));
            c.Shutdown();
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!c.MayNeedHandBack, "nothing to hand back");
        }

        [Test]
        public static void Unconfirmed_FailingAutoNeverRetried_BecauseNeverAttempted()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false; // a 1-fan model that refuses the both-fan mask
            FanController c = new FanController(fw);
            c.AutoAssertEnabled = false;
            for (int i = 0; i < 10; i++) c.Tick(R(50, 45));
            Assert.Equal(0, fw.Count("auto"));
        }

        [Test]
        public static void Unconfirmed_AfterACustomWrite_TheHandBackIsStillWritten()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.AutoAssertEnabled = false;
            c.Tick(R(50, 45));
            Assert.Equal(0, fw.Count("auto"));
            c.ManualCpuPct = 60;
            c.ManualGpuPct = 60;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45));
            Assert.True(fw.Calls.Contains("pct:60,60"), "custom written");
            Assert.True(c.MayNeedHandBack, "the app left Auto");
            c.Shutdown();
            Assert.Equal(1, fw.Count("auto"));
            Assert.True(c.FirmwareAutoConfirmed, "confirmed");
            Assert.True(!c.MayNeedHandBack, "handed back: nothing more to do");
        }

        [Test]
        public static void MarkDirty_ForTheMaxOptionOutsideTheController()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = new FanController(fw);
            c.AutoAssertEnabled = false;
            c.MarkDirty();
            c.Shutdown();
            Assert.Equal(1, fw.Count("auto"));
        }

        [Test]
        public static void Confirmed_DefaultBehaviourUnchanged_AutoAssertedOnFirstTick()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = new FanController(fw);
            c.Tick(R(50, 45));
            Assert.Equal(1, fw.Count("auto"));
        }

        // ---- back-off after failed Auto writes ----

        private sealed class Clk
        {
            public int Now = 1000;
        }

        private static FanController BackoffRig(FakeFirmware fw, Clk clk)
        {
            FanController c = new FanController(fw);
            c.BackoffEnabled = true;
            c.Clock = delegate { return clk.Now; };
            return c;
        }

        [Test]
        public static void Backoff_2s_5s_30s_5min()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            Clk clk = new Clk();
            FanController c = BackoffRig(fw, clk);
            c.Tick(R(50, 45)); // attempt 1 at t=1000
            Assert.Equal(1, fw.Count("auto"));
            clk.Now = 2999; c.Tick(R(50, 45));
            Assert.Equal(1, fw.Count("auto")); // inside 2 s
            clk.Now = 3000; c.Tick(R(50, 45)); // attempt 2
            Assert.Equal(2, fw.Count("auto"));
            clk.Now = 7999; c.Tick(R(50, 45));
            Assert.Equal(2, fw.Count("auto")); // inside 5 s
            clk.Now = 8000; c.Tick(R(50, 45)); // attempt 3
            Assert.Equal(3, fw.Count("auto"));
            clk.Now = 37999; c.Tick(R(50, 45));
            Assert.Equal(3, fw.Count("auto")); // inside 30 s
            clk.Now = 38000; c.Tick(R(50, 45)); // attempt 4
            Assert.Equal(4, fw.Count("auto"));
            clk.Now = 337999; c.Tick(R(50, 45));
            Assert.Equal(4, fw.Count("auto")); // inside 5 min
            clk.Now = 338000; c.Tick(R(50, 45)); // attempt 5
            Assert.Equal(5, fw.Count("auto"));
            clk.Now = 638000; c.Tick(R(50, 45)); // still 5 min, not longer
            Assert.Equal(6, fw.Count("auto"));
        }

        [Test]
        public static void Backoff_SuccessResetsTheSequence()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            Clk clk = new Clk();
            FanController c = BackoffRig(fw, clk);
            c.Tick(R(50, 45));
            clk.Now = 3000; c.Tick(R(50, 45));
            fw.SetAutoResult = true;
            clk.Now = 8000; c.Tick(R(50, 45));
            Assert.True(c.FirmwareAutoConfirmed, "confirmed");
            fw.SetAutoResult = false;
            c.Shutdown(); // urgent: ignores the back-off
            Assert.True(fw.Count("auto") >= 4, "attempts");
        }

        [Test]
        public static void Backoff_UrgentWritesIgnoreIt()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            Clk clk = new Clk();
            FanController c = BackoffRig(fw, clk);
            c.Tick(R(50, 45));
            int before = fw.Count("auto");
            c.OnSuspend(); // at once, inside the back-off window
            Assert.Equal(before + 1, fw.Count("auto"));
            c.Shutdown();
            Assert.Equal(before + 2, fw.Count("auto"));
        }

        [Test]
        public static void Backoff_AlertsOnce()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            Clk clk = new Clk();
            FanController c = BackoffRig(fw, clk);
            Assert.True(!c.TakeAutoFailAlert(), "nothing yet");
            c.Tick(R(50, 45));
            Assert.True(c.TakeAutoFailAlert(), "first failure alerts");
            Assert.True(!c.TakeAutoFailAlert(), "only once");
            clk.Now = 3000; c.Tick(R(50, 45));
            clk.Now = 8000; c.Tick(R(50, 45));
            Assert.True(!c.TakeAutoFailAlert(), "no repeat while it keeps failing");
            fw.SetAutoResult = true;
            clk.Now = 40000; c.Tick(R(50, 45));
            fw.SetAutoResult = false;
            c.OnResume();
            c.OnSuspend();
            Assert.True(c.TakeAutoFailAlert(), "a new series of failures alerts again");
        }

        [Test]
        public static void Backoff_OffByDefault_KeepsTheOldImmediateRetry()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            FanController c = new FanController(fw);
            c.Tick(R(50, 45));
            c.Tick(R(50, 45));
            Assert.Equal(2, fw.Count("auto"));
        }

        // ---- pause while the wizard owns the firmware ----

        [Test]
        public static void Pause_HandsBackAndStopsWriting()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50, 45));
            c.ManualCpuPct = 70;
            c.ManualGpuPct = 70;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45));
            int autos = fw.Count("auto");
            c.SetPaused(true);
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal(autos + 1, fw.Count("auto")); // handed back to the firmware before the wizard starts
            int calls = fw.Calls.Count;
            for (int i = 0; i < 6; i++) c.Tick(R(60, 55));
            Assert.Equal(calls, fw.Calls.Count);
        }

        [Test]
        public static void Pause_ModeChoicesStayAuto_WithTheNote()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50, 45));
            c.SetPaused(true);
            int calls = fw.Calls.Count;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45));
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.True(c.Status.Contains("teste de ambiente"), c.Status);
            Assert.Equal(calls, fw.Calls.Count);
        }

        [Test]
        public static void Pause_FailedAutoIsNotReasserted()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            FanController c = new FanController(fw);
            c.SetPaused(true);
            for (int i = 0; i < 5; i++) c.Tick(R(50, 45));
            Assert.Equal(0, fw.Count("auto"));
        }

        [Test]
        public static void Pause_EmergencyHeatStillWritesAuto_Once()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50, 45));
            c.SetPaused(true);
            int autos = fw.Count("auto");
            c.Tick(R(91, 60));
            Assert.Equal(autos + 1, fw.Count("auto"));
            c.Tick(R(93, 60));
            c.Tick(R(92, 60));
            Assert.Equal(autos + 1, fw.Count("auto")); // not hammered
            c.Tick(R(70, 60)); // cooled below the release point
            c.Tick(R(91, 60));
            Assert.Equal(autos + 2, fw.Count("auto")); // a new heat event again
        }

        [Test]
        public static void Pause_ResumeAllowsTheModesAgain()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50, 45));
            c.SetPaused(true);
            c.SetPaused(false);
            c.ManualCpuPct = 50;
            c.ManualGpuPct = 50;
            c.Mode = FanUserMode.Manual;
            Assert.Equal(FanUserMode.Manual, c.Mode);
            c.Tick(R(50, 45));
            Assert.True(fw.Calls.Contains("pct:50,50"), "writes again");
            Assert.True(!c.Paused, "not paused");
        }

        [Test]
        public static void Pause_OnAnUnconfirmedController_WritesNothing()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = new FanController(fw);
            c.AutoAssertEnabled = false;
            c.SetPaused(true);
            c.SetPaused(false);
            Assert.Equal(0, fw.Calls.Count);
        }
    }
}
