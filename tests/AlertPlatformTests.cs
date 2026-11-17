using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class AlertPlatformTests
    {
        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-alert-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [Test]
        public static void Settings_RoundTrip_MissingAndCorruptFilesGiveDefaults()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "alertas.json");
                AlertSettings d = AlertStore.LoadSettings(path);
                Assert.True(d.Enabled && d.RestartAlerts && d.SustainSeconds == 30 && d.GpuLimitC == 87 && d.CpuLimitC == 0, "defaults when missing");
                d.TempAlerts = false;
                d.CpuLimitC = 92;
                d.QuietHoursEnabled = true;
                Assert.True(AlertStore.SaveSettings(path, d), "saved");
                Assert.True(AlertStore.SaveSettings(path, d), "saved over an existing file");
                AlertSettings back = AlertStore.LoadSettings(path);
                Assert.True(!back.TempAlerts && back.CpuLimitC == 92 && back.QuietHoursEnabled && back.RestartAlerts, "round trip");
                File.WriteAllText(path, "{ not json");
                Assert.True(AlertStore.LoadSettings(path).TempAlerts, "corrupt file = defaults");
                File.WriteAllText(path, "{\"SustainSeconds\":3,\"CpuLimitC\":900}");
                AlertSettings partial = AlertStore.LoadSettings(path);
                Assert.Equal(10, partial.SustainSeconds);   // normalized
                Assert.Equal(105, partial.CpuLimitC);
                Assert.True(partial.Enabled, "members missing from the file keep their defaults");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void State_RoundTrip()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "alertas-estado.json");
                Assert.Equal(0L, AlertStore.LoadState(path).LastCrashTicks);
                AlertState s = new AlertState();
                s.LastCrashTicks = 12345678901234L;
                s.LastJournalTicks = 42;
                Assert.True(AlertStore.SaveState(path, s), "saved");
                AlertState b = AlertStore.LoadState(path);
                Assert.Equal(12345678901234L, b.LastCrashTicks);
                Assert.Equal(42L, b.LastJournalTicks);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Save_UnwritableLocation_ReturnsFalse_NeverThrows()
        {
            Assert.True(!AlertStore.SaveSettings("Z:\\no\\such\\drive\\alertas.json", new AlertSettings()), "false");
        }

        [Test]
        public static void UserNotifyMap_FullscreenAndQuietStates()
        {
            Assert.True(UserNotifyMap.ToContext(3).Fullscreen, "D3D game");
            Assert.True(UserNotifyMap.ToContext(2).Fullscreen, "busy full screen");
            Assert.True(UserNotifyMap.ToContext(4).Fullscreen, "presentation");
            Assert.True(UserNotifyMap.ToContext(7).Fullscreen, "store app");
            Assert.True(UserNotifyMap.ToContext(6).OsQuiet && !UserNotifyMap.ToContext(6).Fullscreen, "focus assist");
            Assert.True(UserNotifyMap.ToContext(1).OsQuiet, "locked");
            Assert.True(!UserNotifyMap.ToContext(0).Fullscreen && !UserNotifyMap.ToContext(0).OsQuiet, "error value: no suppression");
            Assert.True(!UserNotifyMap.ToContext(99).Fullscreen && !UserNotifyMap.ToContext(99).OsQuiet, "unknown value: no suppression");
            AlertContext free = UserNotifyMap.ToContext(5);
            Assert.True(!free.Fullscreen && !free.OsQuiet, "accepts notifications");
        }

        [Test]
        public static void Integration_UserNotifyState_Query_NeverThrows()
        {
            AlertContext c = UserNotifyState.Query();
            Assert.True(c != null, "context");
        }

        private static void Append(string path, string text)
        {
            using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                byte[] b = new UTF8Encoding(false).GetBytes(text);
                fs.Write(b, 0, b.Length);
            }
        }

        private static string Line(DateTime t, string kind, string text)
        {
            return JournalFormat.Format(t, "app", kind, text) + "\r\n";
        }

        [Test]
        public static void JournalTail_ReturnsOnlyNewLines_HoldsATornLine_AndSurvivesRotation()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "diario.log");
                JournalTail tail = new JournalTail(path);
                Assert.Equal(0, tail.ReadNew().Count);                     // no file yet
                DateTime t = new DateTime(2026, 11, 17, 10, 0, 0);
                Append(path, Line(t, JournalKind.Start, "a") + Line(t.AddSeconds(1), JournalKind.State, "b"));
                List<JournalEntry> first = tail.ReadNew();
                Assert.Equal(2, first.Count);
                Assert.Equal(0, tail.ReadNew().Count);                     // nothing new
                string torn = Line(t.AddSeconds(2), JournalKind.State, "c");
                Append(path, torn.Substring(0, 20));                       // writer was cut in the middle
                Assert.Equal(0, tail.ReadNew().Count);
                Append(path, torn.Substring(20));
                List<JournalEntry> third = tail.ReadNew();
                Assert.Equal(1, third.Count);
                Assert.Equal("c", third[0].Text);
                File.Delete(path);                                         // rotation: a short new file
                Append(path, Line(t.AddMinutes(1), JournalKind.State, "d"));
                List<JournalEntry> after = tail.ReadNew();
                Assert.Equal(1, after.Count);
                Assert.Equal("d", after[0].Text);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Reader_Journal_ExistsOrNot()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "diario.log");
                List<JournalEntry> into = new List<JournalEntry>();
                Assert.True(!FailureReportReader.TryReadJournal(path, into), "missing journal is not 'nothing happened'");
                Append(path, Line(new DateTime(2026, 11, 17, 1, 34, 0), JournalKind.WriteBefore, "x"));
                Assert.True(FailureReportReader.TryReadJournal(path, into), "read");
                Assert.Equal(1, into.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Reader_Temps_OnlyTheWindow_MissingFolderIsUnreadable()
        {
            string dir = TempDir();
            try
            {
                List<TempSample> into = new List<TempSample>();
                Assert.True(!FailureReportReader.TryReadTemps(Path.Combine(dir, "nope"), DateTime.Now.AddHours(-1), DateTime.Now, into), "no folder");
                DateTime day = new DateTime(2026, 11, 17);
                File.WriteAllText(Path.Combine(dir, TempLog.FileNameFor(day)),
                    TempLog.Header + "\r\n01:20:00,60.0,50.0,3000,3000\r\n01:31:00,78.0,60.0,4100,3900\r\n01:34:00,80.0,62.0,4200,4000\r\nbroken line\r\n");
                Assert.True(FailureReportReader.TryReadTemps(dir, new DateTime(2026, 11, 17, 1, 29, 0), new DateTime(2026, 11, 17, 1, 37, 0), into), "read");
                Assert.Equal(2, into.Count);
                Assert.Equal(78.0, into[0].CpuC.Value);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Integration_Reader_SystemLog_ReadOnlyQueryWorksOnThisMachine()
        {
            List<ReportEvent> into = new List<ReportEvent>();
            bool ok = FailureReportReader.TryReadEvents("System", DateTime.Now.AddMinutes(-10), DateTime.Now, into);
            Assert.True(ok, "System log query should be readable without elevation");
            foreach (ReportEvent e in into) Assert.True(ReportEventFilter.Classify(e.Provider, e.Id, e.Level).HasValue, "only classified events are kept");
            Assert.True(!FailureReportReader.TryReadEvents("No-Such-Log-Name-xyz", DateTime.Now.AddMinutes(-10), DateTime.Now, new List<ReportEvent>()), "unknown log = unreadable");
        }

        [Test]
        public static void Integration_Reader_Gather_FillsEverySourceFlag_WithoutThrowing()
        {
            CrashEntry e = new CrashEntry();
            e.When = DateTime.Now.AddMinutes(-30);
            e.Kind = FailureKind.PowerLoss;
            FailureReport r = FailureReportReader.Build(e);
            Assert.True(r.Sections.Count >= 7, "all sections");
            Assert.True(r.ToText(null).Length > 200, "text");
        }

        [Test]
        public static void Integration_Cpu_Name_NeverThrows()
        {
            string n = CpuInfo.ReadName();
            Assert.True(n == null || n.Length > 2, "name or null");
        }
    }
}
