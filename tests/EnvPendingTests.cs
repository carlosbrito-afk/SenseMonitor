using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // "Fazer depois": what is left pending after the wizard (or the installer) and the one-time reminder.
    public static class EnvPendingTests
    {
        private static CapabilityProfile ThisPcWith(EnvTestResults t)
        {
            return CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, "t");
        }

        [Test]
        public static void Pending_ListsSkippedGuidedSteps()
        {
            EnvTestResults t = new EnvTestResults();
            t.Fan.Status = StepStatus.Skipped;
            t.Key.Status = StepStatus.Skipped;
            List<Cap> p = ThisPcWith(t).PendingTests();
            Assert.True(p.Contains(Cap.FanControl), "fan control pending");
            Assert.True(p.Contains(Cap.Key), "key pending");
            Assert.True(p.Contains(Cap.ModeQuiet), "modes pending");
            Assert.True(!p.Contains(Cap.Battery), "battery is information only");
            Assert.True(!p.Contains(Cap.ModeTurbo), "an unavailable capability is not pending");
        }

        [Test]
        public static void Pending_NothingWhenEverythingWasTested()
        {
            EnvTestResults t = new EnvTestResults();
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = KeyCandidates.OursId;
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = true;
            t.Fan.GpuOk = true;
            t.SetMode(PerfMode.Silencioso, StepStatus.Passed, true);
            t.SetMode(PerfMode.Desempenho, StepStatus.Passed, true);
            t.SetMode(PerfMode.Economico, StepStatus.Passed, true);
            t.SetMode(PerfMode.DiaADia, StepStatus.Passed, true);
            Assert.Equal(0, ThisPcWith(t).PendingTests().Count);
        }

        [Test]
        public static void Reminder_DueOnceWhenSomethingIsPending()
        {
            Config c = new Config();
            c.Env = ThisPcWith(new EnvTestResults());
            c.EnvStale = false;
            Assert.True(EnvReminder.Due(c), "pending tests and not reminded yet");
            c.EnvReminderShown = true;
            Assert.True(!EnvReminder.Due(c), "only once");
        }

        [Test]
        public static void Reminder_NotWhenStaleOrNoProfile()
        {
            Config c = new Config();
            Assert.True(!EnvReminder.Due(c), "no profile: the wizard itself is offered");
            c.Env = ThisPcWith(new EnvTestResults());
            c.EnvStale = true;
            Assert.True(!EnvReminder.Due(c), "stale: the wizard is offered instead");
        }

        [Test]
        public static void Apply_ResetsTheReminder()
        {
            Config c = new Config();
            c.EnvReminderShown = true;
            EnvTestResults t = new EnvTestResults();
            EnvApply.Apply(c, ThisPcWith(t), t, "t");
            Assert.True(!c.EnvReminderShown, "a new result may leave new pending tests");
        }

        [Test]
        public static void MergeServiceUndo_KeepsTheOriginalRecord()
        {
            Config c = new Config();
            ServiceUndo a = new ServiceUndo();
            a.Name = "ASMSvc";
            a.PrevStartMode = "Auto";
            a.WasRunning = true;
            c.ServiceUndo.Add(a);
            ServiceUndo again = new ServiceUndo();
            again.Name = "ASMSvc";
            again.PrevStartMode = "Disabled";
            ServiceUndo other = new ServiceUndo();
            other.Name = "AASSvc";
            other.PrevStartMode = "Manual";
            EnvApply.MergeServiceUndo(c, new List<ServiceUndo> { again, other });
            Assert.Equal(2, c.ServiceUndo.Count);
            Assert.Equal("Auto", c.ServiceUndo[0].PrevStartMode);
            EnvApply.MergeServiceUndo(c, null);
            Assert.Equal(2, c.ServiceUndo.Count);
        }
    }
}
