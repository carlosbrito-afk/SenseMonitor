using System;
using System.IO;
using System.Security.Cryptography;

namespace Forja.Platform
{
    // The exe being installed, HELD open for the whole elevated install. It sits in a folder the user (and any user-level
    // process) can write to, and an exe that is running can be renamed but not rewritten: without this, something could
    // rename it while the installer waits for a click and drop another SenseMonitor.exe at the same path, and the install
    // would copy that one into Program Files, to run elevated at every logon. Opened with FileShare.Read only (nobody can
    // write, rename or delete it while the handle is open); the copy and its hash are taken from THIS handle, never from
    // the path again. The hash is taken once, at open, and can be compared with the one the first stage saw.
    public sealed class SourceHold : IDisposable
    {
        private readonly FileStream stream;
        private readonly string sha256;

        private SourceHold(FileStream stream, string sha256)
        {
            this.stream = stream;
            this.sha256 = sha256;
        }

        // Lowercase hex SHA-256 of the held file, as read when it was opened.
        public string Sha256 { get { return sha256; } }

        // null + a pt-BR reason when the file cannot be held (somebody has it open for writing, it is missing...).
        public static SourceHold Open(string path, out string error)
        {
            error = null;
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                string h = HashFromStart(fs);
                return new SourceHold(fs, h);
            }
            catch (Exception ex)
            {
                if (fs != null) fs.Dispose();
                error = "não foi possível segurar o arquivo do instalador (" + ex.GetType().Name + ")";
                return null;
            }
        }

        private static string HashFromStart(FileStream fs)
        {
            fs.Position = 0;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
                sha.TransformFinalBlock(buf, 0, 0);
                return Hex(sha.Hash);
            }
        }

        private static string Hex(byte[] b)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        // Writes the held bytes to dest from the start and returns the SHA-256 of what was written.
        public string CopyTo(Stream dest)
        {
            stream.Position = 0;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    dest.Write(buf, 0, n);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                dest.Flush();
                return Hex(sha.Hash);
            }
        }

        // The hash the first stage saw (null = it did not say): null when they are equal, else the reason to refuse.
        public string Verify(string expectedSha256Hex)
        {
            if (expectedSha256Hex == null) return null;
            if (string.Equals(expectedSha256Hex, sha256, StringComparison.OrdinalIgnoreCase)) return null;
            return "o arquivo do instalador mudou entre a primeira janela e a instalação; nada foi instalado. Baixe o SenseMonitor.exe de novo";
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
