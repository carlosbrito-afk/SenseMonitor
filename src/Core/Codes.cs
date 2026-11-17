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
        public const byte ProfileTurbo = 0x05;
        public const byte ProfileEco = 0x06;

        // Get* inputs of the read-only capability probe (docs6-11-17-pesquisa-modelos-acer.md, 4.1):
        // GetGamingSysInfo(0) = bitmap of sensors; GetGamingMiscSetting(0x0A) = bitmap of profiles.
        public const uint SysInfoSensorBitmap = 0x00;
        public const uint MiscProfileBitmap = 0x0A;

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
            return FanBehavior(cpu, gpu, FanLayout.Both);
        }

        // Only the fans the notebook has: CPU = bit 0 (mode at << 16), GPU = bit 3 (mode at << 22).
        // Both = the codes confirmed on this machine (Auto 0x410009, Max 0x820009, Custom 0xC30009).
        public static ulong FanBehavior(FanMode cpu, FanMode gpu, FanLayout layout)
        {
            if (layout == null) layout = FanLayout.Both;
            ulong v = 0;
            if (layout.Cpu) v |= FanBehaviorCpuBit | ((ulong)(int)cpu << 16);
            if (layout.Gpu) v |= FanBehaviorGpuBit | ((ulong)(int)gpu << 22);
            return v;
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

        // GetGamingFanBehavior input that asks only for the fans of the layout (never 0: the firmware answers
        // status 2). null = both, as always.
        public static uint FanBehaviorQueryFor(FanLayout layout)
        {
            if (layout == null) return FanBehaviorQuery;
            return (uint)((layout.Cpu ? FanBehaviorCpuBit : 0UL) | (layout.Gpu ? FanBehaviorGpuBit : 0UL));
        }

        // Read-back for a notebook with the given fans: only those fields are judged. The mode of a fan the
        // layout does not have mirrors the other one, so a caller that compares "both fans" keeps working.
        public static bool DecodeFanBehavior(ulong raw, FanLayout layout, out FanMode? cpu, out FanMode? gpu)
        {
            if (layout == null || layout == FanLayout.Both) return DecodeFanBehavior(raw, out cpu, out gpu);
            cpu = null;
            gpu = null;
            if ((raw & 0xFF) != 0) return false;
            int v = layout.Cpu ? (int)((raw >> 8) & 0x3F) : (int)((raw >> 14) & 0x3F);
            if (v < 1 || v > 3) return false;
            cpu = (FanMode)v;
            gpu = (FanMode)v;
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
