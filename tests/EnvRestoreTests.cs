using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Cancel / close / exit / crash of the environment test restore ONLY what the test itself changed,
    // to the state read before it (never a blind Dia a dia or both-fan Auto), and an interrupted mode test
    // is undone at the next start from a marker.
    public static class EnvRestoreTests
    {
        [Test]
        public static void NothingTouched_EmptyPlan_ZeroWrites()
        {
            EnvTouch t = new EnvTouch();
            RestorePlan p = t.Plan();
            Assert.True(p.IsEmpty, "empty");
            FakeFirmware fw = new FakeFirmware();
            RestoreResult r = RestoreExecutor.Run(p, fw);
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!r.Wrote, "no write reported");
        }

        [Test]
        public static void DetectOnly_ModeKnownButNotTouched_NoWrite()
        {
            EnvTouch t = new EnvTouch();
            t.ModeBefore = PerfMode.Desempenho; // read at detection, but the mode test never started
            Assert.True(t.Plan().IsEmpty, "knowing the mode is not touching it");
        }

        [Test]
        public static void FanTestStarted_RestoresAutoOnTheTestedLayoutOnly()
        {
            EnvTouch t = new EnvTouch();
            t.TouchFans(FanLayout.CpuOnly);
            RestorePlan p = t.Plan();
            Assert.True(p.Fans && !p.Mode, "fans only");
            Assert.True(p.Layout == FanLayout.CpuOnly, "layout kept");
            FakeFirmware fw = new FakeFirmware();
            RestoreExecutor.Run(p, fw);
            Assert.Equal("auto", string.Join("|", fw.Calls.ToArray()));
        }

        [Test]
        public static void ModeTestStarted_RestoresTheModeReadBefore_NotDiaADia()
        {
            EnvTouch t = new EnvTouch();
            t.TouchMode(PerfMode.Desempenho);
            RestorePlan p = t.Plan();
            Assert.True(p.Mode && !p.Fans, "mode only");
            Assert.Equal(PerfMode.Desempenho, p.Target);
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            RestoreExecutor.Run(p, fw);
            Assert.Equal("mode:Desempenho", string.Join("|", fw.Calls.ToArray()));
            Assert.Equal((PerfMode?)PerfMode.Desempenho, fw.ModeValue);
        }

        [Test]
        public static void ModeBeforeUnknown_FallsBackToDiaADia_OnlyBecauseTheTestTouchedIt()
        {
            EnvTouch t = new EnvTouch();
            t.TouchMode(null);
            Assert.Equal(PerfMode.DiaADia, t.Plan().Target);
        }

        [Test]
        public static void BothTouched_FansFirstThenMode()
        {
            EnvTouch t = new EnvTouch();
            t.TouchFans(FanLayout.Both);
            t.TouchMode(PerfMode.Silencioso);
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Turbo;
            RestoreExecutor.Run(t.Plan(), fw);
            Assert.Equal("auto|mode:Silencioso", string.Join("|", fw.Calls.ToArray()));
        }

        [Test]
        public static void FailedWrite_RetriedOnce()
        {
            EnvTouch t = new EnvTouch();
            t.TouchFans(FanLayout.Both);
            FakeFirmware fw = new FakeFirmware();
            fw.SetAutoResult = false;
            RestoreResult r = RestoreExecutor.Run(t.Plan(), fw);
            Assert.Equal(2, fw.Count("auto"));
            Assert.True(!r.FansOk, "reported as not confirmed");
        }

        [Test]
        public static void Plan_AfterRestore_StartsClean()
        {
            EnvTouch t = new EnvTouch();
            t.TouchFans(FanLayout.Both);
            t.Clear();
            Assert.True(t.Plan().IsEmpty, "cleared");
        }

        // ---- the mode-test marker ----

        private sealed class MemMarker : IModeMarker
        {
            public bool Present;
            public PerfMode? Value;
            public string Fp;
            public bool GarbageContent;
            public bool Exists { get { return Present; } }
            public bool Set(PerfMode before) { return Set(before, "x|y"); }
            public bool Set(PerfMode before, string fingerprint) { Present = true; Value = before; Fp = fingerprint; return true; }
            public PerfMode? Read() { return GarbageContent ? null : Value; }
            public string ReadFingerprint() { return Fp; }
            public bool Clear() { Present = false; Value = null; return true; }
        }

        [Test]
        public static void Marker_Absent_NoFirmwareCallAtAll()
        {
            FakeFirmware fw = new FakeFirmware();
            MemMarker m = new MemMarker();
            ModeRecoveryResult r = ModeRecovery.Run(fw, m, null, Ctx());
            Assert.True(!r.Needed, "nothing to do");
            Assert.Equal(0, fw.Calls.Count);
        }

        [Test]
        public static void Marker_Present_RestoresThePreTestMode_AndClears()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Turbo; // the interrupted test left it here
            MemMarker m = new MemMarker();
            m.Set(PerfMode.Silencioso);
            ModeRecoveryResult r = ModeRecovery.Run(fw, m, null, Ctx());
            Assert.True(r.Needed && r.Restored, "restored");
            Assert.Equal("mode:Silencioso", string.Join("|", fw.Calls.ToArray()));
            Assert.True(!m.Present, "marker cleared once confirmed");
        }

        [Test]
        public static void Marker_Present_AlreadyThere_NoWrite_ButCleared()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Silencioso;
            MemMarker m = new MemMarker();
            m.Set(PerfMode.Silencioso);
            ModeRecovery.Run(fw, m, null, Ctx());
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "cleared");
        }

        [Test]
        public static void Marker_WriteRefused_MarkerKept_ForTheNextStart()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Turbo;
            fw.SetModeResult = false;
            MemMarker m = new MemMarker();
            m.Set(PerfMode.DiaADia);
            ModeRecoveryResult r = ModeRecovery.Run(fw, m, null, Ctx());
            Assert.True(!r.Restored, "not restored");
            Assert.True(m.Present, "marker kept");
        }

        [Test]
        public static void Marker_Garbage_ClearedWithoutAWrite()
        {
            FakeFirmware fw = new FakeFirmware();
            MemMarker m = new MemMarker();
            m.Set(PerfMode.Silencioso);
            m.GarbageContent = true;
            ModeRecovery.Run(fw, m, null, Ctx());
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "an unreadable marker never drives a write");
        }

        [Test]
        public static void Marker_FirmwareUnavailable_KeptWithoutWrite()
        {
            ModeRecoveryResult r = ModeRecovery.Run(null, MarkerWith(PerfMode.Silencioso), null, Ctx());
            Assert.True(r.Needed && !r.Restored, "needed, not done");
        }

        private static ModeRecoveryContext Ctx()
        {
            ModeRecoveryContext c = new ModeRecoveryContext();
            c.Fingerprint = "x|y";
            c.Allowed = delegate(PerfMode m) { return true; };
            return c;
        }

        private static MemMarker MarkerWith(PerfMode p)
        {
            MemMarker m = new MemMarker();
            m.Set(p);
            return m;
        }

        [Test]
        public static void Marker_File_RoundTrips_AndGarbageReadsNull()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-modemark-" + Guid.NewGuid().ToString("N"));
            try
            {
                Forja.Platform.FileModeMarker m = new Forja.Platform.FileModeMarker(System.IO.Path.Combine(dir, "modo-teste.flag"));
                Assert.True(!m.Exists, "absent at first");
                Assert.True(m.Set(PerfMode.Desempenho, "nitro anv15-51|v1.60"), "set");
                Assert.True(m.Exists, "exists");
                Assert.Equal((PerfMode?)PerfMode.Desempenho, m.Read());
                Assert.Equal("nitro anv15-51|v1.60", m.ReadFingerprint());
                Assert.True(m.Clear(), "cleared");
                Assert.True(!m.Exists, "gone");
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "modo-teste.flag"), "lixo");
                Assert.True(m.Exists, "file present");
                Assert.True(!m.Read().HasValue, "garbage reads null");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
