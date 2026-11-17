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

        // Result of the environment test (the "Teste de ambiente" wizard): what this notebook can do, for
        // the model + BIOS in Env.Fingerprint. null = never run (an older config migrates, see EnvMigration).
        [DataMember] public CapabilityProfile Env;

        // Set at start when Env belongs to another notebook or BIOS (or the identity could not be read):
        // fan and mode writes stay locked until the wizard runs again. Not saved.
        public bool EnvStale;

        // The one-time "some tests are still pending" reminder was shown for the current profile.
        [DataMember] public bool EnvReminderShown;

        // Acer services the wizard stopped and disabled on the user's click, with what each was before,
        // so Settings can undo it. Empty = nothing was changed.
        [DataMember] public List<ServiceUndo> ServiceUndo;

        // Signed automatic updates (Configurações > Atualizações). Older files lack these and load as "on".
        [DataMember] public bool AutoUpdate;
        // Tray balloon "SenseMonitor atualizado para x.y.z" after a successful update.
        [DataMember] public bool UpdateBalloon;
        // Diagnostic read-only mode: the app never writes to the firmware (no mode change, no fan
        // control, no startup write) and does not run Limpar RAM; sensors are still read. Takes
        // effect at the next start. The --somente-leitura switch turns it on for one run.
        [DataMember] public bool DiagnosticReadOnly;

        // How the last Load went when something unusual happened (restored from the backup, a
        // corrupt file kept aside); null when the main file was read as it was. Not saved.
        public string LoadNote;

        // Manual / Curva / Máximo are offered only after a passed validation with an RPM reference for
        // every fan this notebook has, and never while the environment profile is stale or says the fans
        // did not follow the test.
        public bool ManualFanAllowed
        {
            get
            {
                if (!FanControlValidated || EnvStale) return false;
                if (Env == null) return FanMaxRpmCpu > 0 && FanMaxRpmGpu > 0;
                if (!Env.Allows(Cap.FanControl)) return false;
                FanLayout l = Env.Layout;
                if (l == null) return false;
                return (!l.Cpu || FanMaxRpmCpu > 0) && (!l.Gpu || FanMaxRpmGpu > 0);
            }
        }

        // The fans the firmware commands address (both until an environment profile says otherwise).
        public FanLayout EnvFanLayout
        {
            get { return Env == null || EnvStale ? FanLayout.Both : (Env.Layout ?? FanLayout.Both); }
        }

        // A mode may be written only when this notebook's profile confirmed it and the profile is current.
        public bool ModeAllowed(PerfMode m)
        {
            return !EnvStale && Env != null && Env.AllowsMode(m);
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
            AutoUpdate = true;
            UpdateBalloon = true;
            DiagnosticReadOnly = false;
            Env = null;
            EnvReminderShown = false;
            ServiceUndo = new List<ServiceUndo>();
        }

        // Fixes members that were present in the file but null or unusable.
        private void FixUp()
        {
            if (string.IsNullOrEmpty(FanMode)) FanMode = "Auto";
            if (string.IsNullOrEmpty(LastPage)) LastPage = "Monitor";
            if (Curve == null || Curve.Count == 0) Curve = FanCurve.Default().Points;
            if (ServiceUndo == null) ServiceUndo = new List<ServiceUndo>();
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(UserData.Roaming, "config.json");
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

        public const string BackupSuffix = ".bak";
        public const string TempSuffix = ".tmp";
        public const string CorruptSuffix = ".corrompido";

        // Test seam: runs after the temp file was written and before it replaces the real one.
        internal static Action<string> SaveHook = null;

        // Reads the main file; if it is missing or invalid, the backup (config.json.bak). An invalid
        // main file is never overwritten silently: it is moved to config.json.corrompido (a numbered
        // name when that one is taken). Falls back to defaults only when no readable copy exists.
        public static Config Load(string path)
        {
            bool mainExists = File.Exists(path);
            string note = null;
            if (mainExists)
            {
                Config c = TryRead(path);
                if (c != null) return c;
                string kept = Quarantine(path);
                note = "config.json inválido" + (kept != null ? " (guardado como " + Path.GetFileName(kept) + ")" : "");
            }
            string bak = path + BackupSuffix;
            if (File.Exists(bak))
            {
                Config b = TryRead(bak);
                if (b != null)
                {
                    try { File.Copy(bak, path, true); }
                    catch (Exception) { }
                    b.LoadNote = (note ?? "config.json ausente") + "; restaurado do backup";
                    return b;
                }
            }
            Config defaults = new Config();
            if (note != null) defaults.LoadNote = note + "; sem backup válido, valores padrão";
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

        // null when the file cannot be read or is not a valid Config.
        private static Config TryRead(string path)
        {
            try
            {
                Config c;
                using (FileStream fs = File.OpenRead(path))
                {
                    c = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(fs);
                }
                if (c == null) return null;
                c.FixUp();
                return c;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Moves an invalid file aside; returns where it went (null when it could not be moved).
        private static string Quarantine(string path)
        {
            string target = path + CorruptSuffix;
            for (int i = 2; File.Exists(target) && i <= 20; i++) target = path + CorruptSuffix + "." + i;
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(path, target);
                return target;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Atomic: serialize in memory, write config.json.tmp through to disk, then File.Replace it
        // over config.json keeping the previous version as config.json.bak. A failure at any point
        // leaves the old config.json untouched. An existing config.json that is already invalid is
        // moved aside as config.json.corrompido instead of becoming the backup.
        private static readonly object saveLock = new object();

        public void Save(string path)
        {
            lock (saveLock) { SaveLocked(path); } // two saves in one process never share the temp file
        }

        private void SaveLocked(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Config)).WriteObject(ms, this);
                data = ms.ToArray();
            }
            string tmp = path + TempSuffix;
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (SaveHook != null) SaveHook(tmp);
            if (!File.Exists(path))
            {
                File.Move(tmp, path);
            }
            else if (TryRead(path) == null)
            {
                Quarantine(path);
                File.Move(tmp, path);
            }
            else
            {
                File.Replace(tmp, path, path + BackupSuffix);
            }
        }
    }
}
