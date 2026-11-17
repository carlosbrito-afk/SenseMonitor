using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // How the outcome of the (existing) fan routine becomes the wizard's FanTestResult.
    public static class EnvResultMapTests
    {
        private static FanCheck Check(string name, bool passed, int max, string reason)
        {
            FanCheck c = new FanCheck();
            c.Name = name;
            c.Passed = passed;
            c.Max = max;
            c.Reason = reason;
            return c;
        }

        [Test]
        public static void Passed_BothFans_ReferencesKept()
        {
            FanValidationReport rep = new FanValidationReport();
            rep.Passed = true;
            rep.Cpu = Check("CPU", true, 3500, null);
            rep.Gpu = Check("GPU", true, 3200, null);
            FanTestResult r = EnvResultMap.Fan(rep);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.True(r.CpuOk && r.GpuOk, "both fans");
            Assert.Equal(3500, r.MaxRpmCpu);
            Assert.Equal(3200, r.MaxRpmGpu);
        }

        [Test]
        public static void Passed_OneFan_OnlyThatFan()
        {
            FanValidationReport rep = new FanValidationReport();
            rep.Passed = true;
            rep.Cpu = Check("CPU", true, 3500, null);
            FanTestResult r = EnvResultMap.Fan(rep);
            Assert.Equal(StepStatus.Passed, r.Status);
            Assert.True(r.CpuOk && !r.GpuOk, "cpu only");
            Assert.Equal(0, r.MaxRpmGpu);
        }

        [Test]
        public static void OneFanFailed_Failed_WithTheReason()
        {
            FanValidationReport rep = new FanValidationReport();
            rep.Cpu = Check("CPU", true, 3500, null);
            rep.Gpu = Check("GPU", false, 3200, "a ventoinha não seguiu o comando de 50%");
            FanTestResult r = EnvResultMap.Fan(rep);
            Assert.Equal(StepStatus.Failed, r.Status);
            Assert.True(r.CpuOk && !r.GpuOk, "the cpu fan passed");
            Assert.True(r.Reason.Contains("GPU") && r.Reason.Contains("não seguiu"), r.Reason);
            Assert.Equal(0, r.MaxRpmCpu);
            Assert.Equal(0, r.MaxRpmGpu);
        }

        [Test]
        public static void Aborted_ByHeatOrCancel_IsNotAProofOfFailure_Skipped()
        {
            FanValidationReport rep = new FanValidationReport();
            rep.Aborted = true;
            rep.SkipModeCheck = true;
            rep.Reason = "temperatura chegou a 91 °C (limite 90 °C)";
            FanTestResult r = EnvResultMap.Fan(rep);
            Assert.Equal(StepStatus.Skipped, r.Status);
            Assert.True(r.Reason.Contains("91"), r.Reason);
        }

        [Test]
        public static void Aborted_FirmwareRefused_Failed()
        {
            FanValidationReport rep = new FanValidationReport();
            rep.Aborted = true;
            rep.SkipModeCheck = false;
            rep.Reason = "o firmware recusou a velocidade de 50%";
            FanTestResult r = EnvResultMap.Fan(rep);
            Assert.Equal(StepStatus.Failed, r.Status);
            Assert.True(r.Reason.Contains("recusou"), r.Reason);
        }

        [Test]
        public static void NullReport_Skipped()
        {
            Assert.Equal(StepStatus.Skipped, EnvResultMap.Fan(null).Status);
        }

        [Test]
        public static void ErrorText_FromTheService_IsFailureWithoutProof()
        {
            FanTestResult r = EnvResultMap.FanError("O firmware não respondeu.");
            Assert.Equal(StepStatus.Skipped, r.Status);
            Assert.True(r.Reason.Contains("não respondeu"), r.Reason);
        }

        [Test]
        public static void Modes_CopiedIntoResults()
        {
            ModeTestOutcome o = new ModeTestOutcome();
            ModeStepResult a = new ModeStepResult();
            a.Mode = PerfMode.Silencioso;
            a.Status = StepStatus.Passed;
            a.Proven = true;
            o.Steps.Add(a);
            ModeStepResult b = new ModeStepResult();
            b.Mode = PerfMode.Turbo;
            b.Status = StepStatus.Skipped;
            o.Steps.Add(b);
            EnvTestResults t = new EnvTestResults();
            EnvResultMap.Modes(o, t);
            Assert.Equal(StepStatus.Passed, t.ModeResult(PerfMode.Silencioso).Status);
            Assert.True(t.ModeResult(PerfMode.Silencioso).Proven, "proven kept");
            Assert.Equal(StepStatus.Skipped, t.ModeResult(PerfMode.Turbo).Status);
        }

        [Test]
        public static void Modes_CancelledRun_RemainingModesStayNotRun()
        {
            ModeTestOutcome o = new ModeTestOutcome();
            o.Cancelled = true;
            ModeStepResult a = new ModeStepResult();
            a.Mode = PerfMode.Economico;
            a.Status = StepStatus.Passed;
            a.Proven = true;
            o.Steps.Add(a);
            EnvTestResults t = new EnvTestResults();
            EnvResultMap.Modes(o, t);
            Assert.True(t.ModeResult(PerfMode.Desempenho) == null, "never ran");
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, "t");
            Assert.Equal(CapState.Untested, p.State(Cap.ModePerformance));
        }

        [Test]
        public static void Key_Outcomes()
        {
            EnvTestResults t = new EnvTestResults();
            EnvResultMap.KeyPassed(t, KeyCandidates.AtF5);
            Assert.Equal(StepStatus.Passed, t.Key.Status);
            Assert.Equal(KeyCandidates.AtF5Id, t.Key.CandidateId);
            Assert.True(!t.Key.UserHasNoKey, "has key");

            t = new EnvTestResults();
            EnvResultMap.KeyNone(t);
            Assert.Equal(StepStatus.Passed, t.Key.Status);
            Assert.True(t.Key.UserHasNoKey, "no key");

            t = new EnvTestResults();
            EnvResultMap.KeyTimeout(t);
            Assert.Equal(StepStatus.Failed, t.Key.Status);
        }
    }
}
