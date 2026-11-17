using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // Rolling temperature log on disk: %LOCALAPPDATA%\SenseMonitor\temperaturas\yyyy-MM-dd.csv,
    // one line per minute, 14 days kept. Nothing here throws: logging must never hurt the app.
    public class TempLogStore
    {
        // A day is ~1440 lines of ~35 bytes (about 50 KB); this cap only guards against runaway growth.
        public const int MaxFileBytes = 256 * 1024;
        // Largest file Summaries will read.
        private const int MaxReadBytes = 1024 * 1024;

        private readonly string dir;
        private readonly object gate = new object();
        private DateTime lastMinute = DateTime.MinValue;
        private DateTime lastPurgeDay = DateTime.MinValue;

        public TempLogStore()
            : this(DefaultDirectory())
        {
        }

        public TempLogStore(string directory)
        {
            dir = directory;
        }

        public static string DefaultDirectory()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(baseDir, "SenseMonitor"), "temperaturas");
        }

        public string Directory
        {
            get { return dir; }
        }

        // Writes one line for the minute of t; further calls in the same minute are ignored, so the
        // caller can invoke this on every sensor tick. The file is closed after each write so a power
        // loss keeps everything up to the last minute. A minute is attempted once: a failed or capped
        // write is not retried on every tick. A torn last line (power loss mid-write) is closed first,
        // so the new sample never gets glued to it.
        public void Append(DateTime t, SensorReading r)
        {
            lock (gate)
            {
                DateTime minute = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
                if (minute == lastMinute) return;
                lastMinute = minute;
                try
                {
                    if (string.IsNullOrEmpty(dir)) return;
                    System.IO.Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, TempLog.FileNameFor(t));
                    using (FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        long len = fs.Length;
                        if (len >= MaxFileBytes) return;
                        string prefix = "";
                        if (len == 0) prefix = TempLog.Header + "\r\n";
                        else
                        {
                            fs.Seek(-1, SeekOrigin.End);
                            if (fs.ReadByte() != '\n') prefix = "\r\n";
                        }
                        fs.Seek(0, SeekOrigin.End);
                        byte[] bytes = Encoding.UTF8.GetBytes(prefix + TempLog.FormatLine(t, r) + "\r\n");
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception)
                {
                }
                try
                {
                    if (t.Date != lastPurgeDay && !string.IsNullOrEmpty(dir))
                    {
                        lastPurgeDay = t.Date;
                        Purge(t);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private void Purge(DateTime today)
        {
            try
            {
                List<string> names = new List<string>();
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.csv"))
                {
                    names.Add(Path.GetFileName(f));
                }
                foreach (string old in TempLog.SelectExpired(names, today, TempLog.KeepDays))
                {
                    try { File.Delete(Path.Combine(dir, old)); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        // Per-day summaries for the last N days (today included); newest first; days without samples are omitted.
        public DaySummary[] Summaries(int days)
        {
            return Summaries(days, DateTime.Now);
        }

        public DaySummary[] Summaries(int days, DateTime today)
        {
            List<DaySummary> result = new List<DaySummary>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return result.ToArray();
                List<string> names = new List<string>();
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.csv"))
                {
                    names.Add(Path.GetFileName(f));
                }
                foreach (string name in TempLog.SelectForDays(names, today, days))
                {
                    DateTime day;
                    if (!TempLog.TryParseFileDay(name, out day)) continue;
                    List<TempSample> samples = ReadFile(Path.Combine(dir, name), day);
                    if (samples.Count == 0) continue;
                    result.Add(TempLog.Summarize(day, samples));
                }
                result.Sort(delegate(DaySummary a, DaySummary b) { return b.Day.CompareTo(a.Day); });
            }
            catch (Exception)
            {
            }
            return result.ToArray();
        }

        private static List<TempSample> ReadFile(string path, DateTime day)
        {
            List<TempSample> list = new List<TempSample>();
            try
            {
                if (new FileInfo(path).Length > MaxReadBytes) return list;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        TempSample s = TempLog.ParseLine(day, line);
                        if (s != null) list.Add(s);
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
