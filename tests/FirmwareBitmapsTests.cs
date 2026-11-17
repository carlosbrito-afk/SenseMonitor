using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Decoders of the two capability bitmaps the firmware announces (GetGamingSysInfo(0) and
    // GetGamingMiscSetting(0x0A)), with this PC's real values from docs\2026-11-17-pesquisa-modelos-acer.md
    // (sensors 0x227, profiles 0x53) and invented ones.
    public static class FirmwareBitmapsTests
    {
        [Test]
        public static void Sensors_ThisPc_0x227()
        {
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0000000227000000UL);
            Assert.True(s.StatusOk, "status ok");
            Assert.Equal(0x227, s.Mask);
            Assert.True(s.CpuTemp && s.CpuFan && s.SystemTemp && s.GpuFan && s.GpuTemp, "five sensors");
            Assert.Equal(2, s.FanCount);
            Assert.True(s.Plausible, "plausible");
            Assert.Null(s.Reason);
        }

        [Test]
        public static void Sensors_IdIsBitMinusOne()
        {
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0000000227000000UL);
            Assert.True(s.HasId(0x01) && s.HasId(0x02) && s.HasId(0x03) && s.HasId(0x06) && s.HasId(0x0A), "announced ids");
            Assert.True(!s.HasId(0x04) && !s.HasId(0x05) && !s.HasId(0x07), "not announced ids");
        }

        [Test]
        public static void Sensors_OneFanLaptop()
        {
            // invented: CPU temp + CPU fan only
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0000000003000000UL);
            Assert.True(s.Plausible, "plausible");
            Assert.True(s.CpuTemp && s.CpuFan, "cpu temp and fan");
            Assert.True(!s.GpuFan && !s.GpuTemp && !s.SystemTemp, "nothing else");
            Assert.Equal(1, s.FanCount);
        }

        [Test]
        public static void Sensors_GpuFanOnly_CountsOneFan()
        {
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0000000221000000UL); // bits 0, 5, 9
            Assert.Equal(1, s.FanCount);
            Assert.True(s.GpuFan && !s.CpuFan, "gpu fan only");
        }

        [Test]
        public static void Sensors_NoAnswer_NotPlausible()
        {
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(null);
            Assert.True(!s.StatusOk && !s.Plausible, "no answer");
            Assert.True(s.Reason != null && s.Reason.Length > 0, "reason given");
            Assert.Equal(0, s.FanCount);
        }

        [Test]
        public static void Sensors_StatusNotZero_NotPlausible()
        {
            // 0x2 = invalid request (what GetGamingSysInfo answers to unknown commands)
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0000000227000002UL);
            Assert.True(!s.StatusOk && !s.Plausible, "status 2 rejected");
        }

        [Test]
        public static void Sensors_ZeroAndAllOnes_Garbage()
        {
            Assert.True(!FirmwareBitmaps.DecodeSensors(0UL).Plausible, "empty mask");
            Assert.True(!FirmwareBitmaps.DecodeSensors(0x000000FFFF000000UL).Plausible, "all sixteen bits");
        }

        [Test]
        public static void Sensors_FansWithoutAnyTemperature_Garbage()
        {
            // bits 1 and 5 only: a firmware that announces fans but no temperature is not trusted
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0000000022000000UL);
            Assert.True(!s.Plausible, "no temperature bit");
        }

        [Test]
        public static void Sensors_BitsAbove39Ignored()
        {
            SensorBitmapInfo s = FirmwareBitmaps.DecodeSensors(0x0100000227000000UL); // bit 40: battery status noise
            Assert.Equal(0x227, s.Mask);
        }

        [Test]
        public static void Profiles_ThisPc_0x53()
        {
            ProfileBitmapInfo p = FirmwareBitmaps.DecodeProfiles(0x5300UL, 1);
            Assert.True(p.StatusOk, "status ok");
            Assert.Equal(0x53, p.Mask);
            Assert.True(p.Quiet && p.Balanced && p.Performance && p.Eco, "four profiles");
            Assert.True(!p.Turbo, "Turbo is not announced on this model");
            Assert.True(p.Plausible, "plausible");
            List<PerfMode> modes = p.Modes();
            Assert.Equal(4, modes.Count);
            Assert.True(modes.Contains(PerfMode.Silencioso) && modes.Contains(PerfMode.DiaADia)
                && modes.Contains(PerfMode.Desempenho) && modes.Contains(PerfMode.Economico), "modes");
            Assert.True(!modes.Contains(PerfMode.Turbo), "no Turbo mode");
        }

        [Test]
        public static void Profiles_AllFive_0x73()
        {
            ProfileBitmapInfo p = FirmwareBitmaps.DecodeProfiles(0x7300UL, 1); // bits 0,1,4,5,6
            Assert.True(p.Plausible && p.Turbo && p.Eco, "turbo and eco announced");
            Assert.Equal(5, p.Modes().Count);
        }

        [Test]
        public static void Profiles_ModesAreInDisplayOrder()
        {
            List<PerfMode> m = FirmwareBitmaps.DecodeProfiles(0x7300UL, 1).Modes();
            Assert.Equal(PerfMode.Economico, m[0]);
            Assert.Equal(PerfMode.Silencioso, m[1]);
            Assert.Equal(PerfMode.DiaADia, m[2]);
            Assert.Equal(PerfMode.Desempenho, m[3]);
            Assert.Equal(PerfMode.Turbo, m[4]);
        }

        [Test]
        public static void Profiles_CurrentProfileNotAnnounced_Garbage()
        {
            // the kernel distrusts this bitmap on some models: a current profile that is not in it proves it wrong
            ProfileBitmapInfo p = FirmwareBitmaps.DecodeProfiles(0x5300UL, 5);
            Assert.True(!p.Plausible, "current Turbo is not in 0x53");
            Assert.True(p.Reason != null && p.Reason.Length > 0, "reason");
        }

        [Test]
        public static void Profiles_ZeroAndFF_Garbage()
        {
            Assert.True(!FirmwareBitmaps.DecodeProfiles(0x0000UL, 1).Plausible, "zero");
            Assert.True(!FirmwareBitmaps.DecodeProfiles(0xFF00UL, 1).Plausible, "0xFF");
        }

        [Test]
        public static void Profiles_StatusNotZero_Garbage()
        {
            Assert.True(!FirmwareBitmaps.DecodeProfiles(0x5301UL, 1).Plausible, "status 1");
        }

        [Test]
        public static void Profiles_NoAnswerOrNoCurrent_Garbage()
        {
            Assert.True(!FirmwareBitmaps.DecodeProfiles(null, 1).Plausible, "no answer");
            Assert.True(!FirmwareBitmaps.DecodeProfiles(0x5300UL, null).Plausible, "current profile unreadable: cannot validate");
        }

        [Test]
        public static void Profiles_WithoutBalanced_Garbage()
        {
            // the app always ends on the balanced profile: a bitmap without it cannot be trusted
            ProfileBitmapInfo p = FirmwareBitmaps.DecodeProfiles(0x1100UL, 4); // bits 0 and 4
            Assert.True(!p.Plausible, "no balanced");
        }

        [Test]
        public static void Profiles_Raw_DecodedFromValueByte()
        {
            // value is byte 1 of the answer; byte 0 is the status
            ProfileBitmapInfo p = FirmwareBitmaps.DecodeProfiles(0x0000000000005300UL, 1);
            Assert.Equal(0x53, p.Mask);
            // bits above byte 1 are not part of the bitmap
            ProfileBitmapInfo q = FirmwareBitmaps.DecodeProfiles(0x00AA000000005300UL, 1);
            Assert.Equal(0x53, q.Mask);
        }

        [Test]
        public static void Profiles_OnlyPlausibleBitmapsOfferModes()
        {
            Assert.Equal(0, FirmwareBitmaps.DecodeProfiles(0xFF00UL, 1).Modes().Count);
        }
    }

    public static class ModeCatalogTests
    {
        [Test]
        public static void CodesMatchTheResearch()
        {
            Assert.Equal((byte)0, ModeCatalog.ProfileCode(PerfMode.Silencioso));
            Assert.Equal((byte)1, ModeCatalog.ProfileCode(PerfMode.DiaADia));
            Assert.Equal((byte)4, ModeCatalog.ProfileCode(PerfMode.Desempenho));
            Assert.Equal((byte)5, ModeCatalog.ProfileCode(PerfMode.Turbo));
            Assert.Equal((byte)6, ModeCatalog.ProfileCode(PerfMode.Economico));
        }

        [Test]
        public static void FromCode_RoundTripsAndRejectsUnknown()
        {
            foreach (PerfMode m in new PerfMode[] { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho, PerfMode.Turbo, PerfMode.Economico })
            {
                PerfMode? back = ModeCatalog.FromCode(ModeCatalog.ProfileCode(m));
                Assert.True(back.HasValue && back.Value == m, "round trip " + m);
            }
            Assert.True(!ModeCatalog.FromCode(2).HasValue, "code 2 is unknown");
            Assert.True(!ModeCatalog.FromCode(3).HasValue, "code 3 is unknown");
            Assert.True(!ModeCatalog.FromCode(7).HasValue, "code 7 is unknown");
        }

        [Test]
        public static void Names_AreDistinctPtBr()
        {
            Assert.Equal("Silencioso", ModeCatalog.Name(PerfMode.Silencioso));
            Assert.Equal("Dia a dia", ModeCatalog.Name(PerfMode.DiaADia));
            Assert.Equal("Desempenho", ModeCatalog.Name(PerfMode.Desempenho));
            Assert.Equal("Econômico", ModeCatalog.Name(PerfMode.Economico));
            Assert.Equal("Turbo", ModeCatalog.Name(PerfMode.Turbo));
            Assert.Equal("Desempenho (Gaming)", ModeCatalog.CardName(PerfMode.Desempenho));
        }

        [Test]
        public static void Capability_OfMode()
        {
            Assert.Equal(Cap.ModeQuiet, ModeCatalog.CapOf(PerfMode.Silencioso));
            Assert.Equal(Cap.ModeBalanced, ModeCatalog.CapOf(PerfMode.DiaADia));
            Assert.Equal(Cap.ModePerformance, ModeCatalog.CapOf(PerfMode.Desempenho));
            Assert.Equal(Cap.ModeTurbo, ModeCatalog.CapOf(PerfMode.Turbo));
            Assert.Equal(Cap.ModeEco, ModeCatalog.CapOf(PerfMode.Economico));
            Assert.Equal(PerfMode.Turbo, ModeCatalog.ModeOf(Cap.ModeTurbo).Value);
            Assert.True(!ModeCatalog.ModeOf(Cap.Sensors).HasValue, "not a mode capability");
        }
    }

    public static class FanLayoutTests
    {
        [Test]
        public static void Both_KeepsTheConfirmedCodes()
        {
            // 0x410009 / 0x820009 / 0xC30009 are the codes confirmed on this PC and used by every project
            Assert.Equal(0x410009UL, Codes.FanBehavior(FanMode.Auto, FanMode.Auto, FanLayout.Both));
            Assert.Equal(0x820009UL, Codes.FanBehavior(FanMode.Max, FanMode.Max, FanLayout.Both));
            Assert.Equal(0xC30009UL, Codes.FanBehavior(FanMode.Custom, FanMode.Custom, FanLayout.Both));
            Assert.Equal(Codes.FanBehavior(FanMode.Auto, FanMode.Auto), Codes.FanBehavior(FanMode.Auto, FanMode.Auto, FanLayout.Both));
        }

        [Test]
        public static void CpuOnly_UsesOnlyTheCpuBit()
        {
            Assert.Equal(0x30001UL, Codes.FanBehavior(FanMode.Custom, FanMode.Custom, FanLayout.CpuOnly)); // documented CPU-only custom
            Assert.Equal(0x10001UL, Codes.FanBehavior(FanMode.Auto, FanMode.Auto, FanLayout.CpuOnly));
        }

        [Test]
        public static void GpuOnly_UsesOnlyTheGpuBit()
        {
            Assert.Equal(0xC00008UL, Codes.FanBehavior(FanMode.Custom, FanMode.Custom, FanLayout.GpuOnly)); // documented GPU-only custom
            Assert.Equal(0x400008UL, Codes.FanBehavior(FanMode.Auto, FanMode.Auto, FanLayout.GpuOnly));
        }

        [Test]
        public static void Count_AndFactory()
        {
            Assert.Equal(2, FanLayout.Both.Count);
            Assert.Equal(1, FanLayout.CpuOnly.Count);
            Assert.Equal(1, FanLayout.GpuOnly.Count);
            Assert.True(FanLayout.From(true, true) == FanLayout.Both, "both");
            Assert.True(FanLayout.From(true, false) == FanLayout.CpuOnly, "cpu only");
            Assert.True(FanLayout.From(false, true) == FanLayout.GpuOnly, "gpu only");
            Assert.True(FanLayout.From(false, false) == null, "no fan: no layout");
        }

        [Test]
        public static void SetPercent_OneFan_WritesOnlyThatFan()
        {
            List<string> calls = new List<string>();
            bool ok = FanSequence.SetPercent(delegate(string m, ulong v) { calls.Add(m + ":" + v.ToString("X")); return true; }, 60, 60, FanLayout.CpuOnly);
            Assert.True(ok, "ok");
            Assert.Equal(2, calls.Count);
            Assert.Equal("SetGamingFanBehavior:30001", calls[0]);
            Assert.Equal("SetGamingFanSpeed:3C01", calls[1]); // 60 % = 0x3C, fan id 1
        }

        [Test]
        public static void SetPercent_GpuOnly_WritesOnlyTheGpuFan()
        {
            List<string> calls = new List<string>();
            FanSequence.SetPercent(delegate(string m, ulong v) { calls.Add(m + ":" + v.ToString("X")); return true; }, 50, 70, FanLayout.GpuOnly);
            Assert.Equal(2, calls.Count);
            Assert.Equal("SetGamingFanSpeed:4604", calls[1]); // 70 % = 0x46, fan id 4
        }

        [Test]
        public static void SetPercent_Failure_ReturnsToAutoOfTheSameLayout()
        {
            List<string> calls = new List<string>();
            int n = 0;
            bool ok = FanSequence.SetPercent(delegate(string m, ulong v) { calls.Add(m + ":" + v.ToString("X")); return ++n != 2; }, 60, 60, FanLayout.CpuOnly);
            Assert.True(!ok, "failed");
            Assert.Equal("SetGamingFanBehavior:10001", calls[calls.Count - 1]); // Auto, CPU only
        }

        [Test]
        public static void SetPercent_Both_IsUnchanged()
        {
            List<string> calls = new List<string>();
            FanSequence.SetPercent(delegate(string m, ulong v) { calls.Add(m + ":" + v.ToString("X")); return true; }, 50, 40, FanLayout.Both);
            Assert.Equal(3, calls.Count);
            Assert.Equal("SetGamingFanBehavior:C30009", calls[0]);
            Assert.Equal("SetGamingFanSpeed:3201", calls[1]);
            Assert.Equal("SetGamingFanSpeed:2804", calls[2]);
        }
    }
}
