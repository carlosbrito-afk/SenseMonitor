using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Forja.Core
{
    // What the app tells the update helper, through a file in the admin-only data folder (never through
    // the command line): which process to wait for, which version it is about to install, when this was
    // written, and a nonce the helper echoes back to say "ready". Paths are NOT in here: the helper
    // derives them from the fixed install layout.
    [DataContract]
    public sealed class ApplyInstructions
    {
        [DataMember(Name = "pid")] public int Pid;
        [DataMember(Name = "versao")] public string Versao = null;
        [DataMember(Name = "criadoUtc")] public string CriadoUtc = null;
        [DataMember(Name = "nonce")] public string Nonce = null;
    }

    public static class ApplyInstructionsFile
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
        private const int MaxBytes = 4096;

        public static byte[] Build(int pid, string version, DateTime nowUtc, string nonce)
        {
            ApplyInstructions i = new ApplyInstructions();
            i.Pid = pid;
            i.Versao = version;
            i.CriadoUtc = UpdateState.FormatUtc(nowUtc);
            i.Nonce = nonce;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ApplyInstructions)).WriteObject(ms, i);
                return ms.ToArray();
            }
        }

        public static string NewNonce()
        {
            byte[] b = new byte[16];
            using (System.Security.Cryptography.RandomNumberGenerator r = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                r.GetBytes(b);
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder(32);
            for (int k = 0; k < b.Length; k++) sb.Append(b[k].ToString("x2"));
            return sb.ToString();
        }

        // Strict: a pid, a real version, a fresh timestamp (not older than MaxAge, not from the future)
        // and a 32-hex nonce. null error = valid.
        public static bool TryParse(byte[] json, DateTime nowUtc, out ApplyInstructions result, out string error)
        {
            result = null;
            error = null;
            if (json == null || json.Length == 0 || json.Length > MaxBytes) { error = "instruções vazias ou grandes demais"; return false; }
            ApplyInstructions i;
            try
            {
                using (MemoryStream ms = new MemoryStream(json, false))
                {
                    i = new DataContractJsonSerializer(typeof(ApplyInstructions)).ReadObject(ms) as ApplyInstructions;
                }
            }
            catch (Exception)
            {
                error = "instruções ilegíveis";
                return false;
            }
            if (i == null) { error = "instruções ilegíveis"; return false; }
            if (i.Pid <= 0) { error = "pid inválido"; return false; }
            UpdateVersion v;
            if (!UpdateVersion.TryParse(i.Versao, out v)) { error = "versão inválida"; return false; }
            DateTime? made = UpdateState.ParseUtc(i.CriadoUtc);
            if (!made.HasValue) { error = "data inválida"; return false; }
            if (made.Value > nowUtc + TimeSpan.FromMinutes(1)) { error = "instruções do futuro"; return false; }
            if (nowUtc - made.Value > MaxAge) { error = "instruções velhas demais"; return false; }
            if (i.Nonce == null || !System.Text.RegularExpressions.Regex.IsMatch(i.Nonce, @"\A[0-9a-f]{32}\z")) { error = "nonce inválido"; return false; }
            result = i;
            return true;
        }
    }
}
