using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The wizard's fan test (the existing validation routine adapted to 1 or 2 fans, with progress) and
    // its mode test (every announced profile with read-back, always ending on Dia a dia).
    public static class EnvRunnersTests
    {
        // Fake fans: RPM follows the last fan command ("auto", "pct:...", "max"); one fan reads no GPU RPM.
        private sealed class Rig
        {
            public readonly FakeFirmware Fw = new FakeFirmware();
            public int AutoRpm = 2000;
            public int PctRpm = 3000;
            public int MaxRpm = 6000;
            public double? Temp = 55;
            public double? GpuTemp = 50;
            public bool GpuFan = true;
            public int SleptMs;

            public Rig()
            {
                Fw.ReadingSource = delegate
                {
                    SensorReading r = new SensorReading();
                    string s = State();
                    int rpm = s == "max" ? MaxRpm : (s.StartsWith("pct") ? PctRpm : AutoRpm);
                    r.CpuFanRpm = rpm;
                    r.GpuFanRpm = GpuFan ? rpm - 100 : (int?)null;
                    r.CpuTempC = Temp;
                    r.GpuTempC = GpuTemp;
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

            public FanValidationRunner Runner(FanLayout layout)
            {
                return new FanValidationRunner(Fw,
                    delegate { Fw.Calls.Add("max"); return true; },
                    delegate(int ms) { SleptMs += ms; },
                    50, layout);
            }
        }

        // ---- fan test with a layout ----

        [Test]
        public static void OneFan_OnlyTheCpuFanIsJudged()
        {
            Rig rig = new Rig();
            rig.GpuFan = false;
            rig.GpuTemp = null; // a one-fan notebook with no GPU temperature
            FanValidationReport rep = rig.Runner(FanLayout.CpuOnly).RunFans();
            Assert.True(rep.Passed, rep.Summary());
            Assert.True(rep.Cpu != null && rep.Cpu.Passed, "cpu fan judged");
            Assert.True(rep.Gpu == null, "no GPU fan to judge");
            Assert.Equal(6000, rep.Cpu.Max);
        }

        [Test]
        public static void OneFan_FailureOfThatFanFails()
        {
            Rig rig = new Rig();
            rig.GpuFan = false;
            rig.GpuTemp = null;
            rig.PctRpm = rig.AutoRpm;
            FanValidationReport rep = rig.Runner(FanLayout.CpuOnly).RunFans();
            Assert.True(!rep.Passed, "failed");
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void OneFan_ApplyToConfig_ZeroReferenceForTheMissingFan()
        {
            Rig rig = new Rig();
            rig.GpuFan = false;
            rig.GpuTemp = null;
            FanValidationReport rep = rig.Runner(FanLayout.CpuOnly).RunFans();
            Config c = new Config();
            rep.ApplyTo(c, DateTime.UtcNow);
            Assert.True(c.FanControlValidated, "validated");
            Assert.Equal(6000, c.FanMaxRpmCpu);
            Assert.Equal(0, c.FanMaxRpmGpu);
        }

        [Test]
        public static void TwoFans_StillRequiresBothTemperatures()
        {
            Rig rig = new Rig();
            rig.GpuTemp = null;
            FanValidationReport rep = rig.Runner(FanLayout.Both).RunFans();
            Assert.True(rep.Aborted, "an unreadable GPU temperature stops a two-fan test");
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void CpuTemperatureAlwaysRequiredForACpuFan()
        {
            Rig rig = new Rig();
            rig.GpuFan = false;
            rig.Temp = null;
            FanValidationReport rep = rig.Runner(FanLayout.CpuOnly).RunFans();
            Assert.True(rep.Aborted, "no CPU temperature: nothing guards the test");
        }

        [Test]
        public static void GpuOnlyLayout_JudgesTheGpuFan()
        {
            Rig rig = new Rig();
            rig.Temp = null; // CPU temperature unreadable does not matter to a GPU-only layout
            FanValidationReport rep = rig.Runner(FanLayout.GpuOnly).RunFans();
            Assert.True(rep.Gpu != null && rep.Cpu == null, "gpu only");
            Assert.True(rep.Passed, rep.Summary());
        }

        [Test]
        public static void HotTemperatureStillAbortsOnEitherSensor()
        {
            Rig rig = new Rig();
            rig.GpuTemp = 91;
            FanValidationReport rep = rig.Runner(FanLayout.Both).RunFans();
            Assert.True(rep.Aborted, "91 C on the GPU stops it");
            Assert.True(rep.SkipModeCheck, "no mode changes after heat");
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void DefaultConstructor_IsBothFans()
        {
            Rig rig = new Rig();
            FanValidationRunner r = new FanValidationRunner(rig.Fw, delegate { rig.Fw.Calls.Add("max"); return true; }, delegate(int ms) { }, 50);
            FanValidationReport rep = r.RunFans();
            Assert.True(rep.Cpu != null && rep.Gpu != null, "legacy behaviour kept");
        }

        // ---- progress ----

        [Test]
        public static void Progress_ReportsEveryPhaseAndEndsAtOneHundred()
        {
            Rig rig = new Rig();
            FanValidationRunner r = rig.Runner(FanLayout.Both);
            List<FanProgress> seen = new List<FanProgress>();
            r.Progress = delegate(FanProgress p) { seen.Add(p); };
            r.RunFans();
            Assert.True(seen.Count > 10, "one report per sample: " + seen.Count);
            int last = -1;
            List<string> phases = new List<string>();
            foreach (FanProgress p in seen)
            {
                Assert.True(p.Percent >= last, "never goes backwards");
                last = p.Percent;
                if (phases.Count == 0 || phases[phases.Count - 1] != p.Phase) phases.Add(p.Phase);
                Assert.True(!string.IsNullOrEmpty(p.Text), "text for the phase");
            }
            Assert.Equal("auto|pct|max|auto2", string.Join("|", phases.ToArray()));
            Assert.Equal(100, seen[seen.Count - 1].Percent);
        }

        [Test]
        public static void Progress_TextTellsTheUserWhatToListenFor()
        {
            Rig rig = new Rig();
            FanValidationRunner r = rig.Runner(FanLayout.Both);
            Dictionary<string, string> text = new Dictionary<string, string>();
            r.Progress = delegate(FanProgress p) { text[p.Phase] = p.Text; };
            r.RunFans();
            Assert.True(text["max"].ToLowerInvariant().Contains("escute"), text["max"]);
            Assert.True(text["auto2"].ToLowerInvariant().Contains("escute"), text["auto2"]);
        }

        [Test]
        public static void Progress_ThrowingCallbackDoesNotBreakTheTest()
        {
            Rig rig = new Rig();
            FanValidationRunner r = rig.Runner(FanLayout.Both);
            r.Progress = delegate(FanProgress p) { throw new InvalidOperationException("ui gone"); };
            FanValidationReport rep = r.RunFans();
            Assert.True(rep.Passed, "the test still ran: " + rep.Summary());
        }

        [Test]
        public static void Cancel_StopsAndEndsInAuto()
        {
            Rig rig = new Rig();
            FanValidationRunner r = rig.Runner(FanLayout.Both);
            int n = 0;
            r.Progress = delegate(FanProgress p) { if (++n == 6) r.Cancel(); };
            FanValidationReport rep = r.RunFans();
            Assert.True(rep.Aborted, "aborted");
            Assert.True(rep.SkipModeCheck, "no mode check after a cancel");
            Assert.Equal("auto", rig.Fw.Last());
        }

        // ---- mode plan ----

        private static CapabilityProfile Pre(DetectionRaw r)
        {
            return CapabilityBuilder.Build(DetectionResult.Interpret(r), new EnvTestResults(), "t");
        }

        private static string Join(IList<PerfMode> l)
        {
            List<string> s = new List<string>();
            foreach (PerfMode m in l) s.Add(m.ToString());
            return string.Join(",", s.ToArray());
        }

        [Test]
        public static void Plan_ThisPc_EcoQuietPerformance_NoTurbo_EndsBalanced()
        {
            CapabilityProfile pre = Pre(CapabilityTests.ThisPc());
            List<PerfMode> cand = ModeTestPlan.Candidates(pre);
            Assert.Equal("Economico,Silencioso,Desempenho", Join(cand));
            Assert.True(ModeTestPlan.CanRun(pre), "balanced is announced");
            Assert.Equal("Economico,Silencioso,Desempenho,DiaADia", Join(ModeTestPlan.Order(PerfMode.DiaADia, cand)));
        }

        [Test]
        public static void Plan_TurboOnlyWhenAnnouncedAndNotDangerous_AndLast()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PHN16-71";
            r.ProfileBitmapRaw = 0x7300UL;
            List<PerfMode> cand = ModeTestPlan.Candidates(Pre(r));
            Assert.Equal("Economico,Silencioso,Desempenho,Turbo", Join(cand));

            r.Model = "Predator PH315-53"; // Turbo flagged as dangerous by the table
            Assert.True(!ModeTestPlan.Candidates(Pre(r)).Contains(PerfMode.Turbo), "dangerous Turbo is never planned");
        }

        [Test]
        public static void Plan_CurrentModeIsFirstCandidate_Rotated()
        {
            List<PerfMode> cand = new List<PerfMode> { PerfMode.Economico, PerfMode.Silencioso, PerfMode.Desempenho };
            Assert.Equal("Silencioso,Desempenho,Economico,DiaADia", Join(ModeTestPlan.Order(PerfMode.Economico, cand)));
        }

        [Test]
        public static void Plan_SingleCandidateEqualToCurrent_BalancedFirst()
        {
            List<PerfMode> cand = new List<PerfMode> { PerfMode.Economico };
            Assert.Equal("DiaADia,Economico,DiaADia", Join(ModeTestPlan.Order(PerfMode.Economico, cand)));
        }

        [Test]
        public static void Plan_NoCandidates_JustBalanced()
        {
            Assert.Equal("DiaADia", Join(ModeTestPlan.Order(PerfMode.Silencioso, new List<PerfMode>())));
        }

        [Test]
        public static void Plan_UnknownCurrent_NoRotation()
        {
            List<PerfMode> cand = new List<PerfMode> { PerfMode.Economico, PerfMode.Silencioso };
            Assert.Equal("Economico,Silencioso,DiaADia", Join(ModeTestPlan.Order(null, cand)));
        }

        [Test]
        public static void Plan_BalancedNotAnnounced_CannotRun()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro AN515-44"; // table: no profiles
            r.ProfileBitmapRaw = 0xFF00UL; // and the bitmap is garbage
            CapabilityProfile pre = Pre(r);
            Assert.True(!ModeTestPlan.CanRun(pre), "no balanced profile: nothing is written");
            Assert.Equal(0, ModeTestPlan.Candidates(pre).Count);
        }

        [Test]
        public static void Plan_UnknownModelWithGarbageBitmap_CannotRun()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro ANV15-99";
            r.ProfileBitmapRaw = 0UL;
            Assert.True(!ModeTestPlan.CanRun(Pre(r)), "unknown model and untrusted bitmap: no blind writes");
        }

        // ---- mode runner ----

        private static ModeTestRunner Runner(FakeFirmware fw, Action<int> sleep)
        {
            return new ModeTestRunner(fw, sleep ?? delegate(int ms) { });
        }

        private static string ModeCalls(FakeFirmware fw)
        {
            List<string> l = new List<string>();
            foreach (string c in fw.Calls) if (c.StartsWith("mode:")) l.Add(c.Substring(5));
            return string.Join(",", l.ToArray());
        }

        [Test]
        public static void ModeRun_AllProven_EndsBalanced()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode> { PerfMode.Economico, PerfMode.Silencioso, PerfMode.Desempenho });
            Assert.Equal("Economico,Silencioso,Desempenho,DiaADia", ModeCalls(fw));
            Assert.Equal(4, o.Steps.Count);
            foreach (ModeStepResult s in o.Steps)
            {
                Assert.Equal(StepStatus.Passed, s.Status);
                Assert.True(s.Proven, s.Mode + " proven: the state changed");
            }
            Assert.True(o.EndedBalanced, "ended on Dia a dia");
            Assert.Equal((PerfMode?)PerfMode.DiaADia, fw.ModeValue);
        }

        [Test]
        public static void ModeRun_ReadsBackAfterAPause()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            int slept = 0;
            Runner(fw, delegate(int ms) { slept += ms; }).Run(new List<PerfMode> { PerfMode.Silencioso });
            Assert.True(slept >= 2 * ModeTestRunner.PauseMs, "a pause per step: " + slept);
        }

        [Test]
        public static void ModeRun_IgnoredMode_FailsThatOneOnly_StillEndsBalanced()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            fw.IgnoredModes.Add(PerfMode.Desempenho);
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode> { PerfMode.Silencioso, PerfMode.Desempenho });
            Assert.Equal(StepStatus.Passed, o.Steps[0].Status);
            Assert.Equal(StepStatus.Failed, o.Steps[1].Status);
            Assert.Equal(StepStatus.Passed, o.Steps[2].Status);
            Assert.True(o.EndedBalanced, "ended on Dia a dia");
        }

        [Test]
        public static void ModeRun_TurboDeclined_SkippedAndNeverWritten()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            ModeTestRunner r = Runner(fw, null);
            r.Confirm = delegate(PerfMode m) { return m != PerfMode.Turbo; };
            ModeTestOutcome o = r.Run(new List<PerfMode> { PerfMode.Silencioso, PerfMode.Turbo });
            Assert.Equal("Silencioso,DiaADia", ModeCalls(fw));
            Assert.Equal(StepStatus.Skipped, o.Steps[1].Status);
            Assert.True(!o.Steps[1].Proven, "not proven");
        }

        [Test]
        public static void ModeRun_TurboConfirmed_RunsAfterTheOthers()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            List<PerfMode> asked = new List<PerfMode>();
            ModeTestRunner r = Runner(fw, null);
            r.Confirm = delegate(PerfMode m) { asked.Add(m); return true; };
            r.Run(new List<PerfMode> { PerfMode.Silencioso, PerfMode.Turbo });
            Assert.Equal("Silencioso,Turbo,DiaADia", ModeCalls(fw));
            Assert.Equal("Silencioso,Turbo,DiaADia", Join(asked));
        }

        [Test]
        public static void ModeRun_WriteRefused_Failed()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            fw.SetModeResult = false;
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode> { PerfMode.Silencioso });
            Assert.Equal(StepStatus.Failed, o.Steps[0].Status);
            Assert.True(o.EndedBalanced, "it never left Dia a dia");
        }

        [Test]
        public static void ModeRun_FirstStepEqualToCurrent_IsRotatedSoTheStateChanges()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode> { PerfMode.Economico, PerfMode.Silencioso });
            Assert.Equal("Silencioso,Economico,DiaADia", ModeCalls(fw));
            foreach (ModeStepResult s in o.Steps) Assert.True(s.Proven, s.Mode + " proven");
        }

        [Test]
        public static void ModeRun_CurrentUnreadable_FirstStepNotProven()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = null;
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode> { PerfMode.Silencioso });
            Assert.Equal(StepStatus.Passed, o.Steps[0].Status);
            Assert.True(!o.Steps[0].Proven, "no known starting state: a read-back proves nothing");
            Assert.True(o.Steps[1].Proven, "afterwards the previous state is known");
        }

        [Test]
        public static void ModeRun_OnlyBalancedAndAlreadyBalanced_NotProven()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode>());
            Assert.Equal(1, o.Steps.Count);
            Assert.Equal(StepStatus.Passed, o.Steps[0].Status);
            Assert.True(!o.Steps[0].Proven, "the state did not change");
            Assert.True(o.EndedBalanced, "balanced");
        }

        [Test]
        public static void ModeRun_Cancel_StopsAndRestoresBalanced()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            ModeTestRunner r = null;
            r = Runner(fw, null);
            int steps = 0;
            r.Progress = delegate(ModeTestProgress p) { if (++steps == 2) r.Cancel(); };
            ModeTestOutcome o = r.Run(new List<PerfMode> { PerfMode.Economico, PerfMode.Silencioso, PerfMode.Desempenho });
            Assert.True(o.Cancelled, "cancelled");
            Assert.True(o.EndedBalanced, "restored to Dia a dia");
            Assert.Equal("DiaADia", fw.Last().Substring(5));
            Assert.True(!ModeCalls(fw).Contains("Desempenho"), "the remaining steps never ran");
        }

        [Test]
        public static void ModeRun_Exception_DoesNotThrow_TriesToRestore()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            fw.ThrowOnMode = true;
            ModeTestOutcome o = Runner(fw, null).Run(new List<PerfMode> { PerfMode.Silencioso });
            Assert.True(!o.EndedBalanced, "the firmware cannot be read: the end state is not claimed to be confirmed");
            Assert.Equal(StepStatus.Failed, o.Steps[0].Status);
        }

        [Test]
        public static void ModeRun_ProgressReportsEachStep()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            ModeTestRunner r = Runner(fw, null);
            List<ModeTestProgress> seen = new List<ModeTestProgress>();
            r.Progress = delegate(ModeTestProgress p) { seen.Add(p); };
            r.Run(new List<PerfMode> { PerfMode.Silencioso, PerfMode.Desempenho });
            Assert.Equal(3, seen.Count);
            Assert.Equal(3, seen[0].Total);
            Assert.Equal(PerfMode.Silencioso, seen[0].Mode);
            Assert.True(!string.IsNullOrEmpty(seen[0].Text), "text");
        }

        [Test]
        public static void ModeRun_ResultsFeedTheCapabilityBuilder()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            fw.IgnoredModes.Add(PerfMode.Economico);
            DetectionRaw raw = CapabilityTests.ThisPc();
            CapabilityProfile pre = Pre(raw);
            ModeTestOutcome o = Runner(fw, null).Run(ModeTestPlan.Candidates(pre));
            EnvTestResults t = new EnvTestResults();
            foreach (ModeStepResult s in o.Steps) t.SetMode(s.Mode, s.Status, s.Proven);
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(raw), t, "t");
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeEco));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeQuiet));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModePerformance));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeBalanced));
        }
    }
}
