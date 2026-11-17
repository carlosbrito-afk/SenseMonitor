using System;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // modo-teste.flag in %APPDATA%\SenseMonitor: exists from before the first mode write of the environment
    // test until the test ends on Dia a dia; its content says which profile was active before it ("modo=1").
    // Written through to disk like the fan marker, so a process kill leaves the evidence.
    public sealed class FileModeMarker : IModeMarker
    {
        public const string FileName = "modo-teste.flag";
        private readonly string filePath;

        public FileModeMarker(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            filePath = path;
        }

        public bool Exists
        {
            get
            {
                try { return File.Exists(filePath); }
                catch (Exception) { return false; }
            }
        }

        public bool Set(PerfMode before)
        {
            return Set(before, null);
        }

        public bool Set(PerfMode before, string fingerprint)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                byte[] body = Encoding.UTF8.GetBytes("modo=" + ModeCatalog.ProfileCode(before).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"
                    + "fp=" + (fingerprint ?? "").Replace("\r", "").Replace("\n", "") + "\r\n");
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 16, FileOptions.WriteThrough))
                {
                    fs.Write(body, 0, body.Length);
                    fs.Flush(true);
                }
                return File.Exists(filePath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string ReadFingerprint()
        {
            try
            {
                foreach (string raw in File.ReadAllText(filePath).Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("fp=", StringComparison.Ordinal)) return line.Length > 3 ? line.Substring(3) : null;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        public PerfMode? Read()
        {
            try
            {
                string first = File.ReadAllText(filePath).Split('\n')[0].Trim();
                if (!first.StartsWith("modo=", StringComparison.Ordinal)) return null;
                int code;
                if (!int.TryParse(first.Substring(5).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out code)) return null;
                if (code < 0 || code > 255) return null;
                return ModeCatalog.FromCode((byte)code);
            }
            catch (Exception)
            {
                return null;
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
                }
            }
            return !Exists;
        }
    }
}
