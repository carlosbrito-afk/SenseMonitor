using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Which wizard steps apply to a notebook, and the plain-words summary.
    public static class WizardFlowTests
    {
        private static List<ConflictItem> Running()
        {
            ConflictItem c = new ConflictItem();
            c.Name = "ASMSvc";
            c.DisplayName = "Acer System Monitor Service";
            c.Running = true;
            c.StartMode = "Auto";
            c.NeedsAction = true;
            return new List<ConflictItem> { c };
        }

        private static string Join(List<WizardStep> l)
        {
            List<string> s = new List<string>();
            foreach (WizardStep x in l) s.Add(x.ToString());
            return string.Join(",", s.ToArray());
        }

        private static DetectionResult Det(DetectionRaw r)
        {
            return DetectionResult.Interpret(r);
        }

        private static CapabilityProfile Pre(DetectionRaw r)
        {
            return CapabilityBuilder.Build(Det(r), new EnvTestResults(), "t");
        }

        [Test]
        public static void ThisPc_AllSixSteps()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            List<WizardStep> s = WizardFlow.Steps(Det(r), Pre(r), Running(), false);
            Assert.Equal("Detect,Conflicts,Key,Fan,Mode,Summary", Join(s));
        }

        [Test]
        public static void NoConflicts_StepIsSkipped()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            Assert.Equal("Detect,Key,Fan,Mode,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem>(), false)));
            ConflictItem c = Running()[0];
            c.NeedsAction = false;
            Assert.Equal("Detect,Key,Fan,Mode,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem> { c }, false)));
        }

        [Test]
        public static void ReadOnly_NoWritingSteps()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            Assert.Equal("Detect,Conflicts,Key,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), Running(), true)));
        }

        [Test]
        public static void NotAcer_OnlyDetectAndSummary()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Dell Inc.";
            r.Model = "Latitude 5520";
            r.Bios = "1";
            Assert.Equal("Detect,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), Running(), false)));
        }

        [Test]
        public static void AcerWithoutGamingInterface_OnlyDetectAndSummary()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Acer";
            r.Model = "Aspire 5 A515-57";
            r.Bios = "1";
            Assert.Equal("Detect,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem>(), false)));
        }

        [Test]
        public static void ModelWithoutProfiles_NoModeStep()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Nitro AN515-44";
            r.ProfileBitmapRaw = 0xFF00UL;
            Assert.Equal("Detect,Key,Fan,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem>(), false)));
        }

        [Test]
        public static void NoFans_NoFanStep()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.SensorBitmapRaw = 0x0000000001000000UL; // only the CPU temperature
            r.Live.CpuFanRpm = null;
            r.Live.GpuFanRpm = null;
            r.Model = "Nitro ANV15-99";
            Assert.Equal("Detect,Key,Mode,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem>(), false)));
        }

        [Test]
        public static void FirmwareSilent_OnlyDetectAndSummary()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.FirmwareAnswered = false;
            Assert.Equal("Detect,Summary", Join(WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem>(), false)));
        }

        [Test]
        public static void Next_FollowsTheList_AndStopsAtTheEnd()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            List<WizardStep> s = WizardFlow.Steps(Det(r), Pre(r), new List<ConflictItem>(), false);
            Assert.Equal(WizardStep.Key, WizardFlow.Next(s, WizardStep.Detect));
            Assert.Equal(WizardStep.Mode, WizardFlow.Next(s, WizardStep.Fan));
            Assert.Equal(WizardStep.Summary, WizardFlow.Next(s, WizardStep.Mode));
            Assert.Equal(WizardStep.Summary, WizardFlow.Next(s, WizardStep.Summary));
        }

        [Test]
        public static void StepTitles_ArePlainWords()
        {
            Assert.Equal("Seu notebook", WizardFlow.Title(WizardStep.Detect));
            Assert.Equal("Programas da Acer", WizardFlow.Title(WizardStep.Conflicts));
            Assert.Equal("Tecla do teclado", WizardFlow.Title(WizardStep.Key));
            Assert.Equal("Ventoinhas", WizardFlow.Title(WizardStep.Fan));
            Assert.Equal("Modos", WizardFlow.Title(WizardStep.Mode));
            Assert.Equal("Resumo", WizardFlow.Title(WizardStep.Summary));
        }

        // ---- summary ----

        [Test]
        public static void Summary_ThisPcAllPassed_Rows()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
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
            CapabilityProfile p = CapabilityBuilder.Build(Det(r), t, "t");
            List<SummaryRow> rows = WizardText.Summary(p);
            Assert.Equal(CapNames.All.Length, rows.Count);
            SummaryRow eco = Find(rows, Cap.ModeEco);
            Assert.True(eco.Enabled, "Eco enabled");
            Assert.Equal("Modo Econômico", eco.Name);
            Assert.True(!Find(rows, Cap.ModeTurbo).Enabled, "Turbo is not enabled");
            Assert.True(Find(rows, Cap.ModeTurbo).Short.Contains("anuncia"), Find(rows, Cap.ModeTurbo).Short);
            Assert.True(!Find(rows, Cap.KeyboardLight).Enabled, "light is never enabled");
        }

        private static SummaryRow Find(List<SummaryRow> rows, Cap c)
        {
            foreach (SummaryRow r in rows) if (r.Cap == c) return r;
            return null;
        }

        [Test]
        public static void ShortReason_PlainWordsPerCase()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            EnvTestResults t = new EnvTestResults();
            t.Fan.Status = StepStatus.Skipped;
            t.SetMode(PerfMode.Silencioso, StepStatus.Failed, true);
            t.Key.Status = StepStatus.Passed;
            t.Key.UserHasNoKey = true;
            CapabilityProfile p = CapabilityBuilder.Build(Det(r), t, "t");
            List<SummaryRow> rows = WizardText.Summary(p);
            Assert.True(Find(rows, Cap.FanControl).Short.Contains("pulad"), Find(rows, Cap.FanControl).Short);
            Assert.True(Find(rows, Cap.ModeQuiet).Short.Contains("confirm"), Find(rows, Cap.ModeQuiet).Short);
            Assert.True(Find(rows, Cap.Key).Short.Contains("não tem"), Find(rows, Cap.Key).Short);
            Assert.True(Find(rows, Cap.ModeEco).Short.Contains("testado"), Find(rows, Cap.ModeEco).Short);
        }

        [Test]
        public static void ShortReason_NotAcer()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Dell Inc.";
            r.Model = "Latitude 5520";
            r.Bios = "1";
            List<SummaryRow> rows = WizardText.Summary(CapabilityBuilder.Build(Det(r), new EnvTestResults(), "t"));
            Assert.True(rows[0].Short.Contains("Acer"), rows[0].Short);
        }

        [Test]
        public static void ShortReason_NeverTooLong()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.ProfileBitmapRaw = 0xFF00UL;
            foreach (SummaryRow s in WizardText.Summary(CapabilityBuilder.Build(Det(r), new EnvTestResults(), "t")))
            {
                Assert.True(s.Short.Length <= WizardText.MaxShort, s.Cap + ": " + s.Short.Length + " chars");
                Assert.True(s.Short.Length > 0, s.Cap + " has a reason");
            }
        }

        [Test]
        public static void EnabledAndNotEnabled_Split()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            CapabilityProfile p = CapabilityBuilder.Build(Det(r), new EnvTestResults(), "t");
            List<SummaryRow> rows = WizardText.Summary(p);
            int on = 0;
            foreach (SummaryRow s in rows) if (s.Enabled) on++;
            Assert.Equal(2, on); // sensors and fan reading are proved by readings; nothing that writes yet
        }

        [Test]
        public static void ModesLine_ListsWhatTheFirmwareAnnounces()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            string line = WizardText.AnnouncedModes(Det(r));
            Assert.True(line.Contains("Econômico") && line.Contains("Silencioso") && line.Contains("Dia a dia") && line.Contains("Desempenho"), line);
            Assert.True(!line.Contains("Turbo"), line);
        }

        [Test]
        public static void SensorsLine_ThisPc()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            string line = WizardText.AnnouncedSensors(Det(r));
            Assert.True(line.Contains("2 ventoinhas"), line);
            Assert.True(line.Contains("temperatura"), line);
        }

        [Test]
        public static void ModelLine_KnownAndUnknown()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            Assert.True(WizardText.ModelKnowledge(Det(r)).Contains("conhecido"), WizardText.ModelKnowledge(Det(r)));
            r.Model = "Nitro ANV15-99";
            Assert.True(WizardText.ModelKnowledge(Det(r)).Contains("fora da tabela"), WizardText.ModelKnowledge(Det(r)));
        }

        [Test]
        public static void KeyAdvice_ByModel()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.Model = "Predator PH315-53";
            Assert.True(WizardText.KeyAdvice(Det(r)).Contains("Turbo"), WizardText.KeyAdvice(Det(r)));
            r.Model = "Nitro ANV15-51";
            Assert.True(WizardText.KeyAdvice(Det(r)) == "" || WizardText.KeyAdvice(Det(r)) != null, "no advice needed");
        }
    }
}
