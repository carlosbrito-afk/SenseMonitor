using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The capability profile = firmware bitmaps AND model table AND guided-test results.
    public static class CapabilityTests
    {
        // This PC (Nitro ANV15-51, BIOS V1.60): the real bitmaps of docs\probe-idle.txt and live readings.
        public static DetectionRaw ThisPc()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Acer";
            r.Model = "Nitro ANV15-51";
            r.Bios = "V1.60";
            r.FirmwareClass = true;
            r.FirmwareAnswered = true;
            r.BatteryClass = true;
            r.KeyboardLightMethod = true;
            r.SensorBitmapRaw = 0x0000000227000000UL;
            r.ProfileBitmapRaw = 0x5300UL;
            r.ProfileCode = 1;
            r.Live = new SensorReading();
            r.Live.CpuTempC = 64;
            r.Live.GpuTempC = 49;
            r.Live.CpuFanRpm = 2961;
            r.Live.GpuFanRpm = 2597;
            return r;
        }

        internal static EnvTestResults AllPassedThisPc()
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
            return t;
        }

        internal static CapabilityProfile Build(DetectionRaw raw, EnvTestResults t)
        {
            return CapabilityBuilder.Build(DetectionResult.Interpret(raw), t ?? new EnvTestResults(), "2026-11-17T12:00:00Z");
        }

        [Test]
        public static void ThisPc_NoTestsYet_ReadingsConfirmedControlsLocked()
        {
            CapabilityProfile p = Build(ThisPc(), null);
            Assert.Equal(CapState.Confirmed, p.State(Cap.Sensors));
            Assert.Equal(CapState.Confirmed, p.State(Cap.FanRead));
            Assert.Equal(CapState.Untested, p.State(Cap.FanControl));
            Assert.Equal(CapState.Untested, p.State(Cap.ModeQuiet));
            Assert.Equal(CapState.Untested, p.State(Cap.ModeBalanced));
            Assert.Equal(CapState.Untested, p.State(Cap.ModePerformance));
            Assert.Equal(CapState.Untested, p.State(Cap.ModeEco));
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo));
            Assert.Equal(CapState.Untested, p.State(Cap.Key));
            Assert.True(!p.Allows(Cap.FanControl) && !p.Allows(Cap.ModeEco), "nothing writable before the tests");
            Assert.Equal(0, p.ConfirmedModes().Count);
        }

        [Test]
        public static void ThisPc_AllTestsPassed_Confirmed()
        {
            CapabilityProfile p = Build(ThisPc(), AllPassedThisPc());
            Assert.Equal(CapState.Confirmed, p.State(Cap.FanControl));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeQuiet));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeBalanced));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModePerformance));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeEco));
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo));
            Assert.Equal(CapState.Confirmed, p.State(Cap.Key));
            Assert.Equal(KeyCandidates.OursId, p.KeyCandidateId);
            List<PerfMode> modes = p.ConfirmedModes();
            Assert.Equal(4, modes.Count);
            Assert.Equal(PerfMode.Economico, modes[0]);
            Assert.Equal(PerfMode.Desempenho, modes[3]);
        }

        [Test]
        public static void ThisPc_FanLayoutAndBitmaps()
        {
            CapabilityProfile p = Build(ThisPc(), null);
            Assert.True(p.CpuFan && p.GpuFan, "two fans");
            Assert.Equal(2, p.FanCount);
            Assert.True(p.Layout == FanLayout.Both, "layout both");
            Assert.True(p.ProfileBitmapTrusted, "profile bitmap believed");
            Assert.Equal("0x227", p.SensorBitmap);
            Assert.Equal("0x53", p.ProfileBitmap);
            Assert.Equal("nitro anv15-51|v1.60", p.Fingerprint);
        }

        [Test]
        public static void ThisPc_TurboReasonSaysFirmwareDoesNotAnnounceIt()
        {
            CapabilityProfile p = Build(ThisPc(), null);
            Assert.True(p.Reason(Cap.ModeTurbo).Contains("anuncia"), p.Reason(Cap.ModeTurbo));
        }

        [Test]
        public static void ThisPc_KeyboardLightAndBatteryAreNeverConfirmed()
        {
            CapabilityProfile p = Build(ThisPc(), AllPassedThisPc());
            Assert.Equal(CapState.Untested, p.State(Cap.Battery));
            Assert.Equal(CapState.Unknown, p.State(Cap.KeyboardLight));
        }

        [Test]
        public static void SkippedFanTest_StaysLocked()
        {
            EnvTestResults t = AllPassedThisPc();
            t.Fan.Status = StepStatus.Skipped;
            CapabilityProfile p = Build(ThisPc(), t);
            Assert.Equal(CapState.Untested, p.State(Cap.FanControl));
            Assert.True(p.Reason(Cap.FanControl).Contains("pulad"), p.Reason(Cap.FanControl));
            Assert.True(!p.Allows(Cap.FanControl), "locked");
        }

        [Test]
        public static void FailedFanTest_Unavailable()
        {
            EnvTestResults t = AllPassedThisPc();
            t.Fan.Status = StepStatus.Failed;
            t.Fan.GpuOk = false;
            t.Fan.Reason = "GPU — a ventoinha não seguiu o comando de 50%";
            CapabilityProfile p = Build(ThisPc(), t);
            Assert.Equal(CapState.Unavailable, p.State(Cap.FanControl));
            Assert.True(p.Reason(Cap.FanControl).Contains("GPU"), p.Reason(Cap.FanControl));
        }

        [Test]
        public static void SkippedModeTest_StaysLocked()
        {
            EnvTestResults t = AllPassedThisPc();
            t.SetMode(PerfMode.Economico, StepStatus.Skipped, false);
            CapabilityProfile p = Build(ThisPc(), t);
            Assert.Equal(CapState.Untested, p.State(Cap.ModeEco));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeQuiet));
        }

        [Test]
        public static void FailedMode_Unavailable_OthersKept()
        {
            EnvTestResults t = AllPassedThisPc();
            t.SetMode(PerfMode.Desempenho, StepStatus.Failed, true);
            CapabilityProfile p = Build(ThisPc(), t);
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModePerformance));
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeQuiet));
        }

        [Test]
        public static void ModeAcceptedButStateDidNotChange_IsNotProof()
        {
            // the firmware answers "ok" to a write that changes nothing: read-back proves only a changed state
            EnvTestResults t = AllPassedThisPc();
            t.SetMode(PerfMode.Silencioso, StepStatus.Passed, false);
            CapabilityProfile p = Build(ThisPc(), t);
            Assert.Equal(CapState.Untested, p.State(Cap.ModeQuiet));
        }

        [Test]
        public static void GarbageProfileBitmap_FallsBackToTable()
        {
            DetectionRaw r = ThisPc();
            r.ProfileBitmapRaw = 0xFF00UL; // the kernel distrusts exactly this kind of answer
            CapabilityProfile p = Build(r, null);
            Assert.True(!p.ProfileBitmapTrusted, "bitmap not believed");
            Assert.Equal(CapState.Untested, p.State(Cap.ModeQuiet));
            Assert.Equal(CapState.Untested, p.State(Cap.ModeEco));
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo));
            Assert.True(p.Reason(Cap.ModeQuiet).Contains("tabela"), p.Reason(Cap.ModeQuiet));
        }

        [Test]
        public static void GarbageProfileBitmap_UnknownModel_ModesUnknown()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Nitro ANV15-99";
            r.ProfileBitmapRaw = 0x0000UL;
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unknown, p.State(Cap.ModeQuiet));
            Assert.Equal(CapState.Unknown, p.State(Cap.ModeBalanced));
            Assert.Equal(0, p.ConfirmedModes().Count);
        }

        [Test]
        public static void UnknownModel_PlausibleBitmap_ModesUntestedIncludingTurbo()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Nitro ANV15-99";
            r.ProfileBitmapRaw = 0x7300UL;
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo)); // Turbo is never tested outside the table
            Assert.Equal(CapState.Untested, p.State(Cap.ModePerformance));
            Assert.True(p.ProfileBitmapTrusted, "believed");
        }

        [Test]
        public static void TurboDangerFlaggedInTable_NeverOffered()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Predator PH315-53";
            r.ProfileBitmapRaw = 0x7300UL; // even if the firmware announces Turbo
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeTurbo));
            Assert.True(p.Reason(Cap.ModeTurbo).Contains("overclock") || p.Reason(Cap.ModeTurbo).Contains("tabela"), p.Reason(Cap.ModeTurbo));
        }

        [Test]
        public static void TurboAnnouncedAndTableAllows_UntestedUntilConfirmed()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Predator PHN16-71";
            r.ProfileBitmapRaw = 0x7300UL;
            EnvTestResults t = new EnvTestResults();
            CapabilityProfile p = Build(r, t);
            Assert.Equal(CapState.Untested, p.State(Cap.ModeTurbo));
            t.SetMode(PerfMode.Turbo, StepStatus.Passed, true);
            p = Build(r, t);
            Assert.Equal(CapState.Confirmed, p.State(Cap.ModeTurbo));
        }

        [Test]
        public static void TableSaysNoProfiles_ButBitmapAnnounces_TestDecides()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Nitro AN515-44"; // no platform profile in the table
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Untested, p.State(Cap.ModeQuiet));
            Assert.True(p.Reason(Cap.ModeQuiet).Contains("tabela"), "conflict is stated: " + p.Reason(Cap.ModeQuiet));
        }

        [Test]
        public static void TableSaysNoProfiles_BitmapGarbage_Unavailable()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Nitro AN515-44";
            r.ProfileBitmapRaw = 0xFF00UL;
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeQuiet));
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeBalanced));
        }

        [Test]
        public static void Anv1552_BiosDependentProfile_ReasonMentionsBios()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Nitro ANV15-52";
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Untested, p.State(Cap.ModeQuiet));
            Assert.True(p.Reason(Cap.ModeQuiet).Contains("BIOS"), p.Reason(Cap.ModeQuiet));
        }

        [Test]
        public static void Phn1451_SensorIdsDiffer_SensorsUnknownEvenWithAReading()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Predator PHN14-51";
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unknown, p.State(Cap.Sensors));
        }

        [Test]
        public static void OneFanLaptop_LayoutCpuOnly_ControlNeedsOneFanToPass()
        {
            DetectionRaw r = ThisPc();
            r.SensorBitmapRaw = 0x0000000003000000UL; // CPU temp + CPU fan
            r.Live.GpuFanRpm = null;
            r.Live.GpuTempC = null;
            EnvTestResults t = new EnvTestResults();
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = true;
            CapabilityProfile p = Build(r, t);
            Assert.True(p.CpuFan && !p.GpuFan, "one fan");
            Assert.True(p.Layout == FanLayout.CpuOnly, "layout cpu only");
            Assert.Equal(CapState.Confirmed, p.State(Cap.FanControl));
        }

        [Test]
        public static void FanAnnouncedButStopped_FanReadUntested()
        {
            // 0 RPM can be a fan that stops at idle: announced, not proven
            DetectionRaw r = ThisPc();
            r.Live.CpuFanRpm = 0;
            r.Live.GpuFanRpm = 0;
            CapabilityProfile p = Build(r, null);
            Assert.True(p.CpuFan && p.GpuFan, "announced fans count");
            Assert.Equal(CapState.Untested, p.State(Cap.FanRead));
        }

        [Test]
        public static void GarbageSensorBitmap_UsesLiveReadings()
        {
            DetectionRaw r = ThisPc();
            r.SensorBitmapRaw = 0x000000FFFF000000UL;
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Confirmed, p.State(Cap.Sensors));
            Assert.True(p.CpuFan && p.GpuFan, "fans from the live readings");
        }

        [Test]
        public static void NoReadingsAtAll_Unknown()
        {
            DetectionRaw r = ThisPc();
            r.SensorBitmapRaw = null;
            r.Live = new SensorReading();
            r.Model = "Nitro ANV15-99";
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unknown, p.State(Cap.Sensors));
            Assert.Equal(0, p.FanCount);
            Assert.True(p.Layout == null, "no fan layout");
            Assert.Equal(CapState.Unavailable, p.State(Cap.FanControl));
        }

        [Test]
        public static void NotAcer_EverythingUnavailable()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Dell Inc.";
            r.Model = "Latitude 5520";
            r.Bios = "1.2.3";
            CapabilityProfile p = Build(r, null);
            foreach (Cap c in CapNames.All)
            {
                Assert.Equal(CapState.Unavailable, p.State(c));
            }
            Assert.True(p.Reason(Cap.FanControl).Contains("Acer"), p.Reason(Cap.FanControl));
            Assert.Equal(0, p.ConfirmedModes().Count);
        }

        [Test]
        public static void AcerWithoutGamingInterface_EverythingUnavailable()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Acer";
            r.Model = "Aspire 5 A515-57";
            r.Bios = "V1.10";
            r.FirmwareClass = false;
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unavailable, p.State(Cap.FanControl));
            Assert.Equal(CapState.Unavailable, p.State(Cap.ModeBalanced));
            Assert.True(p.Reason(Cap.FanControl).Contains("AcerGamingFunction"), p.Reason(Cap.FanControl));
        }

        [Test]
        public static void ClassPresentButFirmwareSilent_Unknown()
        {
            // not elevated or the provider failed: the class exists but no instance answers
            DetectionRaw r = ThisPc();
            r.FirmwareAnswered = false;
            r.SensorBitmapRaw = null;
            r.ProfileBitmapRaw = null;
            r.ProfileCode = null;
            r.Live = new SensorReading();
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unknown, p.State(Cap.Sensors));
            Assert.Equal(CapState.Unknown, p.State(Cap.ModeQuiet));
            Assert.True(!p.Allows(Cap.ModeBalanced), "never allowed");
            Assert.True(p.Reason(Cap.Sensors).Contains("administrador"), p.Reason(Cap.Sensors));
        }

        [Test]
        public static void Key_Results()
        {
            DetectionRaw r = ThisPc();
            EnvTestResults t = new EnvTestResults();
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = KeyCandidates.AtF5Id;
            CapabilityProfile p = Build(r, t);
            Assert.Equal(CapState.Confirmed, p.State(Cap.Key));
            Assert.Equal(KeyCandidates.AtF5Id, p.KeyCandidateId);

            t = new EnvTestResults();
            t.Key.Status = StepStatus.Passed;
            t.Key.UserHasNoKey = true;
            p = Build(r, t);
            Assert.Equal(CapState.Unavailable, p.State(Cap.Key));
            Assert.True(p.KeyCandidateId == null || p.KeyCandidateId == "", "no candidate");

            t = new EnvTestResults();
            t.Key.Status = StepStatus.Failed;
            p = Build(r, t);
            Assert.Equal(CapState.Unknown, p.State(Cap.Key));

            t = new EnvTestResults();
            t.Key.Status = StepStatus.Skipped;
            p = Build(r, t);
            Assert.Equal(CapState.Untested, p.State(Cap.Key));
        }

        [Test]
        public static void Key_FailedOnTurboButtonModel_ExplainsTheChannel()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Predator PH315-53";
            EnvTestResults t = new EnvTestResults();
            t.Key.Status = StepStatus.Failed;
            CapabilityProfile p = Build(r, t);
            Assert.True(p.Reason(Cap.Key).Contains("firmware") || p.Reason(Cap.Key).Contains("WMI"), p.Reason(Cap.Key));
        }

        [Test]
        public static void ReadOnlyMode_WritesNotTested_StayLocked()
        {
            EnvTestResults t = new EnvTestResults();
            t.ReadOnly = true;
            CapabilityProfile p = Build(ThisPc(), t);
            Assert.Equal(CapState.Untested, p.State(Cap.FanControl));
            Assert.True(p.Reason(Cap.FanControl).Contains("somente leitura"), p.Reason(Cap.FanControl));
        }

        [Test]
        public static void Battery_ClassAbsent_Unavailable_TableNo_Unavailable()
        {
            DetectionRaw r = ThisPc();
            r.BatteryClass = false;
            Assert.Equal(CapState.Unavailable, Build(r, null).State(Cap.Battery));
            r = ThisPc();
            r.Model = "Predator PH315-52";
            Assert.Equal(CapState.Unavailable, Build(r, null).State(Cap.Battery));
        }

        [Test]
        public static void KeyboardLight_HidModel_Unavailable()
        {
            DetectionRaw r = ThisPc();
            r.Model = "Predator PHN16S-71";
            r.EneKb5130 = true;
            CapabilityProfile p = Build(r, null);
            Assert.Equal(CapState.Unavailable, p.State(Cap.KeyboardLight));
            Assert.True(p.Reason(Cap.KeyboardLight).Contains("I2C"), p.Reason(Cap.KeyboardLight));
        }

        [Test]
        public static void ProfileSurvivesJsonRoundTrip()
        {
            CapabilityProfile p = Build(ThisPc(), AllPassedThisPc());
            Config c = new Config();
            c.Env = p;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-env-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                c.Save(path);
                Config back = Config.Load(path);
                Assert.True(back.Env != null, "profile loaded");
                Assert.Equal(p.Fingerprint, back.Env.Fingerprint);
                Assert.Equal(CapState.Confirmed, back.Env.State(Cap.ModeEco));
                Assert.Equal(CapState.Unavailable, back.Env.State(Cap.ModeTurbo));
                Assert.True(back.Env.CpuFan && back.Env.GpuFan, "fans");
                Assert.Equal(KeyCandidates.OursId, back.Env.KeyCandidateId);
                Assert.Equal(p.Entries.Count, back.Env.Entries.Count);
            }
            finally
            {
                foreach (string f in new string[] { path, path + ".bak", path + ".tmp" })
                {
                    try { System.IO.File.Delete(f); } catch (System.Exception) { }
                }
            }
        }

        [Test]
        public static void UnreadableStateIsNeverConfirmed()
        {
            CapEntry e = new CapEntry();
            e.Id = "Sensors";
            e.State = "garbage";
            Assert.Equal(CapState.Unknown, e.StateValue);
            e.State = null;
            Assert.Equal(CapState.Unknown, e.StateValue);
        }
    }

    public static class FingerprintTests
    {
        [Test]
        public static void ModelPlusBios_Normalized()
        {
            Assert.Equal("nitro anv15-51|v1.60", MachineFingerprint.Make("Nitro ANV15-51", "V1.60"));
            Assert.Equal("nitro anv15-51|v1.60", MachineFingerprint.Make("  Nitro   ANV15-51 ", "v1.60  "));
        }

        [Test]
        public static void MissingPart_NoFingerprint()
        {
            Assert.Null(MachineFingerprint.Make(null, "V1.60"));
            Assert.Null(MachineFingerprint.Make("Nitro ANV15-51", ""));
            Assert.Null(MachineFingerprint.Make("  ", "  "));
        }

        [Test]
        public static void ControlCharactersRemoved()
        {
            Assert.Equal("nitro anv15-51|v1.60", MachineFingerprint.Make("Nitro\tANV15-51\u0000", "V1.60\r\n"));
        }

        [Test]
        public static void SerialNumbersAreNotPartOfIt()
        {
            // only the two inputs exist: no serial, UUID or user name can reach the string
            string fp = MachineFingerprint.Make("Nitro ANV15-51", "V1.60");
            Assert.True(fp.IndexOf('|') > 0 && fp.Split('|').Length == 2, "exactly model|bios");
        }

        [Test]
        public static void Evaluate_NoProfile_NeverTested()
        {
            Assert.Equal(EnvStatus.NeverTested, EnvCheck.Evaluate(null, "nitro anv15-51|v1.60"));
        }

        [Test]
        public static void Evaluate_SameMachine_Fresh()
        {
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = "nitro anv15-51|v1.60";
            Assert.Equal(EnvStatus.Fresh, EnvCheck.Evaluate(p, "nitro anv15-51|v1.60"));
            Assert.Equal(EnvStatus.Fresh, EnvCheck.Evaluate(p, "NITRO ANV15-51|V1.60"));
        }

        [Test]
        public static void Evaluate_BiosUpdate_Stale()
        {
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = "nitro anv15-51|v1.60";
            Assert.Equal(EnvStatus.Stale, EnvCheck.Evaluate(p, "nitro anv15-51|v1.61"));
        }

        [Test]
        public static void Evaluate_OtherPc_Stale()
        {
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = "nitro anv15-51|v1.60";
            Assert.Equal(EnvStatus.Stale, EnvCheck.Evaluate(p, "predator phn16-72|v1.17"));
        }

        [Test]
        public static void Evaluate_IdentityUnreadable_StaleSoWritesRelock()
        {
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = "nitro anv15-51|v1.60";
            Assert.Equal(EnvStatus.Stale, EnvCheck.Evaluate(p, null));
        }

        [Test]
        public static void Evaluate_StoredWithoutFingerprint_Stale()
        {
            CapabilityProfile p = new CapabilityProfile();
            Assert.Equal(EnvStatus.Stale, EnvCheck.Evaluate(p, "nitro anv15-51|v1.60"));
        }
    }
}
