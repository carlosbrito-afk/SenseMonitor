using System;
using System.Collections.Generic;

namespace Forja.Core
{
    public enum FanCondition
    {
        None,
        ThermalProtection,
        NoResponse,
        Watchdog
    }

    // Reads the fan controller's status text (the only public place those states show up).
    // The matching is on stable fragments of the texts FanController produces; a test drives the
    // controller into each state so a reworded status breaks that test, not the alerts silently.
    public static class FanStatusClassifier
    {
        public static FanCondition Classify(string status)
        {
            if (string.IsNullOrEmpty(status)) return FanCondition.None;
            string s = status.ToLowerInvariant();
            if (s.Contains("proteção térmica")) return FanCondition.ThermalProtection;
            if (s.Contains("não respondeu")) return FanCondition.NoResponse;
            if (s.Contains("sem responder")) return FanCondition.Watchdog;
            return FanCondition.None;
        }
    }

    // Which journal lines are worth an alert: the fan watchdog, the sentinel, the start-up recovery.
    // Lines are the ones the app and its sentinel write (diario.log); see JournalKind.
    public static class JournalAlertClassifier
    {
        // null = nothing to tell. Time and kind come from the line; the alert is stamped by the caller.
        public static Alert Classify(JournalEntry e)
        {
            if (e == null || e.Text == null) return null;
            string t = e.Text.ToLowerInvariant();
            if (e.Kind == JournalKind.State)
            {
                if (t.Contains("o vigia interno devolveu") || t.Contains("o guardião devolveu")) return AlertText.Watchdog(e.Time);
                return null;
            }
            if (e.Kind == JournalKind.Sentinel || e.Source == "sentinela")
            {
                if (t.Contains("voltando ao automático")) return AlertText.Watchdog(e.Time);
                if (t.Contains("não confirmado") || t.Contains("não o confirma")) return AlertText.FanUnconfirmed(e.Time);
                return null;
            }
            if (e.Kind == JournalKind.Recovery)
            {
                if (t.Contains("não confirmado")) return AlertText.FanUnconfirmed(e.Time);
                // "automático confirmado (marcador ...)" / "(ventoinha lida fora do automático ...)":
                // the previous run ended with the fans possibly not in Auto and this start fixed it.
                if (t.StartsWith("automático confirmado")) return AlertText.Watchdog(e.Time);
            }
            return null;
        }

        // The alerts for the lines newer than 'after' (local); returns them oldest first.
        public static List<Alert> Select(IList<JournalEntry> lines, DateTime after)
        {
            List<Alert> list = new List<Alert>();
            if (lines == null) return list;
            foreach (JournalEntry e in lines)
            {
                if (e == null || e.Time <= after) continue;
                Alert a = Classify(e);
                if (a != null) list.Add(a);
            }
            list.Sort(delegate(Alert x, Alert y) { return x.Local.CompareTo(y.Local); });
            return list;
        }
    }

    // "New crash since the app last ran." The log time of a crash entry (When) is the key: it is
    // stable between runs. Pure.
    public static class NewCrashDetector
    {
        // On the very first run there is nothing to compare with: only a crash in the last
        // FirstRunHours counts, so installing the app never announces last month's history.
        public const int FirstRunHours = 12;

        public static List<CrashEntry> Select(IList<CrashEntry> entries, long lastSeenTicks, DateTime nowLocal)
        {
            List<CrashEntry> fresh = new List<CrashEntry>();
            if (entries == null) return fresh;
            DateTime floor = lastSeenTicks > 0 ? new DateTime(lastSeenTicks) : nowLocal.AddHours(-FirstRunHours);
            foreach (CrashEntry e in entries)
            {
                if (e != null && e.When > floor) fresh.Add(e);
            }
            return fresh;
        }

        // The value to remember afterwards: the newest log time seen (never goes back).
        public static long NewestTicks(IList<CrashEntry> entries, long previous)
        {
            long best = previous;
            if (entries == null) return best;
            foreach (CrashEntry e in entries)
            {
                if (e != null && e.When.Ticks > best) best = e.When.Ticks;
            }
            return best;
        }
    }

    // Which unexpected restarts the user has not looked at yet. An incident is identified by the log time
    // of the restart plus its kind, stable across runs; the list of seen ones is kept in alertas-estado.json.
    public static class RestartIncidents
    {
        public const int UnseenHours = 72;
        public const int MaxSeen = 300;

        public static string IdOf(CrashEntry e)
        {
            return e.When.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + e.Kind;
        }

        // Restarts of the last UnseenHours whose id is not in 'seen', newest first as given.
        public static List<CrashEntry> Unseen(IList<CrashEntry> entries, ICollection<string> seen, DateTime nowLocal)
        {
            List<CrashEntry> list = new List<CrashEntry>();
            if (entries == null) return list;
            DateTime floor = nowLocal.AddHours(-UnseenHours);
            foreach (CrashEntry e in entries)
            {
                if (e == null || e.When < floor) continue;
                if (seen != null && seen.Contains(IdOf(e))) continue;
                list.Add(e);
            }
            return list;
        }

        // Adds the ids (keeping the newest MaxSeen). Returns true when the list changed.
        public static bool MarkSeen(List<string> seen, IEnumerable<CrashEntry> entries)
        {
            bool changed = false;
            foreach (CrashEntry e in entries)
            {
                string id = IdOf(e);
                if (!seen.Contains(id))
                {
                    seen.Add(id);
                    changed = true;
                }
            }
            if (seen.Count > MaxSeen) seen.RemoveRange(0, seen.Count - MaxSeen);
            return changed;
        }
    }
}
