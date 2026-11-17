using System.Collections.Generic;

namespace Forja.Core
{
    // What GetGamingSysInfo(0) announces: which sensors exist. Bits 24-39 of the answer; the bit
    // id-1 marks the sensor with that id (docs\2026-11-17-pesquisa-modelos-acer.md, 1.3).
    public sealed class SensorBitmapInfo
    {
        public bool StatusOk;
        public int Mask;
        public bool CpuTemp;
        public bool CpuFan;
        public bool SystemTemp;
        public bool GpuFan;
        public bool GpuTemp;
        // Read as announced AND believable (see FirmwareBitmaps.DecodeSensors); otherwise Reason says why.
        public bool Plausible;
        public string Reason;

        public int FanCount
        {
            get { return (CpuFan ? 1 : 0) + (GpuFan ? 1 : 0); }
        }

        public bool HasId(int id)
        {
            if (id < 1 || id > 16) return false;
            return ((Mask >> (id - 1)) & 1) != 0;
        }
    }

    // What GetGamingMiscSetting(0x0A) announces: which performance profiles exist (bit N = profile
    // code N). The Linux kernel stopped trusting it in 6.17 because some firmware answers garbage.
    public sealed class ProfileBitmapInfo
    {
        public bool StatusOk;
        public int Mask;
        public bool Quiet;
        public bool Balanced;
        public bool Performance;
        public bool Turbo;
        public bool Eco;
        public bool Plausible;
        public string Reason;

        // Announced profiles in display order (lowest power first). Empty unless the bitmap is plausible.
        public List<PerfMode> Modes()
        {
            List<PerfMode> l = new List<PerfMode>();
            if (!Plausible) return l;
            if (Eco) l.Add(PerfMode.Economico);
            if (Quiet) l.Add(PerfMode.Silencioso);
            if (Balanced) l.Add(PerfMode.DiaADia);
            if (Performance) l.Add(PerfMode.Desempenho);
            if (Turbo) l.Add(PerfMode.Turbo);
            return l;
        }
    }

    // Pure decoders of the two bitmaps. Both distrust garbage: a bitmap that fails the checks is
    // marked implausible, and the capability logic then falls back to the model table plus tests.
    public static class FirmwareBitmaps
    {
        // Profile codes that exist (0 quiet, 1 balanced, 4 performance, 5 turbo, 6 eco): any other bit is not a profile.
        private const int KnownProfileBits = 0x01 | 0x02 | 0x10 | 0x20 | 0x40;

        // Sensor ids (the bit is id - 1).
        private const int IdCpuTemp = 0x01;
        private const int IdCpuFan = 0x02;
        private const int IdSystemTemp = 0x03;
        private const int IdGpuFan = 0x06;
        private const int IdGpuTemp = 0x0A;

        // raw = gmOutput of GetGamingSysInfo(0); null = the call failed.
        // Believable = status 0, a non-empty mask that is not all sixteen bits, and at least one
        // temperature sensor (a firmware that announces fans but no temperature is not trusted).
        public static SensorBitmapInfo DecodeSensors(ulong? raw)
        {
            SensorBitmapInfo s = new SensorBitmapInfo();
            if (!raw.HasValue)
            {
                s.Reason = "o firmware não respondeu à lista de sensores";
                return s;
            }
            ulong r = raw.Value;
            s.StatusOk = (r & 0xFF) == 0;
            if (!s.StatusOk)
            {
                s.Reason = "o firmware recusou a lista de sensores (status " + (r & 0xFF) + ")";
                return s;
            }
            s.Mask = (int)((r >> 24) & 0xFFFF);
            s.CpuTemp = s.HasId(IdCpuTemp);
            s.CpuFan = s.HasId(IdCpuFan);
            s.SystemTemp = s.HasId(IdSystemTemp);
            s.GpuFan = s.HasId(IdGpuFan);
            s.GpuTemp = s.HasId(IdGpuTemp);
            if (s.Mask == 0)
            {
                s.Reason = "a lista de sensores veio vazia";
            }
            else if (s.Mask == 0xFFFF)
            {
                s.Reason = "a lista de sensores veio toda ligada (dado sem sentido)";
            }
            else if (!s.CpuTemp && !s.GpuTemp)
            {
                s.Reason = "a lista de sensores não inclui nenhuma temperatura";
            }
            else
            {
                s.Plausible = true;
            }
            return s;
        }

        // raw = gmOutput of GetGamingMiscSetting(0x0A) (status byte 0, bitmap byte 1); current =
        // the profile code read from GetGamingMiscSetting(0x0B), null when unreadable.
        // Believable = status 0, mask not 0 and not 0xFF, the CURRENT profile's bit is on (so the
        // bitmap agrees with what the firmware is doing) and the balanced profile is in it (the app
        // always ends on it).
        public static ProfileBitmapInfo DecodeProfiles(ulong? raw, byte? current)
        {
            ProfileBitmapInfo p = new ProfileBitmapInfo();
            if (!raw.HasValue)
            {
                p.Reason = "o firmware não respondeu à lista de perfis";
                return p;
            }
            ulong r = raw.Value;
            p.StatusOk = (r & 0xFF) == 0;
            if (!p.StatusOk)
            {
                p.Reason = "o firmware recusou a lista de perfis (status " + (r & 0xFF) + ")";
                return p;
            }
            p.Mask = (int)((r >> 8) & 0xFF);
            p.Quiet = Bit(p.Mask, ModeCatalog.ProfileCode(PerfMode.Silencioso));
            p.Balanced = Bit(p.Mask, ModeCatalog.ProfileCode(PerfMode.DiaADia));
            p.Performance = Bit(p.Mask, ModeCatalog.ProfileCode(PerfMode.Desempenho));
            p.Turbo = Bit(p.Mask, ModeCatalog.ProfileCode(PerfMode.Turbo));
            p.Eco = Bit(p.Mask, ModeCatalog.ProfileCode(PerfMode.Economico));
            if (p.Mask == 0)
            {
                p.Reason = "a lista de perfis veio vazia";
            }
            else if (p.Mask == 0xFF)
            {
                p.Reason = "a lista de perfis veio toda ligada (dado sem sentido)";
            }
            else if ((p.Mask & ~KnownProfileBits) != 0)
            {
                p.Reason = "a lista de perfis tem bits de perfis desconhecidos (dado sem sentido)";
            }
            else if (!current.HasValue)
            {
                p.Reason = "o perfil atual não pôde ser lido para conferir a lista";
            }
            else if (!Bit(p.Mask, current.Value))
            {
                p.Reason = "o perfil atual (código " + current.Value + ") não consta na lista de perfis";
            }
            else if (!p.Balanced)
            {
                p.Reason = "a lista de perfis não inclui o equilibrado";
            }
            else
            {
                p.Plausible = true;
            }
            return p;
        }

        private static bool Bit(int mask, int index)
        {
            return index >= 0 && index < 31 && ((mask >> index) & 1) != 0;
        }
    }
}
