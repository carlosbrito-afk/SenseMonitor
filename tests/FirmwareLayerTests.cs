using System;
using Forja.Core;

namespace Forja.Tests
{
    // The firmware layer after the environment test: fan behavior read-back per layout, the RPM guard of a
    // one-fan notebook, the read-only probe and the guarded pass-through.
    public static class FirmwareLayerTests
    {
        private static SensorReading R(double cpuT, double gpuT, int? cpuRpm, int? gpuRpm)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpuT;
            r.GpuTempC = gpuT;
            r.CpuFanRpm = cpuRpm;
            r.GpuFanRpm = gpuRpm;
            return r;
        }

        // ---- fan behavior read-back ----

        [Test]
        public static void DecodeBehavior_Both_IsUnchanged()
        {
            FanMode? c;
            FanMode? g;
            Assert.True(Codes.DecodeFanBehavior(0x4100UL, FanLayout.Both, out c, out g), "this PC's real answer");
            Assert.Equal((FanMode?)FanMode.Auto, c);
            Assert.Equal((FanMode?)FanMode.Auto, g);
            Assert.True(!Codes.DecodeFanBehavior(0x100UL, FanLayout.Both, out c, out g), "two fans need both modes");
        }

        [Test]
        public static void DecodeBehavior_CpuOnly_ReadsOnlyTheCpuMode_AndMirrors()
        {
            FanMode? c;
            FanMode? g;
            Assert.True(Codes.DecodeFanBehavior(0x100UL, FanLayout.CpuOnly, out c, out g), "cpu auto");
            Assert.Equal((FanMode?)FanMode.Auto, c);
            Assert.Equal((FanMode?)FanMode.Auto, g); // mirrored: callers that compare both keep working
            Assert.True(Codes.DecodeFanBehavior(0x300UL, FanLayout.CpuOnly, out c, out g), "cpu custom");
            Assert.Equal((FanMode?)FanMode.Custom, g);
        }

        [Test]
        public static void DecodeBehavior_GpuOnly_ReadsTheGpuField()
        {
            FanMode? c;
            FanMode? g;
            Assert.True(Codes.DecodeFanBehavior(0x4000UL, FanLayout.GpuOnly, out c, out g), "gpu auto");
            Assert.Equal((FanMode?)FanMode.Auto, g);
            Assert.Equal((FanMode?)FanMode.Auto, c);
        }

        [Test]
        public static void DecodeBehavior_OneFan_BadStatusOrModeFails()
        {
            FanMode? c;
            FanMode? g;
            Assert.True(!Codes.DecodeFanBehavior(0x101UL, FanLayout.CpuOnly, out c, out g), "status 1");
            Assert.True(!Codes.DecodeFanBehavior(0x000UL, FanLayout.CpuOnly, out c, out g), "mode 0 is not a mode");
            Assert.True(!Codes.DecodeFanBehavior(0x400UL, FanLayout.CpuOnly, out c, out g), "mode 4 is not a mode");
        }

        [Test]
        public static void DecodeBehavior_OtherFanFieldIgnoredWhenNotInTheLayout()
        {
            FanMode? c;
            FanMode? g;
            // some firmware also fills the field of a fan the layout does not use
            Assert.True(Codes.DecodeFanBehavior(0x4100UL, FanLayout.CpuOnly, out c, out g), "extra field ignored");
        }

        [Test]
        public static void BehaviorQuery_PerLayout()
        {
            Assert.Equal(0x9U, Codes.FanBehaviorQueryFor(FanLayout.Both));
            Assert.Equal(0x1U, Codes.FanBehaviorQueryFor(FanLayout.CpuOnly));
            Assert.Equal(0x8U, Codes.FanBehaviorQueryFor(FanLayout.GpuOnly));
            Assert.Equal(Codes.FanBehaviorQuery, Codes.FanBehaviorQueryFor(null));
        }

        // ---- RPM guard on one fan ----

