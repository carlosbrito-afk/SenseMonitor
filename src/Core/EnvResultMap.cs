using System;

namespace Forja.Core
{
    // Translates what the test routines return into the wizard's results (what CapabilityBuilder reads).
    public static class EnvResultMap
    {
        // Passed = every fan of the layout followed the commands. A run stopped by heat, an unreadable
        // temperature or a cancel proves nothing about the fans (Skipped: stays locked); a refused write or a
        // fan that did not follow the command is a real "no" (Failed).
        public static FanTestResult Fan(FanValidationReport rep)
        {
            FanTestResult r = new FanTestResult();
            if (rep == null)
            {
                r.Status = StepStatus.Skipped;
                r.Reason = "o teste não rodou";
                return r;
            }
            r.CpuOk = rep.Cpu != null && rep.Cpu.Passed;
            r.GpuOk = rep.Gpu != null && rep.Gpu.Passed;
            if (rep.Aborted)
            {
                r.Status = rep.SkipModeCheck ? StepStatus.Skipped : StepStatus.Failed;
                r.Reason = rep.Reason;
                return r;
            }
            if (rep.Passed)
            {
                r.Status = StepStatus.Passed;
                r.MaxRpmCpu = rep.Cpu != null ? rep.Cpu.Max : 0;
                r.MaxRpmGpu = rep.Gpu != null ? rep.Gpu.Max : 0;
                return r;
            }
            r.Status = StepStatus.Failed;
            if (rep.Cpu != null && !rep.Cpu.Passed) r.Reason = "CPU — " + rep.Cpu.Reason;
            else if (rep.Gpu != null && !rep.Gpu.Passed) r.Reason = "GPU — " + rep.Gpu.Reason;
            else r.Reason = rep.Reason ?? "motivo desconhecido";
            return r;
        }

        // The service could not even start the test (read-only mode, the firmware did not answer): nothing is proved.
        public static FanTestResult FanError(string text)
        {
            FanTestResult r = new FanTestResult();
            r.Status = StepStatus.Skipped;
            r.Reason = text;
            return r;
        }

        public static void Modes(ModeTestOutcome o, EnvTestResults into)
        {
            if (o == null || into == null) return;
            foreach (ModeStepResult s in o.Steps)
            {
                into.SetMode(s.Mode, s.Status, s.Proven);
            }
        }

        public static void KeyPassed(EnvTestResults t, KeyCandidate c)
        {
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = c == null ? null : c.Id;
            t.Key.UserHasNoKey = false;
        }

        public static void KeyNone(EnvTestResults t)
        {
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = null;
            t.Key.UserHasNoKey = true;
        }

        public static void KeyTimeout(EnvTestResults t)
        {
            t.Key.Status = StepStatus.Failed;
            t.Key.CandidateId = null;
            t.Key.UserHasNoKey = false;
        }
    }
}
