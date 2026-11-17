using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // Alert settings (alertas.json) and what was already announced (alertas-estado.json), both in
    // the app's data folder. Written atomically (temp file, then replace); a missing or damaged file
    // loads as the defaults. Never throws.
    public static class AlertStore
    {
        public const string SettingsFile = "alertas.json";
        public const string StateFile = "alertas-estado.json";

        public static string SettingsPath { get { return Path.Combine(Safety.Dir, SettingsFile); } }
        public static string StatePath { get { return Path.Combine(Safety.Dir, StateFile); } }

        public static AlertSettings LoadSettings()
        {
            return LoadSettings(SettingsPath);
        }

        public static AlertSettings LoadSettings(string path)
        {
            AlertSettings s = Read<AlertSettings>(path);
            return (s ?? new AlertSettings()).Normalize();
        }

        public static bool SaveSettings(AlertSettings s)
        {
            return SaveSettings(SettingsPath, s);
        }

        public static bool SaveSettings(string path, AlertSettings s)
        {
            return Write(path, s);
        }

        public static AlertState LoadState()
        {
            return LoadState(StatePath);
        }

        public static AlertState LoadState(string path)
        {
            return Read<AlertState>(path) ?? new AlertState();
        }

        public static bool SaveState(AlertState s)
        {
            return SaveState(StatePath, s);
        }

        public static bool SaveState(string path, AlertState s)
        {
            return Write(path, s);
        }

        internal static T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(fs);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool Write<T>(string path, T value)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                byte[] data;
                using (MemoryStream ms = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                    data = ms.ToArray();
                }
                string tmp = path + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // Whether a full-screen game or Windows focus assist is in the way of a balloon. The question
    // goes to the shell (SHQueryUserNotificationState); no handle to any game process is opened.
    public static class UserNotifyState
    {
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        public static AlertContext Query()
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) != 0) return AlertContext.Clear();
                return UserNotifyMap.ToContext(state);
            }
            catch (Exception)
            {
                return AlertContext.Clear();
            }
        }
    }

    // CPU model name (for the per-CPU heat limit). One WMI query; null when it fails.
    public static class CpuInfo
    {
        public static string ReadName()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        object n = o["Name"];
                        if (n != null) return n.ToString().Trim();
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }

    // Reads the lines other writers append to diario.log without re-reading what it already
    // returned: remembers the byte offset. A file that shrank (rotated) is read from the start.
    // Only whole lines are returned; a torn last line waits for the next call. Never throws.
    public sealed class JournalTail
    {
        private readonly string path;
        private long position;

        public JournalTail(string path)
        {
            this.path = path;
        }

        // Entries appended since the last call (the first call returns the whole current file).
        public List<JournalEntry> ReadNew()
        {
            List<string> lines = new List<string>();
            try
            {
                if (!File.Exists(path)) return new List<JournalEntry>();
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < position) position = 0;
                    if (fs.Length == position) return new List<JournalEntry>();
                    fs.Seek(position, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - position];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    int end = got;
                    while (end > 0 && buf[end - 1] != (byte)'\n') end--;   // keep a torn last line for later
                    if (end == 0) return new List<JournalEntry>();
                    position += end;
                    string text = new UTF8Encoding(false).GetString(buf, 0, end);
                    foreach (string l in text.Split('\n')) if (l.Length > 0) lines.Add(l);
                }
            }
            catch (Exception)
            {
                return new List<JournalEntry>();
            }
            return JournalFormat.ParseAll(lines);
        }
    }
}
