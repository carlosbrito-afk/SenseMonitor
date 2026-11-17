using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace Forja.Core
{
    // Raw Kernel-Power 41 event: BugcheckCode is 0 when Windows did not record a bugcheck
    // (power loss or hard hang). Times are local.
    public class Kernel41Event
    {
        public DateTime When;
        public long BugcheckCode;
        public ulong? Param1;
        // True when the event XML was read (the fields below are real); false for a bare "something
        // restarted badly" record whose payload could not be parsed.
        public bool PayloadRead;
        // FILETIME of the last power-button press recorded before the crash; 0 = never pressed.
        // A pressed button with no bugcheck means the machine hung and was forced off.
        public ulong PowerButtonTimestamp;
        public int SleepInProgress;
        public bool LongPowerButtonPress;
        public int WheaBootErrorCount;
    }

    // EventLog 6008 ("o desligamento anterior não era esperado"). Logged at the next boot, a few
    // seconds after the 41. Its payload holds the moment the system was last known to be running.
    public class Unexpected6008Event
    {
        public DateTime When;
        public DateTime? DownAt;   // local; null when the payload could not be read
    }

    // What kind of failure an unexpected restart was.
    public enum FailureKind
    {
        Unknown,         // restart seen, but the payload that tells the kind was not readable
        BlueScreen,      // Windows recorded a bugcheck code
        HangPowerButton, // no bugcheck, power button pressed: the PC hung and was forced off
        PowerLoss        // no bugcheck, no button: power cut or a hard shutdown by the hardware
    }

    // Raw WER-SystemErrorReporting 1001 event: Message carries "0x000001c7"-style code and dump path.
    public class Wer1001Event
    {
        public DateTime When;
        public string Message;
    }

    public class DumpFileInfo
    {
        public string Path;
        public DateTime Time;
    }

    public class CrashCodeInfo
    {
        public string Symbol;   // e.g. IRQL_NOT_LESS_OR_EQUAL; null when the code is not in the table
        public string Hint;     // short pt-BR explanation
        public bool Modified;   // code had the 0x10000000 flag (e.g. 0x1000007E)
    }

    // One row of the "Estabilidade do sistema" list: a 41 and/or 1001 event (merged) plus dump info.
    public class CrashEntry
    {
        public DateTime When;
        public long? Code;          // null: unexpected shutdown without a bugcheck code
        public ulong? Param1;
        public bool HasDump;
        public string DumpPath;
        // Estimated moment the PC stopped (EventLog 6008 payload, local time); null when unknown.
        // When is the log time of the restart, usually a few minutes later.
        public DateTime? DownAt;
        public FailureKind Kind;
        public bool PowerButtonPressed;
        public int SleepInProgress;
        public bool LongPowerButtonPress;
        public int WheaBootErrorCount;
        // Executable named in the 1001 text, when the text names one (0xEF); usually null.
        public string DyingProcess;

        // The best estimate of when the PC went down.
        public DateTime EffectiveTime
        {
            get { return DownAt.HasValue ? DownAt.Value : When; }
        }

        public string CodeHex
        {
            get { return Code.HasValue ? CrashParse.FormatCode(Code.Value) : null; }
        }

        public CrashCodeInfo Info
        {
            get { return Code.HasValue ? CrashParse.Describe(Code.Value) : null; }
        }
    }

    public static class CrashParse
    {
        // 1001 and 41 are logged at boot, a few seconds apart.
        public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(2);
        // The dump is written while crashing; the 41 shows up after the next boot.
        public static readonly TimeSpan DumpBefore = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan DumpAfter = TimeSpan.FromMinutes(2);

        private static readonly Regex HexRe = new Regex("0x[0-9a-fA-F]{1,16}", RegexOptions.Compiled);
        private static readonly Regex ProcessRe = new Regex(@"(?<![\w\\/.-])([A-Za-z0-9_-]{2,40}\.exe)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DumpRe = new Regex(@"[A-Za-z]:\\[^\r\n""<>|]*?\.dmp", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private class CodeRow
        {
            public long Code;
            public string Symbol;
            public string Hint;

            public CodeRow(long code, string symbol, string hint)
            {
                Code = code;
                Symbol = symbol;
                Hint = hint;
            }
        }

        private static readonly CodeRow[] Table = new CodeRow[]
        {
            new CodeRow(0xA, "IRQL_NOT_LESS_OR_EQUAL", "Acesso indevido à memória por um driver ou pelo sistema"),
            new CodeRow(0x1A, "MEMORY_MANAGEMENT", "Falha no gerenciador de memória (RAM ou driver)"),
            new CodeRow(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", "Exceção do sistema não tratada (em geral driver)"),
            new CodeRow(0x3B, "SYSTEM_SERVICE_EXCEPTION", "Falha numa chamada do sistema (em geral um driver)"),
            new CodeRow(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", "Acesso a memória que não existe (RAM, disco ou driver)"),
            new CodeRow(0x7A, "KERNEL_DATA_INPAGE_ERROR", "Erro ao ler dados do disco para a memória"),
            new CodeRow(0x7B, "INACCESSIBLE_BOOT_DEVICE", "O Windows não conseguiu acessar o disco de inicialização"),
            new CodeRow(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Um componente interno do sistema falhou (em geral driver)"),
            new CodeRow(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", "Falha inesperada do processador ou da memória"),
            new CodeRow(0x9F, "DRIVER_POWER_STATE_FAILURE", "Um driver não respondeu ao trocar o estado de energia"),
            new CodeRow(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Um driver tentou gravar em memória protegida"),
            new CodeRow(0xC2, "BAD_POOL_CALLER", "Um driver usou a memória do sistema de forma incorreta"),
            new CodeRow(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Um driver acessou memória em momento inválido"),
            new CodeRow(0xEA, "THREAD_STUCK_IN_DEVICE_DRIVER", "Um driver (em geral de vídeo) travou"),
            new CodeRow(0xEF, "CRITICAL_PROCESS_DIED", "Um processo essencial do Windows encerrou"),
            new CodeRow(0xF4, "CRITICAL_OBJECT_TERMINATION", "Um processo essencial do Windows foi encerrado"),
            new CodeRow(0x101, "CLOCK_WATCHDOG_TIMEOUT", "Um núcleo do processador parou de responder"),
            new CodeRow(0x109, "CRITICAL_STRUCTURE_CORRUPTION", "Dados críticos do sistema foram alterados"),
            new CodeRow(0x116, "VIDEO_TDR_FAILURE", "O driver de vídeo parou e não se recuperou"),
            new CodeRow(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", "O driver de vídeo demorou demais para responder"),
            new CodeRow(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "Erro interno do agendador de vídeo"),
            new CodeRow(0x124, "WHEA_UNCORRECTABLE_ERROR", "Erro de hardware (processador, memória, disco ou energia)"),
            new CodeRow(0x133, "DPC_WATCHDOG_VIOLATION", "Um driver demorou demais (em geral disco ou driver)"),
            new CodeRow(0x139, "KERNEL_SECURITY_CHECK_FAILURE", "Verificação de segurança falhou (memória corrompida)"),
            new CodeRow(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", "Memória do sistema corrompida"),
            new CodeRow(0x154, "UNEXPECTED_STORE_EXCEPTION", "Falha no armazenamento (disco ou driver)"),
            new CodeRow(0x1C7, "STORE_DATA_STRUCTURE_CORRUPTION", "Dados de armazenamento em memória corrompidos"),
            new CodeRow(0xC000021A, "STATUS_SYSTEM_PROCESS_TERMINATED", "Um processo essencial do Windows foi encerrado")
        };

        public static string FormatCode(long code)
        {
            if (code < 0) return "0x" + ((ulong)code).ToString("X", CultureInfo.InvariantCulture);
            return "0x" + code.ToString("X", CultureInfo.InvariantCulture);
        }

        public static CrashCodeInfo Describe(long code)
        {
            CrashCodeInfo info = new CrashCodeInfo();
            CodeRow row = Find(code);
            if (row == null && code > 0 && (code & 0xF0000000L) == 0x10000000L)
            {
                row = Find(code & 0x0FFFFFFFL);
                if (row != null) info.Modified = true;
            }
            if (row == null)
            {
                info.Symbol = null;
                info.Hint = "Código ainda sem descrição neste app";
            }
            else
            {
                info.Symbol = row.Symbol;
                info.Hint = row.Hint;
            }
            return info;
        }

        private static CodeRow Find(long code)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Code == code) return Table[i];
            }
            return null;
        }

        // First "0x..." number in the message ("Essa verificação foi: 0x000001c7 (...)").
        public static long? ParseBugcheckFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            Match m = HexRe.Match(message);
            if (!m.Success) return null;
            long v;
            if (long.TryParse(m.Value.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        public static string ParseDumpPathFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            Match m = DumpRe.Match(message);
            return m.Success ? m.Value : null;
        }

        // Reads BugcheckCode (decimal, or 0x-hex) and BugcheckParameter1 from the event 41 XML.
        public static bool TryParseEvent41(string xml, out long code, out ulong? param1)
        {
            code = 0;
            param1 = null;
            if (string.IsNullOrEmpty(xml)) return false;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                bool found = false;
                foreach (XmlNode n in doc.GetElementsByTagName("Data"))
                {
                    XmlAttribute name = n.Attributes == null ? null : n.Attributes["Name"];
                    if (name == null) continue;
                    if (name.Value == "BugcheckCode")
                    {
                        ulong v;
                        if (!TryParseNumber(n.InnerText, out v)) return false;
                        code = unchecked((long)v);
                        found = true;
                    }
                    else if (name.Value == "BugcheckParameter1")
                    {
                        ulong p;
                        if (TryParseNumber(n.InnerText, out p)) param1 = p;
                    }
                }
                return found;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Everything the 41 payload says about HOW the machine went down. The Kernel41Event it
        // returns has no When (the caller owns it). false when the XML is unreadable or has no
        // BugcheckCode; unknown or missing optional fields stay 0 / false.
        public static bool TryParseEvent41Detail(string xml, out Kernel41Event ev)
        {
            ev = null;
            long code;
            ulong? p1;
            if (!TryParseEvent41(xml, out code, out p1)) return false;
            Kernel41Event e = new Kernel41Event();
            e.BugcheckCode = code;
            e.Param1 = p1;
            e.PayloadRead = true;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                foreach (XmlNode n in doc.GetElementsByTagName("Data"))
                {
                    XmlAttribute name = n.Attributes == null ? null : n.Attributes["Name"];
                    if (name == null) continue;
                    ulong v;
                    switch (name.Value)
                    {
                        case "PowerButtonTimestamp":
                            if (TryParseNumber(n.InnerText, out v)) e.PowerButtonTimestamp = v;
                            break;
                        case "SleepInProgress":
                            if (TryParseNumber(n.InnerText, out v) && v < 100) e.SleepInProgress = (int)v;
                            break;
                        case "WHEABootErrorCount":
                            if (TryParseNumber(n.InnerText, out v) && v < 100000) e.WheaBootErrorCount = (int)v;
                            break;
                        case "LongPowerButtonPressDetected":
                            e.LongPowerButtonPress = string.Equals(n.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                                || n.InnerText.Trim() == "1";
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // the code was read; the optional fields stay at their defaults
            }
            ev = e;
            return true;
        }

        // Which kind of failure a restart was. hasBugcheck: Windows recorded a code (from the 41 or
        // the 1001). payloadRead: the 41 payload was available, so "no button" is a fact and not a
        // guess.
        public static FailureKind ClassifyFailure(bool hasBugcheck, bool payloadRead, ulong powerButtonTimestamp)
        {
            if (hasBugcheck) return FailureKind.BlueScreen;
            if (!payloadRead) return FailureKind.Unknown;
            return powerButtonTimestamp != 0 ? FailureKind.HangPowerButton : FailureKind.PowerLoss;
        }

        // PowerButtonTimestamp is the LAST press of the button in the session: one that woke the notebook
        // hours earlier is not evidence of a hang. It counts only when it came after the moment the PC
        // stopped (the 6008 down time, 1 min tolerance), or, without that time, within minutes before the restart.
        public static bool PowerButtonAfterStop(ulong filetime, DateTime? downAt, DateTime restartLocal)
        {
            if (filetime == 0 || filetime > (ulong)long.MaxValue) return false;
            DateTime t;
            try { t = DateTime.FromFileTimeUtc((long)filetime).ToLocalTime(); }
            catch (Exception) { return false; }
            if (downAt.HasValue) return t >= downAt.Value.AddMinutes(-1) && t <= restartLocal.AddMinutes(2);
            double minutesBefore = (restartLocal - t).TotalMinutes;
            return minutesBefore >= -2 && minutesBefore <= 15;
        }

        // The 6008 payload's <Binary> holds two SYSTEMTIMEs (16 bytes each, hex): the moment the
        // system was last known to be up, in local time and then in UTC. Locale-independent, unlike
        // the formatted time/date strings. Returns local time, or null.
        public static DateTime? ParseDownTimeFromBinary(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            string h = hex.Trim();
            if (h.Length < 64 || (h.Length % 2) != 0) return null;
            DateTime? local = SystemTimeAt(h, 0);
            DateTime? utc = SystemTimeAt(h, 32);
            if (utc.HasValue)
            {
                return DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime();
            }
            return local;
        }

        private static DateTime? SystemTimeAt(string hex, int offset)
        {
            try
            {
                int[] f = new int[8];
                for (int i = 0; i < 8; i++)
                {
                    int lo = Convert.ToInt32(hex.Substring(offset + i * 4, 2), 16);
                    int hi = Convert.ToInt32(hex.Substring(offset + i * 4 + 2, 2), 16);
                    f[i] = lo | (hi << 8);
                }
                // year, month, dayOfWeek, day, hour, minute, second, ms
                if (f[0] < 2000 || f[0] > 2100 || f[1] < 1 || f[1] > 12 || f[3] < 1 || f[3] > 31
                    || f[4] > 23 || f[5] > 59 || f[6] > 59 || f[7] > 999) return null;
                return new DateTime(f[0], f[1], f[3], f[4], f[5], f[6], f[7], DateTimeKind.Unspecified);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Down time from the 6008 event XML (first <Binary> payload). null when absent.
        public static DateTime? ParseDownTimeFromEvent6008(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                foreach (XmlNode n in doc.GetElementsByTagName("Binary"))
                {
                    DateTime? t = ParseDownTimeFromBinary(n.InnerText);
                    if (t.HasValue) return t;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // An executable named in the 1001 text ("... o processo csrss.exe terminou ..."). The
        // Windows text for 0xEF usually carries only addresses, so this is null more often than not.
        public static string ParseDyingProcessFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            Match m = ProcessRe.Match(message);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool TryParseNumber(string text, out ulong value)
        {
            value = 0;
            if (text == null) return false;
            string t = text.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(t.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
            }
            return ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        // Merges 41 and 1001 events (within MergeWindow) and attaches minidumps. Newest first.
        public static List<CrashEntry> Merge(IList<Kernel41Event> k41, IList<Wer1001Event> w1001, IList<DumpFileInfo> dumps)
        {
            return Merge(k41, w1001, null, dumps);
        }

        // Same, plus the 6008 events: each one joins the nearest unmatched restart (and gives it the
        // time the PC really stopped); a 6008 alone becomes an entry of kind Unknown.
        public static List<CrashEntry> Merge(IList<Kernel41Event> k41, IList<Wer1001Event> w1001, IList<Unexpected6008Event> e6008, IList<DumpFileInfo> dumps)
        {
            List<CrashEntry> entries = new List<CrashEntry>();
            if (k41 == null) k41 = new List<Kernel41Event>();
            if (w1001 == null) w1001 = new List<Wer1001Event>();
            if (e6008 == null) e6008 = new List<Unexpected6008Event>();
            if (dumps == null) dumps = new List<DumpFileInfo>();

            int n = k41.Count;
            CrashEntry[] byK = new CrashEntry[n];
            for (int i = 0; i < n; i++)
            {
                CrashEntry e = new CrashEntry();
                e.When = k41[i].When;
                e.Code = k41[i].BugcheckCode != 0 ? (long?)k41[i].BugcheckCode : null;
                e.Param1 = k41[i].Param1;
                e.SleepInProgress = k41[i].SleepInProgress;
                e.LongPowerButtonPress = k41[i].LongPowerButtonPress;
                e.WheaBootErrorCount = k41[i].WheaBootErrorCount;
                byK[i] = e;
                entries.Add(e);
            }

            bool[] used = new bool[n];
            List<Wer1001Event> ws = new List<Wer1001Event>(w1001);
            ws.Sort(delegate(Wer1001Event a, Wer1001Event b) { return a.When.CompareTo(b.When); });
            Dictionary<CrashEntry, string> paths = new Dictionary<CrashEntry, string>();
            foreach (Wer1001Event w in ws)
            {
                long? code = ParseBugcheckFromMessage(w.Message);
                string path = ParseDumpPathFromMessage(w.Message);
                int best = NearestUnused(k41, used, w.When);
                CrashEntry target;
                if (best >= 0)
                {
                    used[best] = true;
                    target = byK[best];
                    if (!target.Code.HasValue) target.Code = code;
                }
                else
                {
                    target = new CrashEntry();
                    target.When = w.When;
                    target.Code = code;
                    entries.Add(target);
                }
                if (path != null) paths[target] = path;
                string dying = ParseDyingProcessFromMessage(w.Message);
                if (dying != null && target.DyingProcess == null) target.DyingProcess = dying;
            }

            // 6008: matched against the 41s by time; a 41 that already has a 6008 is not reused.
            bool[] used6008 = new bool[n];
            List<Unexpected6008Event> es = new List<Unexpected6008Event>(e6008);
            es.Sort(delegate(Unexpected6008Event a, Unexpected6008Event b) { return a.When.CompareTo(b.When); });
            foreach (Unexpected6008Event x in es)
            {
                int best = NearestUnused(k41, used6008, x.When);
                if (best >= 0)
                {
                    used6008[best] = true;
                    if (x.DownAt.HasValue) byK[best].DownAt = x.DownAt;
                    continue;
                }
                // not next to a free 41: an entry already there (made from a 1001) absorbs it
                CrashEntry near = null;
                foreach (CrashEntry c in entries)
                {
                    if (Math.Abs((c.When - x.When).TotalSeconds) <= MergeWindow.TotalSeconds) { near = c; break; }
                }
                if (near != null)
                {
                    if (x.DownAt.HasValue && !near.DownAt.HasValue) near.DownAt = x.DownAt;
                    continue;
                }
                CrashEntry alone = new CrashEntry();
                alone.When = x.When;
                alone.DownAt = x.DownAt;
                entries.Add(alone);
            }

            for (int i = 0; i < n; i++)
            {
                bool button = PowerButtonAfterStop(k41[i].PowerButtonTimestamp, byK[i].DownAt, k41[i].When);
                byK[i].PowerButtonPressed = button;
                byK[i].Kind = ClassifyFailure(byK[i].Code.HasValue, k41[i].PayloadRead, button ? 1UL : 0UL);
            }
            foreach (CrashEntry e in entries)
            {
                if (e.Code.HasValue) e.Kind = FailureKind.BlueScreen; // e.g. a 1001 with a code and no 41
            }

            AttachDumps(entries, paths, dumps);
            entries.Sort(delegate(CrashEntry a, CrashEntry b) { return b.When.CompareTo(a.When); });
            return entries;
        }

        private static int NearestUnused(IList<Kernel41Event> k41, bool[] used, DateTime when)
        {
            int best = -1;
            double bestGap = double.MaxValue;
            for (int i = 0; i < k41.Count; i++)
            {
                if (used[i]) continue;
                double gap = Math.Abs((k41[i].When - when).TotalSeconds);
                if (gap <= MergeWindow.TotalSeconds && gap < bestGap)
                {
                    best = i;
                    bestGap = gap;
                }
            }
            return best;
        }

        private static void AttachDumps(List<CrashEntry> entries, Dictionary<CrashEntry, string> paths, IList<DumpFileInfo> dumps)
        {
            bool[] dumpUsed = new bool[dumps.Count];

            // Pass 1: the 1001 message names the dump file.
            foreach (CrashEntry e in entries)
            {
                string path;
                if (!paths.TryGetValue(e, out path)) continue;
                for (int d = 0; d < dumps.Count; d++)
                {
                    if (dumpUsed[d] || dumps[d] == null || dumps[d].Path == null) continue;
                    if (string.Equals(dumps[d].Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        dumpUsed[d] = true;
                        e.HasDump = true;
                        e.DumpPath = dumps[d].Path;
                        break;
                    }
                }
            }

            // Pass 2: by timestamp. Closest pair first so one dump goes to the nearest event only.
            List<KeyValuePair<double, int[]>> pairs = new List<KeyValuePair<double, int[]>>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].HasDump) continue;
                for (int d = 0; d < dumps.Count; d++)
                {
                    if (dumpUsed[d] || dumps[d] == null) continue;
                    TimeSpan delta = entries[i].When - dumps[d].Time;   // positive: event after the dump
                    if (delta <= DumpBefore && delta >= -DumpAfter)
                    {
                        pairs.Add(new KeyValuePair<double, int[]>(Math.Abs(delta.TotalSeconds), new int[] { i, d }));
                    }
                }
            }
            pairs.Sort(delegate(KeyValuePair<double, int[]> a, KeyValuePair<double, int[]> b) { return a.Key.CompareTo(b.Key); });
            foreach (KeyValuePair<double, int[]> p in pairs)
            {
                int ei = p.Value[0];
                int di = p.Value[1];
                if (entries[ei].HasDump || dumpUsed[di]) continue;
                dumpUsed[di] = true;
                entries[ei].HasDump = true;
                entries[ei].DumpPath = dumps[di].Path;
            }
        }
    }
}
