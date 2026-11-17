using System;
using System.Text;
using Forja.Core;

namespace Forja.Tests
{
    public static class UpdateStateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        private static UpdateVersion V(string s)
        {
            UpdateVersion v;
            Assert.True(UpdateVersion.TryParse(s, out v), s);
            return v;
        }

        [Test]
        public static void State_RoundTrips()
        {
            UpdateState s = new UpdateState();
            s.MarkChecked(T0);
            s.BlockForFailure(V("0.3.0"), T0, "assinatura inválida");
            s.BlockPermanently(V("0.4.0"), "reversão");
            UpdateState r = UpdateState.Parse(s.ToBytes());
            Assert.True(r.LastCheckUtc.HasValue && r.LastCheckUtc.Value == T0, "last check");
            Assert.True(r.IsBlocked(V("0.3.0"), T0.AddHours(1)), "timed block");
            Assert.True(r.IsBlocked(V("0.4.0"), T0.AddDays(900)), "permanent block");
            Assert.True(!r.IsBlocked(V("0.5.0"), T0), "other version");
        }

        [Test]
        public static void State_DamagedOrMissingFile_IsFresh()
        {
            Assert.True(UpdateState.Parse(null).LastCheckUtc == null, "null");
            Assert.True(UpdateState.Parse(Encoding.UTF8.GetBytes("garbage{")).LastCheckUtc == null, "garbage");
            Assert.True(UpdateState.Parse(new byte[70000]).LastCheckUtc == null, "huge");
            Assert.True(!UpdateState.Parse(Encoding.UTF8.GetBytes("{}")).IsBlocked(V("1.0.0"), T0), "empty object");
        }

        [Test]
        public static void FailureBlock_Lasts24Hours()
        {
            UpdateState s = new UpdateState();
            s.BlockForFailure(V("0.3.0"), T0, "x");
            Assert.True(s.IsBlocked(V("0.3.0"), T0.AddHours(23).AddMinutes(59)), "still blocked");
            Assert.True(!s.IsBlocked(V("0.3.0"), T0.AddHours(24).AddSeconds(1)), "free after 24 h");
        }

        [Test]
        public static void FailureBlock_NeverShortensPermanent()
        {
            UpdateState s = new UpdateState();
            s.BlockPermanently(V("0.3.0"), "reversão");
            s.BlockForFailure(V("0.3.0"), T0, "x");
            Assert.True(s.IsBlocked(V("0.3.0"), T0.AddDays(30)), "still permanent");
        }

        [Test]
        public static void PermanentBlock_UpgradesTimedBlock()
        {
            UpdateState s = new UpdateState();
            s.BlockForFailure(V("0.3.0"), T0, "x");
            s.BlockPermanently(V("0.3.0"), "reversão");
            Assert.True(s.IsBlocked(V("0.3.0"), T0.AddDays(30)), "permanent");
        }

        [Test]
        public static void UnreadableBlockEnd_FailsClosed()
        {
            UpdateState s = new UpdateState();
            UpdateBlock b = new UpdateBlock();
            b.Versao = "0.3.0";
            b.Ate = "amanhã";
            s.Bloqueios = new System.Collections.Generic.List<UpdateBlock>();
            s.Bloqueios.Add(b);
            Assert.True(s.IsBlocked(V("0.3.0"), T0), "blocked");
        }

        [Test]
        public static void Prune_DropsExpiredAndObsolete_KeepsLive()
        {
            UpdateState s = new UpdateState();
            s.BlockForFailure(V("0.3.0"), T0.AddDays(-3), "old failure");   // expired
            s.BlockForFailure(V("0.4.0"), T0, "fresh failure");             // live
            s.BlockPermanently(V("0.2.5"), "old rollback");                 // not newer than current 0.2.5
            s.BlockPermanently(V("0.6.0"), "rollback");                     // live
            s.Prune(T0, V("0.2.5"));
            Assert.True(!s.IsBlocked(V("0.3.0"), T0), "expired gone");
            Assert.True(s.IsBlocked(V("0.4.0"), T0), "fresh kept");
            Assert.True(s.IsBlocked(V("0.6.0"), T0), "permanent kept");
            Assert.Equal(2, s.Bloqueios.Count);
        }

