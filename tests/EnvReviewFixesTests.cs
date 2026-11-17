using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Fix round 1 of the review: migration only on the validated machine, the support CLIs obey the profile,
    // the fan test needs trusted temperatures, stricter garbage detection and a few cheap minors.
    public static class EnvReviewFixesTests
    {
        private const string Now = "2026-11-17T12:00:00Z";

        // ---- I2: migration only on the one machine that ever passed the legacy validation ----

        private static Config Legacy()
        {
            Config c = new Config();
            c.FanControlValidated = true;
            c.FanMaxRpmCpu = 3500;
            c.FanMaxRpmGpu = 3200;
            c.ModeCheckPassed = true;
            return c;
        }

        [Test]
        public static void Migration_OtherModel_IsStaleAndLocked_NotInherited()
        {
            Config c = Legacy(); // e.g. a roaming profile that reached another notebook
            EnvStatus s = EnvStartup.Apply(c, "Predator PHN16-72", "V1.17", Now);
            Assert.Equal(EnvStatus.Stale, s);
            Assert.True(c.Env == null, "no profile invented for a machine that never passed the test");
            Assert.True(c.EnvStale, "stale");
            Assert.True(!c.ManualFanAllowed, "fan control locked");
            Assert.True(!c.ModeAllowed(PerfMode.DiaADia), "modes locked");
        }

        [Test]
        public static void Migration_UnlistedModel_IsStale()
        {
            Config c = Legacy();
            Assert.Equal(EnvStatus.Stale, EnvStartup.Apply(c, "Nitro ANV15-99", "V1.00", Now));
            Assert.True(!c.ManualFanAllowed, "locked");
        }

        [Test]
        public static void Migration_FromLegacyDirect_OnlyForTheAnv1551()
        {
            Assert.True(EnvMigration.FromLegacy(Legacy(), "Nitro ANV15-52", "V1.53", Now) == null, "ANV15-52 is another machine");
            Assert.True(EnvMigration.FromLegacy(Legacy(), "Nitro ANV15-51", "V1.60", Now) != null, "the validated one");
            Assert.True(EnvMigration.FromLegacy(Legacy(), "nitro  anv15-51", "v1.60", Now) != null, "same model, other spelling");
        }

        [Test]
        public static void Migration_StaleOtherModel_ClearsAfterTheWizard()
        {
            Config c = Legacy();
            EnvStartup.Apply(c, "Predator PHN16-72", "V1.17", Now);
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PHN16-72";
            r.Bios = "V1.17";
            EnvTestResults t = new EnvTestResults();
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(r), t, Now);
            EnvApply.Apply(c, p, t, Now);
            Assert.True(!c.EnvStale, "the new profile belongs to this machine");
            Assert.True(!c.ManualFanAllowed, "but fan control still needs its own test");
        }

        // ---- I3: the support CLIs consult the profile ----

        private static Config Fresh(string model, string bios, DetectionRaw raw, EnvTestResults t)
        {
            Config c = new Config();
            c.Env = CapabilityBuilder.Build(DetectionResult.Interpret(raw), t ?? new EnvTestResults(), Now);
            c.FanControlValidated = true;
            c.FanMaxRpmCpu = 3000;
            c.FanMaxRpmGpu = 3000;
            Assert.Equal(MachineFingerprint.Make(model, bios), c.Env.Fingerprint);
            return c;
        }

        [Test]
        public static void Gate_StaleFingerprint_RefusesBothTools_PointingToTheEnvironmentTest()
        {
            Config c = Fresh("Nitro ANV15-51", "V1.60", CapabilityTests.ThisPc(), null);
            string fan = EnvGate.ForFanTool(c, "Nitro ANV15-51", "V1.61", Now);
            string perf = EnvGate.ForModeTool(c, "Nitro ANV15-51", "V1.61", Now);
            Assert.True(fan != null && fan.Contains("--teste-ambiente"), fan);
            Assert.True(perf != null && perf.Contains("--teste-ambiente"), perf);
        }

        [Test]
        public static void Gate_NeverTested_Refuses()
        {
            Config c = new Config();
            string fan = EnvGate.ForFanTool(c, "Nitro ANV15-51", "V1.60", Now);
            Assert.True(fan != null && fan.Contains("--teste-ambiente"), fan);
        }

        [Test]
        public static void Gate_ThisPc_AllowsBoth()
        {
            Config c = Fresh("Nitro ANV15-51", "V1.60", CapabilityTests.ThisPc(), null);
            Assert.Null(EnvGate.ForFanTool(c, "Nitro ANV15-51", "V1.60", Now));
            Assert.Null(EnvGate.ForModeTool(c, "Nitro ANV15-51", "V1.60", Now));
        }

        [Test]
        public static void Gate_FanControlUnavailable_RefusesTheFanTool()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro AN515-44";
            r.ProfileBitmapRaw = 0xFF00UL;
            Config c = Fresh("Nitro AN515-44", "V1.60", r, null);
            Config d = new Config();
            d.Env = c.Env;
            d.Env.Set(Cap.FanControl, CapState.Unavailable, "Uma ventoinha não seguiu o comando no teste.");
            string fan = EnvGate.ForFanTool(d, "Nitro AN515-44", "V1.60", Now);
            Assert.True(fan != null && fan.Contains("ventoinha"), fan);
        }

        [Test]
        public static void Gate_ProfilesNotAnnounced_RefusesTheModeTool()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PH315-53"; // no platform profile
            r.ProfileBitmapRaw = 0xFF00UL;
            Config c = Fresh("Predator PH315-53", "V1.60", r, null);
            string perf = EnvGate.ForModeTool(c, "Predator PH315-53", "V1.60", Now);
            Assert.True(perf != null && perf.Contains("modo"), perf);
        }

        [Test]
        public static void Gate_UnknownFanControl_Refuses()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PHN14-51"; // other sensor ids: temperatures not trusted
            Config c = Fresh("Predator PHN14-51", "V1.60", r, null);
            Assert.True(EnvGate.ForFanTool(c, "Predator PHN14-51", "V1.60", Now) != null, "refused");
        }

        [Test]
        public static void Gate_ModesCheckOfTheFanTool_OnlyWhenTheThreeModesAreAnnounced()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            Config ok = Fresh("Nitro ANV15-51", "V1.60", r, null);
            Assert.True(EnvGate.ModesAnnounced(ok.Env), "this PC announces them");
            ok.Env.Set(Cap.ModePerformance, CapState.Unavailable, "x");
            Assert.True(!EnvGate.ModesAnnounced(ok.Env), "one missing: no mode check");
        }

        // ---- I5: the fan test needs trusted temperatures ----

        [Test]
        public static void FanTest_Phn1451_PlausibleBitmapButOtherSensorIds_NoFanStep()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PHN14-51";
            DetectionResult d = DetectionResult.Interpret(r);
            CapabilityProfile pre = CapabilityBuilder.Build(d, new EnvTestResults(), Now);
            Assert.Equal(CapState.Unknown, pre.State(Cap.FanControl));
            Assert.True(!WizardFlow.Steps(d, pre, new List<ConflictItem>(), false).Contains(WizardStep.Fan), "no Fan step");
            Assert.True(pre.Reason(Cap.FanControl).Contains("temperatura"), pre.Reason(Cap.FanControl));
        }

        [Test]
        public static void FanTest_NoValidTemperatureAtDetection_NoFanStep()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Live.CpuTempC = null;
            r.Live.GpuTempC = null;
            DetectionResult d = DetectionResult.Interpret(r);
            CapabilityProfile pre = CapabilityBuilder.Build(d, new EnvTestResults(), Now);
            Assert.True(pre.State(Cap.FanControl) != CapState.Untested, "not offered");
            Assert.True(!WizardFlow.Steps(d, pre, new List<ConflictItem>(), false).Contains(WizardStep.Fan), "no Fan step");
        }

        [Test]
        public static void FanTest_ThisPc_StillOffered()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            DetectionResult d = DetectionResult.Interpret(r);
            CapabilityProfile pre = CapabilityBuilder.Build(d, new EnvTestResults(), Now);
            Assert.Equal(CapState.Untested, pre.State(Cap.FanControl));
            Assert.True(WizardFlow.Steps(d, pre, new List<ConflictItem>(), false).Contains(WizardStep.Fan), "Fan step");
        }

        // ---- plausibility of the temperatures over ~5 s ----

        private static List<SensorReading> Samples(params double[] cpu)
        {
            List<SensorReading> l = new List<SensorReading>();
            foreach (double c in cpu)
            {
                SensorReading s = new SensorReading();
                s.CpuTempC = c;
                l.Add(s);
            }
            return l;
        }

        [Test]
        public static void Plausible_SteadyReadings_Pass()
        {
            Assert.True(SensorPlausibility.Check(Samples(64, 64, 65, 64, 66)).Ok, "steady");
        }

        [Test]
        public static void Plausible_ZeroOr255Garbage_Fails()
        {
            Assert.True(!SensorPlausibility.Check(Samples(0, 0, 0, 0, 0)).Ok, "zeros");
            Assert.True(!SensorPlausibility.Check(Samples(255, 255, 255, 255, 255)).Ok, "255");
        }

        [Test]
        public static void Plausible_WildJumps_Fail()
        {
            Assert.True(!SensorPlausibility.Check(Samples(40, 90, 35, 88, 41)).Ok, "jumping");
        }

        [Test]
        public static void Plausible_TooFewValidSamples_Fail()
        {
            Assert.True(!SensorPlausibility.Check(Samples(64, 64)).Ok, "two samples");
            List<SensorReading> l = Samples(64, 64, 64);
            l[1].CpuTempC = null;
            l[2].CpuTempC = null;
            Assert.True(!SensorPlausibility.Check(l).Ok, "only one valid");
        }

        [Test]
        public static void Plausible_GpuOnlyIsEnough_WhenTheGpuIsSteady()
        {
            List<SensorReading> l = new List<SensorReading>();
            for (int i = 0; i < 5; i++)
            {
                SensorReading s = new SensorReading();
                s.GpuTempC = 50 + (i % 2);
                l.Add(s);
            }
            Assert.True(SensorPlausibility.Check(l).Ok, "gpu steady");
        }

        [Test]
        public static void Builder_UsesTheSamples_WhenThereAreEnough()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.LiveSamples = Samples(0, 0, 0, 0, 0);
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(r), new EnvTestResults(), Now);
            Assert.True(p.State(Cap.Sensors) != CapState.Confirmed, "garbage temperatures are never confirmed");
            Assert.True(p.State(Cap.FanControl) != CapState.Untested, "so no fan test");
            r.LiveSamples = Samples(64, 64, 65, 64, 64);
            p = CapabilityBuilder.Build(DetectionResult.Interpret(r), new EnvTestResults(), Now);
            Assert.Equal(CapState.Confirmed, p.State(Cap.Sensors));
        }

        // ---- M1: unknown profile bits ----

        [Test]
        public static void Profiles_UnknownBits_AreGarbage()
        {
            // 0xFE: bits 1..7 on; the kernel dropped this bitmap because of exactly this kind of answer
            ProfileBitmapInfo p = FirmwareBitmaps.DecodeProfiles(0xFE00UL, 1);
            Assert.True(!p.Plausible, "bits for codes 2, 3 and 7 do not exist");
            Assert.True(p.Reason.Contains("desconhec"), p.Reason);
            Assert.True(FirmwareBitmaps.DecodeProfiles(0x5300UL, 1).Plausible, "this PC still fine");
            Assert.True(FirmwareBitmaps.DecodeProfiles(0x7300UL, 1).Plausible, "all five known");
        }

        // ---- M2: Turbo families and unknown models ----

        [Test]
        public static void Turbo_UnlistedOcFamily_IsBlockedByThePrefix()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PH317-54"; // not an entry of the table, but an overclock family
            r.ProfileBitmapRaw = 0x7300UL;
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(r), new EnvTestResults(), Now);
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo));
        }

        [Test]
        public static void Turbo_ModelOutsideTheTable_IsNeverTested()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro ANV15-99";
            r.ProfileBitmapRaw = 0x7300UL;
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(r), new EnvTestResults(), Now);
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo));
            Assert.Equal(CapState.Untested, p.State(Cap.ModePerformance));
        }

        // ---- M6: a profile without a fingerprint never unlocks anything ----

        [Test]
        public static void Apply_WithoutFingerprint_StaysStale()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Bios = null; // identity unreadable during the wizard
            EnvTestResults t = new EnvTestResults();
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = true;
            t.Fan.GpuOk = true;
            t.Fan.MaxRpmCpu = 3000;
            t.Fan.MaxRpmGpu = 3000;
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(r), t, Now);
            Config c = new Config();
            EnvApply.Apply(c, p, t, Now);
            Assert.True(c.EnvStale, "stale");
            Assert.True(!c.ManualFanAllowed, "locked until the identity can be read");
        }

        // ---- M5: stale handling is consistent ----

        [Test]
        public static void StaleProfile_LayoutIsBoth()
        {
            Config c = new Config();
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = "x|y";
            p.CpuFan = true;
            c.Env = p;
            c.EnvStale = false;
            Assert.True(c.EnvFanLayout == FanLayout.CpuOnly, "fresh: the profile's layout");
            c.EnvStale = true;
            Assert.True(c.EnvFanLayout == FanLayout.Both, "stale: the legacy layout everywhere");
        }

        // ---- M7: a key timeout does not stop the key the app always used ----

        [Test]
        public static void KeyTimeout_KeepsListeningForTheOldCode()
        {
            CapabilityProfile p = new CapabilityProfile();
            p.Set(Cap.Key, CapState.Unknown, "x");
            KeyCandidate[] c = KeyCandidates.ForProfile(p);
            Assert.Equal(1, c.Length);
            Assert.True(c[0] == KeyCandidates.Ours, "the code it always used");
            p.Set(Cap.Key, CapState.Unavailable, "x");
            Assert.Equal(0, KeyCandidates.ForProfile(p).Length);
        }
    }
}
