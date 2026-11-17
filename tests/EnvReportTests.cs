using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Forja.Core;

namespace Forja.Tests
{
    // "Exportar relatório do modelo": model, BIOS, bitmaps, test results, app version. No serial numbers,
    // no user names, no paths with user names.
    public static class EnvReportTests
    {
        private const string Now = "2026-11-17T12:00:00Z";

        private static DetectionRaw Full()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.SystemFamily = "Acer_Nitro_V_15";
            r.Cpu = "13th Gen Intel(R) Core(TM) i5-13420H";
            r.Gpus.Add("NVIDIA GeForce RTX 4050 Laptop GPU");
            r.Gpus.Add("Intel(R) UHD Graphics");
            r.RamBytes = 16L * 1024 * 1024 * 1024;
            r.ActionClass = true;
            r.EventClass = true;
            return r;
        }

        private static EnvTestResults Tests()
        {
            EnvTestResults t = new EnvTestResults();
            t.Key.Status = StepStatus.Passed;
            t.Key.CandidateId = KeyCandidates.OursId;
            t.Fan.Status = StepStatus.Passed;
            t.Fan.CpuOk = true;
            t.Fan.GpuOk = true;
            t.Fan.MaxRpmCpu = 3500;
            t.Fan.MaxRpmGpu = 3200;
            t.SetMode(PerfMode.Silencioso, StepStatus.Passed, true);
            t.SetMode(PerfMode.Economico, StepStatus.Failed, true);
            t.SetMode(PerfMode.Desempenho, StepStatus.Skipped, false);
            t.SetMode(PerfMode.DiaADia, StepStatus.Passed, true);
            return t;
        }

        private static EnvReport Make(DetectionRaw raw, EnvTestResults t, string profileDir, string user)
        {
            DetectionResult d = DetectionResult.Interpret(raw);
            CapabilityProfile p = CapabilityBuilder.Build(d, t, Now);
            return EnvReport.Build(d, p, t, new List<ConflictItem>(), "1.2.3.4", Now, profileDir, user);
        }