        [Test]
        public static void TimedBlocks_AreCapped_NewestKept()
        {
            UpdateState s = new UpdateState();
            for (int i = 1; i <= 30; i++) s.BlockForFailure(new UpdateVersion(1, i, 0), T0, "r");
            Assert.Equal(UpdateState.MaxBlocks, s.Bloqueios.Count);
            Assert.True(s.IsBlocked(new UpdateVersion(1, 30, 0), T0), "newest kept");
            Assert.True(!s.IsBlocked(new UpdateVersion(1, 1, 0), T0), "oldest timed block evicted");
        }

        [Test]
        public static void PermanentBlocks_AreNeverEvicted()
        {
            UpdateState s = new UpdateState();
            for (int i = 1; i <= 5; i++) s.BlockPermanently(new UpdateVersion(2, i, 0), "reversão");
            for (int i = 1; i <= 40; i++) s.BlockForFailure(new UpdateVersion(1, i, 0), T0, "falha");
            for (int i = 1; i <= 5; i++) Assert.True(s.IsBlocked(new UpdateVersion(2, i, 0), T0.AddDays(900)), "permanent 2." + i + " survives the churn");
            Assert.True(s.Bloqueios.Count <= UpdateState.MaxBlocks, "timed blocks still capped");
        }

        [Test]
        public static void PermanentBlocks_GrowPastTheTimedCap()
        {
            UpdateState s = new UpdateState();
            for (int i = 1; i <= 25; i++) s.BlockPermanently(new UpdateVersion(3, i, 0), "r");
            Assert.Equal(25, s.Bloqueios.Count);
            s.BlockForFailure(new UpdateVersion(1, 1, 0), T0, "x");   // nothing timed to evict: not recorded, nothing lost
            Assert.Equal(25, s.Bloqueios.Count);
            Assert.True(s.IsBlocked(new UpdateVersion(3, 1, 0), T0), "oldest permanent still there");
        }

        [Test]
        public static void Schedule_FirstRunIsDue()
        {
            Assert.True(UpdateSchedule.IsDue(T0, null, null, true), "startup, never checked");
            Assert.True(UpdateSchedule.IsDue(T0, null, null, false), "periodic, never checked");
        }

        [Test]
        public static void Schedule_DailyAndStartup()
        {
            DateTime last = T0.AddHours(-5);
            Assert.True(!UpdateSchedule.IsDue(T0, last, last, true), "startup 5 h after: too soon");
            Assert.True(UpdateSchedule.IsDue(T0, T0.AddHours(-7), T0.AddHours(-7), true), "startup 7 h after");
            Assert.True(!UpdateSchedule.IsDue(T0, T0.AddHours(-23), T0.AddHours(-23), false), "periodic 23 h");
            Assert.True(UpdateSchedule.IsDue(T0, T0.AddHours(-24), T0.AddHours(-24), false), "periodic 24 h");
            Assert.True(UpdateSchedule.IsDue(T0, T0.AddDays(-10), T0.AddDays(-10), false), "overdue");
        }

        [Test]
        public static void Schedule_FailedAttemptWaitsAnHour()
        {
            DateTime check = T0.AddDays(-2);
            Assert.True(!UpdateSchedule.IsDue(T0, check, T0.AddMinutes(-30), false), "attempt 30 min ago");
            Assert.True(UpdateSchedule.IsDue(T0, check, T0.AddMinutes(-61), false), "attempt 61 min ago");
            Assert.True(!UpdateSchedule.IsDue(T0, null, T0.AddMinutes(-5), true), "never checked but just tried");
        }

        [Test]
        public static void Schedule_ClockSetBack_DoesNotFreeze()
        {
            Assert.True(UpdateSchedule.IsDue(T0, T0.AddDays(5), T0.AddDays(5), false), "last check in the future");
        }

