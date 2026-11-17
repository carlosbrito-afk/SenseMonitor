using System;
using System.IO;
using Forja.Core;

namespace Forja.Tests
{
    public static class ConfigTests
    {
        private static string TempPath()
        {
            return Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"), "config.json");
        }

        private static void Cleanup(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Test]
        public static void Load_Missing_ReturnsDefaults()
        {
            string path = TempPath();
            try
            {
                Config c = Config.Load(path);
                Assert.Equal("Auto", c.FanMode);
                Assert.Equal(4, c.Curve.Count);
                Assert.Equal(50, c.Curve[0].TempC);
                Assert.Equal(100, c.Curve[3].Pct);
                Assert.Equal("Monitor", c.LastPage);
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void SaveLoad_RoundTrips()
        {
            string path = TempPath();
            try
            {
                Config c = new Config();
                c.FanMode = "Manual";
                c.ManualCpuPct = 65;
                c.ManualGpuPct = 40;
                c.PerfWarningShown = true;
                c.LastPage = "Curve";
                c.Curve = new System.Collections.Generic.List<CurvePoint>();
                c.Curve.Add(new CurvePoint(40, 35));
                c.Curve.Add(new CurvePoint(90, 100));
                c.Save(path);

                Config r = Config.Load(path);
                Assert.Equal("Manual", r.FanMode);
                Assert.Equal(65, r.ManualCpuPct);
                Assert.Equal(40, r.ManualGpuPct);
                Assert.True(r.PerfWarningShown, "PerfWarningShown");
                Assert.Equal("Curve", r.LastPage);
                Assert.Equal(2, r.Curve.Count);
                Assert.Equal(40, r.Curve[0].TempC);
                Assert.Equal(35, r.Curve[0].Pct);
                Assert.Equal(90, r.Curve[1].TempC);
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_CorruptFile_ReturnsDefaults()
        {
            string path = TempPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{ this is not json !!");
                Config c = Config.Load(path);
                Assert.Equal(4, c.Curve.Count);
                // file was overwritten with valid defaults
                Config again = Config.Load(path);
                Assert.Equal("Auto", again.FanMode);
                Assert.True(File.ReadAllText(path).Contains("\"FanMode\""), "file should be rewritten with defaults");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void Load_PartialFile_FillsMissingMembers()
        {
            string path = TempPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\"ManualCpuPct\":70}");
                Config c = Config.Load(path);
                Assert.Equal(70, c.ManualCpuPct);
                Assert.Equal("Auto", c.FanMode);
                Assert.Equal("Monitor", c.LastPage);
                Assert.Equal(50, c.ManualGpuPct);
                Assert.Equal(4, c.Curve.Count);
            }
            finally { Cleanup(path); }
        }

        // Files written while the app still had safety stages must keep loading.
        [Test]
        public static void Load_OldStageFields_AreIgnored()
        {
            string path = TempPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\"FanMode\":\"Manual\",\"LastPage\":\"Ventoinha\",\"ManualCpuPct\":65,"
                    + "\"Stage\":2,\"StageStartedUtc\":\"2026-11-14\"}");
                Config c = Config.Load(path);
                Assert.Equal("Manual", c.FanMode);
                Assert.Equal("Ventoinha", c.LastPage);
                Assert.Equal(65, c.ManualCpuPct);
                c.Save(path);
                Assert.True(!File.ReadAllText(path).Contains("Stage"), "old stage fields dropped on save");
            }
            finally { Cleanup(path); }
        }

        [Test]
        public static void DefaultPath_IsUnderAppData()
        {
            // the suite runs with the user folders redirected to a sandbox (SandboxTests): the real shape is checked without the redirect
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SenseMonitor", "config.json");
            Assert.Equal(expected, Path.Combine(UserData.RealRoaming, "config.json"));
            Assert.Equal(Path.Combine(UserData.Roaming, "config.json"), Config.DefaultPath);
        }

        [Test]
        public static void AutoUpdate_DefaultsOn_AndSurvivesRoundTrip_AndOldFilesLoadAsOn()
        {
            string path = TempPath();
            try
            {
                Config d = Config.Load(path);
                Assert.True(d.AutoUpdate && d.UpdateBalloon, "defaults on");
                d.AutoUpdate = false;
                d.UpdateBalloon = false;
                d.Save(path);
                Config r = Config.Load(path);
                Assert.True(!r.AutoUpdate && !r.UpdateBalloon, "saved off");
                // a config written before the updater existed has neither member
                System.IO.File.WriteAllText(path, "{\"FanMode\":\"Auto\",\"LastPage\":\"Monitor\"}");
                Config old = Config.Load(path);
                Assert.True(old.AutoUpdate && old.UpdateBalloon, "old file: on");
            }
            finally { Cleanup(path); }
        }
    }
}
