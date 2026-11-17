using System;
using System.Security.Cryptography;
using System.Text;

namespace Forja.Core
{
    public enum SignatureCheck
    {
        Ok,
        // The key is missing, the placeholder, or not parseable.
        BadKey,
        // The embedded key is smaller than MinKeyBits.
        WeakKey,
        // The "public" key XML carries private parameters: refuse (it would mean a leaked key).
        PrivateKeyEmbedded,
        // Signature missing, malformed, wrong length, or does not match the data.
        BadSignature
    }

    // RSA (3072 bits minimum) + SHA-256, PKCS#1 v1.5. Used for the release manifest.
    public static class UpdateVerifier
    {
        public const int MinKeyBits = 3072;

        public static SignatureCheck Verify(byte[] data, string signatureBase64, string publicKeyXml)
        {
            if (!UpdateKey.IsConfigured(publicKeyXml)) return SignatureCheck.BadKey;
            RSACryptoServiceProvider rsa = null;
            try
            {
                rsa = new RSACryptoServiceProvider();
                rsa.PersistKeyInCsp = false;
                try { rsa.FromXmlString(publicKeyXml); }
                catch (Exception) { return SignatureCheck.BadKey; }
                if (!rsa.PublicOnly) return SignatureCheck.PrivateKeyEmbedded;
                RSAParameters p = rsa.ExportParameters(false);
                if (p.Modulus == null || p.Modulus.Length * 8 < MinKeyBits) return SignatureCheck.WeakKey;

                byte[] sig = DecodeSignature(signatureBase64);
                if (sig == null || data == null || data.Length == 0) return SignatureCheck.BadSignature;
                // A PKCS#1 signature is exactly as long as the modulus.
                if (sig.Length != p.Modulus.Length) return SignatureCheck.BadSignature;
                bool ok;
                try { ok = rsa.VerifyData(data, "SHA256", sig); }
                catch (CryptographicException) { return SignatureCheck.BadSignature; }
                return ok ? SignatureCheck.Ok : SignatureCheck.BadSignature;
            }
            finally
            {
                if (rsa != null) rsa.Dispose();
            }
        }

        // Base64 text of a .sig file (surrounding whitespace tolerated); null when not valid base64.
        public static byte[] DecodeSignature(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.Trim();
            if (t.Length == 0 || t.Length > 4096) return null;
            try { return Convert.FromBase64String(t); }
            catch (FormatException) { return null; }
        }

        public static string Describe(SignatureCheck r)
        {
            switch (r)
            {
                case SignatureCheck.Ok: return "assinatura válida";
                case SignatureCheck.BadKey: return "chave pública do app ausente ou não configurada";
                case SignatureCheck.WeakKey: return "chave pública fraca (menos de " + MinKeyBits + " bits)";
                case SignatureCheck.PrivateKeyEmbedded: return "a chave embutida contém parte privada";
                default: return "assinatura inválida";
            }
        }
    }

    // Release-side signing (used by ForjaRelease and the tests, never by the app itself).
    public static class UpdateSigner
    {
        public static string NewPrivateKeyXml(int bits)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(bits))
            {
                rsa.PersistKeyInCsp = false;
                return rsa.ToXmlString(true);
            }
        }

        public static string PublicPart(string privateKeyXml)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(privateKeyXml);
                return rsa.ToXmlString(false);
            }
        }

        // Base64 of the RSA/SHA-256 signature of exactly these bytes.
        public static string Sign(byte[] data, string privateKeyXml)
        {
            if (data == null || data.Length == 0) throw new ArgumentException("nada para assinar");
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(privateKeyXml);
                if (rsa.PublicOnly) throw new ArgumentException("a chave informada não é privada");
                if (rsa.ExportParameters(false).Modulus.Length * 8 < UpdateVerifier.MinKeyBits)
                {
                    throw new ArgumentException("chave menor que " + UpdateVerifier.MinKeyBits + " bits");
                }
                return Convert.ToBase64String(rsa.SignData(data, "SHA256"));
            }
        }
    }
}
