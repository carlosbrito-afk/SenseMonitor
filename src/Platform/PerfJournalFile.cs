using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // %APPDATA%\SenseMonitor\teste-desempenho.journal: one line per step of the performance test,
    // written through to disk before the step runs (WriteThrough + Flush(true)), so after a
    // crash or power loss the last line says where the test was. Never throws.
    public sealed class PerfJournalFile : IPerfJournal
    {
        private const int KeepLines = 400;
        private readonly string path;
        private readonly object gate = new object();

        public PerfJournalFile() : this(DefaultPath()) { }

        public PerfJournalFile(string path)
        {
            this.path = path;
        }

        public static string DefaultPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Display.AppName, PerfJournal.FileName);
        }

        public void Write(string kind, string detail)
        {
            lock (gate)
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    byte[] bytes = new UTF8Encoding(false).GetBytes(PerfJournal.Line(DateTime.UtcNow, kind, detail) + "\r\n");
                    using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                    }
                }
                catch (Exception)
                {
                    // the journal must never stop the test
                }
            }
        }

        // Reads the whole journal. exists=false when there is none; readable=false when it cannot be read.
        public static List<string> ReadAll(string path, out bool exists, out bool readable)
        {
            List<string> lines = new List<string>();
            exists = false;
            readable = true;
            try
            {
                if (!File.Exists(path)) return lines;
                exists = true;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (Exception)
            {
                readable = false;
            }
            return lines;
        }

        // The crash sentence of an unfinished earlier test, or null.
        public static string PreviousCrashNote(string path)
        {
            bool exists, readable;
            List<string> lines = ReadAll(path, out exists, out readable);
            return readable ? PerfJournal.CrashNote(lines) : null;
        }

        // Keeps the file small: only the last lines survive. Called before a new test starts
        // (never in the middle of one).
        public static void TrimToRecent(string path)
        {
            try
            {
                bool exists, readable;
                List<string> lines = ReadAll(path, out exists, out readable);
                if (!exists || !readable || lines.Count <= KeepLines) return;
                File.WriteAllLines(path, PerfJournal.Trim(lines, KeepLines).ToArray(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }
    }
}
