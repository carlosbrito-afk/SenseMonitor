using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Forja.Core
{
    public enum ConfigRead
    {
        Ok,
        Missing,
        Corrupt
    }

    [DataContract]
    public class Config
    {
        // Older files may still carry "Stage"/"StageStartedUtc" (safety stages, removed by user
        // decision): unknown members are ignored on load and dropped on the next save.
        [DataMember] public string FanMode;
        [DataMember] public List<CurvePoint> Curve;
        [DataMember] public int ManualCpuPct;
        [DataMember] public int ManualGpuPct;
        [DataMember] public bool PerfWarningShown;
        [DataMember] public string LastPage;

        // Hardware validation of fan control (SenseMonitor.exe --validar-ventoinha, run once with
        // the user present). Until it passes, only Automático is offered. Older files lack these
        // members and load as "not validated".
        [DataMember] public bool FanControlValidated;
        [DataMember] public string FanValidationUtc;
        [DataMember] public string FanValidationNotes;
        // Settled RPM at Máximo per fan, measured by the validation: the RPM-response guard's reference.
        [DataMember] public int FanMaxRpmCpu;
        [DataMember] public int FanMaxRpmGpu;
        // Mode check of the same run (Silencioso / Desempenho with read-back).
        [DataMember] public string ModeCheckUtc;
        [DataMember] public bool ModeCheckPassed;
        [DataMember] public string ModeCheckNotes;

        // Manual / Curva / Máximo are offered only after a passed validation with an RPM reference.
        public bool ManualFanAllowed
        {
            get { return FanControlValidated && FanMaxRpmCpu > 0 && FanMaxRpmGpu > 0; }
        }

        public Config()
        {
            SetDefaults();
        }

        // DataContract deserialization skips constructors, so defaults are applied here first
        // and members missing from the file keep them.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SetDefaults();
        }

        private void SetDefaults()
        {
            FanMode = "Auto";
            Curve = FanCurve.Default().Points;
            ManualCpuPct = 50;
            ManualGpuPct = 50;
            PerfWarningShown = false;
            LastPage = "Monitor";
            FanControlValidated = false;
            FanValidationUtc = null;
            FanValidationNotes = null;
            FanMaxRpmCpu = 0;
            FanMaxRpmGpu = 0;
            ModeCheckUtc = null;
            ModeCheckPassed = false;
            ModeCheckNotes = null;
        }

        // Fixes members that were present in the file but null or unusable.
        private void FixUp()
        {
            if (string.IsNullOrEmpty(FanMode)) FanMode = "Auto";
            if (string.IsNullOrEmpty(LastPage)) LastPage = "Monitor";
            if (Curve == null || Curve.Count == 0) Curve = FanCurve.Default().Points;
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SenseMonitor", "config.json");
            }
        }

        // Read-only: never creates, truncates or rewrites the file (Load does, when the file is
        // missing or unreadable). Used by the self-test, which must change nothing.
        public static ConfigRead TryLoad(string path, out Config config)
        {
            config = null;
            try
            {
                if (!File.Exists(path)) return ConfigRead.Missing;
                Config c;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    c = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(fs);
                }
                if (c == null) return ConfigRead.Corrupt;
                c.FixUp();
                config = c;
                return ConfigRead.Ok;
            }
            catch (Exception)
            {
                return ConfigRead.Corrupt;
            }
        }

        public static Config Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    Config c;
                    using (FileStream fs = File.OpenRead(path))
                    {
                        c = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(fs);
                    }
                    if (c != null)
                    {
                        c.FixUp();
                        return c;
                    }
                }
                catch (Exception)
                {
                    // corrupt or unreadable: fall through to defaults
                }
            }
            Config defaults = new Config();
            try
            {
                defaults.Save(path);
            }
            catch (Exception)
            {
                // cannot persist (read-only location); defaults still usable in memory
            }
            return defaults;
        }

        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = File.Create(path))
            {
                new DataContractJsonSerializer(typeof(Config)).WriteObject(fs, this);
            }
        }
    }
}
