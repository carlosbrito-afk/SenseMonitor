using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Pure decisions behind the expanded cleanup: name matching, the event-log filter, the
    // commands that are built (never run here) and the keep-lists.
    public static class CleanupRulesTests
    {
        private static EventChannelInfo Ch(string name, EventChannelKind kind, long records)
        {
            EventChannelInfo c = new EventChannelInfo();
            c.Name = name;
            c.Kind = kind;
            c.Records = records;
            return c;
        }

        private static bool Has(List<string> l, string name)
        {
            foreach (string s in l) if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- wildcard matching ----

        [Test]
        public static void Wildcard_ExtensionPatternIsExact_NotThe8dot3Quirk()
        {
            Assert.True(CleanupNames.Matches("CbsPersist_1.log", "*.log"), "plain match");
            Assert.True(CleanupNames.Matches("X.LOG", "*.log"), "case-insensitive");
            Assert.True(!CleanupNames.Matches("x.logx", "*.log"), "longer extension is not a match");
            Assert.True(!CleanupNames.Matches("x.log.bak", "*.log"), "suffix after the extension is not a match");
            Assert.True(CleanupNames.Matches("a.cab", "*.cab"), "cab");
            Assert.True(!CleanupNames.Matches("a.cabs", "*.cab"), "cabs is not cab");
        }

        [Test]
        public static void Wildcard_PrefixAndSuffix()
        {
            Assert.True(CleanupNames.Matches("thumbcache_256.db", "thumbcache_*.db"), "thumbcache");
            Assert.True(!CleanupNames.Matches("iconcache_256.db", "thumbcache_*.db"), "other prefix");
            Assert.True(CleanupNames.Matches("anything", "*"), "star matches all");
            Assert.True(CleanupNames.Matches("exact.txt", "exact.txt"), "no wildcard is an exact match");
            Assert.True(!CleanupNames.Matches("exact.txt2", "exact.txt"), "exact means exact");
            Assert.True(CleanupNames.Matches("a.etl", "?.etl"), "question mark is one character");
            Assert.True(!CleanupNames.Matches("ab.etl", "?.etl"), "question mark is only one");
            Assert.True(!CleanupNames.Matches(null, "*.log"), "null name");
        }

        [Test]
        public static void Wildcard_AnyOfSeveralPatterns()
        {
            string[] p = { "*.log", "*.cab" };
            Assert.True(CleanupNames.MatchesAny("a.cab", p), "second pattern");
            Assert.True(!CleanupNames.MatchesAny("a.txt", p), "none");
            Assert.True(CleanupNames.MatchesAny("a.txt", null), "no patterns means everything");
        }

        // ---- keep lists ----

        [Test]
        public static void KeepNames_AreExactAndCaseInsensitive()
        {
            string[] keep = { "CBS.log" };
            Assert.True(CleanupNames.IsKept("cbs.LOG", keep, null), "the current CBS.log stays");
            Assert.True(!CleanupNames.IsKept("CbsPersist_1.log", keep, null), "rotated logs may go");
            Assert.True(!CleanupNames.IsKept("CBS.log.old", keep, null), "only the exact name");
            Assert.True(!CleanupNames.IsKept("a", null, null), "no lists, nothing kept");
        }

        [Test]
        public static void KeepPrefixes_ProtectBlueScreenReports()
        {
            string[] prefixes = CleanupSafety.CrashReportPrefixes;
            Assert.True(CleanupNames.IsKept("Kernel_1a_123abc", null, prefixes), "bugcheck report");
            Assert.True(CleanupNames.IsKept("kernel_124_x", null, prefixes), "case-insensitive");
            Assert.True(CleanupNames.IsKept("LiveKernelEvent_141_x", null, prefixes), "live kernel event");
            Assert.True(CleanupNames.IsKept("WHEA_x", null, prefixes), "hardware error report");
            Assert.True(!CleanupNames.IsKept("AppCrash_chrome.exe_1", null, prefixes), "application crash reports may go");
            Assert.True(!CleanupNames.IsKept("NonCritical_x", null, prefixes), "non critical may go");
        }

        // ---- event log filter ----

        [Test]
        public static void EventFilter_NeverClearsTheLogsTheHealthPageAndInvestigationRead()
        {
            Assert.True(EventLogFilter.IsExcluded("System"), "System");
            Assert.True(EventLogFilter.IsExcluded("system"), "System, any case");
            Assert.True(EventLogFilter.IsExcluded("Application"), "Application");
            Assert.True(EventLogFilter.IsExcluded("Security"), "Security");
            Assert.True(EventLogFilter.IsExcluded("Setup"), "Setup");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Kernel-Power/Thermal-Operational"), "Kernel-Power");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Kernel-WHEA/Operational"), "WHEA");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-WER-Diag/Operational"), "WER");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-WerKernel/Operational"), "WerKernel");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Diagnostics-Performance/Operational"), "boot performance");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Kernel-Boot/Operational"), "any kernel channel");
        }

        [Test]
        public static void EventFilter_WerRuleDoesNotCatchPowerOrPowerShell()
        {
            Assert.True(!EventLogFilter.IsExcluded("Microsoft-Windows-PowerShell/Operational"), "PowerShell is ordinary");
            Assert.True(!EventLogFilter.IsExcluded("Microsoft-Windows-WLAN-AutoConfig/Operational"), "wifi");
        }

        [Test]
        public static void EventFilter_KeepsSecurityToolHistory()
        {
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Windows Defender/Operational"), "defender history");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Windows Firewall With Advanced Security/Firewall"), "firewall");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-BitLocker/BitLocker Management"), "bitlocker");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Storage-Storport/Operational"), "storage errors");
            Assert.True(EventLogFilter.IsExcluded("Microsoft-Windows-Ntfs/Operational"), "ntfs");
        }

        [Test]
        public static void EventFilter_SelectsOnlyOperationalAndAnalyticWithRecords()
        {
            List<EventChannelInfo> all = new List<EventChannelInfo>();
            all.Add(Ch("System", EventChannelKind.Administrative, 5000));
            all.Add(Ch("Application", EventChannelKind.Administrative, 900));
            all.Add(Ch("Security", EventChannelKind.Administrative, 100));
            all.Add(Ch("Setup", EventChannelKind.Operational, 10));
            all.Add(Ch("Microsoft-Windows-WLAN-AutoConfig/Operational", EventChannelKind.Operational, 40));
            all.Add(Ch("Microsoft-Windows-PowerShell/Operational", EventChannelKind.Operational, 7));
            all.Add(Ch("Microsoft-Windows-PushNotification-Platform/Analytic", EventChannelKind.Analytic, 3));
            all.Add(Ch("Microsoft-Windows-NlaSvc/Operational", EventChannelKind.Operational, 0));
            all.Add(Ch("Microsoft-Windows-Bar/Debug", EventChannelKind.Debug, 12));
            all.Add(Ch("Microsoft-Windows-Baz/Admin", EventChannelKind.Administrative, 12));
            all.Add(Ch("Microsoft-Windows-Kernel-Power/Thermal-Operational", EventChannelKind.Operational, 20));
            all.Add(Ch("Microsoft-Windows-WHEA-Logger/Operational", EventChannelKind.Operational, 20));
            all.Add(Ch("Microsoft-Windows-Diagnostics-Performance/Operational", EventChannelKind.Operational, 20));
            all.Add(Ch("Microsoft-Windows-WER-PayloadHealth/Operational", EventChannelKind.Operational, 20));
            List<string> pick = EventLogFilter.Select(all);
            Assert.Equal(3, pick.Count);
            Assert.True(Has(pick, "Microsoft-Windows-WLAN-AutoConfig/Operational"), "operational with records");
            Assert.True(Has(pick, "Microsoft-Windows-PowerShell/Operational"), "powershell operational");
            Assert.True(Has(pick, "Microsoft-Windows-PushNotification-Platform/Analytic"), "analytic with records");
        }

        [Test]
        public static void EventFilter_NullAndEmptyListsGiveNothing()
        {
            Assert.Equal(0, EventLogFilter.Select(null).Count);
            Assert.Equal(0, EventLogFilter.Select(new List<EventChannelInfo>()).Count);
        }

        [Test]
        public static void EventFilter_SkipsNamesThatCouldBreakTheCommandLine()
        {
            List<EventChannelInfo> all = new List<EventChannelInfo>();
            all.Add(Ch("Evil\" /q & calc", EventChannelKind.Operational, 5));
            all.Add(Ch("Line\r\nBreak/Operational", EventChannelKind.Operational, 5));
            all.Add(Ch("", EventChannelKind.Operational, 5));
            all.Add(Ch(null, EventChannelKind.Operational, 5));
            all.Add(Ch("Microsoft-Windows-Known Folders API Service", EventChannelKind.Operational, 5));
            List<string> pick = EventLogFilter.Select(all);
            Assert.Equal(1, pick.Count);
            Assert.Equal("Microsoft-Windows-Known Folders API Service", pick[0]);
        }

        [Test]
        public static void EventFilter_ResultIsSortedAndWithoutDuplicates()
        {
            List<EventChannelInfo> all = new List<EventChannelInfo>();
            all.Add(Ch("Microsoft-Windows-WLAN-AutoConfig/Operational", EventChannelKind.Operational, 1));
            all.Add(Ch("Microsoft-Windows-NCSI/Operational", EventChannelKind.Operational, 1));
            all.Add(Ch("microsoft-windows-ncsi/operational", EventChannelKind.Operational, 1));
            List<string> pick = EventLogFilter.Select(all);
            Assert.Equal(2, pick.Count);
            Assert.Equal("Microsoft-Windows-NCSI/Operational", pick[0]);
            Assert.Equal("Microsoft-Windows-WLAN-AutoConfig/Operational", pick[1]);
        }

        // ---- commands (built here, never run) ----

        [Test]
        public static void Commands_FlushDns_UsesTheFullSystem32Path()
        {
            CleanupCommand c = CleanupCommands.FlushDns(@"C:\Windows\System32");
            Assert.Equal(@"C:\Windows\System32\ipconfig.exe", c.FileName);
            Assert.Equal("/flushdns", c.Arguments);
            CleanupCommand c2 = CleanupCommands.FlushDns(@"C:\Windows\System32\");
            Assert.Equal(@"C:\Windows\System32\ipconfig.exe", c2.FileName);
        }

        [Test]
        public static void Commands_ClearEventLog_QuotesTheChannel()
        {
            CleanupCommand c = CleanupCommands.ClearEventLog(@"C:\Windows\System32", "Microsoft-Windows-Known Folders API Service");
            Assert.Equal(@"C:\Windows\System32\wevtutil.exe", c.FileName);
            Assert.Equal("cl \"Microsoft-Windows-Known Folders API Service\"", c.Arguments);
        }

        [Test]
        public static void Commands_ClearEventLog_RefusesDangerousOrExcludedChannels()
        {
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", "A\" & calc"));
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", ""));
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", null));
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", "System"));
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", "Microsoft-Windows-Kernel-Power/Diagnostic"));
            Assert.Null(CleanupCommands.ClearEventLog("", "Microsoft-Windows-NCSI/Operational"));
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", "Microsoft-Windows-Foo/Operational"));   // not on the allowlist
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", "Microsoft-Windows-PCI/Operational"));
        }

        // ---- the longer forbidden list ----

        [Test]
        public static void Safety_NeverOffered_PrefetchRecycleBinWindowsOldPanther()
        {
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\Prefetch"), "prefetch folder");
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\Prefetch\CHROME.EXE-1.pf"), "prefetch file");
            Assert.True(CleanupSafety.IsForbidden(@"C:\$Recycle.Bin\S-1-5\x"), "recycle bin");
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows.old\Users"), "Windows.old");
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\Panther\setupact.log"), "Panther is left alone");
            Assert.True(CleanupSafety.IsForbidden(@"C:\System Volume Information\x"), "restore points");
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\LiveKernelReports\x.dmp"), "live kernel reports");
            Assert.True(!CleanupSafety.IsForbidden(@"C:\Windows\Logs\CBS\CbsPersist_1.log"), "CBS logs are ordinary");
            Assert.True(!CleanupSafety.IsForbidden(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_x"), "WER archive is ordinary");
        }
    }
}
