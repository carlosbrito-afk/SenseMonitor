using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    // manifest.json of a release. The file is signed byte for byte (manifest.json.sig), so the
    // app verifies the signature over the downloaded bytes FIRST and only then parses them.
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "versao")] public string Versao;
        [DataMember(Name = "sha256")] public string Sha256;
        [DataMember(Name = "tamanho")] public long Tamanho;
        [DataMember(Name = "data")] public string Data;
        [DataMember(Name = "notas")] public string Notas;
        // Optional "10.0.19041": the oldest Windows build this release supports.
        [DataMember(Name = "minimoWindows")] public string MinimoWindows;

        public UpdateVersion Version
        {
            get
            {
                UpdateVersion v;
                return UpdateVersion.TryParse(Versao, out v) ? v : null;
            }
        }
    }

    public static class UpdateManifestFile
    {
        public const int MaxManifestBytes = 64 * 1024;
        public const long MaxExeBytes = 20L * 1024 * 1024;
        public const int MaxNotesChars = 4000;

        private static readonly Regex Sha256Shape = new Regex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

        // Parses AND validates. On failure manifest is null and error says why (pt-BR, one line).
        public static bool TryParse(byte[] json, out UpdateManifest manifest, out string error)
        {
            manifest = null;
            error = null;
            if (json == null || json.Length == 0) { error = "manifesto vazio"; return false; }
            if (json.Length > MaxManifestBytes) { error = "manifesto grande demais"; return false; }
            UpdateManifest m;
            try
            {
                using (MemoryStream ms = new MemoryStream(json, false))
                {
                    m = new DataContractJsonSerializer(typeof(UpdateManifest)).ReadObject(ms) as UpdateManifest;
                }
            }
            catch (Exception)
            {
                error = "manifesto ilegível";
                return false;
            }
            if (m == null) { error = "manifesto ilegível"; return false; }
            error = Validate(m);
            if (error != null) return false;
            m.Sha256 = m.Sha256.ToLowerInvariant();
            manifest = m;
            return true;
        }

        // null = valid.
        public static string Validate(UpdateManifest m)
        {
            if (m == null) return "manifesto vazio";
            if (m.Version == null) return "versão inválida no manifesto";
            if (m.Sha256 == null || !Sha256Shape.IsMatch(m.Sha256)) return "sha256 inválido no manifesto";
            if (m.Tamanho <= 0 || m.Tamanho > MaxExeBytes) return "tamanho inválido no manifesto";
            if (m.Data != null && m.Data.Length > 40) return "data inválida no manifesto";
            if (m.Notas != null && m.Notas.Length > MaxNotesChars) return "notas grandes demais no manifesto";
            if (m.MinimoWindows != null)
            {
                Version v;
                if (!System.Version.TryParse(m.MinimoWindows, out v)) return "minimoWindows inválido no manifesto";
            }
            return null;
        }

        // Is this OS new enough for the release? No minimum means yes.
        public static bool OsSupported(UpdateManifest m, Version os)
        {
            if (m == null || string.IsNullOrEmpty(m.MinimoWindows)) return true;
            Version min;
            if (!System.Version.TryParse(m.MinimoWindows, out min)) return false;
            return os != null && os >= min;
        }

        // Release side: the exact bytes to sign. Compact JSON, UTF-8 without BOM.
        public static byte[] Build(string versao, string sha256, long tamanho, string dataIso, string notas, string minimoWindows)
        {
            UpdateManifest m = new UpdateManifest();
            m.Versao = versao;
            m.Sha256 = sha256 == null ? null : sha256.ToLowerInvariant();
            m.Tamanho = tamanho;
            m.Data = dataIso;
            m.Notas = notas;
            m.MinimoWindows = minimoWindows;
            string err = Validate(m);
            if (err != null) throw new ArgumentException(err);
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(UpdateManifest)).WriteObject(ms, m);
                return ms.ToArray();
            }
        }

        // Size and SHA-256 of the downloaded exe against the (already verified) manifest.
        // null = matches.
        public static string CheckExe(UpdateManifest m, long actualSize, string actualSha256Hex)
        {
            if (m == null) return "manifesto ausente";
            if (actualSize != m.Tamanho) return "tamanho do arquivo diferente do manifesto";
            if (actualSha256Hex == null || !string.Equals(actualSha256Hex, m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "SHA-256 do arquivo diferente do manifesto";
            }
            return null;
        }

        public static string Sha256Hex(Stream s)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] hash = h.ComputeHash(s);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
            {
                return Sha256Hex(ms);
            }
        }
    }
}
