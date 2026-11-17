using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using Forja.Core;

namespace Forja.Platform
{
    // Read-only history of blue screens / unexpected shutdowns from the System log
    // (Kernel-Power 41 + WER-SystemErrorReporting 1001) plus the minidumps on disk.
    // Both the log and C:\Windows\Minidump are readable without administrator rights.
    public static class CrashHistory
    {
        private const string Provider41 = "Microsoft-Windows-Kernel-Power";
        private const string Provider1001 = "Microsoft-Windows-WER-SystemErrorReporting";
        private const string Provider6008 = "EventLog";

        public static string MinidumpDirectory()
        {
            string root = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(root)) root = @"C:\Windows";
            return Path.Combine(root, "Minidump");
        }

        // Returns false when the System log cannot be read ("unknown"); true with an empty list
        // means there really were no crashes. Never throws. Newest first.
        public static bool TryRead(int days, out List<CrashEntry> entries)
        {
            return TryRead("System", DateTime.Now.AddDays(-days), MinidumpDirectory(), out entries);
        }

        internal static bool TryRead(string logName, DateTime sinceLocal, string dumpDir, out List<CrashEntry> entries)
        {
            entries = null;
            try
            {
                List<Kernel41Event> k41 = new List<Kernel41Event>();
                List<Wer1001Event> w1001 = new List<Wer1001Event>();
                List<Unexpected6008Event> e6008 = new List<Unexpected6008Event>();
                ReadEvents(logName, sinceLocal, k41, w1001, e6008);
                List<DumpFileInfo> dumps = ListDumps(dumpDir);
                entries = CrashParse.Merge(k41, w1001, e6008, dumps);
                return true;
            }
            catch (Exception)
            {
                entries = null;
                return false;
            }
        }

        private static void ReadEvents(string logName, DateTime sinceLocal, List<Kernel41Event> k41, List<Wer1001Event> w1001, List<Unexpected6008Event> e6008)
        {
            string stamp = sinceLocal.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            string query =
                "*[System[TimeCreated[@SystemTime>='" + stamp + "'] and (" +
                "(EventID=41 and Provider[@Name='" + Provider41 + "']) or " +
                "(EventID=1001 and Provider[@Name='" + Provider1001 + "']) or " +
                "(EventID=6008 and Provider[@Name='" + Provider6008 + "']))]]";
            EventLogQuery q = new EventLogQuery(logName, PathType.LogName, query);
            using (System.Diagnostics.Eventing.Reader.EventLogReader r = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
            {
                EventRecord rec;
                while ((rec = r.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        if (!rec.TimeCreated.HasValue) continue;
                        DateTime when = rec.TimeCreated.Value.ToLocalTime();
                        if (rec.Id == 41)
                        {
                            Kernel41Event detail;
                            if (CrashParse.TryParseEvent41Detail(rec.ToXml(), out detail))
                            {
                                detail.When = when;
                                k41.Add(detail);
                            }
                            else
                            {
                                // Unreadable payload still means an abnormal restart happened.
                                Kernel41Event e = new Kernel41Event();
                                e.When = when;
                                k41.Add(e);
                            }
                        }
                        else if (rec.Id == 6008)
                        {
                            Unexpected6008Event x = new Unexpected6008Event();
                            x.When = when;
                            x.DownAt = CrashParse.ParseDownTimeFromEvent6008(rec.ToXml());
                            e6008.Add(x);
                        }
                        else if (rec.Id == 1001)
                        {
                            Wer1001Event e = new Wer1001Event();
                            e.When = when;
                            e.Message = MessageOf(rec);
                            w1001.Add(e);
                        }
                    }
                }
            }
        }

        // The formatted text is localized; the raw properties are the fallback when no message DLL resolves.
        private static string MessageOf(EventRecord rec)
        {
            string text = null;
            try { text = rec.FormatDescription(); }
            catch (Exception) { text = null; }
            if (!string.IsNullOrEmpty(text)) return text;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (EventProperty p in rec.Properties)
                {
                    if (p.Value != null) sb.Append(p.Value.ToString()).Append(' ');
                }
                return sb.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        internal static List<DumpFileInfo> ListDumps(string dir)
        {
            List<DumpFileInfo> list = new List<DumpFileInfo>();
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, "*.dmp"))
                    {
                        try
                        {
                            DumpFileInfo d = new DumpFileInfo();
                            d.Path = f;
                            d.Time = File.GetLastWriteTime(f);
                            list.Add(d);
                        }
                        catch (Exception) { }
                    }
                }
                string root = Environment.GetEnvironmentVariable("SystemRoot");
                if (!string.IsNullOrEmpty(root))
                {
                    string full = Path.Combine(root, "MEMORY.DMP");
                    if (File.Exists(full))
                    {
                        DumpFileInfo d = new DumpFileInfo();
                        d.Path = full;
                        d.Time = File.GetLastWriteTime(full);
                        list.Add(d);
                    }
                }
            }
            catch (Exception)
            {
            }
            return list;
        }
    }
}
