using Forja.Core;

namespace Forja.Tests
{
    public static class CodesTests
    {
        [Test]
        public static void SensorQuery_Cpu_IsExpected()
        {
            // Confirmed by probe: GetGamingSysInfo(0x101) answers the CPU temperature.
            Assert.Equal(0x101UL, Codes.SensorQuery(Codes.SensorCpuTemp));
            Assert.Equal(0x201UL, Codes.SensorQuery(Codes.SensorCpuFan));
            Assert.Equal(0x601UL, Codes.SensorQuery(Codes.SensorGpuFan));
            Assert.Equal(0xA01UL, Codes.SensorQuery(Codes.SensorGpuTemp));
        }

        [Test]
        public static void DecodeSensor_ExtractsValue()
        {
            // Raw answers from docs\probe-idle.txt and docs\probe-load.txt.
            Assert.Equal(64, Codes.DecodeSensor(0x4000UL));
            Assert.Equal(2961, Codes.DecodeSensor(0xB9100UL));
            Assert.Equal(49, Codes.DecodeSensor(0x3100UL));
        }

        [Test]
        public static void Decode_ErrorStatus_ReturnsNull()
        {
            // Status 2 = invalid request (e.g. GetGamingSysInfo(0x103) in the probe).
            Assert.Null(Codes.DecodeSensor(0x2UL));
            Assert.Null(Codes.DecodeProfile(0x2UL));
        }

        [Test]
        public static void ProfileSet_Balanced_RoundTrips()
        {
            Assert.Equal(0x10BUL, Codes.ProfileSet(Codes.ProfileBalanced));
            // GetGamingMiscSetting(0x0B) read 0x100 on the balanced profile.
            Assert.Equal((byte)Codes.ProfileBalanced, Codes.DecodeProfile(0x100UL).Value);
            Assert.Equal((byte)Codes.ProfileQuiet, Codes.DecodeProfile(0x000UL).Value);
        }

        [Test]
        public static void FanBehavior_AutoAuto_IsExpected()
        {
            Assert.Equal(0x410009UL, Codes.FanBehavior(FanMode.Auto, FanMode.Auto));
            Assert.Equal(0x820009UL, Codes.FanBehavior(FanMode.Max, FanMode.Max));
        }

        [Test]
        public static void FanSpeed_Cpu50_IsExpected()
        {
            Assert.Equal(0x3201UL, Codes.FanSpeed(Codes.FanCpu, 50));
            Assert.Equal(0x6404UL, Codes.FanSpeed(Codes.FanGpu, 100));
            // never below 30 %
            Assert.Equal(0x1E01UL, Codes.FanSpeed(Codes.FanCpu, -5));
            Assert.Equal(0x1E04UL, Codes.FanSpeed(Codes.FanGpu, 0));
        }

        [Test]
        public static void DecodeFanBehavior_ReadLayout()
        {
            // Evidence: GetGamingFanBehavior(0x9) -> 0x4100 (Auto/Auto).
            FanMode? cpu;
            FanMode? gpu;
            Assert.True(Codes.DecodeFanBehavior(0x4100UL, out cpu, out gpu), "decode");
            Assert.Equal(FanMode.Auto, cpu.Value);
            Assert.Equal(FanMode.Auto, gpu.Value);
        }

        [Test]
        public static void DecodeFanBehavior_MaxAndCustom()
        {
            FanMode? cpu;
            FanMode? gpu;
            ulong raw = ((ulong)(int)FanMode.Max << 8) | ((ulong)(int)FanMode.Custom << 14);
            Assert.True(Codes.DecodeFanBehavior(raw, out cpu, out gpu), "decode");
            Assert.Equal(FanMode.Max, cpu.Value);
            Assert.Equal(FanMode.Custom, gpu.Value);
        }

        [Test]
        public static void DecodeFanBehavior_BadStatusOrUnknownMode()
        {
            FanMode? cpu;
            FanMode? gpu;
            Assert.True(!Codes.DecodeFanBehavior(0x2UL, out cpu, out gpu), "status 2");
            Assert.True(!Codes.DecodeFanBehavior(0x0UL, out cpu, out gpu), "mode 0 unknown");
            Assert.True(!Codes.DecodeFanBehavior(0x3F00UL | 0x4000UL, out cpu, out gpu), "mode 63 unknown");
        }

        [Test]
        public static void MaxStillActive_DecidesFromReadBack()
        {
            // read failed: unknown, keep the state
            Assert.Equal(null, Codes.MaxStillActive(false, null, null));
            Assert.Equal(true, Codes.MaxStillActive(true, FanMode.Max, FanMode.Max).Value);
            Assert.Equal(false, Codes.MaxStillActive(true, FanMode.Auto, FanMode.Auto).Value);
            Assert.Equal(false, Codes.MaxStillActive(true, FanMode.Max, FanMode.Auto).Value);
            Assert.Equal(false, Codes.MaxStillActive(true, FanMode.Custom, FanMode.Max).Value);
        }

        [Test]
        public static void FanBehaviorQuery_MatchesProbeInput()
        {
            Assert.Equal(0x9U, Codes.FanBehaviorQuery);
        }
    }
}
