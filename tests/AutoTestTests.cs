using System;
using Forja.Core;

namespace Forja.Tests
{
    // The pure decisions of --autoteste.
    public static class AutoTestTests
    {
        private static SensorReading Good()
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = 52;
            r.GpuTempC = 48;
            r.CpuFanRpm = 2400;
            r.GpuFanRpm = 2500;
            return r;
        }

        [Test]
        public static void Sensors_AllPlausible_Ok()
        {
            AutoCheck c = AutoChecks.Sensors(Good());
            Assert.Equal(AutoStatus.Ok, c.Status);
            Assert.True(c.Detail.Contains("52") && c.Detail.Contains("2400"), c.Detail);
        }

        [Test]
        public static void Sensors_AnyMissingValue_Fails_NamingIt()
        {
            SensorReading r = Good();
            r.GpuFanRpm = null;
            AutoCheck c = AutoChecks.Sensors(r);
            Assert.Equal(AutoStatus.Falhou, c.Status);
            Assert.True(c.Detail.Contains("ventoinha da GPU"), c.Detail);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Sensors(null).Status);
        }

        [Test]
        public static void Sensors_ImplausibleValues_Fail()
        {
            SensorReading r = Good();
            r.CpuTempC = 0;
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Sensors(r).Status);
            r = Good();
            r.GpuTempC = 140;
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Sensors(r).Status);
            r = Good();
            r.CpuFanRpm = 65535;
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Sensors(r).Status);
            r = Good();
            r.CpuFanRpm = 0; // a fan that is stopped at idle is plausible
            Assert.Equal(AutoStatus.Ok, AutoChecks.Sensors(r).Status);
        }

        [Test]
        public static void ModeRead_NullFails()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.ModeRead(PerfMode.DiaADia).Status);
            Assert.True(AutoChecks.ModeRead(PerfMode.Desempenho).Detail.Contains("Desempenho"), "names the mode");
            Assert.Equal(AutoStatus.Falhou, AutoChecks.ModeRead(null).Status);
        }

        [Test]
        public static void FanValidation_FromConfig()
        {
            Config c = new Config();
            Assert.Equal(AutoStatus.Pulado, AutoChecks.FanValidation(ConfigRead.Ok, c).Status);
            c.FanValidationUtc = "2026-11-16T10:00:00Z";
            Assert.Equal(AutoStatus.Falhou, AutoChecks.FanValidation(ConfigRead.Ok, c).Status);
            c.FanControlValidated = true;
            c.FanMaxRpmCpu = 5000;
            c.FanMaxRpmGpu = 5200;
            Assert.Equal(AutoStatus.Ok, AutoChecks.FanValidation(ConfigRead.Ok, c).Status);
        }

        [Test]
        public static void FanValidation_MissingConfigIsSkipped_CorruptConfigFails_NeitherIsTouched()
        {
            AutoCheck missing = AutoChecks.FanValidation(ConfigRead.Missing, null);
            Assert.Equal(AutoStatus.Pulado, missing.Status);
            Assert.True(missing.Detail.Contains("nada foi criado"), missing.Detail);
            AutoCheck corrupt = AutoChecks.FanValidation(ConfigRead.Corrupt, null);
            Assert.Equal(AutoStatus.Falhou, corrupt.Status);
            Assert.True(corrupt.Detail.Contains("não foi tocado"), corrupt.Detail);
        }

        [Test]
        public static void FanState_NeedsBothFansRead()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.FanState(true, FanMode.Auto, FanMode.Auto).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.FanState(false, null, null).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.FanState(true, FanMode.Auto, null).Status);
        }

        [Test]
        public static void Ram_NotRunByDefault_ReportsSkippedWithTheOptInFlag()
        {
            AutoCheck c = AutoChecks.RamSkipped();
            Assert.Equal(AutoStatus.Pulado, c.Status);
            Assert.True(c.Detail.Contains("--com-limpeza-ram"), c.Detail);
        }

        [Test]
        public static void MemoryRead_IsAReadOnlyMeasurementThatCanFail()
        {
            RamCleanResult r = Ram(16000, 9000, 9000, true);
            Assert.Equal(AutoStatus.Ok, AutoChecks.MemoryRead(r.Before).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.MemoryRead(new MemorySnapshot()).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.MemoryRead(null).Status);
        }

        [Test]
        public static void Ram_AnyStepRefusedOrFallback_Fails_ZeroFreedStillOk()
        {
            RamCleanResult partial = Ram(16000, 9000, 8200, true);
            partial.PurgeStatus = unchecked((int)0xC0000022);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Ram(partial, true).Status);
            RamCleanResult fallback = Ram(16000, 9000, 8200, true);
            fallback.UsedFallback = true;
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Ram(fallback, true).Status);
            Assert.Equal(AutoStatus.Ok, AutoChecks.Ram(Ram(16000, 9000, 9000, true), true).Status);
        }

        [Test]
        public static void Budget_StepLimitIsCutToWhatIsLeftOfTheNinetySeconds()
        {
            Assert.Equal(20, AutoBudget.StepLimitSec(20, 0));
            Assert.Equal(10, AutoBudget.StepLimitSec(20, 80));
            Assert.Equal(0, AutoBudget.StepLimitSec(20, 89.5));
            Assert.Equal(0, AutoBudget.StepLimitSec(20, 120));
            Assert.Equal(90, AutoBudget.TotalSec);
            AutoCheck c = AutoChecks.NoTime("Sensores");
            Assert.Equal(AutoStatus.Pulado, c.Status);
            Assert.True(c.Detail.Contains("sem tempo"), c.Detail);
        }

        [Test]
        public static void PerfJournal_Check_FailsAfterACrash_SkippedWithoutJournal()
        {
            Assert.Equal(AutoStatus.Pulado, AutoChecks.PerfJournalState(false, true, null).Status);
            Assert.Equal(AutoStatus.Ok, AutoChecks.PerfJournalState(true, true, null).Status);
            AutoCheck crash = AutoChecks.PerfJournalState(true, true, "o último teste terminou numa queda na fase \"X\"");
            Assert.Equal(AutoStatus.Falhou, crash.Status);
            Assert.True(crash.Detail.Contains("queda"), crash.Detail);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.PerfJournalState(true, false, null).Status);
        }

        [Test]
        public static void Report_Json_HasTotalsAndEveryCheck()
        {
            AutoTestReport r = new AutoTestReport();
            r.StartedUtc = new DateTime(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc);
            r.ElapsedSec = 12.5;
            r.Checks.Add(new AutoCheck("Sensores", AutoStatus.Ok, "CPU 50 \"ok\""));
            r.Checks.Add(new AutoCheck("Bateria", AutoStatus.Falhou, "sem relatório"));
            string j = r.ToJson();
            Assert.True(j.StartsWith("{") && j.EndsWith("}"), j);
            Assert.True(j.Contains("\"ok\":1") && j.Contains("\"falhou\":1") && j.Contains("\"pulado\":0"), j);
            Assert.True(j.Contains("\"name\":\"Sensores\"") && j.Contains("\"status\":\"FALHOU\""), j);
            Assert.True(j.Contains("CPU 50 \\\"ok\\\""), "escaped: " + j);
        }

        private static RamCleanResult Ram(long totalMb, long beforeUsedMb, long afterUsedMb, bool ok)
        {
            RamCleanResult r = new RamCleanResult();
            long mb = 1024L * 1024L;
            r.Before.Total = totalMb * mb;
            r.Before.Available = (totalMb - beforeUsedMb) * mb;
            r.After.Total = totalMb * mb;
            r.After.Available = (totalMb - afterUsedMb) * mb;
            r.EmptyStatus = ok ? 0 : -1;
            r.FlushStatus = ok ? 0 : -1;
            r.PurgeStatus = ok ? 0 : -1;
            return r;
        }

        [Test]
        public static void Ram_Sane_ReportsMegabytesFreed()
        {
            AutoCheck c = AutoChecks.Ram(Ram(16000, 9000, 8200, true), true);
            Assert.Equal(AutoStatus.Ok, c.Status);
            Assert.True(c.Detail.Contains("liberou 800 MB"), c.Detail);
        }

        [Test]
        public static void Ram_AllCommandsRefused_FailsWhenAdmin_SkipsWhenNot()
        {
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Ram(Ram(16000, 9000, 9000, false), true).Status);
            Assert.Equal(AutoStatus.Pulado, AutoChecks.Ram(Ram(16000, 9000, 9000, false), false).Status);
        }

        [Test]
        public static void Ram_UnreadableMeasurement_Fails()
        {
            RamCleanResult r = new RamCleanResult();
            r.EmptyStatus = 0;
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Ram(r, true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Ram(null, true).Status);
        }

        [Test]
        public static void Hook_NeedsInstallAndRemoval()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.Hook(true, true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Hook(false, true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Hook(true, false).Status);
            Assert.True(AutoChecks.Hook(true, true).Detail.Contains("nenhuma tecla"), "says no key is recorded");
        }

        private const string Exe = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";

        private static string TaskXml(string command)
        {
            return "<Task><Actions><Exec><Command>" + command + "</Command></Exec></Actions></Task>";
        }

        [Test]
        public static void Autostart_PointsAtTheInstalledCopy_Ok()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.Autostart(true, Exe, 0, TaskXml("&quot;" + Exe + "&quot;")).Status);
            Assert.Equal(AutoStatus.Ok, AutoChecks.Autostart(true, Exe, 0, TaskXml(Exe.ToLowerInvariant())).Status);
        }

        [Test]
        public static void Autostart_WrongTargetMissingTaskOrNotInstalled()
        {
            AutoCheck other = AutoChecks.Autostart(true, Exe, 0, TaskXml(@"C:\dev\bin\SenseMonitor.exe"));
            Assert.Equal(AutoStatus.Falhou, other.Status);
            Assert.True(other.Detail.Contains(@"C:\dev\bin"), other.Detail);
            AutoCheck missing = AutoChecks.Autostart(true, Exe, 1, null);
            Assert.Equal(AutoStatus.Falhou, missing.Status);
            Assert.True(missing.Detail.Contains("não existe"), missing.Detail);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Autostart(true, Exe, 0, "<Task/>").Status);
            Assert.Equal(AutoStatus.Pulado, AutoChecks.Autostart(false, Exe, -1, null).Status);
        }

        [Test]
        public static void Autostart_QueryThatCouldNotRun_IsNotReportedAsMissingTask()
        {
            AutoCheck c = AutoChecks.Autostart(true, Exe, -1, null);
            Assert.Equal(AutoStatus.Falhou, c.Status);
            Assert.True(c.Detail.Contains("não foi possível consultar") && !c.Detail.Contains("não existe"), c.Detail);
        }

        [Test]
        public static void Mutex_NeedsARealSecondCopyToBeTurnedAway()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.Mutex(true, true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Mutex(false, true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Mutex(true, false).Status);
            AutoCheck noChild = AutoChecks.Mutex(true, null);
            Assert.Equal(AutoStatus.Falhou, noChild.Status);
            Assert.True(noChild.Detail.Contains("segunda cópia"), noChild.Detail);
        }

        [Test]
        public static void Battery_Decisions()
        {
            BatteryInfo b = new BatteryInfo();
            b.DesignMwh = 50000;
            b.FullChargeMwh = 45000;
            b.CycleCount = 120;
            AutoCheck ok = AutoChecks.Battery(true, b);
            Assert.Equal(AutoStatus.Ok, ok.Status);
            Assert.True(ok.Detail.Contains("90") && ok.Detail.Contains("120"), ok.Detail);
            Assert.Equal(AutoStatus.Pulado, AutoChecks.Battery(false, null).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Battery(true, null).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Battery(true, new BatteryInfo()).Status);
        }

        [Test]
        public static void Disks_Decisions()
        {
            DiskInfo d = new DiskInfo();
            d.Name = "NVMe 1TB";
            d.HealthCode = 0;
            AutoCheck ok = AutoChecks.Disks(new DiskInfo[] { d });
            Assert.Equal(AutoStatus.Ok, ok.Status);
            Assert.True(ok.Detail.Contains("NVMe 1TB"), ok.Detail);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Disks(null).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Disks(new DiskInfo[0]).Status);
        }

        [Test]
        public static void Disks_UnhealthyFails_NoHealthDataProvesNothing()
        {
            DiskInfo good = new DiskInfo(); good.Name = "A"; good.HealthCode = 0;
            DiskInfo warn = new DiskInfo(); warn.Name = "B"; warn.HealthCode = 1;
            DiskInfo bad = new DiskInfo(); bad.Name = "C"; bad.HealthCode = 2;
            DiskInfo unknown = new DiskInfo(); unknown.Name = "D";
            AutoCheck w = AutoChecks.Disks(new DiskInfo[] { good, warn });
            Assert.Equal(AutoStatus.Falhou, w.Status);
            Assert.True(w.Detail.Contains("B"), w.Detail);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Disks(new DiskInfo[] { bad }).Status);
            AutoCheck u = AutoChecks.Disks(new DiskInfo[] { good, unknown });
            Assert.Equal(AutoStatus.Pulado, u.Status);
            Assert.True(u.Detail.Contains("D"), u.Detail);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Disks(new DiskInfo[] { unknown, bad }).Status);
        }

        [Test]
        public static void Crashes_Decisions()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.Crashes(true, 0, 90).Status);
            Assert.True(AutoChecks.Crashes(true, 2, 90).Detail.Contains("2 quedas"), "plural");
            Assert.True(AutoChecks.Crashes(true, 1, 90).Detail.Contains("1 queda "), "singular");
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Crashes(false, 0, 90).Status);
        }

        [Test]
        public static void TempLog_Decisions()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.TempLog(true, true, null, true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.TempLog(false, false, "UnauthorizedAccessException", true).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.TempLog(true, false, null, true).Status);
            AutoCheck temp = AutoChecks.TempLog(true, true, null, false);
            Assert.Equal(AutoStatus.Ok, temp.Status);
            Assert.True(temp.Detail.Contains("pasta temporária"), "says the real folder was not used: " + temp.Detail);
        }

        [Test]
        public static void Startup_Cleanup_Repair_Restore_Decisions()
        {
            Assert.Equal(AutoStatus.Ok, AutoChecks.Startup(7, null).Status);
            Assert.Equal(AutoStatus.Ok, AutoChecks.Startup(0, null).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Startup(null, "IOException").Status);

            Assert.Equal(AutoStatus.Falhou, AutoChecks.Cleanup(null).Status);
            CleanupPlan plan = new CleanupPlan();
            CleanupTarget t = new CleanupTarget();
            t.Available = true;
            t.Analyzed = true;
            t.Bytes = 300L * 1024 * 1024;
            plan.Targets.Add(t);
            AutoCheck c = AutoChecks.Cleanup(plan);
            Assert.Equal(AutoStatus.Ok, c.Status);
            Assert.True(c.Detail.Contains("300 MB") && c.Detail.Contains("nada foi apagado"), c.Detail);
            plan.Cancelled = true;
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Cleanup(plan).Status);
            Assert.Equal(AutoStatus.Falhou, AutoChecks.Cleanup(new CleanupPlan()).Status);

            Assert.Equal(AutoStatus.Ok, AutoChecks.Repair(true, true).Status);
            AutoCheck missing = AutoChecks.Repair(true, false);
            Assert.Equal(AutoStatus.Falhou, missing.Status);
            Assert.True(missing.Detail.Contains("sfc.exe"), missing.Detail);

            Assert.Equal(AutoStatus.Falhou, AutoChecks.Restore(true, 3).Status);
            Assert.Equal(AutoStatus.Ok, AutoChecks.Restore(false, 3).Status);
            Assert.Equal(AutoStatus.Pulado, AutoChecks.Restore(false, 0).Status);
            Assert.Equal(AutoStatus.Pulado, AutoChecks.Restore(null, null).Status);
        }

        [Test]
        public static void Report_TextListsEachCheckWithItsStatus()
        {
            AutoTestReport r = new AutoTestReport();
            r.StartedUtc = new DateTime(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc);
            r.ElapsedSec = 41.6;
            r.Checks.Add(new AutoCheck("Sensores", AutoStatus.Ok, "tudo certo"));
            r.Checks.Add(new AutoCheck("Bateria", AutoStatus.Falhou, "sem relatório"));
            r.Checks.Add(new AutoCheck("Ponto", AutoStatus.Pulado, "não dá"));
            string t = r.ToText();
            Assert.Equal("1 OK, 1 FALHOU, 1 PULADO", r.Summary());
            Assert.True(t.Contains("OK      Sensores — tudo certo"), t);
            Assert.True(t.Contains("FALHOU  Bateria — sem relatório"), t);
            Assert.True(t.Contains("PULADO  Ponto — não dá"), t);
            Assert.True(t.Contains("42 s"), t);
        }
    }
}
