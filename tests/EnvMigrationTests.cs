using System;
using Forja.Core;

namespace Forja.Tests
{
    // Startup rules of the environment profile: migration of the single-machine flags, stale detection
    // and the locks that follow, and how a finished wizard run is written into the config.
    public static class EnvMigrationTests
    {
        private const string Now = "2026-11-17T12:00:00Z";

        private static Config ValidatedThisPc()
        {
            Config c = new Config();
            c.FanControlValidated = true;
            c.FanMaxRpmCpu = 3500;
            c.FanMaxRpmGpu = 3200;
            c.ModeCheckPassed = true;
            return c;
        }

        [Test]
        public static void ThisPc_KeepsItsValidationAfterTheUpgrade()
        {
            Config c = ValidatedThisPc();
            Assert.True(c.ManualFanAllowed, "validated before the upgrade");
            EnvStatus s = EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.Equal(EnvStatus.Fresh, s);
            Assert.True(c.Env != null, "profile created from the old flags");
            Assert.True(c.Env.Migrated, "marked as migrated");
            Assert.True(!c.EnvStale, "not stale");
            Assert.True(c.ManualFanAllowed, "manual fan control still allowed on this PC");
            Assert.True(c.ModeAllowed(PerfMode.Silencioso) && c.ModeAllowed(PerfMode.DiaADia) && c.ModeAllowed(PerfMode.Desempenho), "the three tested modes stay allowed");
            Assert.True(!c.ModeAllowed(PerfMode.Economico), "Eco was never tested: locked");
            Assert.True(!c.ModeAllowed(PerfMode.Turbo), "Turbo: not on this model");
            Assert.Equal("nitro anv15-51|v1.60", c.Env.Fingerprint);
            Assert.True(c.Env.CpuFan && c.Env.GpuFan, "both fans were validated");
            Assert.Equal(CapState.Confirmed, c.Env.State(Cap.FanControl));
            Assert.Equal(CapState.Untested, c.Env.State(Cap.ModeEco));
            Assert.Equal(CapState.Untested, c.Env.State(Cap.Key));
        }

        [Test]
        public static void Migration_NothingValidated_NoProfile()
        {
            Config c = new Config();
            Assert.True(EnvMigration.FromLegacy(c, "Nitro ANV15-51", "V1.60", Now) == null, "no legacy flags: nothing to migrate");
            EnvStatus s = EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.Equal(EnvStatus.NeverTested, s);
            Assert.True(c.Env == null, "stays null: the wizard is offered");
            Assert.True(!c.ManualFanAllowed, "locked");
            Assert.True(!c.ModeAllowed(PerfMode.DiaADia), "nothing is allowed before the test");
        }

        [Test]
        public static void Migration_OnlyFansValidated_ModesStayLocked()
        {
            Config c = ValidatedThisPc();
            c.ModeCheckPassed = false;
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.True(c.ManualFanAllowed, "fans migrated");
            Assert.True(!c.ModeAllowed(PerfMode.DiaADia), "modes were not validated");
        }

        [Test]
        public static void Migration_OnlyModesValidated_FansStayLocked()
        {
            Config c = new Config();
            c.ModeCheckPassed = true;
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.True(!c.ManualFanAllowed, "no fan validation");
            Assert.True(c.ModeAllowed(PerfMode.DiaADia), "modes migrated");
        }

        [Test]
        public static void Migration_UnknownModel_DoesNotInheritTheValidation()
        {
            // the legacy test only ever ran on the Nitro ANV15-51: another model must not inherit it
            Config c = ValidatedThisPc();
            Assert.Equal(EnvStatus.Stale, EnvStartup.Apply(c, "Nitro ANV15-99", "V1.00", Now));
            Assert.True(!c.ManualFanAllowed, "locked");
            Assert.True(c.Env == null, "no profile");
        }

        [Test]
        public static void Migration_IdentityUnreadable_RelocksEverything()
        {
            Config c = ValidatedThisPc();
            EnvStatus s = EnvStartup.Apply(c, null, null, Now);
            Assert.Equal(EnvStatus.Stale, s);
            Assert.True(c.EnvStale, "stale");
            Assert.True(!c.ManualFanAllowed, "fan control locked until the identity is known");
        }

        [Test]
        public static void SameFingerprint_Fresh()
        {
            Config c = ValidatedThisPc();
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            EnvStatus s = EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.Equal(EnvStatus.Fresh, s);
            Assert.True(!c.EnvStale, "fresh");
            Assert.True(c.ManualFanAllowed, "allowed");
        }

        [Test]
        public static void BiosUpdate_MarksStale_AndRelocksFansAndModes()
        {
            Config c = ValidatedThisPc();
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            EnvStatus s = EnvStartup.Apply(c, "Nitro ANV15-51", "V1.61", Now);
            Assert.Equal(EnvStatus.Stale, s);
            Assert.True(c.EnvStale, "stale flag");
            Assert.True(!c.ManualFanAllowed, "fan writes relocked");
            Assert.True(!c.ModeAllowed(PerfMode.DiaADia), "mode writes relocked");
            Assert.True(c.Env != null, "the old profile is kept (the wizard replaces it)");
        }

