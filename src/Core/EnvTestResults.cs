using System.Collections.Generic;

namespace Forja.Core
{
    public enum StepStatus
    {
        NotRun,
        Skipped,
        Passed,
        Failed
    }

    public sealed class KeyTestResult
    {
        public StepStatus Status;
        // Passed: which candidate answered (KeyCandidates.*Id).
        public string CandidateId;
        // The user said "my notebook does not have that key".
        public bool UserHasNoKey;
        // Carried from an earlier run when the step was skipped as "already tested" (null = this run).
        public string Utc;
        public string Method;
    }

    public sealed class FanTestResult
    {
        public StepStatus Status;
        public bool CpuOk;
        public bool GpuOk;
        public string Reason;
        // "automático" (the wizard's own sequence) or "manual" (the user drove it and confirmed); null = not recorded.
        public string Method;
        public string Utc;
        // Settled RPM at Máximo per fan (the RPM-response guard's reference).
        public int MaxRpmCpu;
        public int MaxRpmGpu;
    }

    public sealed class ModeStepResult
    {
        public PerfMode Mode;
        public StepStatus Status;
        // The write was accepted and read back, AND the state really changed (a write that changes
        // nothing proves nothing: the firmware answers "ok" to anything).
        public bool Proven;
        public string Utc;
        public string Method;
    }

    // What the guided steps found, in one place.
    public sealed class EnvTestResults
    {
        public KeyTestResult Key = new KeyTestResult();
        public FanTestResult Fan = new FanTestResult();
        public readonly List<ModeStepResult> Modes = new List<ModeStepResult>();
        // Diagnostic read-only mode: the steps that write could not run.
        public bool ReadOnly;

        public ModeStepResult ModeResult(PerfMode m)
        {
            foreach (ModeStepResult r in Modes)
            {
                if (r.Mode == m) return r;
            }
            return null;
        }

        public void SetMode(PerfMode m, StepStatus status, bool proven)
        {
            ModeStepResult r = ModeResult(m);
            if (r == null)
            {
                r = new ModeStepResult();
                r.Mode = m;
                Modes.Add(r);
            }
            r.Status = status;
            r.Proven = proven;
        }
    }
}