        private static void ValidJson(string json)
        {
            using (XmlDictionaryReader rd = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max))
            {
                while (rd.Read()) { }
            }
        }

        [Test]
        public static void Text_HasTheFactsASupporterNeeds()
        {
            EnvReport rep = Make(Full(), Tests(), null, null);
            Assert.True(rep.Text.Contains("Nitro ANV15-51"), "model");
            Assert.True(rep.Text.Contains("V1.60"), "BIOS");
            Assert.True(rep.Text.Contains("0x227"), "sensor bitmap");
            Assert.True(rep.Text.Contains("0x53"), "profile bitmap");
            Assert.True(rep.Text.Contains("1.2.3.4"), "app version");
            Assert.True(rep.Text.Contains("i5-13420H"), "cpu");
            Assert.True(rep.Text.Contains("RTX 4050") && rep.Text.Contains("UHD"), "gpus");
            Assert.True(rep.Text.Contains("16"), "ram");
            Assert.True(rep.Text.Contains("2026-11-17"), "date");
        }

        [Test]
        public static void Text_HasTestResultsAndCapabilityStates()
        {
            EnvReport rep = Make(Full(), Tests(), null, null);
            Assert.True(rep.Text.Contains("Tecla"), "key test");
            Assert.True(rep.Text.Contains(KeyCandidates.OursId), "which key code answered");
            Assert.True(rep.Text.Contains("Econômico"), "eco mode");
            Assert.True(rep.Text.Contains("Disponível"), "state names");
            Assert.True(rep.Text.Contains("Indisponível"), "unavailable ones are listed too");
            Assert.True(rep.Text.Contains("3500"), "fan max reference");
        }

        [Test]
        public static void Json_IsWellFormedAndHasTheSameFacts()
        {
            EnvReport rep = Make(Full(), Tests(), null, null);
            ValidJson(rep.Json);
            Assert.True(rep.Json.Contains("\"model\""), "model key");
            Assert.True(rep.Json.Contains("Nitro ANV15-51"), "model value");
            Assert.True(rep.Json.Contains("\"sensorBitmap\""), "bitmap key");
            Assert.True(rep.Json.Contains("0x227"), "bitmap value");
            Assert.True(rep.Json.Contains("\"appVersion\""), "version key");
            Assert.True(rep.Json.Contains("\"capabilities\""), "capabilities");
        }

        [Test]
        public static void Json_EscapesQuotesBackslashesAndControlCharacters()
        {
            DetectionRaw r = Full();
            r.Cpu = "CPU \"quoted\" \\ back\tslash\r\nline";
            EnvReport rep = Make(r, Tests(), null, null);
            ValidJson(rep.Json);
        }

        [Test]
        public static void NoSerialNumbersAndNoUserNames()
        {
            DetectionRaw r = Full();
            // a value that carries a profile path (as an exception message or a driver string could)
            r.Gpus.Add("Adapter at C:\\Users\\maria.silva\\AppData\\x");
            r.Cpu = "CPU of maria.silva";
            EnvReport rep = Make(r, Tests(), "C:\\Users\\maria.silva", "maria.silva");
            Assert.True(!rep.Text.Contains("C:\\Users\\maria.silva"), "profile path scrubbed from the text");
            Assert.True(!rep.Json.Contains("maria.silva\\\\AppData"), "profile path scrubbed from the json");
            Assert.True(rep.Text.Contains("%USERPROFILE%"), "replaced by the variable");
            Assert.True(rep.Text.Contains("não contém número de série"), "the report states what it leaves out");
        }

        [Test]
        public static void PathsWithTheUserNameAreNeverWritten()
        {
            EnvReport rep = Make(Full(), Tests(), "C:\\Users\\carlos", "carlos");
            Assert.True(!rep.Text.ToLowerInvariant().Contains("\\users\\carlos"), "no profile path");
            Assert.True(!rep.Json.ToLowerInvariant().Contains("users\\\\carlos"), "no profile path in json");
        }

        [Test]
        public static void FingerprintInReport_IsModelPlusBiosOnly()
        {
            EnvReport rep = Make(Full(), Tests(), null, null);
            Assert.True(rep.Json.Contains("nitro anv15-51|v1.60"), "fingerprint");
        }

        [Test]
        public static void MachineWithoutAcerInterface_StillProducesAReport()
        {
            DetectionRaw r = new DetectionRaw();
            r.Manufacturer = "Dell Inc.";
            r.Model = "Latitude 5520";
            r.Bios = "1.2.3";
            EnvReport rep = Make(r, new EnvTestResults(), null, null);
            ValidJson(rep.Json);
            Assert.True(rep.Text.Contains("Latitude 5520"), "model");
            Assert.True(rep.Text.Contains("—") || rep.Text.Contains("não"), "missing values shown plainly");
        }

        [Test]
        public static void UnlistedModel_SaysSo()
        {
            DetectionRaw r = Full();
            r.Model = "Nitro ANV15-99";
            EnvReport rep = Make(r, new EnvTestResults(), null, null);
            Assert.True(rep.Text.Contains("fora da tabela") || rep.Text.Contains("não listado"), "says the model is not in the table");
        }

        [Test]
        public static void ConflictServices_AreListed()
        {
            DetectionRaw raw = Full();
            DetectionResult d = DetectionResult.Interpret(raw);
            CapabilityProfile p = CapabilityBuilder.Build(d, new EnvTestResults(), Now);
            ConflictItem c = new ConflictItem();
            c.Name = "ASMSvc";
            c.DisplayName = "Acer System Monitor Service";
            c.Running = true;
            c.StartMode = "Auto";
            c.NeedsAction = true;
            EnvReport rep = EnvReport.Build(d, p, new EnvTestResults(), new List<ConflictItem> { c }, "1.0.0.0", Now, null, null);
            Assert.True(rep.Text.Contains("ASMSvc"), "service name");
            ValidJson(rep.Json);
        }

        [Test]
        public static void SuggestedFileName_HasNoUserOrPcName()
        {
            string n = EnvReport.SuggestedFileName(Full(), new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
            Assert.True(n.StartsWith("relatorio-modelo-"), n);
            Assert.True(n.Contains("anv15-51"), n);
            Assert.True(n.Contains("20261117"), n);
            foreach (char bad in Path.GetInvalidFileNameChars()) Assert.True(n.IndexOf(bad) < 0, "invalid char " + (int)bad);
        }

        [Test]
        public static void SuggestedFileName_UnknownModel_StillValid()
        {
            DetectionRaw r = new DetectionRaw();
            string n = EnvReport.SuggestedFileName(r, new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
            Assert.True(n.StartsWith("relatorio-modelo-"), n);
            foreach (char bad in Path.GetInvalidFileNameChars()) Assert.True(n.IndexOf(bad) < 0, "invalid char");
        }
    }
}