        [Test]
        public static void StaleClearsAfterANewProfile()
        {
            Config c = ValidatedThisPc();
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.61", Now);
            Assert.True(c.EnvStale, "stale");
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), AllPassed(), Now);
            p.Fingerprint = MachineFingerprint.Make("Nitro ANV15-51", "V1.61");
            EnvApply.Apply(c, p, AllPassed(), Now);
            Assert.True(!c.EnvStale, "the new profile belongs to this BIOS");
            Assert.True(c.ManualFanAllowed, "fan control back after the new test");
        }

        [Test]
        public static void OtherPc_Stale()
        {
            Config c = ValidatedThisPc();
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.Equal(EnvStatus.Stale, EnvStartup.Apply(c, "Predator PHN16-72", "V1.17", Now));
            Assert.True(!c.ManualFanAllowed, "locked on the other PC");
        }

        [Test]
        public static void ManualFanAllowed_NeedsEveryFanOfTheLayout()
        {
            Config c = new Config();
            c.FanControlValidated = true;
            c.FanMaxRpmCpu = 3000;
            c.FanMaxRpmGpu = 0;
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = "x|y";
            p.CpuFan = true;
            p.GpuFan = false;
            p.Set(Cap.FanControl, CapState.Confirmed, "ok");
            c.Env = p;
            Assert.True(c.ManualFanAllowed, "one fan: only its reference is needed");
            p.GpuFan = true;
            Assert.True(!c.ManualFanAllowed, "two fans: both references are needed");
            c.FanMaxRpmGpu = 2800;
            Assert.True(c.ManualFanAllowed, "both present");
            p.Set(Cap.FanControl, CapState.Untested, "x");
            Assert.True(!c.ManualFanAllowed, "profile does not confirm fan control");
        }

        [Test]
        public static void ManualFanAllowed_LegacyWithoutProfile_Unchanged()
        {
            Config c = new Config();
            Assert.True(!c.ManualFanAllowed, "default locked");
            c.FanControlValidated = true;
            Assert.True(!c.ManualFanAllowed, "no RPM reference");
            c.FanMaxRpmCpu = 100;
            c.FanMaxRpmGpu = 100;
            Assert.True(c.ManualFanAllowed, "legacy rules");
        }

        private static EnvTestResults AllPassed()
        {
            EnvTestResults t = new EnvTestResults();
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = KeyCandidates.OursId;
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = true;
            t.Fan.GpuOk = true;
            t.Fan.MaxRpmCpu = 3400;
            t.Fan.MaxRpmGpu = 3100;
            t.SetMode(PerfMode.Silencioso, StepStatus.Passed, true);
            t.SetMode(PerfMode.Desempenho, StepStatus.Passed, true);
            t.SetMode(PerfMode.Economico, StepStatus.Passed, true);
            t.SetMode(PerfMode.DiaADia, StepStatus.Passed, true);
            return t;
        }

        [Test]
        public static void Apply_WritesProfileAndLegacyFlags()
        {
            Config c = new Config();
            EnvTestResults t = AllPassed();
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, Now);
            EnvApply.Apply(c, p, t, Now);
            Assert.True(c.Env == p, "profile stored");
            Assert.True(c.FanControlValidated, "legacy fan flag");
            Assert.Equal(3400, c.FanMaxRpmCpu);
            Assert.Equal(3100, c.FanMaxRpmGpu);
            Assert.True(c.ModeCheckPassed, "legacy mode flag");
            Assert.Equal(Now, c.FanValidationUtc);
            Assert.True(c.ManualFanAllowed, "allowed");
            Assert.True(c.ModeAllowed(PerfMode.Economico), "Eco confirmed");
        }

        [Test]
        public static void Apply_FanSkipped_LocksAndResetsTheMode()
        {
            Config c = ValidatedThisPc();
            c.FanMode = "Curve";
            EnvTestResults t = AllPassed();
            t.Fan.Status = StepStatus.Skipped;
            t.Fan.MaxRpmCpu = 0;
            t.Fan.MaxRpmGpu = 0;
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, Now);
            EnvApply.Apply(c, p, t, Now);
            Assert.True(!c.FanControlValidated, "fan control locked");
            Assert.True(!c.ManualFanAllowed, "locked");
            Assert.Equal("Auto", c.FanMode);
            Assert.Equal(0, c.FanMaxRpmCpu);
        }

        [Test]
        public static void Apply_ModeFailed_ModeCheckNotPassed()
        {
            Config c = new Config();
            EnvTestResults t = AllPassed();
            t.SetMode(PerfMode.Desempenho, StepStatus.Failed, true);
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(CapabilityTests.ThisPc()), t, Now);
            EnvApply.Apply(c, p, t, Now);
            Assert.True(!c.ModeCheckPassed, "a failed mode is not a pass");
            Assert.True(!c.ModeAllowed(PerfMode.Desempenho), "that mode stays locked");
            Assert.True(c.ModeAllowed(PerfMode.Silencioso), "the others stay");
        }

        [Test]
        public static void LegacyFanTool_SyncsTheProfile()
        {
            Config c = ValidatedThisPc();
            EnvStartup.Apply(c, "Nitro ANV15-51", "V1.60", Now);
            FanValidationReport rep = new FanValidationReport();
            rep.Passed = false;
            rep.Aborted = true;
            rep.Reason = "temperatura chegou a 91 °C";
            rep.ApplyTo(c, DateTime.UtcNow);
            Assert.True(!c.FanControlValidated, "legacy flag revoked");
            Assert.True(!c.Env.Allows(Cap.FanControl), "the profile follows");
            Assert.True(!c.ManualFanAllowed, "locked");
        }

        [Test]
        public static void LegacyFanTool_WithoutProfile_IsUnchanged()
        {
            Config c = new Config();
            FanValidationReport rep = new FanValidationReport();
            rep.Passed = true;
            rep.Cpu = new FanCheck();
            rep.Cpu.Max = 3000;
            rep.Gpu = new FanCheck();
            rep.Gpu.Max = 2800;
            rep.ApplyTo(c, DateTime.UtcNow);
            Assert.True(c.FanControlValidated, "validated");
            Assert.True(c.Env == null, "no profile invented");
            Assert.True(c.ManualFanAllowed, "legacy rules");
        }
    }
}
