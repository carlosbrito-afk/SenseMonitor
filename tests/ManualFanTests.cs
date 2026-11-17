using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The wizard's manual fan test: the pick, the guarded write, the live hold, the judgement and the way back to Automático.
    public static class ManualFanTests
    {
        private sealed class Rig
        {
            public readonly FakeFirmware Fw = new FakeFirmware();
            public int Ms;
            public int AutoRpm = 2700;
            public Func<int, int> PctRpm = delegate(int pct) { return 2700 + (pct - 40) * 60; };
            public double Temp = 55;
            public Action<int> OnSleep;
            public bool AutoReadBackOk = true;
            public readonly List<string> Layouts = new List<string>();
            public ManualFanRunner Runner;

            public Rig(FanLayout notebook)
            {
                Fw.ReadingSource = delegate
                {
                    SensorReading r = new SensorReading();
                    int v = AutoRpm;
                    string last = null;
                    for (int i = Fw.Calls.Count - 1; i >= 0 && last == null; i--)
                    {
                        string c = Fw.Calls[i];
                        if (c == "auto" || c.StartsWith("pct")) last = c;
                    }
                    if (last != null && last.StartsWith("pct")) v = PctRpm(int.Parse(last.Substring(4).Split(',')[0]));
                    r.CpuFanRpm = v;
                    r.GpuFanRpm = v - 100;
                    r.CpuTempC = Temp;
                    r.GpuTempC = Temp - 5;
                    return r;
                };
                Runner = new ManualFanRunner(Fw, delegate(int ms) { Ms += ms; if (OnSleep != null) OnSleep(Ms); }, notebook);
                Runner.SetLayout = delegate(FanLayout l) { Layouts.Add(l.Cpu && l.Gpu ? "both" : (l.Cpu ? "cpu" : "gpu")); };
                Runner.AutoReadBack = delegate { return AutoReadBackOk; };
            }
        }

        private static ManualFanResult Hold(Rig rig, ManualFanTarget t, int pct, int seconds)
        {
            rig.OnSleep = delegate(int ms) { if (rig.Ms >= 2000 + seconds * 1000) rig.Runner.RequestStop(); };
            return rig.Runner.Run(t, pct);
        }

        // ---- the pick ----

        [Test]
        public static void Pick_SubLayoutAddressesOnlyTheChosenFan()
        {
            Assert.True(ManualFanJudge.Sub(FanLayout.Both, ManualFanTarget.Cpu) == FanLayout.CpuOnly, "cpu");
            Assert.True(ManualFanJudge.Sub(FanLayout.Both, ManualFanTarget.Gpu) == FanLayout.GpuOnly, "gpu");
            Assert.True(ManualFanJudge.Sub(FanLayout.Both, ManualFanTarget.Both) == FanLayout.Both, "both");
            Assert.True(ManualFanJudge.Sub(FanLayout.CpuOnly, ManualFanTarget.Both) == FanLayout.CpuOnly, "a one-fan notebook is never asked for the other");
            Assert.True(ManualFanJudge.Sub(FanLayout.CpuOnly, ManualFanTarget.Gpu) == null, "nothing to address");
        }

        [Test]
        public static void Pick_SliderIsClampedTo30To100()
        {
            Assert.Equal(30, ManualFanJudge.ClampPct(5));
            Assert.Equal(100, ManualFanJudge.ClampPct(180));
            Assert.Equal(70, ManualFanJudge.ClampPct(70));
        }

        // ---- the run ----

        [Test]
        public static void Run_WritesCustomOnlyToTheChosenFan_ThenAutoOnAllOfThem()
        {
            Rig rig = new Rig(FanLayout.Both);
            ManualFanResult r = Hold(rig, ManualFanTarget.Gpu, 80, 6);
            Assert.True(r.Applied, "written");
            Assert.Equal("pct:80,80", rig.Fw.Calls[0]);
            Assert.Equal("auto", rig.Fw.Last());
            Assert.Equal("gpu|both", string.Join("|", rig.Layouts.ToArray()));
            Assert.True(r.AutoConfirmed, "Automático read back");
            Assert.True(r.Gpu != null && r.Cpu == null, "only the chosen fan is judged");
        }

        [Test]
        public static void Run_FanFollowsTheCommand_IsJudgedAsMoved()
        {
            Rig rig = new Rig(FanLayout.Both);
            ManualFanResult r = Hold(rig, ManualFanTarget.Both, 80, 6);
            Assert.True(r.Cpu.Moved && r.Gpu.Moved, r.Cpu.Reason + " / " + r.Gpu.Reason);
            Assert.Equal(2700, r.Cpu.Baseline);
            Assert.Equal(5100, r.Cpu.Settled);
            Assert.True(r.Cpu.EstimatedMax > 5000, "Máximo implied by the reading: " + r.Cpu.EstimatedMax);
            Assert.True(r.StopReason == null, "the user took it back");
        }

        [Test]
        public static void Run_FanIgnoresTheCommand_IsNotMoved()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm; };
            ManualFanResult r = Hold(rig, ManualFanTarget.Cpu, 80, 6);
            Assert.True(!r.Cpu.Moved && !r.Cpu.Inconclusive, "an ignored write is a real no: " + r.Cpu.Reason);
        }

        [Test]
        public static void Run_SpeedNearAutomatico_IsInconclusiveNotAFailure()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.AutoRpm = 4500; // 70 % of 6500 is 4550: indistinguishable from what Automático does
            rig.PctRpm = delegate(int pct) { return 4500; };
            ManualFanResult r = Hold(rig, ManualFanTarget.Cpu, 70, 6);
            Assert.True(r.Cpu.Inconclusive, "cannot conclude: " + r.Cpu.Reason);
            Assert.True(r.Cpu.Reason.Contains("inconclusivo"), r.Cpu.Reason);
        }

        [Test]
        public static void Run_KnownMaximum_DecidesTheDirection()
        {
            // Automático is already loud (6000 of a 7500 maximum): 30 % must make it SLOWER
            Rig rig = new Rig(FanLayout.CpuOnly);
            rig.AutoRpm = 6000;
            rig.PctRpm = delegate(int pct) { return 3400; };
            rig.Runner.RefMaxCpu = 7500;
            ManualFanResult r = Hold(rig, ManualFanTarget.Cpu, 30, 6);
            Assert.True(r.Cpu.Moved, "slower counts when slower is the way: " + r.Cpu.Reason);
        }

        [Test]
        public static void Run_HotDuringHold_StopsAtOnceAndAsksForNothingMore()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.OnSleep = delegate(int ms) { if (rig.Ms >= 5000) rig.Temp = 91; };
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 90);
            Assert.True(r.SkipFurther, "no further manual attempt after heat");
            Assert.True(r.StopReason.Contains("90"), r.StopReason);
            Assert.Equal("auto", rig.Fw.Last());
            Assert.True(r.AutoConfirmed, "Automático written");
        }

        [Test]
        public static void Run_UnreadableTemperature_RefusesToWriteAnything()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.Fw.ReadingSource = delegate { SensorReading r = new SensorReading(); r.CpuFanRpm = 2700; return r; };
            ManualFanResult r2 = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(!r2.Applied && r2.SkipFurther, "no trusted temperature, no Custom");
            Assert.Equal(0, rig.Fw.Count("pct:80,80"));
            Assert.True(r2.StopReason.Contains("ilegível"), r2.StopReason);
        }

        [Test]
        public static void Run_RefusedWrite_ReportsItAndWritesAuto()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.Fw.SetPercentResult = false;
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(!r.Applied, "refused");
            Assert.True(r.Error.Contains("recusou"), r.Error);
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Run_Cancel_StopsAndWritesAuto()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.OnSleep = delegate(int ms) { if (rig.Ms >= 4000) rig.Runner.Cancel(); };
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(r.SkipFurther && r.StopReason.Contains("cancelado"), r.StopReason);
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Run_CancelledBeforeStart_WritesNoCustom_OnlyAutomatico()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.Runner.Cancel();
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.Equal("auto", string.Join("|", rig.Fw.Calls.ToArray()));
            Assert.True(!r.Applied, "no Custom written");
        }

        [Test]
        public static void Run_HoldLimit_ReturnsToAutoByItself()
        {
            Rig rig = new Rig(FanLayout.Both);
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(r.HeldMs >= ManualFanRunner.HoldLimitMs, "held to the limit");
            Assert.True(r.StopReason.Contains("tempo"), r.StopReason);
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Run_AutoNotConfirmedByTheReadBack_IsReportedAndRetried()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.AutoReadBackOk = false;
            ManualFanResult r = Hold(rig, ManualFanTarget.Both, 80, 6);
            Assert.True(!r.AutoConfirmed, "the firmware did not say Automático");
            Assert.Equal(ManualFanRunner.AutoTries, rig.Fw.Count("auto"));
        }

        [Test]
        public static void Run_Progress_CarriesLiveRpmAndTemperatures()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanProgress> seen = new List<ManualFanProgress>();
            rig.Runner.Progress = delegate(ManualFanProgress p) { seen.Add(p); };
            Hold(rig, ManualFanTarget.Both, 80, 6);
            Assert.True(seen.Count >= 5, "one report per second: " + seen.Count);
            Assert.Equal(5100, seen[seen.Count - 1].CpuRpm.Value);
            Assert.Equal(55.0, seen[0].CpuTempC.Value);
            Assert.True(seen[seen.Count - 1].Settled, "settled after a few seconds");
            Assert.True(!seen[0].Settled, "not at the first reading");
        }

        // ---- what the attempts add up to ----

        private static ManualFanAttempt Attempt(Rig rig, ManualFanTarget t, int pct, bool? heard)
        {
            ManualFanAttempt a = new ManualFanAttempt();
            a.Result = Hold(rig, t, pct, 6);
            a.Heard = heard;
            return a;
        }

        [Test]
        public static void Decide_HeardAndMeasuredAndAutoBack_Passes_AsManual()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.Equal("manual", r.Method);
            Assert.True(r.CpuOk && r.GpuOk, "both fans");
            Assert.True(r.MaxRpmCpu > 0 && r.MaxRpmGpu > 0, "the RPM guard gets a reference");
        }

        [Test]
        public static void Decide_UserSaysNothingChanged_FailsWithTheReason()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, false));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Failed, r.Status);
            Assert.True(r.Reason.Contains("não mudou"), r.Reason);
        }

        // The user's word wins: "Ouvi a ventoinha mudar" unlocks the fan control even when the measured RPM change is small.
        [Test]
        public static void Decide_HeardButTheRpmDidNotMove_UserWordWins_PassesAsUserConfirmed()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm + 100; }; // 3.7 %: below the 10 % the measurement asks for
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            Assert.True(!l[0].Result.Cpu.Moved && !l[0].Result.Cpu.Inconclusive, "the measurement alone would say no");
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.Equal(ManualFanOutcome.MethodUserConfirmed, r.Method);
            Assert.Equal("manual (confirmado pelo usuário, sem medição)", r.Method);
            Assert.True(r.CpuOk && r.GpuOk, "both fans");
            Assert.Equal(0, r.MaxRpmCpu);
            Assert.Equal(0, r.MaxRpmGpu);
            // the measured numbers are kept, not thrown away
            Assert.True(r.Reason != null && r.Reason.Contains("2700") && r.Reason.Contains("2800"), r.Reason);
        }

        [Test]
        public static void Decide_HeardAndTheRpmIsUnreadable_UserWordWins()
        {
            Rig rig = new Rig(FanLayout.Both);
            Func<SensorReading> good = rig.Fw.ReadingSource;
            rig.Fw.ReadingSource = delegate
            {
                SensorReading r0 = good();
                r0.CpuFanRpm = null;
                r0.GpuFanRpm = null;
                return r0;
            };
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.Equal(ManualFanOutcome.MethodUserConfirmed, r.Method);
            Assert.True(r.Reason.Contains("sem leitura"), r.Reason);
            Assert.Equal(0, r.MaxRpmCpu);
        }

        [Test]
        public static void Decide_HeardNearAutomatico_InconclusiveMeasurement_UserWordWins()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.AutoRpm = 4500;
            rig.PctRpm = delegate(int pct) { return 4500; };
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 70, true));
            Assert.True(l[0].Result.Cpu.Inconclusive, "the measurement could not conclude");
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.Equal(ManualFanOutcome.MethodUserConfirmed, r.Method);
        }

        [Test]
        public static void Decide_HeardAndMeasuredMoved_KeepsThePlainManualMethodAndTheReference()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal("manual", r.Method);
            Assert.True(r.MaxRpmCpu > 0 && r.MaxRpmGpu > 0, "measured: the RPM guard keeps its reference");
        }

        [Test]
        public static void Decide_OneFanMeasuredOneByTheUsersWord_IsUserConfirmedButKeepsTheMeasuredReference()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Cpu, 80, true));
            Rig rig2 = new Rig(FanLayout.Both);
            rig2.PctRpm = delegate(int pct) { return rig2.AutoRpm; };
            l.Add(Attempt(rig2, ManualFanTarget.Gpu, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.Equal(ManualFanOutcome.MethodUserConfirmed, r.Method);
            Assert.True(r.MaxRpmCpu > 0, "the fan that was measured keeps its reference");
            Assert.Equal(0, r.MaxRpmGpu);
        }

        [Test]
        public static void Decide_HeardButAutoNotConfirmed_StillNotAPass_EvenByTheUsersWord()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.AutoReadBackOk = false;
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm; };
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.True(r.Status != StepStatus.Passed, "Automático must be confirmed by the read-back first");
            Assert.True(r.Reason.Contains("automático"), r.Reason);
        }

        [Test]
        public static void Decide_HeardAfterHeat_IsStillNeverAPass()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm; };
            rig.OnSleep = delegate(int ms) { if (rig.Ms >= 5000) rig.Temp = 92; };
            ManualFanAttempt a = new ManualFanAttempt();
            a.Result = rig.Runner.Run(ManualFanTarget.Both, 90);
            a.Heard = true;
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(a);
            Assert.Equal(StepStatus.Skipped, ManualFanOutcome.Decide(FanLayout.Both, l).Status);
        }

        [Test]
        public static void Decide_UserWordCoversOnlyTheFansAddressed()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm; };
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Cpu, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Skipped, r.Status);
            Assert.True(r.CpuOk && !r.GpuOk, "cpu by the user's word, gpu still to do");
            Assert.True(r.Reason.Contains("GPU"), r.Reason);
        }

        [Test]
        public static void UserConfirmed_UnlocksTheFanControlWithoutAnRpmReference()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm; };
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            EnvTestResults t = new EnvTestResults();
            t.Fan = ManualFanOutcome.Decide(FanLayout.Both, l);
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, "2026-11-17T12:00:00Z");
            Assert.Equal(CapState.Confirmed, p.State(Cap.FanControl));
            Assert.Equal(ManualFanOutcome.MethodUserConfirmed, p.Find(Cap.FanControl).Method);
            Config c = new Config();
            EnvApply.Apply(c, p, t, "2026-11-17T12:00:00Z");
            Assert.True(c.ManualFanAllowed, "the user's word unlocks the manual control");
            Assert.Equal(0, c.FanMaxRpmCpu);
        }

        [Test]
        public static void PlainManualWithoutAReference_StaysLocked()
        {
            // only the user-confirmed method may unlock without an RPM reference
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            EnvTestResults t = new EnvTestResults();
            t.Fan = ManualFanOutcome.Decide(FanLayout.Both, l);
            t.Fan.MaxRpmCpu = 0;
            t.Fan.MaxRpmGpu = 0;
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, "2026-11-17T12:00:00Z");
            Config c = new Config();
            EnvApply.Apply(c, p, t, "2026-11-17T12:00:00Z");
            Assert.True(!c.ManualFanAllowed, "no reference and no user word: locked");
        }

        [Test]
        public static void UserConfirmed_IsCarriedByThePriorTestSkip()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.PctRpm = delegate(int pct) { return rig.AutoRpm; };
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            EnvTestResults t = new EnvTestResults();
            t.Fan = ManualFanOutcome.Decide(FanLayout.Both, l);
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, "2026-11-17T12:00:00Z");
            PriorTest prior = PriorTests.Fan(p, p.Fingerprint);
            Assert.True(prior != null, "a prior test exists");
            EnvTestResults next = new EnvTestResults();
            PriorTests.CarryFan(next, p, prior, 0, 0);
            Assert.Equal(ManualFanOutcome.MethodUserConfirmed, next.Fan.Method);
            CapabilityProfile p2 = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), next, "2026-11-17T13:00:00Z");
            Config c = new Config();
            EnvApply.Apply(c, p2, next, "2026-11-17T13:00:00Z");
            Assert.True(c.ManualFanAllowed, "still unlocked after 'Pular (já testado)'");
        }

        [Test]
        public static void Decide_NoAnswer_ProvesNothing()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, null));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Skipped, r.Status);
        }

        [Test]
        public static void Decide_OnlyOneOfTwoFansTested_StaysLockedAndSaysWhichIsMissing()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Cpu, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Skipped, r.Status);
            Assert.True(r.CpuOk && !r.GpuOk, "cpu ok, gpu not yet");
            Assert.True(r.Reason.Contains("GPU"), r.Reason);
            // the second fan completes it
            Rig rig2 = new Rig(FanLayout.Both);
            l.Add(Attempt(rig2, ManualFanTarget.Gpu, 80, true));
            Assert.Equal(StepStatus.Passed, ManualFanOutcome.Decide(FanLayout.Both, l).Status);
        }

        [Test]
        public static void Decide_AutoNotConfirmed_IsNotAPass()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.AutoReadBackOk = false;
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.True(r.Status != StepStatus.Passed, "the step cannot pass until Automático is confirmed");
            Assert.True(r.Reason.Contains("automático"), r.Reason);
        }

        [Test]
        public static void Decide_HeatStops_AndIsNeverAPass()
        {
            Rig rig = new Rig(FanLayout.Both);
            rig.OnSleep = delegate(int ms) { if (rig.Ms >= 5000) rig.Temp = 92; };
            ManualFanAttempt a = new ManualFanAttempt();
            a.Result = rig.Runner.Run(ManualFanTarget.Both, 90);
            a.Heard = true;
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(a);
            FanTestResult r = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.Equal(StepStatus.Skipped, r.Status);
            Assert.True(r.Reason.Contains("90"), r.Reason);
        }

        [Test]
        public static void ManualResult_FeedsTheCapabilityProfileLikeTheAutomaticOne()
        {
            Rig rig = new Rig(FanLayout.Both);
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(Attempt(rig, ManualFanTarget.Both, 80, true));
            EnvTestResults t = new EnvTestResults();
            t.Fan = ManualFanOutcome.Decide(FanLayout.Both, l);
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, "2026-11-17T12:00:00Z");
            Assert.Equal(CapState.Confirmed, p.State(Cap.FanControl));
        }
    }
}
