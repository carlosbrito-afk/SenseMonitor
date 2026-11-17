using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // What an earlier environment test already proved on THIS notebook (same model + BIOS fingerprint): the wizard says so and
    // offers to skip the step while keeping the capability confirmed.
    public sealed class PriorTest
    {
        public string Utc;
        // "manual", "automático" or null (an older profile that did not record it).
        public string Method;

        public string DateText { get { return PriorTests.DateText(Utc); } }
    }

    public static class PriorTests
    {
        private static bool Same(CapabilityProfile saved, string fingerprint)
        {
            return saved != null && !string.IsNullOrEmpty(fingerprint) && MachineFingerprint.Same(saved.Fingerprint, fingerprint);
        }

        private static PriorTest From(CapabilityProfile saved, Cap cap)
        {
            CapEntry e = saved.Find(cap);
            PriorTest p = new PriorTest();
            p.Utc = !string.IsNullOrEmpty(e.Utc) ? e.Utc : saved.CreatedUtc;
            p.Method = e.Method;
            return p;
        }

        // The key answered in an earlier run (a "this notebook has no such key" answer is not a test, so it is never reused).
        public static PriorTest Key(CapabilityProfile saved, string fingerprint)
        {
            if (!Same(saved, fingerprint) || saved.State(Cap.Key) != CapState.Confirmed || KeyCandidates.ById(saved.KeyCandidateId) == null) return null;
            return From(saved, Cap.Key);
        }

        public static PriorTest Fan(CapabilityProfile saved, string fingerprint)
        {
            if (!Same(saved, fingerprint) || saved.State(Cap.FanControl) != CapState.Confirmed) return null;
            return From(saved, Cap.FanControl);
        }

        public static PriorTest Mode(CapabilityProfile saved, string fingerprint, PerfMode m)
        {
            if (!Same(saved, fingerprint) || saved.State(ModeCatalog.CapOf(m)) != CapState.Confirmed) return null;
            return From(saved, ModeCatalog.CapOf(m));
        }

        // "17/11/2026" from an ISO UTC stamp; a stamp that cannot be read is "data desconhecida".
        public static string DateText(string utc)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(utc) && DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            return "data desconhecida";
        }

        // ---- carrying an earlier result into this run (the step was skipped on purpose) ----

        public static void CarryKey(EnvTestResults t, CapabilityProfile saved, PriorTest p)
        {
            if (t == null || saved == null || p == null) return;
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = saved.KeyCandidateId;
            t.Key.UserHasNoKey = false;
            t.Key.Utc = p.Utc;
            t.Key.Method = p.Method;
        }

        public static void CarryFan(EnvTestResults t, CapabilityProfile saved, PriorTest p, int maxCpu, int maxGpu)
        {
            if (t == null || saved == null || p == null) return;
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = saved.CpuFan;
            t.Fan.GpuOk = saved.GpuFan;
            t.Fan.MaxRpmCpu = maxCpu;
            t.Fan.MaxRpmGpu = maxGpu;
            t.Fan.Reason = null;
            t.Fan.Utc = p.Utc;
            t.Fan.Method = p.Method;
        }

        public static void CarryMode(EnvTestResults t, PerfMode m, PriorTest p)
        {
            if (t == null || p == null) return;
            t.SetMode(m, StepStatus.Passed, true);
            ModeStepResult r = t.ModeResult(m);
            r.Utc = p.Utc;
            r.Method = p.Method;
        }
    }
}
