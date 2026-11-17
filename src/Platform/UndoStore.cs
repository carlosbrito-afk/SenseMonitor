using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // The undo records of the Acer services the environment test disabled BEFORE a config exists (the installer's
    // run, from the Downloads folder). They are written at the moment of disabling to
    // %ProgramData%\SenseMonitor\estado\servicos-desfazer.json, in the verified state folder (StateFolder: a folder that fails
    // verification is renamed aside and recreated, never taken over in place; only Administrators and SYSTEM can write)
    // (a standard user must not be able to plant records that make an elevated app change services). The first
    // start of the installed app merges them into config.json and removes the file; a cancelled or failed
    // install leaves them there, so nothing is lost.
    public sealed class UndoStore
    {
        public const string FileName = "servicos-desfazer.json";
        private readonly string dir;
        private readonly bool secure;

        public static string DefaultDir
        {
            get { return StateFolder.DefaultPath; }
        }

        public UndoStore() : this(DefaultDir, true)
        {
        }

        // secure=false only in tests (a temp folder): no ACL changes.
        public UndoStore(string directory, bool secure)
        {
            dir = directory;
            this.secure = secure;
        }

        private string FilePath { get { return Path.Combine(dir, FileName); } }

        // The folder is verified before it is read or written (StateFolder: owner, ACL, no reparse point; a folder that
        // fails is renamed aside and replaced by a fresh protected one, never taken over in place). A test folder
        // (secure=false) is used as it is.
        private bool EnsureDir()
        {
            if (!secure)
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            return StateFolder.Prepare(dir, Safety.Journal) != null;
        }

        // Written through to disk (the evidence must survive a cancel or a crash right after).
        public bool Save(List<ServiceUndo> records)
        {
            try
            {
                if (!EnsureDir()) return false;
                byte[] data = new UTF8Encoding(false).GetBytes(ServiceUndoFile.ToJson(records));
                string tmp = FilePath + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public List<ServiceUndo> Load()
        {
            try
            {
                if (secure && !Directory.Exists(dir)) return new List<ServiceUndo>();
                if (!EnsureDir()) return new List<ServiceUndo>(); // an untrusted folder is renamed aside and replaced before any read
                if (!File.Exists(FilePath)) return new List<ServiceUndo>();
                return ServiceUndoFile.FromJson(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception)
            {
                return new List<ServiceUndo>();
            }
        }

        public bool Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return !File.Exists(FilePath);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
