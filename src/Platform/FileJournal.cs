using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // The journal file: %APPDATA%\SenseMonitor\diario.log (+ diario.log.1, the previous one).
    // Every line is appended with a write-through handle that only has the "append data" right, so
    // the line is on disk when Write returns, appends from two processes (app and sentinel) do not
    // overwrite each other, and a process killed right after still leaves its last line readable.
    // The file is opened per line (a handful of lines per day). Never throws.
    public sealed class FileJournal : IJournal
    {
        private readonly string filePath;
        private readonly string source;
        private readonly long maxBytes;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();

        public FileJournal(string path, string source, long maxBytes, Func<DateTime> clock)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            filePath = path;
            this.source = string.IsNullOrEmpty(source) ? "app" : source;
            this.maxBytes = maxBytes;
            this.clock = clock ?? delegate { return DateTime.Now; };
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(UserData.Roaming, "diario.log");
            }
        }

        public string FilePath { get { return filePath; } }

        public void Write(string kind, string text)
        {
            try
            {
                string line = JournalFormat.Format(clock(), source, kind, text) + "\r\n";
                byte[] bytes = new UTF8Encoding(false).GetBytes(line);
                lock (sync)
                {
                    string dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    Rotate(bytes.Length);
                    using (FileStream fs = new FileStream(filePath, FileMode.OpenOrCreate, FileSystemRights.AppendData,
                        FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.WriteThrough))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception)
            {
                // nowhere else to report; the journal must never take the app down
            }
        }

        private void Rotate(int nextLineBytes)
        {
            try
            {
                FileInfo fi = new FileInfo(filePath);
                if (!fi.Exists || !JournalRotation.ShouldRotate(fi.Length, nextLineBytes, maxBytes)) return;
                string prev = JournalRotation.PreviousPath(filePath);
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(filePath, prev);
            }
            catch (Exception)
            {
                // the other process rotated first, or the file is busy: append to what is there
            }
        }

        // Oldest first: the previous file, then the current one; at most maxEntries (the newest).
        // For the Health page; unreadable files and torn lines are skipped.
        public static List<JournalEntry> ReadRecent(string path, int maxEntries)
        {
            List<string> lines = new List<string>();
            AddLines(lines, JournalRotation.PreviousPath(path));
            AddLines(lines, path);
            List<JournalEntry> all = JournalFormat.ParseAll(lines);
            if (maxEntries > 0 && all.Count > maxEntries) all.RemoveRange(0, all.Count - maxEntries);
            return all;
        }

        private static void AddLines(List<string> into, string file)
        {
            try
            {
                if (!File.Exists(file)) return;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null) into.Add(line);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
