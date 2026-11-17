using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Forja.Core
{
    [DataContract]
    public sealed class UpdateBlock
    {
        [DataMember(Name = "versao")] public string Versao = null;
        // UTC "yyyy-MM-ddTHH:mm:ssZ"; null = blocked for good (a rolled-back release).
        [DataMember(Name = "ate")] public string Ate = null;
        [DataMember(Name = "motivo")] public string Motivo = null;
    }

    // What the updater remembers between runs: when it last looked, and which versions it must
    // not try again (failed checks: 24 h; releases that were rolled back: for good). It lives in
    // the admin-only update folder and holds no trust decision: everything it influences is
    // re-verified cryptographically, so tampering can at most cause a retry.
    [DataContract]
    public sealed class UpdateState
    {
        public const int MaxBlocks = 20;
        // Permanent blocks may grow past MaxBlocks (they are never evicted), up to this.
        public const int HardCap = 200;
        public static readonly TimeSpan FailureBlock = TimeSpan.FromHours(24);

        [DataMember(Name = "ultimaVerificacao")] public string UltimaVerificacao = null;
        [DataMember(Name = "ultimaTentativa")] public string UltimaTentativa = null;
        [DataMember(Name = "bloqueios")] public List<UpdateBlock> Bloqueios = null;
        // The helper could not apply (swap failed, other instance running): the app does not try again
        // before this moment, so it is not closed and reopened at every idle window.
        [DataMember(Name = "aplicarDepoisDe")] public string AplicarDepoisDe = null;

        public static string FormatUtc(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public static DateTime? ParseUtc(string text)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(text) && DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
            {
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            }
            return null;
        }

        public DateTime? LastCheckUtc { get { return ParseUtc(UltimaVerificacao); } }

        // Never shortens an existing, later deferral.
        public void DeferApply(DateTime nowUtc, TimeSpan span)
        {
            DateTime until = nowUtc + span;
            DateTime? cur = ParseUtc(AplicarDepoisDe);
            if (cur.HasValue && cur.Value > until) return;
            AplicarDepoisDe = FormatUtc(until);
        }

        // True while an apply is being held back. An unreadable value fails closed (held back); a value
        // further away than a week (clock tampering) is ignored.
        public bool ApplyDeferred(DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(AplicarDepoisDe)) return false;
            DateTime? until = ParseUtc(AplicarDepoisDe);
            if (!until.HasValue) return true;
            if (until.Value - nowUtc > TimeSpan.FromDays(7)) return false;
            return until.Value > nowUtc;
        }
        public DateTime? LastAttemptUtc { get { return ParseUtc(UltimaTentativa); } }

        public void MarkAttempt(DateTime nowUtc)
        {
            UltimaTentativa = FormatUtc(nowUtc);
        }

        // A check that reached the end (even "nothing new") counts as a completed check.
        public void MarkChecked(DateTime nowUtc)
        {
            UltimaTentativa = FormatUtc(nowUtc);
            UltimaVerificacao = FormatUtc(nowUtc);
        }

        private List<UpdateBlock> List()
        {
            if (Bloqueios == null) Bloqueios = new List<UpdateBlock>();
            return Bloqueios;
        }

        public bool IsBlocked(UpdateVersion v, DateTime nowUtc)
        {
            if (v == null) return true;
            string key = v.ToString();
            List<UpdateBlock> l = List();
            for (int i = 0; i < l.Count; i++)
            {
                UpdateBlock b = l[i];
                if (b == null || b.Versao != key) continue;
                if (b.Ate == null) return true;
                DateTime? until = ParseUtc(b.Ate);
                // An unreadable end date is treated as blocked (fail closed).
                if (!until.HasValue || until.Value > nowUtc) return true;
            }
            return false;
        }

        // Do not retry this version for 24 h (never shortens an existing longer or permanent block).
        public void BlockForFailure(UpdateVersion v, DateTime nowUtc, string reason)
        {
            if (v == null) return;
            string key = v.ToString();
            string until = FormatUtc(nowUtc + FailureBlock);
            UpdateBlock existing = Find(key);
            if (existing != null)
            {
                if (existing.Ate == null) return;
                DateTime? old = ParseUtc(existing.Ate);
                if (old.HasValue && old.Value >= nowUtc + FailureBlock) return;
                existing.Ate = until;
                existing.Motivo = Clip(reason);
                return;
            }
            AddBlock(key, until, reason);
        }

        // A release that was installed and then rolled back: never again.
        public void BlockPermanently(UpdateVersion v, string reason)
        {
            if (v == null) return;
            string key = v.ToString();
            UpdateBlock existing = Find(key);
            if (existing != null)
            {
                existing.Ate = null;
                existing.Motivo = Clip(reason);
                return;
            }
            AddBlock(key, null, reason);
        }

        // Drops expired timed blocks and blocks of versions that are not newer than the running one.
        public void Prune(DateTime nowUtc, UpdateVersion current)
        {
            List<UpdateBlock> l = List();
            for (int i = l.Count - 1; i >= 0; i--)
            {
                UpdateBlock b = l[i];
                UpdateVersion bv = null;
                bool drop = b == null || !UpdateVersion.TryParse(b.Versao, out bv);
                if (!drop)
                {
                    if (current != null && !bv.IsNewerThan(current)) drop = true;
                    else if (b.Ate != null)
                    {
                        DateTime? until = ParseUtc(b.Ate);
                        if (until.HasValue && until.Value <= nowUtc) drop = true;
                    }
                }
                if (drop) l.RemoveAt(i);
            }
        }

        private UpdateBlock Find(string key)
        {
            List<UpdateBlock> l = List();
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] != null && l[i].Versao == key) return l[i];
            }
            return null;
        }

        private void AddBlock(string key, string until, string reason)
        {
            List<UpdateBlock> l = List();
            if (l.Count >= MaxBlocks)
            {
                // Make room by dropping the oldest TIMED block; a permanent block (a rolled-back release)
                // is never evicted. With nothing to drop, a timed block is simply not recorded (it would
                // only mean one more retry in 24 h) while a permanent one is always recorded.
                int victim = -1;
                for (int i = 0; i < l.Count; i++)
                {
                    if (l[i] != null && l[i].Ate != null) { victim = i; break; }
                }
                if (victim >= 0) l.RemoveAt(victim);
                else if (until != null) return;
                else if (l.Count >= HardCap) return;
            }
            UpdateBlock b = new UpdateBlock();
            b.Versao = key;
            b.Ate = until;
            b.Motivo = Clip(reason);
            l.Add(b);
        }

        private static string Clip(string s)
        {
            if (s == null) return null;
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        // Never throws: a missing or damaged file is simply a fresh state.
        public static UpdateState Parse(byte[] json)
        {
            UpdateState s;
            return TryParse(json, out s) ? s : new UpdateState();
        }

        // false = unreadable (the caller may fall back to a previous copy).
        public static bool TryParse(byte[] json, out UpdateState state)
        {
            state = null;
            try
            {
                if (json != null && json.Length > 0 && json.Length <= 64 * 1024)
                {
                    using (MemoryStream ms = new MemoryStream(json, false))
                    {
                        state = new DataContractJsonSerializer(typeof(UpdateState)).ReadObject(ms) as UpdateState;
                    }
                }
            }
            catch (Exception)
            {
                state = null;
            }
            return state != null;
        }

        public byte[] ToBytes()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(UpdateState)).WriteObject(ms, this);
                return ms.ToArray();
            }
        }
    }

    // When to look for a release: once a day, plus a check shortly after the app starts.
    public static class UpdateSchedule
    {
        public const int StartupDelayMs = 2 * 60 * 1000;
        public static readonly TimeSpan Period = TimeSpan.FromHours(24);
        // After a start the check also runs when the last completed one is older than this
        // (so restarting the app all day does not hammer the API).
        public static readonly TimeSpan StartupMinAge = TimeSpan.FromHours(6);
        // Any attempt (failed ones too) keeps the next one at least this far away.
        public static readonly TimeSpan RetryAfterAttempt = TimeSpan.FromHours(1);

        public static bool IsDue(DateTime nowUtc, DateTime? lastCheckUtc, DateTime? lastAttemptUtc, bool startupCheck)
        {
            // A clock set back must not freeze the schedule.
            if (lastAttemptUtc.HasValue && lastAttemptUtc.Value <= nowUtc && nowUtc - lastAttemptUtc.Value < RetryAfterAttempt) return false;
            if (!lastCheckUtc.HasValue || lastCheckUtc.Value > nowUtc) return true;
            TimeSpan age = nowUtc - lastCheckUtc.Value;
            return age >= (startupCheck ? StartupMinAge : Period);
        }
    }

    public enum UpdateVerdict
    {
        Proceed,
        UpToDate,
        Blocked,
        OsTooOld
    }

    public static class UpdatePolicy
    {
        // Cheap pre-check from the release tag, before any other download.
        public static UpdateVerdict ForRelease(UpdateVersion current, UpdateVersion candidate, UpdateState state, DateTime nowUtc)
        {
            if (current == null || candidate == null || !candidate.IsNewerThan(current)) return UpdateVerdict.UpToDate;
            if (state != null && state.IsBlocked(candidate, nowUtc)) return UpdateVerdict.Blocked;
            return UpdateVerdict.Proceed;
        }

        // After the manifest signature verified.
        public static UpdateVerdict ForManifest(UpdateVersion current, UpdateManifest manifest, Version os, UpdateState state, DateTime nowUtc)
        {
            if (manifest == null) return UpdateVerdict.UpToDate;
            UpdateVerdict v = ForRelease(current, manifest.Version, state, nowUtc);
            if (v != UpdateVerdict.Proceed) return v;
            if (!UpdateManifestFile.OsSupported(manifest, os)) return UpdateVerdict.OsTooOld;
            return UpdateVerdict.Proceed;
        }

        // Updates only ever replace the installed copy; a development build run from another
        // folder never updates itself.
        public static bool IsInstalledCopy(string runningExePath, string installedExePath)
        {
            if (string.IsNullOrEmpty(runningExePath) || string.IsNullOrEmpty(installedExePath)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(runningExePath), Path.GetFullPath(installedExePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
