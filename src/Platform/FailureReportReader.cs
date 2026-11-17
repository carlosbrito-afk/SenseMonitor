using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using Forja.Core;

namespace Forja.Platform
{
    // Gathers what FailureReport needs for one unexpected restart: the System log events in the
    // minutes before it, the app's own journal and the temperature log. Strictly read-only (the
    // System log and the app's own files); never throws; every source reports whether it could be
    // read, so the report never turns "could not look" into "nothing there".
    public static class FailureReportReader
    {
        private const int MaxMessage = 160;
        private const int MaxExamined = 5000;   // records looked at in one window before giving up

        public static FailureReport Build(CrashEntry entry)
        {
            return FailureReport.Build(Gather(entry));
        }

        public static FailureReportInput Gather(CrashEntry entry)
        {
            FailureReportInput input = new FailureReportInput();
            input.Entry = entry;
            input.Events = new List<ReportEvent>();
            input.EventsDropped = new Dictionary<ReportCategory, int>();
            bool limit;
            input.EventsRead = TryReadEvents("System", FailureReport.WindowStart(entry), FailureReport.WindowEnd(entry), input.Events,
                input.EventsDropped, out limit);
            input.EventsLimitHit = limit;
            input.Journal = new List<JournalEntry>();
            input.JournalRead = TryReadJournal(Path.Combine(Safety.Dir, "diario.log"), input.Journal);
            input.Temps = new List<TempSample>();
            input.TempsRead = TryReadTemps(TempLogStore.DefaultDirectory(), entry.EffectiveTime.AddMinutes(-FailureReport.TempMinutes - 1),
                entry.EffectiveTime.AddMinutes(2), input.Temps);
            return input;
        }

        internal static bool TryReadEvents(string logName, DateTime fromLocal, DateTime toLocal, List<ReportEvent> into)
        {
            Dictionary<ReportCategory, int> dropped = new Dictionary<ReportCategory, int>();
            bool limit;
            return TryReadEvents(logName, fromLocal, toLocal, into, dropped, out limit);
        }

        // Reads the window NEWEST FIRST (the last minutes before the stop matter most), keeps at most
        // FailureReport.MaxEventsPerSection per category and counts what it left out, so a storm of
        // identical disk errors cannot push out the final minutes, and the report says it was cut.
        // Events are classified before their text is formatted (formatting is the slow part).
        internal static bool TryReadEvents(string logName, DateTime fromLocal, DateTime toLocal, List<ReportEvent> into,
            Dictionary<ReportCategory, int> dropped, out bool limitHit)
        {
            limitHit = false;
            try
            {
                string a = fromLocal.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                string b = toLocal.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                System.Text.StringBuilder prov = new System.Text.StringBuilder();
                foreach (string p in ReportEventFilter.Providers)
                {
                    if (prov.Length > 0) prov.Append(" or ");
                    prov.Append("Provider[@Name='").Append(p).Append("']");
                }
                string query = "*[System[TimeCreated[@SystemTime>='" + a + "' and @SystemTime<='" + b + "'] and (" + prov + ")]]";
                EventLogQuery q = new EventLogQuery(logName, PathType.LogName, query);
                q.ReverseDirection = true;
                Dictionary<ReportCategory, int> kept = new Dictionary<ReportCategory, int>();
                int examined = 0;
                using (EventLogReader r = new EventLogReader(q))
                {
                    while (true)
                    {
                        EventRecord rec = r.ReadEvent();
                        if (rec == null) break;
                        using (rec)
                        {
                            if (++examined > MaxExamined)
                            {
                                limitHit = true;
                                break;
                            }
                            if (!rec.TimeCreated.HasValue) continue;
                            int level = rec.Level.HasValue ? rec.Level.Value : 4;
                            ReportCategory? cat = ReportEventFilter.Classify(rec.ProviderName, rec.Id, level);
                            if (!cat.HasValue) continue;
                            int n;
                            kept.TryGetValue(cat.Value, out n);
                            if (n >= FailureReport.MaxEventsPerSection)
                            {
                                int d;
                                dropped.TryGetValue(cat.Value, out d);
                                dropped[cat.Value] = d + 1;
                                continue;
                            }
                            kept[cat.Value] = n + 1;
                            ReportEvent e = new ReportEvent();
                            e.When = rec.TimeCreated.Value.ToLocalTime();
                            e.Provider = rec.ProviderName;
                            e.Id = rec.Id;
                            e.Level = level;
                            e.Message = ShortMessage(rec);
                            into.Add(e);
                        }
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ShortMessage(EventRecord rec)
        {
            string text = null;
            try { text = rec.FormatDescription(); }
            catch (Exception) { text = null; }
            if (string.IsNullOrEmpty(text)) return "";
            int nl = text.IndexOfAny(new char[] { '\r', '\n' });
            if (nl > 0) text = text.Substring(0, nl);
            text = text.Trim();
            return text.Length > MaxMessage ? text.Substring(0, MaxMessage - 1) + "…" : text;
        }

        // true when the journal exists (an empty window is then a real "nothing written").
        internal static bool TryReadJournal(string path, List<JournalEntry> into)
        {
            try
            {
                if (!File.Exists(path) && !File.Exists(JournalRotation.PreviousPath(path))) return false;
                into.AddRange(FileJournal.ReadRecent(path, 4000));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The minute-by-minute log of the days the window touches. true when the folder exists.
        internal static bool TryReadTemps(string dir, DateTime fromLocal, DateTime toLocal, List<TempSample> into)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                for (DateTime day = fromLocal.Date; day <= toLocal.Date; day = day.AddDays(1))
                {
                    string file = Path.Combine(dir, TempLog.FileNameFor(day));
                    if (!File.Exists(file)) continue;
                    FileInfo fi = new FileInfo(file);
                    if (fi.Length > 1024 * 1024) continue;
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            TempSample s = TempLog.ParseLine(day, line);
                            if (s != null && s.Time >= fromLocal && s.Time <= toLocal) into.Add(s);
                        }
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Opens the folder that holds the memory dumps in Explorer (read-only; nothing is opened or
        // parsed). The folder may not exist when Windows never wrote a dump: then its parent opens.
        public static bool OpenDumpFolder()
        {
            try
            {
                string dir = CrashHistory.MinidumpDirectory();
                if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + dir + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
