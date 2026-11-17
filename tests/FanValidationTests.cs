using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The hardware validation routine (run once with the user present) and its pure decisions.
    public static class FanValidationTests
    {
        // Fake fans: RPM follows the last fan command ("auto", "pct:...", "max").
        private sealed class Rig
        {
            public readonly FakeFirmware Fw = new FakeFirmware();
            public int AutoRpm = 2000;
            public int PctRpm = 3000;
            public int MaxRpm = 6000;
            public double? Temp = 55;
            public Action<int> OnSleep;
            public bool MaxThrows;
            public int SleptMs;

            public Rig()
            {
                Fw.ReadingSource = delegate
                {
                    SensorReading r = new SensorReading();
                    string s = State();
                    int rpm = s == "max" ? MaxRpm : (s.StartsWith("pct") ? PctRpm : AutoRpm);
                    r.CpuFanRpm = rpm;
                    r.GpuFanRpm = rpm - 100;
                    r.CpuTempC = Temp;
                    r.GpuTempC = Temp.HasValue ? Temp.Value - 5 : (double?)null;
                    return r;
                };
            }

            public string State()
            {
                for (int i = Fw.Calls.Count - 1; i >= 0; i--)
                {
                    string c = Fw.Calls[i];
                    if (c == "auto" || c == "max" || c.StartsWith("pct")) return c;
                }
                return "auto";
            }

            public FanValidationRunner Runner()
            {
                return new FanValidationRunner(Fw,
                    delegate
                    {
                        if (MaxThrows) throw new InvalidOperationException("boom");
                        Fw.Calls.Add("max");
                        return true;
                    },
                    delegate(int ms) { SleptMs += ms; if (OnSleep != null) OnSleep(SleptMs); },
                    50);
            }
        }

        private static List<int?> L(params int[] v)
        {
            List<int?> l = new List<int?>();
            foreach (int x in v) l.Add(x);
            return l;
        }

        [Test]
        public static void Runner_FansFollowCommands_PassesAndRecordsReference()
        {
            Rig rig = new Rig();
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Passed, "passed: " + rep.Summary());
            Assert.True(!rep.Aborted, "not aborted");
            Assert.Equal(6000, rep.Cpu.Max);
            Assert.Equal(5900, rep.Gpu.Max);
            // auto (baseline) -> 50 % -> max -> auto, and nothing after the final auto
            List<string> fan = new List<string>();
            foreach (string c in rig.Fw.Calls) if (!c.StartsWith("mode")) fan.Add(c);
            Assert.Equal("auto|pct:50,50|max|auto", string.Join("|", fan.ToArray()));
            Assert.True(rig.SleptMs >= 3 * FanValidationRunner.PhaseMs, "each phase waits ~8 s: " + rig.SleptMs);

            Config cfg = new Config();
            rep.ApplyTo(cfg, new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc));
            Assert.True(cfg.FanControlValidated, "validated");
            Assert.True(cfg.ManualFanAllowed, "manual allowed");
            Assert.Equal(6000, cfg.FanMaxRpmCpu);
            Assert.Equal(5900, cfg.FanMaxRpmGpu);
            Assert.True(cfg.FanValidationUtc.StartsWith("2026-11-17"), "date: " + cfg.FanValidationUtc);
            Assert.True(cfg.FanValidationNotes.Contains("2000") && cfg.FanValidationNotes.Contains("6000"), "raw numbers kept: " + cfg.FanValidationNotes);
        }

        [Test]
        public static void Runner_CustomWriteIgnored_FailsAndEndsInAuto()
        {
            Rig rig = new Rig();
            rig.PctRpm = rig.AutoRpm; // firmware accepts the code but nothing changes
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(!rep.Passed, "must fail");
            Assert.Equal("auto", rig.Fw.Last());
            Config cfg = new Config();
            cfg.FanControlValidated = true;
            cfg.FanMaxRpmCpu = 1;
            cfg.FanMaxRpmGpu = 1;
            rep.ApplyTo(cfg, DateTime.UtcNow);
            Assert.True(!cfg.FanControlValidated, "a failed run revokes the validation");
            Assert.True(!cfg.ManualFanAllowed, "locked");
        }

        [Test]
        public static void Runner_SetPercentRefused_AbortsStraightToAuto()
        {
            Rig rig = new Rig();
            rig.Fw.SetPercentResult = false;
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Aborted && !rep.Passed, "aborted");
            Assert.True(!rig.Fw.Calls.Contains("max"), "stops at the first failure");
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Runner_HotDuringTest_AbortsToAuto()
        {
            Rig rig = new Rig();
            rig.Temp = 91;
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Aborted && !rep.Passed, "aborted when hot");
            Assert.True(rep.Reason.Contains("90"), "says why: " + rep.Reason);
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Runner_UnreadableTemperature_AbortsAndSkipsModes()
        {
            Rig rig = new Rig();
            rig.Temp = null;
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Aborted && !rep.Passed, "unreadable temperature is not 0 °C");
            Assert.True(rep.Reason.Contains("ilegível"), rep.Reason);
            Assert.True(rep.SkipModeCheck, "no mode changes after a temperature problem");
            Assert.True(!rig.Fw.Calls.Contains("pct:50,50"), "stops before any Custom write");
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Runner_HotAbort_SkipsModes_RefusalDoesNot()
        {
            Rig hot = new Rig();
            hot.Temp = 91;
            Assert.True(hot.Runner().RunFans().SkipModeCheck, "heat skips the mode check");
            Rig refused = new Rig();
            refused.Fw.SetPercentResult = false;
            Assert.True(!refused.Runner().RunFans().SkipModeCheck, "a refused write still allows the mode check");
        }

        [Test]
        public static void Runner_CancelledBeforeStart_WritesNothingButAuto()
        {
            Rig rig = new Rig();
            FanValidationRunner r = rig.Runner();
            r.Cancel();
            FanValidationReport rep = r.RunFans();
            Assert.True(rep.Aborted && rep.SkipModeCheck, "cancelled");
            foreach (string c in rig.Fw.Calls) Assert.Equal("auto", c);
        }

        [Test]
        public static void Runner_CancelledDuringCustomPhase_NoMaxWrite()
        {
            Rig rig = new Rig();
            FanValidationRunner r = rig.Runner();
            rig.OnSleep = delegate(int slept) { if (rig.State().StartsWith("pct")) r.Cancel(); };
            FanValidationReport rep = r.RunFans();
            Assert.True(rep.Aborted, "aborted");
            Assert.True(!rig.Fw.Calls.Contains("max"), "the cancel flag is checked before every write");
            Assert.Equal("auto", rig.Fw.Last());
        }
        [Test]
        public static void Runner_Exception_EndsInAuto()
        {
            Rig rig = new Rig();
            rig.MaxThrows = true;
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Aborted && !rep.Passed, "aborted");
            Assert.Equal("auto", rig.Fw.Last());
            Assert.True(rep.Reason.Contains("InvalidOperationException"), "reason: " + rep.Reason);
        }

        [Test]
        public static void Evaluate_ResponsiveFan_Passes()
        {
            FanCheck c = FanValidation.EvaluateFan("CPU", L(2000, 2010, 1990, 2000),
                L(2100, 2600, 2950, 3000, 3020, 2990, 3000, 3010), L(4000, 5000, 5900, 6000, 6000, 6010, 5990, 6000),
                L(5000, 3500, 2500, 2100, 2050, 2000, 2010, 2000), 50);
            Assert.True(c.Passed, c.Reason);
            Assert.Equal(2000, c.Baseline);
            Assert.Equal(3000, c.Pct);
            Assert.Equal(6000, c.Max);
            Assert.Equal(2000, c.AutoAgain);
        }

        [Test]
        public static void Evaluate_FiftyPercentNearAuto_IsInconclusiveAndFails()
        {
            FanCheck c = FanValidation.EvaluateFan("CPU", L(3000, 3000, 3000, 3000),
                L(3000, 3000, 3000, 3000), L(6000, 6000, 6000, 6000), L(3000, 3000, 3000, 3000), 50);
            Assert.True(!c.Passed, "cannot tell a working 50 % from an ignored one");
            Assert.True(c.Reason.Contains("inconclusivo"), c.Reason);
        }

        [Test]
        public static void Evaluate_MaxDidNotRise_Fails()
        {
            FanCheck c = FanValidation.EvaluateFan("GPU", L(2000, 2000, 2000, 2000),
                L(3000, 3000, 3000, 3000), L(3100, 3100, 3100, 3100), L(2000, 2000, 2000, 2000), 50);
            Assert.True(!c.Passed, "max must be clearly faster");
        }

        [Test]
        public static void Evaluate_NoReturnAfterAuto_Fails()
        {
            FanCheck c = FanValidation.EvaluateFan("CPU", L(2000, 2000, 2000, 2000),
                L(3000, 3000, 3000, 3000), L(6000, 6000, 6000, 6000), L(6000, 6000, 5900, 5900), 50);
            Assert.True(!c.Passed, "stuck at max after Auto");
        }

        [Test]
        public static void Evaluate_GradualSpinDownAfterAuto_Passes()
        {
            // Real run on this notebook (2026-11-17): the firmware winds the fan down slowly.
            FanCheck c = FanValidation.EvaluateFan("CPU", L(2743, 2743, 2732, 2739),
                L(4162, 4162, 4162, 4178, 4178, 4162, 4170, 4178),
                L(6475, 7619, 7461, 7486, 7461, 7461, 7486, 7461),
                L(7235, 7000, 6759, 6494, 6286, 5972, 5734, 5472), 50);
            Assert.True(c.Passed, c.Reason);
        }

        [Test]
        public static void Evaluate_SmallDipNearMaxAfterAuto_Fails()
        {
            FanCheck c = FanValidation.EvaluateFan("CPU", L(2000, 2000, 2000, 2000),
                L(3000, 3000, 3000, 3000), L(6000, 6000, 6000, 6000), L(6000, 5900, 5600, 5500), 50);
            Assert.True(!c.Passed, "a dip under 20 % is not a return to Auto");
        }

        [Test]
        public static void Evaluate_MissingRpm_Fails()
        {
            List<int?> none = new List<int?>(new int?[] { null, null, null, null });
            FanCheck c = FanValidation.EvaluateFan("CPU", none, L(3000, 3000), L(6000, 6000), L(2000, 2000), 50);
            Assert.True(!c.Passed, "no reading, no validation");
        }

        // ---- mode check ----

        [Test]
        public static void ModeCheck_AllModesReadBack_PassesAndEndsInDiaADia()
        {
            Rig rig = new Rig();
            rig.Fw.ModeValue = PerfMode.DiaADia;
            ModeCheckReport rep = rig.Runner().RunModes();
            Assert.True(rep.Passed, string.Join(" / ", rep.Lines.ToArray()));
            Assert.Equal((PerfMode?)PerfMode.DiaADia, rig.Fw.ModeValue);
            List<string> modes = new List<string>();
            foreach (string c in rig.Fw.Calls) if (c.StartsWith("mode")) modes.Add(c);
            Assert.Equal("mode:Silencioso|mode:DiaADia|mode:Desempenho|mode:DiaADia", string.Join("|", modes.ToArray()));
            Config cfg = new Config();
            rep.ApplyTo(cfg, DateTime.UtcNow);
            Assert.True(cfg.ModeCheckPassed, "recorded");
            Assert.True(!string.IsNullOrEmpty(cfg.ModeCheckNotes), "notes");
        }

        [Test]
        public static void ModeCheck_IgnoredMode_Fails_AndStillRestoresDiaADia()
        {
            Rig rig = new Rig();
            rig.Fw.ModeValue = PerfMode.DiaADia;
            rig.Fw.IgnoredModes.Add(PerfMode.Desempenho);
            ModeCheckReport rep = rig.Runner().RunModes();
            Assert.True(!rep.Passed, "Desempenho did not stick");
            Assert.Equal((PerfMode?)PerfMode.DiaADia, rig.Fw.ModeValue);
            Assert.Equal("mode:DiaADia", rig.Fw.Last());
        }

        [Test]
        public static void ModeCheck_Exception_FailsWithoutThrowing()
        {
            Rig rig = new Rig();
            rig.Fw.ThrowOnMode = true;
            ModeCheckReport rep = rig.Runner().RunModes();
            Assert.True(!rep.Passed, "failed");
        }

        [Test]
        public static void Config_ValidationFields_RoundTripAndOldFilesLoad()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forja-val-" + Guid.NewGuid().ToString("N"), "config.json");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, "{\"FanMode\":\"Auto\",\"FirmwareCurve\":true}");
                Config old = Config.Load(path);
                Assert.True(!old.FanControlValidated, "old files stay locked");
                old.FanControlValidated = true;
                old.FanMaxRpmCpu = 5800;
                old.FanMaxRpmGpu = 5600;
                old.FanValidationUtc = "2026-11-17T10:00:00Z";
                old.FanValidationNotes = "CPU 2000/3000/5800/2050";
                old.ModeCheckPassed = true;
                old.ModeCheckNotes = "ok";
                old.Save(path);
                Assert.True(!System.IO.File.ReadAllText(path).Contains("FirmwareCurve"), "dead field dropped");
                Config r = Config.Load(path);
                Assert.True(r.ManualFanAllowed, "allowed after reload");
                Assert.Equal(5600, r.FanMaxRpmGpu);
                Assert.Equal("CPU 2000/3000/5800/2050", r.FanValidationNotes);
                Assert.True(r.ModeCheckPassed, "mode check kept");
            }
            finally
            {
                try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path), true); }
                catch (Exception) { }
            }
        }
    }
}
