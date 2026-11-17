namespace Forja.Core
{
    public enum FanMode
    {
        Auto = 1,
        Max = 2,
        Custom = 3
    }

    // Packed integer codes of the AcerGamingFunction WMI class, confirmed by the
    // probe on this machine (see docs\firmware-codes.md).
    public static class Codes
    {
        // GetGamingSysInfo: command 0x01 (0x02 was rejected by the firmware).
        public const byte SysInfoReadSensor = 0x01;

        public const byte SensorCpuTemp = 0x01;
        public const byte SensorCpuFan = 0x02;
        public const byte SensorGpuFan = 0x06;
        public const byte SensorGpuTemp = 0x0A;

        // Get/SetGamingMiscSetting selector for the performance profile.
        public const byte MiscProfile = 0x0B;

        public const byte ProfileQuiet = 0x00;
        public const byte ProfileBalanced = 0x01;
        public const byte ProfilePerformance = 0x04;

        public const byte FanCpu = 0x01;
        public const byte FanGpu = 0x04;

        private const ulong FanBehaviorCpuBit = 0x1UL;
        private const ulong FanBehaviorGpuBit = 0x8UL;

        public static ulong SensorQuery(byte sensorId)
        {
            return SysInfoReadSensor | ((ulong)sensorId << 8);
        }

        // Status byte (bits 0-7) must be 0; value is bits 8-23.
        public static int? DecodeSensor(ulong raw)
        {
            if ((raw & 0xFF) != 0) return null;
            return (int)((raw >> 8) & 0xFFFF);
        }

        public static ulong ProfileSet(byte profile)
        {
            return MiscProfile | ((ulong)profile << 8);
        }

        public static byte? DecodeProfile(ulong raw)
        {
            if ((raw & 0xFF) != 0) return null;
            return (byte)((raw >> 8) & 0xFF);
        }

        public static ulong FanBehavior(FanMode cpu, FanMode gpu)
        {
            return FanBehaviorCpuBit | FanBehaviorGpuBit
                | ((ulong)(int)cpu << 16) | ((ulong)(int)gpu << 22);
        }

        // GetGamingFanBehavior input that asks for both fans (mask CPU | GPU).
        public const uint FanBehaviorQuery = 0x9;

        // The read layout differs from the write layout (docs\firmware-codes.md):
        // status in bits 0-7 (0 = ok), CPU mode at bits 8-13, GPU mode at bits 14-19.
        // Returns false when the status is not 0 or either mode is not Auto/Max/Custom.
        public static bool DecodeFanBehavior(ulong raw, out FanMode? cpu, out FanMode? gpu)
        {
            cpu = null;
            gpu = null;
            if ((raw & 0xFF) != 0) return false;
            int c = (int)((raw >> 8) & 0x3F);
            int g = (int)((raw >> 14) & 0x3F);
            if (c < 1 || c > 3 || g < 1 || g > 3) return false;
            cpu = (FanMode)c;
            gpu = (FanMode)g;
            return true;
        }

        // Is the "Máximo" option still in effect? null = unknown (read failed): keep the
        // current state; false = the firmware no longer reports Max on both fans.
        public static bool? MaxStillActive(bool readOk, FanMode? cpu, FanMode? gpu)
        {
            if (!readOk) return null;
            return cpu == FanMode.Max && gpu == FanMode.Max;
        }

        public const int MinFanPct = 30;

        // Unconfirmed on hardware (writes of this code were not tested yet).
        // Never asks for less than 30 %.
        public static ulong FanSpeed(byte fanId, int pct)
        {
            if (pct < MinFanPct) pct = MinFanPct;
            if (pct > 100) pct = 100;
            return fanId | ((ulong)pct << 8);
        }
    }
}
