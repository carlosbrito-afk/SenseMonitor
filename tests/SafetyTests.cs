using System;
using System.Collections.Generic;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public class ListJournal : IJournal
    {
        public List<string> Lines = new List<string>();

        public void Write(string kind, string text)
        {
            Lines.Add(kind + "|" + text);
        }

        public bool Has(string kind, string part)
        {
            foreach (string l in Lines)
            {
                if (l.StartsWith(kind + "|") && l.Contains(part)) return true;
            }
            return false;
        }

        public int Count(string kind)
        {
            int n = 0;
            foreach (string l in Lines) { if (l.StartsWith(kind + "|")) n++; }
            return n;
        }
    }

    public class MemFlag : IFlag
    {
        public bool Value;
        public bool FailSet;
        public bool FailClear;
        public bool IsSet { get { return Value; } }
        public bool Set() { if (FailSet) return false; Value = true; return true; }
        public bool Clear() { if (FailClear) return false; Value = false; return true; }
    }

    // FakeFirmware plus the extended calls, with a scriptable fan behavior read-back.
    public class ExtFirmware : IFirmwareExt
    {
        public FakeFirmware Inner = new FakeFirmware();
        public bool MaxResult = true;
        public bool ReadOk = true;
        public FanMode CpuRead = FanMode.Auto;
        public FanMode GpuRead = FanMode.Auto;
        public bool AvailableValue = true;
        public Action OnWrite;

        public SensorReading ReadSensors() { return Inner.ReadSensors(); }
        public PerfMode? GetMode() { return Inner.GetMode(); }
        public bool SetMode(PerfMode m) { Touch(); return Inner.SetMode(m); }
        public bool SetFanAuto() { Touch(); return Inner.SetFanAuto(); }
        public bool SetFanPercent(int c, int g) { Touch(); return Inner.SetFanPercent(c, g); }
        public bool Available { get { return AvailableValue; } }
        public bool SetFanMax() { Touch(); Inner.Calls.Add("max"); return MaxResult; }
        public bool? IsFanMax() { return null; }
        public bool GetFanBehavior(out FanMode? cpu, out FanMode? gpu)
        {
            cpu = ReadOk ? (FanMode?)CpuRead : null;
            gpu = ReadOk ? (FanMode?)GpuRead : null;
            return ReadOk;
        }
        private void Touch() { if (OnWrite != null) OnWrite(); }
    }

    public static class SafetyTests
    {
        internal static int clockMs;

        internal static GuardedFirmware Guard(IFirmware fw, ListJournal j, MemFlag marker, bool readOnly, Func<bool> before)
        {
            clockMs = 1000;
            return new GuardedFirmware(fw, j, marker, readOnly, before, delegate { return clockMs; });
        }

        // ---- journal format ----------------------------------------------------------------

        [Test]
        public static void JournalFormat_RoundTrips()
        {
            DateTime t = new DateTime(2026, 11, 17, 14, 3, 22, 123);
            string line = JournalFormat.Format(t, "app", JournalKind.WriteBefore, "modo -> Desempenho");
            Assert.Equal("2026-11-17 14:03:22.123\tapp\tESCRITA-ANTES\tmodo -> Desempenho", line);
            JournalEntry e;
            Assert.True(JournalFormat.TryParse(line, out e), "parses");
            Assert.Equal(t, e.Time);
            Assert.Equal("app", e.Source);
            Assert.Equal("ESCRITA-ANTES", e.Kind);
            Assert.Equal("modo -> Desempenho", e.Text);
        }

        [Test]
        public static void JournalFormat_TextNeverBreaksTheLine()
        {
            string line = JournalFormat.Format(DateTime.Now, "app", "X", "a\r\nb\tc\u0001d");
            Assert.True(!line.Contains("\n") && !line.Contains("\r"), "no line breaks");
            JournalEntry e;
            Assert.True(JournalFormat.TryParse(line, out e), "parses");
            Assert.Equal("a  b cd", e.Text);
        }

        [Test]
        public static void JournalFormat_LongTextIsCut()
        {
            string line = JournalFormat.Format(DateTime.Now, "app", "X", new string('a', 1000));
            JournalEntry e;
            Assert.True(JournalFormat.TryParse(line, out e), "parses");
            Assert.True(e.Text.Length <= JournalFormat.MaxTextLength, "cut to " + e.Text.Length);
        }

        [Test]
        public static void JournalFormat_ParseAllSkipsTornAndForeignLines()
        {
            string ok = JournalFormat.Format(new DateTime(2026, 1, 2, 3, 4, 5, 6), "app", "INICIO", "x");
            List<JournalEntry> list = JournalFormat.ParseAll(new string[] { "lixo", ok, "2026-01-02 03:04", "", null });
            Assert.Equal(1, list.Count);
            Assert.Equal("INICIO", list[0].Kind);
        }

        [Test]
        public static void JournalRotation_Decision()
        {
            Assert.True(!JournalRotation.ShouldRotate(0, 100, 1000), "empty never rotates");
            Assert.True(!JournalRotation.ShouldRotate(500, 100, 1000), "room left");
            Assert.True(JournalRotation.ShouldRotate(950, 100, 1000), "would pass the cap");
            Assert.True(!JournalRotation.ShouldRotate(950, 100, 0), "no cap");
            Assert.Equal("a.log.1", JournalRotation.PreviousPath("a.log"));
        }

        // ---- file journal (real files in a temp folder) ---------------------------------------

        internal static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [Test]
        public static void FileJournal_WritesLinesAndReadsThemBack()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "diario.log");
                FileJournal j = new FileJournal(path, "app", 100000, delegate { return new DateTime(2026, 11, 17, 10, 0, 0); });
                j.Write(JournalKind.Start, "um");
                j.Write(JournalKind.WriteBefore, "dois");
                List<JournalEntry> list = FileJournal.ReadRecent(path, 10);
                Assert.Equal(2, list.Count);
                Assert.Equal("um", list[0].Text);
                Assert.Equal("ESCRITA-ANTES", list[1].Kind);
                // readable by another reader while the journal object is still alive (flushed, not buffered)
                string raw = File.ReadAllText(path);
                Assert.True(raw.Contains("dois"), "content on disk right after Write");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void FileJournal_RotatesAtTheCap_KeepingTwoFiles()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "diario.log");
                FileJournal j = new FileJournal(path, "app", 600, null);
                for (int i = 0; i < 40; i++) j.Write(JournalKind.State, "linha numero " + i);
                Assert.True(File.Exists(path), "current");
                Assert.True(File.Exists(path + ".1"), "previous");
                Assert.True(!File.Exists(path + ".2"), "only two files");
                Assert.True(new FileInfo(path).Length <= 600, "current under the cap: " + new FileInfo(path).Length);
                List<JournalEntry> list = FileJournal.ReadRecent(path, 1000);
                Assert.Equal("linha numero 39", list[list.Count - 1].Text);
                Assert.True(list.Count > 5 && list.Count < 40, "older lines dropped: " + list.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void FileJournal_TwoWritersDoNotOverwriteEachOther()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "diario.log");
                FileJournal a = new FileJournal(path, "app", 100000, null);
                FileJournal b = new FileJournal(path, "sentinela", 100000, null);
                a.Write("X", "a1");
                b.Write("X", "b1");
                a.Write("X", "a2");
                b.Write("X", "b2");
                List<JournalEntry> list = FileJournal.ReadRecent(path, 10);
                Assert.Equal(4, list.Count);
                Assert.Equal("a1", list[0].Text);
                Assert.Equal("sentinela", list[1].Source);
                Assert.Equal("b2", list[3].Text);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void FileJournal_NeverThrows()
        {
            FileJournal j = new FileJournal(@"Z:\nao\existe\diario.log", "app", 1000, null);
            j.Write("X", "y");
        }

        // ---- file flag ---------------------------------------------------------------------

        [Test]
        public static void FileFlag_SetClear()
        {
            string dir = TempDir();
            try
            {
                FileFlag f = new FileFlag(Path.Combine(dir, "sub", "m.flag"));
                Assert.True(!f.IsSet, "starts clear");
                Assert.True(f.Set(), "set");
                Assert.True(f.IsSet, "is set");
                Assert.True(f.Set(), "set twice is fine");
                Assert.True(f.Clear(), "clear");
                Assert.True(!f.IsSet, "cleared");
                Assert.True(f.Clear(), "clear twice is fine");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- guarded firmware: journal -------------------------------------------------------

        [Test]
        public static void Guard_ModeWrite_JournalsBeforeAndAfter()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.Inner.ModeValue = PerfMode.DiaADia;
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, new MemFlag(), false, null);
            Assert.True(g.SetMode(PerfMode.Silencioso), "ok");
            Assert.Equal(2, j.Lines.Count);
            Assert.True(j.Lines[0].StartsWith("ESCRITA-ANTES|modo -> Silencioso"), j.Lines[0]);
            Assert.True(j.Lines[1].StartsWith("ESCRITA-DEPOIS|modo -> Silencioso: ok"), j.Lines[1]);
        }

        [Test]
        public static void Guard_BeforeLineIsWrittenBeforeTheFirmwareIsTouched()
        {
            // A process killed right after the write must already have the "before" line on disk.
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            int linesAtWrite = -1;
            fw.OnWrite = delegate { linesAtWrite = j.Lines.Count; };
            GuardedFirmware g = Guard(fw, j, new MemFlag(), false, null);
            g.SetFanAuto();
            Assert.Equal(1, linesAtWrite);
            Assert.True(j.Lines[0].StartsWith("ESCRITA-ANTES"), j.Lines[0]);
        }

        [Test]
        public static void Guard_FailedWrite_IsJournaledWithTheRealMode()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.Inner.ModeValue = PerfMode.Silencioso;
            fw.Inner.SetModeResult = false;
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, new MemFlag(), false, null);
            Assert.True(!g.SetMode(PerfMode.Desempenho), "reported as failed");
            Assert.True(j.Has("ESCRITA-DEPOIS", "falhou"), "failure line");
            Assert.True(j.Has("ESCRITA-DEPOIS", "modo lido: Silencioso"), "read-back in the line");
        }

        [Test]
        public static void Guard_Reads_AreNotJournaled()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.Inner.ModeValue = PerfMode.DiaADia;
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, new MemFlag(), false, null);
            g.ReadSensors();
            g.GetMode();
            FanMode? c; FanMode? gg;
            g.GetFanBehavior(out c, out gg);
            Assert.Equal(0, j.Lines.Count);
        }

        [Test]
        public static void Guard_RepeatedIdenticalFailures_AreSummarized()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.Inner.SetAutoResult = false;
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, new MemFlag(), false, null);
            for (int i = 0; i < 20; i++)
            {
                clockMs += 2000; // 2 s ticks, inside the 30 s window
                g.SetFanAuto();
                if (clockMs > 1000 + 28000) break;
            }
            Assert.True(j.Lines.Count <= 3, "not one pair per retry: " + j.Lines.Count);
            // after the window the failure is reported again with the count of what was skipped
            clockMs += 40000;
            fw.Inner.SetAutoResult = true;
            g.SetFanAuto();
            Assert.True(j.Has("ESCRITA-DEPOIS", "falhou mais"), "summary line");
            Assert.True(j.Lines[j.Lines.Count - 1].Contains(": ok"), "recovery line: " + j.Lines[j.Lines.Count - 1]);
        }

        // ---- guarded firmware: read-only -----------------------------------------------------

        [Test]
        public static void ReadOnly_NoWriteReachesTheFirmware()
        {
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            MemFlag marker = new MemFlag();
            GuardedFirmware g = Guard(fw, j, marker, true, null);
            Assert.True(!g.SetMode(PerfMode.Desempenho), "mode refused");
            Assert.True(!g.SetFanPercent(50, 50), "speed refused");
            Assert.True(!g.SetFanMax(), "max refused");
            Assert.True(g.SetFanAuto(), "auto reported as done (nothing to hand back)");
            Assert.Equal(0, fw.Inner.Calls.Count);
            Assert.True(!marker.Value, "the marker is never set");
            Assert.Equal(4, j.Count("BLOQUEADO"));
            Assert.Equal(0, j.Count("ESCRITA-ANTES"));
        }

        [Test]
        public static void ReadOnly_SafetyAutoStillWorksAndIsJournaled()
        {
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            MemFlag marker = new MemFlag();
            marker.Value = true;
            GuardedFirmware g = Guard(fw, j, marker, true, null);
            Assert.True(g.SetFanAutoSafety(), "ok");
            Assert.Equal(1, fw.Inner.Count("auto"));
            Assert.True(!marker.Value, "marker cleared after the confirmed Auto");
            Assert.True(j.Has("ESCRITA-ANTES", "somente leitura ligado"), "journaled as the read-only exception");
        }

        // ---- guarded firmware: fan marker ----------------------------------------------------

        [Test]
        public static void Marker_SetBeforeTheFirstCustomWrite_ClearedOnlyAfterAuto()
        {
            ExtFirmware fw = new ExtFirmware();
            MemFlag marker = new MemFlag();
            bool markerAtWrite = false;
            fw.OnWrite = delegate { markerAtWrite = marker.Value; };
            GuardedFirmware g = Guard(fw, new ListJournal(), marker, false, null);
            Assert.True(g.SetFanPercent(40, 40), "write ok");
            Assert.True(markerAtWrite, "marker was already set when the firmware was written");
            Assert.True(marker.Value, "marker stays while Custom");
            Assert.True(g.SetFanMax(), "max ok");
            Assert.True(marker.Value, "marker stays while Máximo");
            Assert.True(g.SetFanAuto(), "auto ok");
            Assert.True(!marker.Value, "marker cleared after Auto");
        }

        [Test]
        public static void Marker_CannotBeWritten_RefusesTheCustomWrite()
        {
            ExtFirmware fw = new ExtFirmware();
            MemFlag marker = new MemFlag();
            marker.FailSet = true;
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, marker, false, null);
            Assert.True(!g.SetFanPercent(40, 40), "refused");
            Assert.True(!g.SetFanMax(), "refused");
            Assert.Equal(0, fw.Inner.Calls.Count);
            Assert.True(j.Has("BLOQUEADO", "marcador"), "journaled");
            // Auto never needs the marker
            Assert.True(g.SetFanAuto(), "auto allowed");
        }

        [Test]
        public static void Marker_StaysWhenAutoWriteFails()
        {
            ExtFirmware fw = new ExtFirmware();
            MemFlag marker = new MemFlag();
            GuardedFirmware g = Guard(fw, new ListJournal(), marker, false, null);
            g.SetFanPercent(40, 40);
            fw.Inner.SetAutoResult = false;
            Assert.True(!g.SetFanAuto(), "failed");
            Assert.True(marker.Value, "still marked");
        }

        [Test]
        public static void Marker_StaysWhenReadBackSaysNotAuto()
        {
            ExtFirmware fw = new ExtFirmware();
            MemFlag marker = new MemFlag();
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, marker, false, null);
            g.SetFanPercent(40, 40);
            fw.CpuRead = FanMode.Custom;
            fw.GpuRead = FanMode.Custom;
            g.SetFanAuto();
            Assert.True(marker.Value, "firmware did not confirm Auto");
            Assert.True(j.Has("ESCRITA-DEPOIS", "marcador mantido"), "journaled");
        }

        [Test]
        public static void Marker_ClearedWhenReadBackIsUnavailable()
        {
            ExtFirmware fw = new ExtFirmware();
            MemFlag marker = new MemFlag();
            GuardedFirmware g = Guard(fw, new ListJournal(), marker, false, null);
            g.SetFanPercent(40, 40);
            fw.ReadOk = false;
            g.SetFanAuto();
            Assert.True(!marker.Value, "accepted Auto counts when the read-back cannot be read");
        }

        [Test]
        public static void Sentinel_NotStarted_RefusesTheCustomWrite()
        {
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            GuardedFirmware g = Guard(fw, j, new MemFlag(), false, delegate { return false; });
            Assert.True(!g.SetFanPercent(40, 40), "refused");
            Assert.Equal(0, fw.Inner.Calls.Count);
            Assert.True(j.Has("BLOQUEADO", "guardião"), "journaled");
        }

        [Test]
        public static void Sentinel_IsAskedBeforeEachCustomWrite_NotBeforeAuto()
        {
            ExtFirmware fw = new ExtFirmware();
            int asked = 0;
            GuardedFirmware g = Guard(fw, new ListJournal(), new MemFlag(), false, delegate { asked++; return true; });
            g.SetFanPercent(40, 40);
            g.SetFanMax();
            g.SetFanAuto();
            g.SetMode(PerfMode.DiaADia);
            Assert.Equal(2, asked);
        }

    }
}
