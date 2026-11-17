using System;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // A flag that is a file: set = the file exists. Written through to disk (the evidence has to
    // survive a process kill or a power loss right after). Used for the fan marker
    // (ventoinha-custom.flag) and the open-session flag (sessao-aberta.flag).
    public sealed class FileFlag : IFlag
    {
        private readonly string filePath;

        public FileFlag(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            filePath = path;
        }

        public string FilePath { get { return filePath; } }

        public bool IsSet
        {
            get
            {
                try { return File.Exists(filePath); }
                catch (Exception) { return false; }
            }
        }

        public bool Set()
        {
            try
            {
                if (File.Exists(filePath)) return true;
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                byte[] body = Encoding.UTF8.GetBytes(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 16, FileOptions.WriteThrough))
                {
                    fs.Write(body, 0, body.Length);
                    fs.Flush(true); // the new file itself must reach the disk, not only its bytes
                }
                return File.Exists(filePath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Clear()
        {
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    if (File.Exists(filePath)) File.Delete(filePath);
                    if (!File.Exists(filePath)) return true;
                }
                catch (Exception)
                {
                    // another process may have it open for a moment
                }
                System.Threading.Thread.Sleep(50);
            }
            return !IsSet;
        }
    }
}
