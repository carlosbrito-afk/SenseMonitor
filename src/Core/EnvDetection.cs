using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // Raw facts of the read-only detection (filled by Platform.EnvProbe, or by a test): nothing here is
    // a decision. No serial number, UUID or user name is ever part of it.
    public sealed class DetectionRaw
    {
        public string Manufacturer;
        public string Model;
        public string SystemFamily;
        public string Bios;
        public string Cpu;
        public List<string> Gpus = new List<string>();
        public long RamBytes;

        // root\WMI AcerGamingFunction: the class exists / an instance answered a read (needs administrator).
        public bool FirmwareClass;
        public bool FirmwareAnswered;
        // Only the class (or method name) exists: nothing is called.
        public bool BatteryClass;
        public bool ActionClass;
        public bool EventClass;
        public bool KeyboardLightMethod;
        // HID VID_0CF2 PID_5130 (the ENE keyboard controller of the S series) is enumerated.
        public bool EneKb5130;

        public ulong? SensorBitmapRaw;
        public ulong? ProfileBitmapRaw;
        public byte? ProfileCode;
        public SensorReading Live = new SensorReading();
        // About five readings one second apart (EnvProbe): the plausibility of the temperatures is judged on these.
        public List<SensorReading> LiveSamples = new List<SensorReading>();
    }

    // Are the temperatures believable? Needs at least 3 readings of one temperature, all between 10 and 115 C,
    // no jump above 15 C between two consecutive readings. A garbage sensor (0, 255, wild values) never passes, so
    // it never guards a fan test.
    public sealed class PlausibilityResult
    {
        public bool Ok;
        public string Reason;
    }

    public static class SensorPlausibility
    {
        public const double Min = 10;
        public const double Max = 115;
        public const double MaxJump = 15;

        public static PlausibilityResult Check(List<SensorReading> samples)
        {
            PlausibilityResult r = new PlausibilityResult();
            if (samples == null || samples.Count < 3)
            {
                r.Reason = "poucas leituras para julgar";
                return r;
            }
            List<double> cpu = new List<double>();
            List<double> gpu = new List<double>();
            foreach (SensorReading s in samples)
            {
                if (s == null) continue;
                if (s.CpuTempC.HasValue) cpu.Add(s.CpuTempC.Value);
                if (s.GpuTempC.HasValue) gpu.Add(s.GpuTempC.Value);
            }
            if (Steady(cpu) || Steady(gpu))
            {
                r.Ok = true;
                return r;
            }
            r.Reason = "as temperaturas lidas não parecem de um sensor real";
            return r;
        }

        private static bool Steady(List<double> v)
        {
            if (v.Count < 3) return false;
            for (int i = 0; i < v.Count; i++)
            {
                if (double.IsNaN(v[i]) || v[i] < Min || v[i] > Max) return false;
                if (i > 0 && System.Math.Abs(v[i] - v[i - 1]) > MaxJump) return false;
            }
            return true;
        }
    }

    // The raw facts plus what they mean.
    public sealed class DetectionResult
    {
        public DetectionRaw Raw;
        public bool IsAcer;
        public string Code;
        public ModelEntry Table;
        public string Fingerprint;
        public SensorBitmapInfo Sensors;
        public ProfileBitmapInfo Profiles;

        // The gaming interface exists AND answered: the only case where the firmware is read or written.
        public bool HasGamingInterface
        {
            get { return Raw != null && Raw.FirmwareClass && Raw.FirmwareAnswered; }
        }

        public static DetectionResult Interpret(DetectionRaw raw)
        {
            if (raw == null) raw = new DetectionRaw();
            DetectionResult d = new DetectionResult();
            d.Raw = raw;
            d.IsAcer = !string.IsNullOrEmpty(raw.Manufacturer) && raw.Manufacturer.ToLowerInvariant().Contains("acer");
            d.Code = ModelTable.ExtractCode(raw.Model);
            d.Table = ModelTable.Find(raw.Model);
            d.Fingerprint = MachineFingerprint.Make(raw.Model, raw.Bios);
            d.Sensors = FirmwareBitmaps.DecodeSensors(raw.SensorBitmapRaw);
            d.Profiles = FirmwareBitmaps.DecodeProfiles(raw.ProfileBitmapRaw, raw.ProfileCode);
            return d;
        }
    }

    // Model + BIOS version, normalised. No serial numbers and no user name: only those two inputs exist.
    public static class MachineFingerprint
    {
        // null when either part is missing (the identity cannot be vouched for).
        public static string Make(string model, string bios)
        {
            string m = Normalize(model);
            string b = Normalize(bios);
            if (m.Length == 0 || b.Length == 0) return null;
            return m + "|" + b;
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (c < ' ' && c != '\t' && c != '\r' && c != '\n') continue; // other control characters vanish
                if (c == '|') continue; // the separator stays unambiguous
                if (char.IsWhiteSpace(c) || c < ' ')
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space)
                {
                    sb.Append(' ');
                    space = false;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // The model part only ("model|bios"): the same notebook after a BIOS update. An empty model is no identity.
        public static bool SameModel(string a, string b)
        {
            string ma = ModelPart(a);
            string mb = ModelPart(b);
            if (string.IsNullOrEmpty(ma) || string.IsNullOrEmpty(mb)) return false;
            return string.Equals(ma, mb, StringComparison.OrdinalIgnoreCase);
        }

        private static string ModelPart(string fp)
        {
            if (string.IsNullOrEmpty(fp)) return null;
            int bar = fp.IndexOf('|');
            return (bar < 0 ? fp : fp.Substring(0, bar)).Trim();
        }
    }

    public enum EnvStatus
    {
        // No profile yet: the wizard is offered.
        NeverTested,
        // The profile belongs to this machine and this BIOS.
        Fresh,
        // BIOS update, another PC, or the identity could not be read: fan and mode writes relock.
        Stale
    }

    public static class EnvCheck
    {
        public static EnvStatus Evaluate(CapabilityProfile stored, string currentFingerprint)
        {
            if (stored == null) return EnvStatus.NeverTested;
            if (!MachineFingerprint.Same(stored.Fingerprint, currentFingerprint)) return EnvStatus.Stale;
            return EnvStatus.Fresh;
        }

        public const string StaleNote = "Este notebook ou a versão da BIOS mudou desde o teste de ambiente: refaça o teste em Configurações.";
        public const string NeverNote = "O teste de ambiente ainda não foi feito: abra Configurações e rode o teste.";
    }
}
