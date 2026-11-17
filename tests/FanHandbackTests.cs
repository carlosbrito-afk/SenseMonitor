using System;
using Forja.Core;

namespace Forja.Tests
{
    // Handing the fans back to the firmware on exit/suspend paths where the normal write
    // can fail (COM refuses out-calls while the UI thread handles a sent message).
    public static class FanHandbackTests
    {
        private static SensorReading R(double? cpu)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            return r;
        }

        private static FanController Ready(FakeFirmware fw)
        {
            FanController c = TestFans.Validated(fw);
            c.Tick(R(40));
            fw.Calls.Clear();
            return c;
        }

        [Test]
        public static void FirmwareAutoConfirmed_UnknownUntilFirstAutoWrite()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            Assert.True(!c.FirmwareAutoConfirmed, "unknown at construction");
            c.Tick(R(40));
            Assert.True(c.FirmwareAutoConfirmed, "first Auto tick confirmed");
        }

        [Test]
        public static void NotifyFirmwareAuto_AfterFailedShutdown_Confirms()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            fw.SetAutoResult = false;
            c.Shutdown();
            Assert.True(!c.FirmwareAutoConfirmed, "normal write failed");
            Assert.Equal("falha ao voltar ao automático — tentando de novo", c.Status);
            c.NotifyFirmwareAuto();
            Assert.True(c.FirmwareAutoConfirmed, "isolated write counted");
            Assert.Equal("Automático", c.Status);
        }

        [Test]
        public static void NotifyFirmwareAuto_CountsAsAutoWrite()
        {
            // The fan page's "Máximo" reconcile watches AutoWriteCount; an emergency Auto
            // written by the isolated writer must be visible there too.
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            int before = c.AutoWriteCount;
            c.NotifyFirmwareAuto();
            Assert.Equal(before + 1, c.AutoWriteCount);
            Assert.Equal(0, fw.Calls.Count);
        }

        [Test]
        public static void NotifyFirmwareAuto_ClearsFailureWarning()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            fw.SetAutoResult = false;
            c.Shutdown();
            Assert.True(c.StatusIsWarning, "failed Auto is a warning");
            c.NotifyFirmwareAuto();
            Assert.True(!c.StatusIsWarning, "confirmed plain Auto is not a warning");
        }

        [Test]
        public static void NotifyFirmwareAuto_AfterFailedSuspend_ResumeDoesNotRewrite()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            fw.SetAutoResult = false;
            c.OnSuspend();
            Assert.True(!c.FirmwareAutoConfirmed, "suspend write failed");
            c.NotifyFirmwareAuto();
            fw.SetAutoResult = true;
            fw.Calls.Clear();
            c.OnResume();
            Assert.Equal(0, fw.Count("auto"));
            c.Tick(R(45));
            Assert.Equal(0, fw.Count("auto")); // already Auto: no redundant write
        }

        [Test]
        public static void NotifyFirmwareAuto_InManual_NextTickReappliesSpeed()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = Ready(fw);
            c.ManualCpuPct = 50;
            c.ManualGpuPct = 50;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(40));
            c.NotifyFirmwareAuto(); // someone else put the firmware in Auto
            c.Tick(R(40));
            Assert.Equal(2, fw.Count("pct:50,50"));
        }

        [Test]
        public static void Handback_NormalOk_SkipsIsolated()
        {
            int isolated = 0;
            FanHandback.Result r = FanHandback.Run(delegate { return true; }, delegate { isolated++; return true; });
            Assert.True(r.Confirmed, "confirmed");
            Assert.True(!r.UsedIsolated, "isolated not used");
            Assert.Equal(0, isolated);
        }

        [Test]
        public static void Handback_NormalFails_UsesIsolated()
        {
            FanHandback.Result r = FanHandback.Run(delegate { return false; }, delegate { return true; });
            Assert.True(r.Confirmed, "isolated confirmed");
            Assert.True(r.UsedIsolated, "isolated used");
        }

        [Test]
        public static void Handback_BothFail_NotConfirmed_SoExitRetries()
        {
            FanHandback.Result r = FanHandback.Run(delegate { return false; }, delegate { return false; });
            Assert.True(!r.Confirmed, "not confirmed");
            Assert.True(r.UsedIsolated, "isolated tried");
        }

        [Test]
        public static void Handback_ThrowingStepCountsAsFailure()
        {
            FanHandback.Result r = FanHandback.Run(delegate { throw new InvalidOperationException(); }, delegate { return true; });
            Assert.True(r.Confirmed, "isolated after a throwing normal step");
            Assert.True(r.UsedIsolated, "isolated used");
            r = FanHandback.Run(delegate { return false; }, delegate { throw new InvalidOperationException(); });
            Assert.True(!r.Confirmed, "throwing isolated step = not confirmed");
        }

        [Test]
        public static void RamResult_AllFailed()
        {
            RamCleanResult r = new RamCleanResult();
            Assert.True(r.AllFailed, "nothing ran");
            r.EmptyStatus = unchecked((int)0xC0000061);
            r.FlushStatus = unchecked((int)0xC0000061);
            r.PurgeStatus = unchecked((int)0xC0000061);
            Assert.True(r.AllFailed, "all steps refused");
            Assert.Equal("Não foi possível limpar a RAM", Display.CleanResultText(r));
            r.PurgeStatus = 0;
            Assert.True(!r.AllFailed, "one step worked");
            Assert.Equal("Nada a liberar agora", Display.CleanResultText(r)); // no snapshots -> nothing measured
        }

        [Test]
        public static void RamResult_SuccessText()
        {
            const long GiB = 1024L * 1024L * 1024L;
            RamCleanResult r = new RamCleanResult();
            r.EmptyStatus = 0;
            r.FlushStatus = 0;
            r.PurgeStatus = 0;
            r.Before.Total = 32 * GiB; r.Before.Available = 18 * GiB; r.Before.Modified = GiB;
            r.BeforePurge.Standby = 3 * GiB;
            r.After.Total = 32 * GiB; r.After.Available = 21 * GiB; r.After.Modified = GiB; r.After.Standby = 0;
            Assert.Equal("3,0 GB a menos em uso · cache em espera limpo: 3,0 GB", Display.CleanResultText(r));
        }
    }
}
