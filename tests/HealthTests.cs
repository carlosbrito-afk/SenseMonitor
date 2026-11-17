using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Samples below are synthetic (no personal data): shaped like the real System log
    // events and powercfg battery report measured on the target machine.
    public static class CrashParseTests
    {
        private const string Msg1001Pt =
            "O computador foi reinicializado após uma verificação de erro. Essa verificação foi: " +
            "0x000001c7 (0x0000000000000001, 0xfffff80123456789, 0x0000000000000000, 0x0000000000000000). " +
            "Um despejo foi salvo em: C:\\Windows\\Minidump\\111326-12345-01.dmp. " +
            "ID do relatório: 3f5c2b1a-1111-2222-3333-444455556666.";

        private const string Xml41 =
            "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">" +
            "<System><Provider Name=\"Microsoft-Windows-Kernel-Power\" Guid=\"{331C3B3A-2005-44C2-AC5E-77220C37D6B4}\" />" +
            "<EventID>41</EventID><TimeCreated SystemTime=\"2026-11-13T14:02:11.1234567Z\" /></System>" +
            "<EventData><Data Name=\"BugcheckCode\">455</Data><Data Name=\"BugcheckParameter1\">0x1</Data>" +
            "<Data Name=\"BugcheckParameter2\">0xfffff80123456789</Data><Data Name=\"SleepInProgress\">0</Data></EventData></Event>";

        private static DateTime T(int h, int m, int s)
        {
            return new DateTime(2026, 11, 13, h, m, s);
        }

        private static Kernel41Event K(DateTime when, long code)
        {
            Kernel41Event k = new Kernel41Event();
            k.When = when;
            k.BugcheckCode = code;
            return k;
        }

        private static Wer1001Event W(DateTime when, string msg)
        {
            Wer1001Event w = new Wer1001Event();
            w.When = when;
            w.Message = msg;
            return w;
        }

        private static DumpFileInfo D(string path, DateTime when)
        {
            DumpFileInfo d = new DumpFileInfo();
            d.Path = path;
            d.Time = when;
            return d;
        }

        [Test]
        public static void ParseBugcheck_Pt1001()
        {
            long? c = CrashParse.ParseBugcheckFromMessage(Msg1001Pt);
            Assert.True(c.HasValue, "expected a code");
            Assert.Equal(0x1c7L, c.Value);
        }

        [Test]
        public static void ParseBugcheck_English_And_Garbage()
        {
            long? c = CrashParse.ParseBugcheckFromMessage("The computer has rebooted from a bugcheck.  The bugcheck was: 0x00000133 (0x0, 0x1).");
            Assert.Equal(0x133L, c.Value);
            Assert.Null(CrashParse.ParseBugcheckFromMessage(null));
            Assert.Null(CrashParse.ParseBugcheckFromMessage(""));
            Assert.Null(CrashParse.ParseBugcheckFromMessage("sem codigo nenhum aqui"));
        }

        [Test]
        public static void ParseDumpPath_FromMessage()
        {
            Assert.Equal("C:\\Windows\\Minidump\\111326-12345-01.dmp", CrashParse.ParseDumpPathFromMessage(Msg1001Pt));
            Assert.Null(CrashParse.ParseDumpPathFromMessage("sem caminho"));
            Assert.Null(CrashParse.ParseDumpPathFromMessage(null));
        }

        [Test]
        public static void ParseEvent41_Xml_ReadsCodeAndParam1()
        {
            long code;
            ulong? p1;
            Assert.True(CrashParse.TryParseEvent41(Xml41, out code, out p1), "should parse");
            Assert.Equal(455L, code);
            Assert.True(p1.HasValue && p1.Value == 1UL, "param1 should be 1");
        }

        [Test]
        public static void ParseEvent41_BadXml_False()
        {
            long code;
            ulong? p1;
            Assert.True(!CrashParse.TryParseEvent41("<oops", out code, out p1), "bad xml");
            Assert.True(!CrashParse.TryParseEvent41(null, out code, out p1), "null xml");
            Assert.True(!CrashParse.TryParseEvent41("<Event><EventData><Data Name=\"Other\">1</Data></EventData></Event>", out code, out p1), "no BugcheckCode");
        }

        [Test]
        public static void ParseEvent41_HexCodeText()
        {
            long code;
            ulong? p1;
            string xml = "<Event><EventData><Data Name=\"BugcheckCode\">0x1c7</Data></EventData></Event>";
            Assert.True(CrashParse.TryParseEvent41(xml, out code, out p1), "should parse hex");
            Assert.Equal(0x1c7L, code);
            Assert.True(!p1.HasValue, "no param1");
        }

        [Test]
        public static void FormatCode_Hex()
        {
            Assert.Equal("0x1C7", CrashParse.FormatCode(455));
            Assert.Equal("0xA", CrashParse.FormatCode(10));
            Assert.Equal("0x1000007E", CrashParse.FormatCode(0x1000007eL));
        }

        [Test]
        public static void Describe_KnownCodes_HaveSymbolAndHint()
        {
            long[] codes = { 0xA, 0x3B, 0x50, 0x7E, 0xD1, 0xEF, 0x124, 0x133, 0x139, 0x1C7 };
            string[] syms =
            {
                "IRQL_NOT_LESS_OR_EQUAL", "SYSTEM_SERVICE_EXCEPTION", "PAGE_FAULT_IN_NONPAGED_AREA",
                "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "CRITICAL_PROCESS_DIED",
                "WHEA_UNCORRECTABLE_ERROR", "DPC_WATCHDOG_VIOLATION", "KERNEL_SECURITY_CHECK_FAILURE",
                "STORE_DATA_STRUCTURE_CORRUPTION"
            };
            for (int i = 0; i < codes.Length; i++)
            {
                CrashCodeInfo info = CrashParse.Describe(codes[i]);
                Assert.Equal(syms[i], info.Symbol);
                Assert.True(!string.IsNullOrEmpty(info.Hint), "hint for " + syms[i]);
                Assert.True(!info.Modified, "not modified " + syms[i]);
            }
        }

        [Test]
        public static void Describe_ModifiedFlag_UsesBaseCode()
        {
            CrashCodeInfo info = CrashParse.Describe(0x1000007eL);
            Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", info.Symbol);
            Assert.True(info.Modified, "modified flag");
        }

        [Test]
        public static void Describe_Unknown_NoSymbol()
        {
            CrashCodeInfo info = CrashParse.Describe(0x77777L);
            Assert.Null(info.Symbol);
            Assert.True(!string.IsNullOrEmpty(info.Hint), "unknown still has a hint");
        }

        [Test]
        public static void Merge_41And1001_Within2Min_OneEntry()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(11, 2, 11), 455));
            List<Wer1001Event> w = new List<Wer1001Event>();
            w.Add(W(T(11, 3, 0), Msg1001Pt));
            List<DumpFileInfo> d = new List<DumpFileInfo>();
            d.Add(D("C:\\Windows\\Minidump\\111326-12345-01.dmp", T(10, 58, 0)));
            List<CrashEntry> r = CrashParse.Merge(k, w, d);
            Assert.Equal(1, r.Count);
            Assert.Equal(0x1c7L, r[0].Code.Value);
            Assert.True(r[0].HasDump, "dump by path");
            Assert.Equal("C:\\Windows\\Minidump\\111326-12345-01.dmp", r[0].DumpPath);
            Assert.Equal(T(11, 2, 11), r[0].When);
        }

        [Test]
        public static void Merge_1001MoreThan2MinAway_TwoEntries()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(11, 2, 0), 455));
            List<Wer1001Event> w = new List<Wer1001Event>();
            w.Add(W(T(11, 4, 1), Msg1001Pt));
            List<CrashEntry> r = CrashParse.Merge(k, w, new List<DumpFileInfo>());
            Assert.Equal(2, r.Count);
        }

        [Test]
        public static void Merge_Exactly2Min_Merges()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(11, 2, 0), 455));
            List<Wer1001Event> w = new List<Wer1001Event>();
            w.Add(W(T(11, 4, 0), Msg1001Pt));
            List<CrashEntry> r = CrashParse.Merge(k, w, new List<DumpFileInfo>());
            Assert.Equal(1, r.Count);
        }

        [Test]
        public static void Merge_41WithCodeZero_NoCode()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(9, 0, 0), 0));
            List<CrashEntry> r = CrashParse.Merge(k, new List<Wer1001Event>(), new List<DumpFileInfo>());
            Assert.Equal(1, r.Count);
            Assert.True(!r[0].Code.HasValue, "no code");
            Assert.True(!r[0].HasDump, "no dump");
            Assert.Null(r[0].DumpPath);
        }

        [Test]
        public static void Merge_41CodeZero_1001SuppliesCode()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(9, 0, 0), 0));
            List<Wer1001Event> w = new List<Wer1001Event>();
            w.Add(W(T(9, 0, 30), "A verificação foi: 0x0000007e (0x1, 0x2)."));
            List<CrashEntry> r = CrashParse.Merge(k, w, new List<DumpFileInfo>());
            Assert.Equal(1, r.Count);
            Assert.Equal(0x7eL, r[0].Code.Value);
        }

        [Test]
        public static void Merge_41ModifiedCodeKept()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(9, 0, 0), 0x1000007eL));
            List<CrashEntry> r = CrashParse.Merge(k, new List<Wer1001Event>(), new List<DumpFileInfo>());
            Assert.Equal(0x1000007eL, r[0].Code.Value);
            Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", r[0].Info.Symbol);
        }

        [Test]
        public static void Merge_1001Alone_Entry()
        {
            List<Wer1001Event> w = new List<Wer1001Event>();
            w.Add(W(T(8, 0, 0), Msg1001Pt));
            List<CrashEntry> r = CrashParse.Merge(new List<Kernel41Event>(), w, new List<DumpFileInfo>());
            Assert.Equal(1, r.Count);
            Assert.Equal(0x1c7L, r[0].Code.Value);
            Assert.True(!r[0].HasDump, "dump file not in list, so not marked present");
        }

        [Test]
        public static void Merge_NewestFirst()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(8, 0, 0), 10));
            k.Add(K(T(12, 0, 0), 59));
            k.Add(K(T(10, 0, 0), 80));
            List<CrashEntry> r = CrashParse.Merge(k, new List<Wer1001Event>(), new List<DumpFileInfo>());
            Assert.Equal(3, r.Count);
            Assert.Equal(59L, r[0].Code.Value);
            Assert.Equal(80L, r[1].Code.Value);
            Assert.Equal(10L, r[2].Code.Value);
        }

        [Test]
        public static void Merge_1001PicksNearest41()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(10, 0, 0), 10));
            k.Add(K(T(10, 2, 0), 0));
            List<Wer1001Event> w = new List<Wer1001Event>();
            w.Add(W(T(10, 1, 40), "0x0000007e (0x1)"));
            List<CrashEntry> r = CrashParse.Merge(k, w, new List<DumpFileInfo>());
            Assert.Equal(2, r.Count);
            // The 1001 is nearer the 10:02 event, so that one gets 0x7E; the 10:00 one keeps 0xA.
            Assert.Equal(0x7eL, r[0].Code.Value);
            Assert.Equal(10L, r[1].Code.Value);
        }

        [Test]
        public static void Merge_DumpByTimestamp_WindowAndSingleUse()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(11, 0, 0), 59));
            k.Add(K(T(11, 0, 50), 80));
            List<DumpFileInfo> d = new List<DumpFileInfo>();
            d.Add(D("C:\\Windows\\Minidump\\a.dmp", T(10, 57, 0)));
            List<CrashEntry> r = CrashParse.Merge(k, new List<Wer1001Event>(), d);
            // One dump, two events: the nearer event (11:00:00, 3 min after the dump) claims it.
            int with = 0;
            foreach (CrashEntry e in r) if (e.HasDump) with++;
            Assert.Equal(1, with);
            Assert.True(r[1].HasDump && r[1].Code.Value == 59, "11:00:00 entry owns the dump");
            Assert.Equal("C:\\Windows\\Minidump\\a.dmp", r[1].DumpPath);
        }

        [Test]
        public static void Merge_DumpTooOld_NotMatched()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(T(11, 0, 0), 59));
            List<DumpFileInfo> d = new List<DumpFileInfo>();
            d.Add(D("C:\\Windows\\Minidump\\old.dmp", T(9, 0, 0)));
            d.Add(D("C:\\Windows\\Minidump\\future.dmp", T(11, 30, 0)));
            List<CrashEntry> r = CrashParse.Merge(k, new List<Wer1001Event>(), d);
            Assert.True(!r[0].HasDump, "no dump in window");
        }

        [Test]
        public static void Merge_NullInputs_Empty()
        {
            List<CrashEntry> r = CrashParse.Merge(null, null, null);
            Assert.Equal(0, r.Count);
        }
    }

    public static class BatteryParseTests
    {
        // Mirrors the real powercfg report: default namespace, and a RuntimeEstimates block that
        // reuses the element name DesignCapacity (a naive search would hit that one).
        private const string Sample =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<BatteryReport xmlns=\"http://schemas.microsoft.com/battery/2012\">" +
            "<ReportInformation><ReportVersion>1.1</ReportVersion></ReportInformation>" +
            "<Batteries><Battery><Id>TEST1</Id><Chemistry>LION</Chemistry>" +
            "<DesignCapacity>57488</DesignCapacity><FullChargeCapacity>51359</FullChargeCapacity>" +
            "<CycleCount>114</CycleCount></Battery></Batteries>" +
            "<RuntimeEstimates><DesignCapacity><Capacity>57488</Capacity><ActiveRuntime>PT2H11M58S</ActiveRuntime></DesignCapacity>" +
            "<FullChargeCapacity><Capacity>51359</Capacity></FullChargeCapacity></RuntimeEstimates></BatteryReport>";

        [Test]
        public static void Parse_Sample_Values()
        {
            BatteryInfo b = BatteryParse.Parse(Sample);
            Assert.True(b != null, "parsed");
            Assert.Equal(57488, b.DesignMwh);
            Assert.Equal(51359, b.FullChargeMwh);
            Assert.True(b.CycleCount.HasValue && b.CycleCount.Value == 114, "cycles");
            Assert.True(Math.Abs(b.HealthPct - 89.34) < 0.05, "health " + b.HealthPct);
            Assert.True(Math.Abs(b.WearPct - 10.66) < 0.05, "wear " + b.WearPct);
        }

        [Test]
        public static void Parse_MissingCycleCount_Null()
        {
            string xml = Sample.Replace("<CycleCount>114</CycleCount>", "");
            BatteryInfo b = BatteryParse.Parse(xml);
            Assert.True(b != null, "parsed");
            Assert.True(!b.CycleCount.HasValue, "no cycles");
        }

        [Test]
        public static void Parse_Invalid_Null()
        {
            Assert.Null(BatteryParse.Parse(null));
            Assert.Null(BatteryParse.Parse(""));
            Assert.Null(BatteryParse.Parse("<BatteryReport"));
            Assert.Null(BatteryParse.Parse("<BatteryReport><Batteries/></BatteryReport>"));
        }

        [Test]
        public static void Parse_ZeroDesign_Null()
        {
            string xml = Sample.Replace("<DesignCapacity>57488</DesignCapacity><FullChargeCapacity>", "<DesignCapacity>0</DesignCapacity><FullChargeCapacity>");
            Assert.Null(BatteryParse.Parse(xml));
        }

        [Test]
        public static void Parse_FullAboveDesign_HealthCappedAt100()
        {
            string xml = Sample.Replace("<FullChargeCapacity>51359</FullChargeCapacity><CycleCount>", "<FullChargeCapacity>60000</FullChargeCapacity><CycleCount>");
            BatteryInfo b = BatteryParse.Parse(xml);
            Assert.True(b.HealthPct <= 100.0, "capped");
            Assert.True(b.WearPct >= 0.0, "wear non-negative");
        }

        [Test]
        public static void Parse_FirstValidBatteryWins()
        {
            string xml =
                "<BatteryReport xmlns=\"http://schemas.microsoft.com/battery/2012\"><Batteries>" +
                "<Battery><DesignCapacity></DesignCapacity><FullChargeCapacity>0</FullChargeCapacity></Battery>" +
                "<Battery><DesignCapacity>40000</DesignCapacity><FullChargeCapacity>30000</FullChargeCapacity><CycleCount>7</CycleCount></Battery>" +
                "</Batteries></BatteryReport>";
            BatteryInfo b = BatteryParse.Parse(xml);
            Assert.Equal(40000, b.DesignMwh);
            Assert.Equal(7, b.CycleCount.Value);
        }
    }

    public static class DiskMapTests
    {
        [Test]
        public static void HealthText_Maps()
        {
            Assert.Equal("Saudável", DiskMap.HealthText(0));
            Assert.Equal("Atenção", DiskMap.HealthText(1));
            Assert.Equal("Com problema", DiskMap.HealthText(2));
            Assert.Equal("Não informado", DiskMap.HealthText(5));
            Assert.Equal("Não informado", DiskMap.HealthText(99));
            Assert.Equal("Não informado", DiskMap.HealthText(null));
        }

        [Test]
        public static void HealthLevel_Maps()
        {
            Assert.Equal(DiskHealthLevel.Ok, DiskMap.Level(0));
            Assert.Equal(DiskHealthLevel.Warning, DiskMap.Level(1));
            Assert.Equal(DiskHealthLevel.Bad, DiskMap.Level(2));
            Assert.Equal(DiskHealthLevel.Unknown, DiskMap.Level(5));
            Assert.Equal(DiskHealthLevel.Unknown, DiskMap.Level(null));
        }

        [Test]
        public static void OperationalText_Maps()
        {
            Assert.Equal("OK", DiskMap.OperationalText(new int[] { 2 }));
            Assert.Equal("Degradado, Falha prevista", DiskMap.OperationalText(new int[] { 3, 5 }));
            Assert.Equal("Não informado", DiskMap.OperationalText(null));
            Assert.Equal("Não informado", DiskMap.OperationalText(new int[0]));
            Assert.Equal("Desconhecido", DiskMap.OperationalText(new int[] { 777 }));
        }

        [Test]
        public static void MediaAndBus_Maps()
        {
            Assert.Equal("SSD", DiskMap.MediaTypeText(4));
            Assert.Equal("HDD", DiskMap.MediaTypeText(3));
            Assert.Null(DiskMap.MediaTypeText(0));
            Assert.Null(DiskMap.MediaTypeText(null));
            Assert.Equal("NVMe", DiskMap.BusTypeText(17));
            Assert.Equal("SATA", DiskMap.BusTypeText(11));
            Assert.Null(DiskMap.BusTypeText(0));
            Assert.Null(DiskMap.BusTypeText(null));
        }

        [Test]
        public static void Sanitize_Values()
        {
            Assert.True(!DiskMap.SanitizeTemp(0).HasValue, "0 C is not a real reading");
            Assert.True(!DiskMap.SanitizeTemp(-5).HasValue, "negative");
            Assert.True(!DiskMap.SanitizeTemp(500).HasValue, "absurd");
            Assert.Equal(41, DiskMap.SanitizeTemp(41).Value);
            Assert.True(!DiskMap.SanitizeTemp(null).HasValue, "null");
            Assert.Equal(0, DiskMap.SanitizeWear(0).Value);
            Assert.Equal(100, DiskMap.SanitizeWear(100).Value);
            Assert.True(!DiskMap.SanitizeWear(101).HasValue, "wear above 100");
            Assert.True(!DiskMap.SanitizeWear(255).HasValue, "wear 255 means unknown");
            Assert.True(!DiskMap.SanitizeWear(-1).HasValue, "wear negative");
            Assert.True(!DiskMap.SanitizeHours(0).HasValue, "0 hours is unknown");
            Assert.Equal(1234L, DiskMap.SanitizeHours(1234).Value);
            Assert.True(!DiskMap.SanitizeHours(-1).HasValue, "negative hours");
        }

        [Test]
        public static void HasAnyReliability_Detects()
        {
            DiskInfo d = new DiskInfo();
            Assert.True(!d.HasReliability, "empty has none");
            d.WearPct = 3;
            Assert.True(d.HasReliability, "wear counts");
            d = new DiskInfo();
            d.ReadErrors = 0;
            Assert.True(d.HasReliability, "zero errors is still a reading");
        }
    }

    public static class HealthFormatTests
    {
        private static readonly CultureInfo Br = new CultureInfo("pt-BR");

        [Test]
        public static void Plural_BlueScreens()
        {
            Assert.Equal("1 tela azul", HealthFormat.BlueScreens(1));
            Assert.Equal("3 telas azuis", HealthFormat.BlueScreens(3));
            Assert.Equal("0 telas azuis", HealthFormat.BlueScreens(0));
        }

        [Test]
        public static void Plural_UnexpectedShutdowns()
        {
            Assert.Equal("1 desligamento inesperado sem código", HealthFormat.UnexpectedShutdowns(1));
            Assert.Equal("7 desligamentos inesperados sem código", HealthFormat.UnexpectedShutdowns(7));
        }

        [Test]
        public static void Wh_Format()
        {
            Assert.Equal("57,5 Wh", HealthFormat.Wh(57488));
            Assert.Equal("51,4 Wh", HealthFormat.Wh(51359));
        }

        [Test]
        public static void Percent_RoundsToInteger()
        {
            Assert.Equal("89%", HealthFormat.Percent(89.34));
            Assert.Equal("100%", HealthFormat.Percent(100.0));
        }

        [Test]
        public static void DiskSize_Decimal()
        {
            Assert.Equal("512 GB", HealthFormat.DiskSize(512110190592L));
            Assert.Equal("1 TB", HealthFormat.DiskSize(1000204886016L));
            Assert.Equal("2 TB", HealthFormat.DiskSize(2000398934016L));
            Assert.Equal("não informado", HealthFormat.DiskSize(null));
        }

        [Test]
        public static void Temp_And_Hours_And_Count()
        {
            Assert.Equal("41 °C", HealthFormat.TempC(41.2, "x"));
            Assert.Equal("x", HealthFormat.TempC(null, "x"));
            Assert.Equal("1.234 h", HealthFormat.Hours(1234));
            Assert.Equal("não informado", HealthFormat.Hours(null));
            Assert.Equal("0", HealthFormat.Count(0));
            Assert.Equal("não informado", HealthFormat.Count(null));
            Assert.Equal("3%", HealthFormat.WearText(3));
            Assert.Equal("não informado", HealthFormat.WearText(null));
        }

        [Test]
        public static void Minutes_Format()
        {
            Assert.Equal("0 min", HealthFormat.Minutes(0));
            Assert.Equal("12 min", HealthFormat.Minutes(12));
            Assert.Equal("1 h 05 min", HealthFormat.Minutes(65));
            Assert.Equal("2 h 00 min", HealthFormat.Minutes(120));
        }

        [Test]
        public static void DayLabel_ContainsDate()
        {
            string s = HealthFormat.DayLabel(new DateTime(2026, 11, 16));
            Assert.True(s.EndsWith("16/11"), "label was " + s);
            Assert.True(!s.Contains("."), "no dots");
        }
    }

    public static class TempLogTests
    {
        private static SensorReading R(double? cpu, double? gpu, int? cpuRpm, int? gpuRpm)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            r.CpuFanRpm = cpuRpm;
            r.GpuFanRpm = gpuRpm;
            return r;
        }

        [Test]
        public static void FormatLine_InvariantCulture_NullsEmpty()
        {
            CultureInfo old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("pt-BR");
                string line = TempLog.FormatLine(new DateTime(2026, 11, 16, 20, 47, 9), R(54.26, null, 3100, null));
                Assert.Equal("20:47:09,54.3,,3100,", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = old;
            }
        }

        [Test]
        public static void FormatLine_NullReading_AllEmpty()
        {
            Assert.Equal("01:02:03,,,,", TempLog.FormatLine(new DateTime(2026, 1, 1, 1, 2, 3), null));
        }

        [Test]
        public static void ParseLine_RoundTrip()
        {
            DateTime day = new DateTime(2026, 11, 16);
            string line = TempLog.FormatLine(new DateTime(2026, 11, 16, 20, 47, 9), R(54.26, 61.0, 3100, 3300));
            TempSample s = TempLog.ParseLine(day, line);
            Assert.True(s != null, "parsed");
            Assert.Equal(new DateTime(2026, 11, 16, 20, 47, 9), s.Time);
            Assert.True(Math.Abs(s.CpuC.Value - 54.3) < 0.001, "cpu");
            Assert.True(Math.Abs(s.GpuC.Value - 61.0) < 0.001, "gpu");
            Assert.Equal(3100, s.CpuRpm.Value);
            Assert.Equal(3300, s.GpuRpm.Value);
        }

        [Test]
        public static void ParseLine_EmptyFields_Null()
        {
            TempSample s = TempLog.ParseLine(new DateTime(2026, 11, 16), "20:47:09,,70.5,,");
            Assert.True(s != null, "parsed");
            Assert.True(!s.CpuC.HasValue, "cpu null");
            Assert.True(s.GpuC.HasValue, "gpu set");
            Assert.True(!s.CpuRpm.HasValue && !s.GpuRpm.HasValue, "rpm null");
        }

        [Test]
        public static void ParseLine_Malformed_Null()
        {
            DateTime day = new DateTime(2026, 11, 16);
            Assert.Null(TempLog.ParseLine(day, null));
            Assert.Null(TempLog.ParseLine(day, ""));
            Assert.Null(TempLog.ParseLine(day, TempLog.Header));
            Assert.Null(TempLog.ParseLine(day, "garbage"));
            Assert.Null(TempLog.ParseLine(day, "25:99:00,1,2,3,4"));
            Assert.Null(TempLog.ParseLine(day, "20:47:09,abc,2,3,4"));
        }

        [Test]
        public static void Summarize_MaxAvgAndMinutesAbove()
        {
            DateTime day = new DateTime(2026, 11, 16);
            List<TempSample> list = new List<TempSample>();
            list.Add(Sample(day, 1, 50, 40));
            list.Add(Sample(day, 2, 85, null));
            list.Add(Sample(day, 3, 95, 60));
            DaySummary d = TempLog.Summarize(day, list);
            Assert.Equal(3, d.Samples);
            Assert.True(Math.Abs(d.CpuMax.Value - 95) < 0.001, "cpu max");
            Assert.True(Math.Abs(d.CpuAvg.Value - 76.6667) < 0.01, "cpu avg");
            Assert.True(Math.Abs(d.GpuMax.Value - 60) < 0.001, "gpu max");
            Assert.True(Math.Abs(d.GpuAvg.Value - 50) < 0.001, "gpu avg");
            Assert.Equal(2, d.MinutesAbove80);
            Assert.Equal(1, d.MinutesAbove90);
        }

        [Test]
        public static void Summarize_ThresholdIsStrictlyAbove()
        {
            DateTime day = new DateTime(2026, 11, 16);
            List<TempSample> list = new List<TempSample>();
            list.Add(Sample(day, 1, 80, null));
            list.Add(Sample(day, 2, 90, null));
            DaySummary d = TempLog.Summarize(day, list);
            Assert.Equal(1, d.MinutesAbove80);
            Assert.Equal(0, d.MinutesAbove90);
        }

        [Test]
        public static void Summarize_EitherSensorCountsOncePerMinute()
        {
            DateTime day = new DateTime(2026, 11, 16);
            List<TempSample> list = new List<TempSample>();
            list.Add(Sample(day, 1, 91, 92));
            DaySummary d = TempLog.Summarize(day, list);
            Assert.Equal(1, d.MinutesAbove80);
            Assert.Equal(1, d.MinutesAbove90);
        }

        [Test]
        public static void Summarize_Empty_NullStats()
        {
            DaySummary d = TempLog.Summarize(new DateTime(2026, 11, 16), new List<TempSample>());
            Assert.Equal(0, d.Samples);
            Assert.True(!d.CpuMax.HasValue && !d.CpuAvg.HasValue && !d.GpuMax.HasValue && !d.GpuAvg.HasValue, "null stats");
        }

        [Test]
        public static void FileName_And_ParseDay()
        {
            Assert.Equal("2026-11-16.csv", TempLog.FileNameFor(new DateTime(2026, 11, 16, 23, 59, 0)));
            DateTime d;
            Assert.True(TempLog.TryParseFileDay("2026-11-16.csv", out d), "valid");
            Assert.Equal(new DateTime(2026, 11, 16), d);
            Assert.True(!TempLog.TryParseFileDay("notes.csv", out d), "other file");
            Assert.True(!TempLog.TryParseFileDay("2026-13-40.csv", out d), "bad date");
            Assert.True(!TempLog.TryParseFileDay("2026-11-16.txt", out d), "wrong ext");
        }

        [Test]
        public static void SelectExpired_Keeps14Days_IgnoresForeignFiles()
        {
            DateTime today = new DateTime(2026, 11, 16);
            string[] names =
            {
                "2026-11-16.csv", "2026-11-03.csv", "2026-11-02.csv", "2026-10-01.csv",
                "readme.txt", "backup.csv", "2026-11-02.csv.bak"
            };
            List<string> exp = TempLog.SelectExpired(names, today, 14);
            Assert.Equal(2, exp.Count);
            Assert.True(exp.Contains("2026-11-02.csv"), "expired");
            Assert.True(exp.Contains("2026-10-01.csv"), "expired old");
            Assert.True(!exp.Contains("2026-11-03.csv"), "13 days old is kept");
        }

        [Test]
        public static void SelectForDays_LastNDaysIncludingToday()
        {
            DateTime today = new DateTime(2026, 11, 16);
            string[] names = { "2026-11-16.csv", "2026-11-10.csv", "2026-11-09.csv", "junk.csv", "2026-11-12.csv" };
            List<string> sel = TempLog.SelectForDays(names, today, 7);
            Assert.Equal(3, sel.Count);
            Assert.True(sel.Contains("2026-11-16.csv") && sel.Contains("2026-11-12.csv") && sel.Contains("2026-11-10.csv"), "window 10..16");
        }

        private static TempSample Sample(DateTime day, int minute, double? cpu, double? gpu)
        {
            TempSample s = new TempSample();
            s.Time = day.AddMinutes(minute);
            s.CpuC = cpu;
            s.GpuC = gpu;
            return s;
        }
    }

    public static class TempLogStoreTests
    {
        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sm-templog-test-" + Guid.NewGuid().ToString("N"));
            return dir;
        }

        private static SensorReading R(double cpu, double gpu)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            r.CpuFanRpm = 3000;
            return r;
        }

        [Test]
        public static void Append_WritesHeaderAndOneLinePerMinute()
        {
            string dir = NewDir();
            try
            {
                TempLogStore s = new TempLogStore(dir);
                DateTime t = new DateTime(2026, 11, 16, 10, 0, 5);
                s.Append(t, R(50, 40));
                s.Append(t.AddSeconds(20), R(51, 41));   // same minute: ignored
                s.Append(t.AddMinutes(1), R(60, 45));
                string[] lines = File.ReadAllLines(Path.Combine(dir, "2026-11-16.csv"));
                Assert.Equal(3, lines.Length);
                Assert.Equal(TempLog.Header, lines[0]);
                Assert.Equal("10:00:05,50.0,40.0,3000,", lines[1]);
                Assert.True(lines[2].StartsWith("10:01:05,60.0"), "second line " + lines[2]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void Append_NewDay_NewFile_AndPurgesOld()
        {
            string dir = NewDir();
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "2026-11-01.csv"), TempLog.Header + "\r\n");
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep me");
                TempLogStore s = new TempLogStore(dir);
                s.Append(new DateTime(2026, 11, 16, 23, 59, 0), R(50, 40));
                s.Append(new DateTime(2026, 11, 17, 0, 0, 0), R(51, 41));
                Assert.True(!File.Exists(Path.Combine(dir, "2026-11-01.csv")), "old purged");
                Assert.True(File.Exists(Path.Combine(dir, "notes.txt")), "foreign file untouched");
                Assert.True(File.Exists(Path.Combine(dir, "2026-11-16.csv")), "day 1 file");
                Assert.True(File.Exists(Path.Combine(dir, "2026-11-17.csv")), "day 2 file");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void Append_TornLastLine_StartsOnANewLine()
        {
            string dir = NewDir();
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "2026-11-16.csv");
                File.WriteAllText(path, TempLog.Header + "\r\n10:00:05,50.0");
                TempLogStore s = new TempLogStore(dir);
                s.Append(new DateTime(2026, 11, 16, 10, 1, 5), R(60, 45));
                string[] lines = File.ReadAllLines(path);
                Assert.Equal(3, lines.Length);
                Assert.Equal("10:00:05,50.0", lines[1]);
                Assert.True(lines[2].StartsWith("10:01:05,60.0"), "new line " + lines[2]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void Append_CappedWrite_NotRetriedInSameMinute()
        {
            string dir = NewDir();
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "2026-11-16.csv");
                File.WriteAllBytes(path, new byte[TempLogStore.MaxFileBytes + 10]);
                TempLogStore s = new TempLogStore(dir);
                s.Append(new DateTime(2026, 11, 16, 12, 0, 0), R(50, 40));
                File.WriteAllText(path, TempLog.Header + "\r\n");
                s.Append(new DateTime(2026, 11, 16, 12, 0, 30), R(51, 41));
                Assert.Equal(1, File.ReadAllLines(path).Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void Append_CapsFileSize()
        {
            string dir = NewDir();
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "2026-11-16.csv");
                File.WriteAllBytes(path, new byte[TempLogStore.MaxFileBytes + 10]);
                long before = new FileInfo(path).Length;
                TempLogStore s = new TempLogStore(dir);
                s.Append(new DateTime(2026, 11, 16, 12, 0, 0), R(50, 40));
                Assert.Equal(before, new FileInfo(path).Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void Summaries_ReadsDaysNewestFirst_SkipsBadLines()
        {
            string dir = NewDir();
            try
            {
                TempLogStore s = new TempLogStore(dir);
                DateTime today = new DateTime(2026, 11, 16);
                s.Append(today.AddDays(-1).AddHours(10), R(70, 50));
                s.Append(today.AddDays(-1).AddHours(10).AddMinutes(1), R(90.5, 55));
                s.Append(today.AddHours(9), R(60, 45));
                File.AppendAllText(Path.Combine(dir, "2026-11-16.csv"), "lixo sem formato\r\n");
                DaySummary[] r = s.Summaries(7, today);
                Assert.Equal(2, r.Length);
                Assert.Equal(today, r[0].Day);
                Assert.Equal(1, r[0].Samples);
                Assert.Equal(today.AddDays(-1), r[1].Day);
                Assert.Equal(2, r[1].Samples);
                Assert.True(Math.Abs(r[1].CpuMax.Value - 90.5) < 0.001, "max");
                Assert.Equal(1, r[1].MinutesAbove90);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void Summaries_MissingDirectory_Empty_NeverThrows()
        {
            TempLogStore s = new TempLogStore(Path.Combine(Path.GetTempPath(), "sm-does-not-exist-" + Guid.NewGuid().ToString("N")));
            DaySummary[] r = s.Summaries(7);
            Assert.Equal(0, r.Length);
        }

        [Test]
        public static void Append_UnwritableDirectory_NeverThrows()
        {
            // A file where the directory should be: CreateDirectory fails.
            string file = Path.Combine(Path.GetTempPath(), "sm-templog-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "x");
            try
            {
                TempLogStore s = new TempLogStore(Path.Combine(file, "sub"));
                s.Append(DateTime.Now, R(50, 40));
            }
            finally
            {
                try { File.Delete(file); } catch (Exception) { }
            }
        }
    }
}
