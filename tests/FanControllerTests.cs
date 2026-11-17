using System;
using Forja.Core;

namespace Forja.Tests
{
    public static class FanControllerTests
    {
        private static SensorReading R(double? cpu, double? gpu)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            return r;
        }

        private static SensorReading R(double? cpu) { return R(cpu, null); }

        // Controller with CurveAvailable already true (one valid reading while Auto).
        private static FanController Ready(FakeFirmware fw)
        {
            FanController c = TestFans.Validated(fw);
            c.Tick(R(40));
            fw.Calls.Clear(); // drop the startup SetFanAuto
            return c;
        }

        [Test]
        public static void StatusIsWarning_OnlyForAutoFallbackNotices()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            Assert.True(!c.StatusIsWarning, "plain Auto");
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            Assert.True(!c.StatusIsWarning, "manual is not a warning");
            fw.SetPercentResult = false;
            c.ManualCpuPct = 60;
            c.Tick(R(50));
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.True(c.StatusIsWarning, "fallback after failure");
            c.Mode = FanUserMode.Auto;
            Assert.True(!c.StatusIsWarning, "user action clears the notice");
        }

        [Test]
        public static void StatusIsWarning_ThermalProtectionAndFailedAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(95));
            Assert.True(c.StatusIsWarning, "thermal protection");
            c.Tick(R(60));
            Assert.True(!c.StatusIsWarning, "protection released, manual again");
            fw.SetAutoResult = false;
            c.OnSuspend();
            Assert.True(c.StatusIsWarning, "failed return to Auto");
        }

        [Test]
        public static void AutoWriteCount_CountsOnlySuccessfulSetFanAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            Assert.Equal(0, c.AutoWriteCount);
            c.Tick(R(40));
            Assert.Equal(1, c.AutoWriteCount);
            c.Tick(R(40));
            Assert.Equal(1, c.AutoWriteCount);
            c.OnSuspend();
            Assert.Equal(2, c.AutoWriteCount);
            fw.SetAutoResult = false;
            c.OnSuspend();
            Assert.Equal(2, c.AutoWriteCount);
        }

        [Test]
        public static void Auto_NeverSetsSpeed()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(40, 40));
            c.Tick(R(95, 95));
            c.Tick(R(null, null));
            c.Tick(R(null, null));
            c.Tick(R(null, null));
            foreach (string call in fw.Calls)
            {
                Assert.True(!call.StartsWith("pct:"), "Auto must never set a speed: " + call);
            }
            // startup asserts Auto once (firmware may have kept a manual speed from a crashed run)
            Assert.Equal(1, fw.Count("auto"));
            Assert.Equal(1, fw.Calls.Count);
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("Automático", c.Status);
        }

        [Test]
        public static void Manual_AppliesFloor()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 10;
            c.ManualGpuPct = 5;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(40));
            Assert.Equal(1, fw.Calls.Count);
            Assert.Equal("pct:30,30", fw.Calls[0]);
        }

        [Test]
        public static void Manual_ClampsTo100_AndSendsOnlyOnChange()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 250;
            c.ManualGpuPct = 60;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(40));
            c.Tick(R(41));
            c.Tick(R(42));
            Assert.Equal(1, fw.Calls.Count);
            Assert.Equal("pct:100,60", fw.Calls[0]);
            Assert.Equal("Manual: CPU 100% / GPU 60%", c.Status);
            c.ManualGpuPct = 70;
            c.Tick(R(42));
            Assert.Equal(2, fw.Calls.Count);
            Assert.Equal("pct:100,70", fw.Calls[1]);
        }

        [Test]
        public static void Curve_FollowsCurve()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(70));
            Assert.Equal("pct:60,60", fw.Last());
            Assert.Equal("Curva: 60%", c.Status);
        }

        [Test]
        public static void Curve_Hysteresis_NoFlapping()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(70));
            c.Tick(R(68));
            c.Tick(R(70));
            Assert.Equal(1, fw.Calls.Count);
            Assert.Equal("pct:60,60", fw.Calls[0]);
            // exactly 3 below the point that set it: lowers
            c.Tick(R(67));
            Assert.Equal(2, fw.Calls.Count);
            Assert.Equal("pct:54,54", fw.Calls[1]);
            // rising raises immediately
            c.Tick(R(72));
            Assert.Equal("pct:64,64", fw.Last());
        }

        [Test]
        public static void Hot90_Forces100_Until80()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 40;
            c.ManualGpuPct = 40;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            Assert.Equal("pct:40,40", fw.Last());
            c.Tick(R(90));
            Assert.Equal("pct:100,100", fw.Last());
            Assert.Equal("Proteção térmica: 100%", c.Status);
            c.Tick(R(85));
            c.Tick(R(80));
            Assert.Equal("pct:100,100", fw.Last());
            Assert.Equal(2, fw.Calls.Count);
            c.Tick(R(79));
            Assert.Equal("pct:40,40", fw.Last());
            Assert.Equal("Manual: CPU 40% / GPU 40%", c.Status);
        }

        [Test]
        public static void Hot90_OnGpuAlone_AndHoldsUntilBothBelow80()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 40;
            c.ManualGpuPct = 40;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 50));
            c.Tick(R(60, 91));
            Assert.Equal("pct:100,100", fw.Last());
            c.Tick(R(60, 85)); // GPU still >= 80
            Assert.Equal("pct:100,100", fw.Last());
            c.Tick(R(60, 79));
            Assert.Equal("pct:40,40", fw.Last());
        }

        [Test]
        public static void Hot90_AppliesInCurveMode()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            FanCurve flat = new FanCurve();
            flat.Points.Add(new CurvePoint(30, 30));
            flat.Points.Add(new CurvePoint(100, 100));
            c.Curve = flat;
            c.Mode = FanUserMode.Curve;
            c.Tick(R(60));
            c.Tick(R(92));
            Assert.Equal("pct:100,100", fw.Last());
            c.Tick(R(70));
            Assert.Equal("pct:70,70", fw.Last()); // curve re-evaluated from scratch after thermal exit
        }

        [Test]
        public static void ThreeNullReads_FallsBackToAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 50;
            c.ManualGpuPct = 50;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            Assert.Equal(1, fw.Calls.Count);
            c.Tick(R(null));
            c.Tick(R(null));
            Assert.Equal(FanUserMode.Manual, c.Mode);
            Assert.Equal(1, fw.Calls.Count);
            c.Tick(R(null));
            Assert.Equal("auto", fw.Last());
            Assert.Equal(2, fw.Calls.Count);
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("temperatura indisponível — automático", c.Status);
            Assert.True(!c.CurveAvailable, "curve must be unavailable after fallback");
            c.Tick(R(null));
            Assert.Equal(2, fw.Calls.Count);
            Assert.Equal("temperatura indisponível — automático", c.Status); // kept after later ticks
            c.Tick(R(50));
            Assert.Equal("temperatura indisponível — automático", c.Status);
            c.Mode = FanUserMode.Auto; // user changes mode: explanation goes away
            Assert.Equal("Automático", c.Status);
        }

        [Test]
        public static void NullReadsMustBeConsecutive()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            c.Tick(R(null));
            c.Tick(R(null));
            c.Tick(R(50));
            c.Tick(R(null));
            c.Tick(R(null));
            Assert.Equal(FanUserMode.Manual, c.Mode);
            Assert.Equal(0, fw.Count("auto"));
        }

        [Test]
        public static void ZeroNaNInfinity_CountAsUnavailable()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(0));
            c.Tick(R(double.NaN));
            c.Tick(R(double.PositiveInfinity));
            Assert.Equal("auto", fw.Last());
            Assert.Equal(FanUserMode.Auto, c.Mode);

            FakeFirmware fw2 = new FakeFirmware();
            FanController c2 = new FanController(fw2);
            c2.Tick(R(-5));
            Assert.True(!c2.CurveAvailable, "negative temp is not a valid reading");
            c2.Tick(R(0));
            Assert.True(!c2.CurveAvailable, "0 C is not a valid reading");
        }

        [Test]
        public static void InvalidTemp_NeverReachesCurve()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(70));
            int before = fw.Calls.Count;
            c.Tick(R(double.NaN, double.PositiveInfinity));
            c.Tick(R(double.NaN, double.PositiveInfinity));
            Assert.Equal(before, fw.Calls.Count);
        }

        [Test]
        public static void SetFailure_FallsBackToAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            fw.SetPercentResult = false;
            c.Tick(R(50));
            Assert.Equal("pct:30,30", fw.Calls[0]);
            Assert.Equal("auto", fw.Calls[1]);
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("falha ao controlar a ventoinha — automático", c.Status);
            c.Tick(R(50));
            Assert.Equal(2, fw.Calls.Count);
            Assert.Equal("falha ao controlar a ventoinha — automático", c.Status); // kept
        }

        [Test]
        public static void SwitchToAuto_CallsSetFanAutoOnce()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            c.Mode = FanUserMode.Auto;
            c.Tick(R(50));
            c.Tick(R(50));
            c.Mode = FanUserMode.Auto;
            Assert.Equal(1, fw.Count("auto"));
            Assert.Equal("Automático", c.Status);
        }

        [Test]
        public static void Suspend_ForcesAuto_ResumeRequiresFreshReading()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 60;
            c.ManualGpuPct = 60;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            c.OnSuspend();
            Assert.Equal("auto", fw.Last());
            Assert.Equal("suspenso — automático", c.Status);
            int n = fw.Calls.Count;
            c.Tick(R(50)); // ticks while suspended do nothing
            Assert.Equal(n, fw.Calls.Count);
            c.OnResume();
            Assert.Equal(n, fw.Calls.Count);
            Assert.Equal("aguardando leitura após retomar", c.Status);
            c.Tick(R(null));
            c.Tick(R(0));
            Assert.Equal(n, fw.Calls.Count);
            c.Tick(R(52));
            Assert.Equal(n + 1, fw.Calls.Count);
            Assert.Equal("pct:60,60", fw.Last());
            Assert.Equal(FanUserMode.Manual, c.Mode);
        }

        [Test]
        public static void Shutdown_SetsAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            c.Shutdown();
            Assert.Equal("auto", fw.Last());
            int n = fw.Calls.Count;
            c.Tick(R(50));
            Assert.Equal(n, fw.Calls.Count);
        }

        [Test]
        public static void NoCpuTemp_CurveUnavailable()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            Assert.True(!c.CurveAvailable, "unavailable before any reading");
            c.Tick(R(null, 60));
            Assert.True(!c.CurveAvailable, "GPU alone does not make curve available");
            c.Mode = FanUserMode.Curve;
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("temperatura indisponível — automático", c.Status);
            c.Tick(R(null, 60));
            foreach (string call in fw.Calls) { Assert.True(!call.StartsWith("pct:"), call); }
            Assert.Equal("temperatura indisponível — automático", c.Status);
        }

        [Test]
        public static void CurveAvailable_FlipsBackAfterRecovery()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50));
            Assert.True(c.CurveAvailable, "first valid reading enables curve");
            c.Tick(R(null));
            c.Tick(R(null));
            Assert.True(c.CurveAvailable, "two nulls are tolerated");
            c.Tick(R(null));
            Assert.True(!c.CurveAvailable, "three nulls disable curve");
            c.Tick(R(55));
            Assert.True(c.CurveAvailable, "valid reading re-enables curve");
            c.Mode = FanUserMode.Curve;
            Assert.Equal(FanUserMode.Curve, c.Mode);
        }

        [Test]
        public static void Curve_UsesMaxOfValid_GpuMissingUsesCpu()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(65, 75)); // max = 75 -> 70
            Assert.Equal("pct:70,70", fw.Last());
            FakeFirmware fw2 = new FakeFirmware();
            FanController c2 = Ready(fw2);
            c2.Mode = FanUserMode.Curve;
            c2.Tick(R(75, null)); // GPU unavailable -> CPU alone
            Assert.Equal("pct:70,70", fw2.Last());
            FakeFirmware fw3 = new FakeFirmware();
            FanController c3 = Ready(fw3);
            c3.Mode = FanUserMode.Curve;
            c3.Tick(R(75, 0)); // GPU 0 = unsupported
            Assert.Equal("pct:70,70", fw3.Last());
        }

        [Test]
        public static void CurveSetter_AlwaysHoldsNormalizedCurve()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Curve = new FanCurve(); // empty -> default
            Assert.Equal(4, c.Curve.Points.Count);
            FanCurve raw = new FanCurve();
            raw.Points.Add(new CurvePoint(80, 90));
            raw.Points.Add(new CurvePoint(40, 5));
            c.Curve = raw;
            Assert.Equal(40, c.Curve.Points[0].TempC);
            Assert.Equal(30, c.Curve.Points[0].Pct);
            c.Curve = null;
            Assert.Equal(4, c.Curve.Points.Count);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(70));
            Assert.Equal("pct:60,60", fw.Last());
        }

        [Test]
        public static void CurveRequested_WhileUnavailable_StaysAuto()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.Mode = FanUserMode.Curve;
            Assert.Equal(FanUserMode.Auto, c.Mode);
            c.Tick(R(70));
            foreach (string call in fw.Calls) { Assert.True(!call.StartsWith("pct:"), call); }
        }

        [Test]
        public static void Manual_PartialCpuDropout_KeepsProtection()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 30;
            c.ManualGpuPct = 30;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(95, 60));
            Assert.Equal("pct:100,100", fw.Last());
            c.Tick(R(null, 60));
            Assert.Equal(1, fw.Calls.Count);
            Assert.Equal("Proteção térmica: 100%", c.Status);
            c.Tick(R(95, 60));
            Assert.Equal(1, fw.Calls.Count);
            c.Tick(R(70, 60)); // both valid and < 80: released
            Assert.Equal("pct:30,30", fw.Last());
        }

        [Test]
        public static void Manual_IntermittentCpuDropout_StaysProtected()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(95, 60));
            for (int i = 0; i < 4; i++)
            {
                c.Tick(R(null, 60));
                c.Tick(R(null, 60));
                c.Tick(R(85, 60)); // CPU back but still >= 80
            }
            Assert.Equal(1, fw.Count("pct:100,100"));
            Assert.Equal(1, fw.Calls.Count);
        }

        [Test]
        public static void Manual_GpuDropoutWhileProtected_KeepsProtection()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(70, 92));
            Assert.Equal("pct:100,100", fw.Last());
            c.Tick(R(70, null)); // GPU read 0/null: cannot prove it cooled
            Assert.Equal(1, fw.Calls.Count);
            c.Tick(R(70, 70));
            Assert.Equal("pct:30,30", fw.Last());
        }

        [Test]
        public static void Curve_PartialCpuDropout_DoesNotLowerTarget()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(80, 50));
            Assert.Equal("pct:85,85", fw.Last());
            c.Tick(R(null, 50));
            Assert.Equal(1, fw.Calls.Count);
            c.Tick(R(null, 90)); // may still raise
            Assert.Equal("pct:100,100", fw.Last());
        }

        [Test]
        public static void Curve_GpuDropout_DoesNotLowerTarget()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Curve;
            c.Tick(R(50, 80));
            Assert.Equal("pct:85,85", fw.Last());
            c.Tick(R(50, null));
            Assert.Equal(1, fw.Calls.Count);
            c.Tick(R(50, 50)); // both valid again: lowers
            Assert.Equal("pct:30,30", fw.Last());
        }

        [Test]
        public static void Startup_FirstAutoTickAssertsAutoOnce()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            Assert.Equal(0, fw.Calls.Count);
            c.Tick(R(50));
            c.Tick(R(50));
            Assert.Equal(1, fw.Count("auto"));
        }

        [Test]
        public static void SetFanAutoFails_ReportsDistinctStatusAndRetries()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            FanController c = TestFans.Validated(fw);
            c.Tick(R(50));
            Assert.Equal("falha ao voltar ao automático — tentando de novo", c.Status);
            c.Tick(R(50));
            Assert.Equal(2, fw.Count("auto"));
            fw.SetAutoResult = true;
            c.Tick(R(50));
            Assert.Equal("Automático", c.Status);
            c.Tick(R(50));
            Assert.Equal(3, fw.Count("auto"));
        }

        [Test]
        public static void Resume_SuspendAutoFailed_CpuStaysNull_KeepsRetrying()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            fw.SetAutoResult = false;
            c.OnSuspend();
            c.OnResume();
            int before = fw.Count("auto");
            c.Tick(R(null));
            c.Tick(R(null));
            c.Tick(R(null));
            Assert.Equal(before + 3, fw.Count("auto"));
            Assert.Equal("falha ao voltar ao automático — tentando de novo", c.Status);
            fw.SetAutoResult = true;
            c.Tick(R(null));
            Assert.Equal("aguardando leitura após retomar", c.Status);
            int settled = fw.Count("auto");
            c.Tick(R(null));
            Assert.Equal(settled, fw.Count("auto"));
        }

        [Test]
        public static void OnResume_WithoutSuspend_ForcesAuto_AndIsIdempotent()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50));
            Assert.Equal("pct:30,30", fw.Last());
            c.OnResume();
            Assert.Equal("auto", fw.Last());
            int n = fw.Calls.Count;
            c.OnResume();
            Assert.Equal(n, fw.Calls.Count);
            c.Tick(R(null));
            Assert.Equal(n, fw.Calls.Count);
            c.Tick(R(50));
            Assert.Equal("pct:30,30", fw.Last());
        }
    }
}
