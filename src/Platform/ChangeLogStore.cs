using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Forja.Core;

namespace Forja.Platform
{
    [DataContract]
    internal class ChangeLogFile
    {
        [DataMember(Name = "version")] public int Version;
        [DataMember(Name = "entries")] public List<ChangeEntry> Entries;
    }

    // JSON file under %APPDATA%\SenseMonitor\historico.json. Writes go to a temp file first
    // and replace the old one, so a power cut never leaves half a history.
    public class ChangeLogStore : IChangeLogStore
    {
        private readonly string path;

        public ChangeLogStore(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            this.path = path;
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(UserData.Roaming, "historico.json");
            }
        }

        public List<ChangeEntry> Load()
        {
            if (!File.Exists(path)) return new List<ChangeEntry>();
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { return new List<ChangeEntry>(); }
            catch (UnauthorizedAccessException) { return new List<ChangeEntry>(); }
            if (IsBlank(bytes)) return new List<ChangeEntry>();
            try
            {
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    ChangeLogFile f = (ChangeLogFile)new DataContractJsonSerializer(typeof(ChangeLogFile)).ReadObject(ms);
                    if (f == null || f.Entries == null) throw new SerializationException("no entries");
                    List<ChangeEntry> ok = new List<ChangeEntry>();
                    foreach (ChangeEntry e in f.Entries)
                    {
                        if (e != null && !string.IsNullOrEmpty(e.Id)) ok.Add(e);
                    }
                    return ok;
                }
            }
            catch (Exception)
            {
                KeepBackup();
                return new List<ChangeEntry>();
            }
        }

        public void Save(List<ChangeEntry> entries)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ChangeLogFile f = new ChangeLogFile();
            f.Version = 1;
            f.Entries = entries ?? new List<ChangeEntry>();
            string tmp = path + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(ChangeLogFile)).WriteObject(fs, f);
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private void KeepBackup()
        {
            try { File.Copy(path, path + ".bak", true); }
            catch (Exception) { }
        }

        private static bool IsBlank(byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                if (b != 0x20 && b != 0x09 && b != 0x0A && b != 0x0D && b != 0xEF && b != 0xBB && b != 0xBF) return false;
            }
            return true;
        }
    }
}