        [Test]
        public static void Policy_ForRelease()
        {
            UpdateState s = new UpdateState();
            Assert.Equal(UpdateVerdict.Proceed, UpdatePolicy.ForRelease(V("0.2.0"), V("0.2.1"), s, T0));
            Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.ForRelease(V("0.2.0"), V("0.2.0"), s, T0));
            Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.ForRelease(V("0.2.1"), V("0.2.0"), s, T0));
            Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.ForRelease(V("0.2.0"), null, s, T0));
            s.BlockForFailure(V("0.2.1"), T0, "x");
            Assert.Equal(UpdateVerdict.Blocked, UpdatePolicy.ForRelease(V("0.2.0"), V("0.2.1"), s, T0));
            Assert.Equal(UpdateVerdict.Proceed, UpdatePolicy.ForRelease(V("0.2.0"), V("0.2.2"), s, T0));
        }

        [Test]
        public static void Policy_ForManifest_DowngradeAndOs()
        {
            UpdateState s = new UpdateState();
            UpdateManifest m = new UpdateManifest();
            m.Versao = "0.1.0";
            Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.ForManifest(V("0.2.0"), m, new Version(10, 0, 22631), s, T0));
            m.Versao = "0.2.1";
            Assert.Equal(UpdateVerdict.Proceed, UpdatePolicy.ForManifest(V("0.2.0"), m, new Version(10, 0, 22631), s, T0));
            m.MinimoWindows = "10.0.26100";
            Assert.Equal(UpdateVerdict.OsTooOld, UpdatePolicy.ForManifest(V("0.2.0"), m, new Version(10, 0, 22631), s, T0));
            Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.ForManifest(V("0.2.0"), null, new Version(10, 0), s, T0));
        }

        [Test]
        public static void IsInstalledCopy_OnlyTheProgramFilesExe()
        {
            string inst = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";
            Assert.True(UpdatePolicy.IsInstalledCopy(inst, inst), "same");
            Assert.True(UpdatePolicy.IsInstalledCopy(@"c:\program files\sensemonitor\SENSEMONITOR.EXE", inst), "case");
            Assert.True(UpdatePolicy.IsInstalledCopy(@"C:\Program Files\SenseMonitor\..\SenseMonitor\SenseMonitor.exe", inst), "dot segments");
            Assert.True(!UpdatePolicy.IsInstalledCopy(@"C:\claude\forja\bin\SenseMonitor.exe", inst), "dev build");
            Assert.True(!UpdatePolicy.IsInstalledCopy(@"C:\Program Files\SenseMonitor\atualizacao\SenseMonitor.novo.exe", inst), "staged copy");
            Assert.True(!UpdatePolicy.IsInstalledCopy(null, inst), "null");
            Assert.True(!UpdatePolicy.IsInstalledCopy(inst, ""), "empty");
        }
    }

    public static class UpdateGateTests
    {
        private static IdleFacts Idle()
        {
            IdleFacts f = new IdleFacts();
            f.HiddenForMs = 5 * 60 * 1000;
            return f;
        }

        [Test]
        public static void Idle_Applies()
        {
            Assert.Equal(GateResult.Ok, UpdateGate.Check(Idle()));
        }

        [Test]
        public static void EachBusyFact_Blocks()
        {
            IdleFacts f = Idle(); f.WindowVisible = true;
            Assert.Equal(GateResult.WindowVisible, UpdateGate.Check(f));
            f = Idle(); f.FanCustomActive = true;
            Assert.Equal(GateResult.FanCustomActive, UpdateGate.Check(f));
            f = Idle(); f.MaintenanceBusy = true;
            Assert.Equal(GateResult.MaintenanceBusy, UpdateGate.Check(f));
            f = Idle(); f.Cleaning = true;
            Assert.Equal(GateResult.Cleaning, UpdateGate.Check(f));
            f = Idle(); f.ToolRunning = true;
            Assert.Equal(GateResult.ToolRunning, UpdateGate.Check(f));
            f = Idle(); f.UpdateBusy = true;
            Assert.Equal(GateResult.UpdateBusy, UpdateGate.Check(f));
            f = Idle(); f.Exiting = true;
            Assert.Equal(GateResult.Exiting, UpdateGate.Check(f));
        }

        [Test]
        public static void RecentlyHidden_Waits()
        {
            IdleFacts f = Idle();
            f.HiddenForMs = UpdateGate.MinHiddenMs - 1;
            Assert.Equal(GateResult.HiddenTooShort, UpdateGate.Check(f));
            f.HiddenForMs = UpdateGate.MinHiddenMs;
            Assert.Equal(GateResult.Ok, UpdateGate.Check(f));
        }

        [Test]
        public static void NullFacts_NeverApply()
        {
            Assert.True(UpdateGate.Check(null) != GateResult.Ok, "null");
        }

        [Test]
        public static void EveryResult_HasAText()
        {
            foreach (GateResult r in Enum.GetValues(typeof(GateResult)))
            {
                Assert.True(!string.IsNullOrEmpty(UpdateGate.Describe(r)), r.ToString());
            }
        }
    }
}
