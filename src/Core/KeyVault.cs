using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Forja.Core
{
    // The release private key at rest: AES-256-CBC with a key derived from a passphrase by
    // PBKDF2-HMAC-SHA256 (at least 200,000 iterations), then HMAC-SHA256 over everything
    // (encrypt-then-MAC, separate keys). Layout:
    //   "SMK1" | iterations (int32 BE) | salt (16) | iv (16) | ciphertext | hmac (32)
    // The passphrase exists only in memory while it is typed; losing it (or the file) means the key is
    // gone and installed copies can only be replaced by a manual reinstall.
    public static class KeyVault
    {
        public const int MinIterations = 200000;
        public const int DefaultIterations = 600000;
        public const int MinPassphraseChars = 12;
        private const string Magic = "SMK1";
        private const int SaltLen = 16, IvLen = 16, MacLen = 32;

        public static string PassphraseProblem(string pass)
        {
            if (pass == null || pass.Length < MinPassphraseChars) return "a frase-senha precisa ter pelo menos " + MinPassphraseChars + " caracteres";
            return null;
        }

        public static byte[] Protect(string privateKeyXml, string passphrase, int iterations)
        {
            return Protect(privateKeyXml, passphrase, iterations, MinIterations);
        }

        // Tests only: lets the suite use a cheap iteration count. The production entry points above and
        // below always pass MinIterations.
        internal static byte[] Protect(string privateKeyXml, string passphrase, int iterations, int minIterations)
        {
            if (string.IsNullOrEmpty(privateKeyXml)) throw new ArgumentException("chave vazia");
            if (PassphraseProblem(passphrase) != null) throw new ArgumentException(PassphraseProblem(passphrase));
            if (iterations < minIterations) throw new ArgumentException("iterações abaixo do mínimo");
            byte[] salt = Random(SaltLen);
            byte[] iv = Random(IvLen);
            byte[] encKey, macKey;
            Derive(passphrase, salt, iterations, out encKey, out macKey);
            byte[] plain = new UTF8Encoding(false).GetBytes(privateKeyXml);
            byte[] cipher;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (ICryptoTransform t = aes.CreateEncryptor())
                {
                    cipher = t.TransformFinalBlock(plain, 0, plain.Length);
                }
            }
            Array.Clear(plain, 0, plain.Length);
            byte[] head = Header(iterations, salt, iv);
            byte[] body = new byte[head.Length + cipher.Length];
            Buffer.BlockCopy(head, 0, body, 0, head.Length);
            Buffer.BlockCopy(cipher, 0, body, head.Length, cipher.Length);
            byte[] mac = Mac(macKey, body);
            byte[] all = new byte[body.Length + MacLen];
            Buffer.BlockCopy(body, 0, all, 0, body.Length);
            Buffer.BlockCopy(mac, 0, all, body.Length, MacLen);
            Array.Clear(encKey, 0, encKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            return all;
        }

        // null (and a reason) when the passphrase is wrong or the file was altered; the two cases are
        // deliberately not told apart.
        public static string Unprotect(byte[] file, string passphrase, out string error)
        {
            return Unprotect(file, passphrase, out error, MinIterations);
        }

        internal static string Unprotect(byte[] file, string passphrase, out string error, int minIterations)
        {
            error = null;
            int headLen = 4 + 4 + SaltLen + IvLen;
            if (file == null || file.Length < headLen + 16 + MacLen || file.Length > 64 * 1024) { error = "arquivo de chave inválido"; return null; }
            if (Encoding.ASCII.GetString(file, 0, 4) != Magic) { error = "arquivo de chave inválido"; return null; }
            int iterations = (file[4] << 24) | (file[5] << 16) | (file[6] << 8) | file[7];
            if (iterations < minIterations || iterations > 50000000) { error = "arquivo de chave inválido (iterações fora do limite)"; return null; }
            byte[] salt = new byte[SaltLen], iv = new byte[IvLen];
            Buffer.BlockCopy(file, 8, salt, 0, SaltLen);
            Buffer.BlockCopy(file, 8 + SaltLen, iv, 0, IvLen);
            byte[] encKey, macKey;
            Derive(passphrase ?? "", salt, iterations, out encKey, out macKey);
            byte[] body = new byte[file.Length - MacLen];
            Buffer.BlockCopy(file, 0, body, 0, body.Length);
            byte[] want = Mac(macKey, body);
            byte[] have = new byte[MacLen];
            Buffer.BlockCopy(file, file.Length - MacLen, have, 0, MacLen);
            if (!ConstantTimeEquals(want, have)) { error = "frase-senha errada ou arquivo alterado"; return null; }
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encKey;
                    aes.IV = iv;
                    using (ICryptoTransform t = aes.CreateDecryptor())
                    {
                        byte[] plain = t.TransformFinalBlock(body, headLen, body.Length - headLen);
                        string xml = new UTF8Encoding(false).GetString(plain);
                        Array.Clear(plain, 0, plain.Length);
                        return xml;
                    }
                }
            }
            catch (CryptographicException)
            {
                error = "frase-senha errada ou arquivo alterado";
                return null;
            }
            finally
            {
                Array.Clear(encKey, 0, encKey.Length);
                Array.Clear(macKey, 0, macKey.Length);
            }
        }

        private static byte[] Header(int iterations, byte[] salt, byte[] iv)
        {
            byte[] h = new byte[4 + 4 + SaltLen + IvLen];
            Encoding.ASCII.GetBytes(Magic).CopyTo(h, 0);
            h[4] = (byte)(iterations >> 24);
            h[5] = (byte)(iterations >> 16);
            h[6] = (byte)(iterations >> 8);
            h[7] = (byte)iterations;
            Buffer.BlockCopy(salt, 0, h, 8, SaltLen);
            Buffer.BlockCopy(iv, 0, h, 8 + SaltLen, IvLen);
            return h;
        }

        private static void Derive(string pass, byte[] salt, int iterations, out byte[] encKey, out byte[] macKey)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(pass, salt, iterations, HashAlgorithmName.SHA256))
            {
                byte[] k = kdf.GetBytes(64);
                encKey = new byte[32];
                macKey = new byte[32];
                Buffer.BlockCopy(k, 0, encKey, 0, 32);
                Buffer.BlockCopy(k, 32, macKey, 0, 32);
                Array.Clear(k, 0, k.Length);
            }
        }

        private static byte[] Mac(byte[] key, byte[] data)
        {
            using (HMACSHA256 h = new HMACSHA256(key))
            {
                return h.ComputeHash(data);
            }
        }

        private static byte[] Random(int n)
        {
            byte[] b = new byte[n];
            using (RandomNumberGenerator r = RandomNumberGenerator.Create())
            {
                r.GetBytes(b);
            }
            return b;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }
}