        [Test]
        public static void RpmGuard_OneFan_NeedsOnlyThatReference()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetFanLayout(FanLayout.CpuOnly);
            c.SetRpmReference(6000, 0);
            Assert.True(c.RpmGuardEnabled, "enabled with the CPU reference alone");
            c.SetFanLayout(FanLayout.Both);
            Assert.True(!c.RpmGuardEnabled, "two fans need both references");
        }

        [Test]
        public static void RpmGuard_OneFan_FallsBackWhenThatFanIgnoresTheWrite()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetFanLayout(FanLayout.CpuOnly);
            c.SetRpmReference(6000, 0);
            c.Tick(R(50, 45, 1800, null));
            c.ManualCpuPct = 90;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45, 1800, null));
            Assert.Equal("pct:90,30", fw.Last()); // the GPU value is ignored by a one-fan layout (WmiFirmware)
            c.Tick(R(50, 45, 1810, null));
            c.Tick(R(50, 45, 1790, null));
            c.Tick(R(50, 45, 1800, null));
            Assert.Equal(FanUserMode.Auto, c.Mode);
            Assert.Equal("Ventoinha não respondeu ao comando — automático", c.Status);
        }

        [Test]
        public static void RpmGuard_OneFan_AFollowedWriteIsNotBlockedByTheMissingGpuFan()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetFanLayout(FanLayout.CpuOnly);
            c.SetRpmReference(6000, 0);
            c.Tick(R(50, 45, 1800, null));
            c.ManualCpuPct = 90;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45, 1800, null));
            c.Tick(R(50, 45, 4000, null));
            c.Tick(R(50, 45, 5300, null));
            c.Tick(R(50, 45, 5400, null));
            c.Tick(R(50, 45, 5400, null));
            Assert.Equal(FanUserMode.Manual, c.Mode);
        }

        [Test]
        public static void RpmGuard_TwoFans_UnchangedByTheLayoutApi()
        {
            FakeFirmware fw = new FakeFirmware();
            FanController c = TestFans.Validated(fw);
            c.SetFanLayout(FanLayout.Both);
            c.SetRpmReference(6000, 6000);
            c.Tick(R(50, 45, 1800, 1700));
            c.ManualCpuPct = 90;
            c.ManualGpuPct = 90;
            c.Mode = FanUserMode.Manual;
            c.Tick(R(50, 45, 1800, 1700));
            c.Tick(R(50, 45, 1810, 1690));
            c.Tick(R(50, 45, 1790, 1700));
            c.Tick(R(50, 45, 1800, 1710));
            Assert.Equal(FanUserMode.Auto, c.Mode);
        }

        // ---- the probe is read-only and passes through the guard ----

        private sealed class ProbeFw : IFirmware, IFirmwareProbe
        {
            public int Calls;
            public bool Available { get { return true; } }
            public SensorReading ReadSensors() { return new SensorReading(); }
            public PerfMode? GetMode() { return PerfMode.DiaADia; }
            public bool SetMode(PerfMode m) { Calls++; return true; }
            public bool SetFanAuto() { Calls++; return true; }
            public bool SetFanPercent(int a, int b) { Calls++; return true; }
            public ulong? ReadSensorBitmapRaw() { return 0x227000000UL; }
            public ulong? ReadProfileBitmapRaw() { return 0x5300UL; }
            public byte? ReadProfileCode() { return 1; }
        }

        [Test]
        public static void GuardedFirmware_PassesTheProbeThrough_WithoutAWrite()
        {
            ProbeFw inner = new ProbeFw();
            GuardedFirmware g = new GuardedFirmware(inner, NullJournal.Instance, null, true, null, null);
            Assert.Equal(0x227000000UL, g.ReadSensorBitmapRaw().Value);
            Assert.Equal(0x5300UL, g.ReadProfileBitmapRaw().Value);
            Assert.Equal((byte)1, g.ReadProfileCode().Value);
            Assert.Equal(0, inner.Calls);
        }

        [Test]
        public static void GuardedFirmware_ProbeOnAFirmwareWithoutIt_IsNull()
        {
            GuardedFirmware g = new GuardedFirmware(new FakeFirmware(), NullJournal.Instance, null, false, null, null);
            Assert.True(!g.ReadSensorBitmapRaw().HasValue, "no probe support");
            Assert.True(!g.ReadProfileBitmapRaw().HasValue, "no probe support");
            Assert.True(!g.ReadProfileCode().HasValue, "no probe support");
        }

        [Test]
        public static void GuardedFirmware_ModeNames_IncludeEcoAndTurbo()
        {
            Assert.Equal("Econômico", GuardedFirmware.ModeName(PerfMode.Economico));
            Assert.Equal("Turbo", GuardedFirmware.ModeName(PerfMode.Turbo));
            Assert.Equal("Dia a dia", GuardedFirmware.ModeName(PerfMode.DiaADia));
            Assert.Equal("Desempenho", GuardedFirmware.ModeName(PerfMode.Desempenho));
            Assert.Equal("Silencioso", GuardedFirmware.ModeName(PerfMode.Silencioso));
        }

        [Test]
        public static void GuardedFirmware_EcoWriteIsJournaledLikeAnyMode()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
            GuardedFirmware g = new GuardedFirmware(fw, new ListJournal(lines), null, false, null, null);
            Assert.True(g.SetMode(PerfMode.Economico), "ok");
            Assert.True(lines.Exists(delegate(string s) { return s.Contains("Econômico"); }), "journaled by name: " + string.Join(" | ", lines.ToArray()));
        }

        [Test]
        public static void GuardedFirmware_ReadOnly_BlocksEcoAndTurbo()
        {
            FakeFirmware fw = new FakeFirmware();
            System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
            GuardedFirmware g = new GuardedFirmware(fw, new ListJournal(lines), null, true, null, null);
            Assert.True(!g.SetMode(PerfMode.Economico), "blocked");
            Assert.True(!g.SetMode(PerfMode.Turbo), "blocked");
            Assert.Equal(0, fw.Calls.Count);
        }

        private sealed class ListJournal : IJournal
        {
            private readonly System.Collections.Generic.List<string> lines;
            public ListJournal(System.Collections.Generic.List<string> lines) { this.lines = lines; }
            public void Write(string kind, string text) { lines.Add(kind + " " + text); }
        }
    }
}
