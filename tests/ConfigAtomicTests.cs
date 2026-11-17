using System;
using System.Collections.Generic;
using System.IO;
using Forja.Core;

namespace Forja.Tests
{
    // B05: the config is never half-written, the previous version is kept as config.json.bak, an
    // invalid file is never overwritten silently and the backup is used when the main file is bad.
    public static class ConfigAtomicTests
    {
        private static string TempPath()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return Path.Combine(d, "config.json");
        }

        private static void Cleanup(string path)
        {
            Config.SaveHook = null;
            string dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        private static Config Validated()
        {
            Config c = new Config();
            c.FanControlValidated = true;
            c.FanMaxRpmCpu = 5200;
            c.FanMaxRpmGpu = 5400;
            c.ManualCpuPct = 61;
            return c;
        }

        [Test]
        public static void Save_FirstTime_LeavesNoTempAndNoBackup()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Assert.True(File.Exists(path), "config.json");
                Assert.True(!File.Exists(path + ".tmp"), "no temp left");
                Assert.True(!File.Exists(path + ".bak"), "nothing to back up yet");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Save_SecondTime_KeepsThePreviousVersionAsBackup()
        {
            string path = TempPath();
            try
            {
                Config a = Validated();
                a.ManualCpuPct = 40;
                a.Save(path);
                Config b = Validated();
                b.ManualCpuPct = 77;
                b.Save(path);
                Assert.Equal(77, Config.Load(path).ManualCpuPct);
                Assert.True(File.Exists(path + ".bak"), "backup");
                Config bak = Config.Load(path + ".bak");
                Assert.Equal(40, bak.ManualCpuPct);
                Assert.True(!File.Exists(path + ".tmp"), "no temp left");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Save_CutInTheMiddle_KeepsThePreviousFileReadable()
        {
            string path = TempPath();
            try
            {
                Config a = Validated();
                a.ManualCpuPct = 40;
                a.Save(path);
                // the process dies after the temp file was written, before the replace
                Config.SaveHook = delegate(string tmp) { throw new IOException("simulated power loss"); };
                Config b = Validated();
                b.ManualCpuPct = 99;
                Assert.Throws<IOException>(delegate { b.Save(path); });
                Config.SaveHook = null;
                Config r = Config.Load(path);
                Assert.Equal(40, r.ManualCpuPct);
                Assert.True(r.FanControlValidated, "validation kept");
                Assert.Null(r.LoadNote);
                // and the next save works over the leftover temp file
                b.Save(path);
                Assert.Equal(99, Config.Load(path).ManualCpuPct);
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_TruncatedMain_FallsBackToTheBackup_WithoutLosingValidation()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Validated().Save(path); // now there is a .bak
                string json = File.ReadAllText(path);
                File.WriteAllText(path, json.Substring(0, json.Length / 2)); // torn write
                Config r = Config.Load(path);
                Assert.True(r.FanControlValidated, "FanControlValidated survives");
                Assert.Equal(5200, r.FanMaxRpmCpu);
                Assert.True(r.LoadNote != null && r.LoadNote.Contains("backup"), "note: " + r.LoadNote);
                // the main file was restored from the backup
                Assert.True(Config.Load(path).FanControlValidated, "main readable again");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_CorruptMain_IsKeptAside_NeverOverwritten()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Validated().Save(path);
                File.WriteAllText(path, "{ isto nao e json");
                Config.Load(path);
                Assert.True(File.Exists(path + ".corrompido"), "kept as config.json.corrompido");
                Assert.Equal("{ isto nao e json", File.ReadAllText(path + ".corrompido"));
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_SecondCorruption_DoesNotReplaceTheFirstKeptFile()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Validated().Save(path);
                File.WriteAllText(path, "primeiro");
                Config.Load(path);
                File.WriteAllText(path, "segundo");
                Config.Load(path);
                Assert.Equal("primeiro", File.ReadAllText(path + ".corrompido"));
                Assert.Equal("segundo", File.ReadAllText(path + ".corrompido.2"));
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_CorruptMainAndNoBackup_UsesDefaults_AndKeepsTheBadFile()
        {
            string path = TempPath();
            try
            {
                File.WriteAllText(path, "lixo");
                Config r = Config.Load(path);
                Assert.True(!r.FanControlValidated, "defaults");
                Assert.True(r.LoadNote != null && r.LoadNote.Contains("padrão"), "note: " + r.LoadNote);
                Assert.Equal("lixo", File.ReadAllText(path + ".corrompido"));
                Assert.True(Config.Load(path).LoadNote == null, "a fresh valid file was written");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_MainMissingBackupPresent_RestoresIt()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Validated().Save(path);
                File.Delete(path);
                Config r = Config.Load(path);
                Assert.True(r.FanControlValidated, "from the backup");
                Assert.True(File.Exists(path), "main recreated");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_CorruptBackupToo_UsesDefaults()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Validated().Save(path);
                File.WriteAllText(path, "x");
                File.WriteAllText(path + ".bak", "y");
                Config r = Config.Load(path);
                Assert.True(!r.FanControlValidated, "nothing readable: defaults");
                Assert.True(File.Exists(path + ".corrompido"), "main kept aside");
                Assert.Equal("y", File.ReadAllText(path + ".bak"));
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Save_OverAnAlreadyCorruptMain_KeepsItAsideInsteadOfMakingItTheBackup()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                File.WriteAllText(path, "quebrado");
                Config.SaveHook = null;
                Validated().Save(path);
                Assert.Equal("quebrado", File.ReadAllText(path + ".corrompido"));
                Assert.True(!File.Exists(path + ".bak"), "garbage is not a backup");
                Assert.True(Config.Load(path).FanControlValidated, "new file valid");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_UnusualRead_ReportsNothingWhenTheMainFileIsFine()
        {
            string path = TempPath();
            try
            {
                Validated().Save(path);
                Assert.Null(Config.Load(path).LoadNote);
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void DiagnosticReadOnly_DefaultsOffAndRoundTrips()
        {
            string path = TempPath();
            try
            {
                Assert.True(!new Config().DiagnosticReadOnly, "off by default");
                Config c = new Config();
                c.DiagnosticReadOnly = true;
                c.Save(path);
                Assert.True(Config.Load(path).DiagnosticReadOnly, "saved");
                // an older file without the member loads as off
                File.WriteAllText(path, "{\"FanMode\":\"Auto\"}");
                Assert.True(!Config.Load(path).DiagnosticReadOnly, "missing member: off");
            }
            finally { Cleanup(path); }
        }
    }
}
