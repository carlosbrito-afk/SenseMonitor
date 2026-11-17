using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // "Já testado neste notebook": what an earlier run proved is recognised only on the same model + BIOS, carried into the new
    // run when the step is skipped, and the key step says what the model table knows.
    public static class PriorTestsTests
    {
        private static DetectionResult Det()
        {
            return DetectionResult.Interpret(CapabilityTests.ThisPc());
        }

        private static CapabilityProfile Proven(string utc, string fanMethod)
        {
            DetectionResult d = Det();
            EnvTestResults t = new EnvTestResults();
            EnvResultMap.KeyPassed(t, KeyCandidates.Ours);
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = true;
            t.Fan.GpuOk = true;
            t.Fan.MaxRpmCpu = 7461;
            t.Fan.MaxRpmGpu = 6900;
            t.Fan.Method = fanMethod;
            t.SetMode(PerfMode.Silencioso, StepStatus.Passed, true);
            t.SetMode(PerfMode.DiaADia, StepStatus.Passed, true);
            return CapabilityBuilder.Build(d, t, utc);
        }

        [Test]
        public static void Stamp_ConfirmedTests_RecordWhenAndHow()
        {
            CapabilityProfile p = Proven("2026-11-10T12:00:00Z", "manual");
            CapEntry fan = p.Find(Cap.FanControl);
            Assert.Equal("manual", fan.Method);
            Assert.Equal("2026-11-10T12:00:00Z", fan.Utc);
            Assert.Equal("2026-11-10T12:00:00Z", p.Find(Cap.Key).Utc);
            Assert.True(p.Find(Cap.Sensors).Utc == null, "readings are not guided tests: no stamp");
        }

        [Test]
        public static void Stamp_SurvivesTheConfigFile()
        {
            string path = System.IO.Path.Combine(TestRunner.Sandbox, "stamp-config.json");
            Config c = new Config();
            c.Env = Proven("2026-11-10T12:00:00Z", "automático");
            c.Save(path);
            Config r = Config.Load(path);
            Assert.Equal("automático", r.Env.Find(Cap.FanControl).Method);
            Assert.Equal("2026-11-10T12:00:00Z", r.Env.Find(Cap.FanControl).Utc);
            System.IO.File.Delete(path);
        }

        [Test]
        public static void Prior_SameNotebook_IsRecognised()
        {
            CapabilityProfile p = Proven("2026-11-10T12:00:00Z", "manual");
            string fp = Det().Fingerprint;
            Assert.True(PriorTests.Key(p, fp) != null, "key");
            Assert.True(PriorTests.Fan(p, fp) != null, "fan");
            PriorTest m = PriorTests.Mode(p, fp, PerfMode.Silencioso);
            Assert.True(m != null, "mode");
            Assert.True(PriorTests.Mode(p, fp, PerfMode.Desempenho) == null, "a mode that was never confirmed has no prior");
            Assert.Equal("manual", PriorTests.Fan(p, fp).Method);
        }

        [Test]
        public static void Prior_AnotherNotebookOrBios_IsNeverReused()
        {
            CapabilityProfile p = Proven("2026-11-10T12:00:00Z", "manual");
            Assert.True(PriorTests.Fan(p, "Nitro ANV15-52|V1.99") == null, "another model and BIOS");
            Assert.True(PriorTests.Fan(p, null) == null, "identity unknown");
            Assert.True(PriorTests.Fan(null, Det().Fingerprint) == null, "no saved profile");
            string biosChanged = Det().Fingerprint + "x";
            Assert.True(PriorTests.Key(p, biosChanged) == null, "a changed BIOS asks again");
        }

        [Test]
        public static void Prior_UserSaidNoKey_IsNotATest()
        {
            DetectionResult d = Det();
            EnvTestResults t = new EnvTestResults();
            EnvResultMap.KeyNone(t);
            CapabilityProfile p = CapabilityBuilder.Build(d, t, "2026-11-10T12:00:00Z");
            Assert.True(PriorTests.Key(p, d.Fingerprint) == null, "no key answered nothing");
        }

        [Test]
        public static void Prior_FanThatFailedBefore_IsNotReused()
        {
            DetectionResult d = Det();
            EnvTestResults t = new EnvTestResults();
            t.Fan.Status = StepStatus.Failed;
            t.Fan.Reason = "CPU — x";
            CapabilityProfile p = CapabilityBuilder.Build(d, t, "2026-11-10T12:00:00Z");
            Assert.True(PriorTests.Fan(p, d.Fingerprint) == null, "a failed test is not a prior pass");
        }

        [Test]
        public static void Carry_KeepsEverythingConfirmed_AndTheOriginalDate()
        {
            CapabilityProfile saved = Proven("2026-11-10T12:00:00Z", "manual");
            DetectionResult d = Det();
            string fp = d.Fingerprint;
            EnvTestResults t = new EnvTestResults();
            PriorTests.CarryKey(t, saved, PriorTests.Key(saved, fp));
            PriorTests.CarryFan(t, saved, PriorTests.Fan(saved, fp), 7461, 6900);
            PriorTests.CarryMode(t, PerfMode.Silencioso, PriorTests.Mode(saved, fp, PerfMode.Silencioso));
            PriorTests.CarryMode(t, PerfMode.DiaADia, PriorTests.Mode(saved, fp, PerfMode.DiaADia));
            CapabilityProfile next = CapabilityBuilder.Build(d, t, "2026-11-17T09:00:00Z");
            Assert.Equal(CapState.Confirmed, next.State(Cap.Key));
            Assert.Equal(KeyCandidates.OursId, next.KeyCandidateId);
            Assert.Equal(CapState.Confirmed, next.State(Cap.FanControl));
            Assert.Equal(CapState.Confirmed, next.State(Cap.ModeQuiet));
            Assert.Equal("2026-11-10T12:00:00Z", next.Find(Cap.FanControl).Utc);
            Assert.Equal("manual", next.Find(Cap.FanControl).Method);
            Assert.Equal(CapState.Untested, next.State(Cap.ModePerformance));
            Assert.Equal(7461, t.Fan.MaxRpmCpu);
        }

        [Test]
        public static void DateText_ReadsTheStamp()
        {
            Assert.Equal("data desconhecida", PriorTests.DateText(null));
            Assert.Equal("data desconhecida", PriorTests.DateText("lixo"));
            Assert.True(PriorTests.DateText("2026-11-10T12:00:00Z").StartsWith("10/11/2026"), PriorTests.DateText("2026-11-10T12:00:00Z"));
            PriorTest p = new PriorTest();
            p.Utc = "2026-11-10T12:00:00Z";
            p.Method = "manual";
            string text = WizardText.PriorText(p, "testada");
            Assert.True(text.StartsWith("Já testada neste notebook em 10/11/2026") && text.Contains("manual"), text);
        }

        // ---- key knowledge from the model table ----

        [Test]
        public static void KeyKnowledge_NitroV15_IsConfirmedWithTheKnownCode()
        {
            string t = WizardText.KeyKnowledge(Det());
            Assert.True(t.StartsWith("Tecla confirmada no Nitro V 15 (ANV15-51): código conhecido"), t);
        }

        [Test]
        public static void KeyKnowledge_ExpectedButNeverTestedModel_SaysNotConfirmed()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro ANV15-41";
            string t = WizardText.KeyKnowledge(DetectionResult.Interpret(r));
            Assert.True(t.Contains("Nitro V 15 (ANV15-41)") && t.Contains("não confirmada"), t);
        }

        [Test]
        public static void KeyKnowledge_UnknownModel_SaysSo()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro XYZ99-99";
            string t = WizardText.KeyKnowledge(DetectionResult.Interpret(r));
            Assert.True(t.Contains("fora da tabela") && t.Contains("não está confirmada"), t);
        }

        [Test]
        public static void KeyKnowledge_ModelWithoutAKeyboardKey_KeepsTheChannelAdvice()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PH315-53";
            string t = WizardText.KeyKnowledge(DetectionResult.Interpret(r));
            Assert.True(t.Contains("evento do firmware"), t);
        }
    }
}
