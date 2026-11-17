using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // Where the performance test writes down, line by line and flushed to disk, what it is about
    // to do. If the machine dies mid-test the last line says where.
    public interface IPerfJournal
    {
        void Write(string kind, string detail);
    }

    // The journal format: "2026-11-17T03:00:00Z|KIND|detail". Kinds: INICIO (a test begins),
    // MODO (about to write a mode), FASE (a phase begins), AMOSTRA (heartbeat during a phase),
    // ABORTO, RESTAURANDO, FIM (the test ended and the restore result is in the detail).
    public static class PerfJournal
    {
        public const string FileName = "teste-desempenho.journal";
        public const string Start = "INICIO";
        public const string End = "FIM";

        public static string Line(DateTime utc, string kind, string detail)
        {
            string d = (detail ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
            return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "|" + kind + "|" + d;
        }

        // null when the last test in the journal ended with FIM (or there is no journal);
        // otherwise a plain sentence naming the last thing it was doing.
        public static string CrashNote(IEnumerable<string> lines)
        {
            if (lines == null) return null;
            string lastStartTime = null;
            bool open = false;
            string lastKind = null, lastDetail = null, lastTime = null;
            foreach (string raw in lines)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string[] p = raw.Split(new char[] { '|' }, 3);
                if (p.Length < 2) continue;
                if (p[1] == Start)
                {
                    open = true;
                    lastStartTime = p[0];
                }
                else if (p[1] == End)
                {
                    open = false;
                    continue;
                }
                lastTime = p[0];
                lastKind = p[1];
                lastDetail = p.Length > 2 ? p[2] : "";
            }
            if (!open) return null;
            string where = lastKind == Start ? "no começo" : "na fase \"" + lastDetail + "\"";
            return "o último teste (iniciado em " + (lastStartTime ?? "?") + " UTC) terminou numa queda " + where + " (última anotação às " + (lastTime ?? "?") + " UTC)";
        }

        // Keeps the journal small: the lines of the last tests only.
        public static List<string> Trim(List<string> lines, int keep)
        {
            if (lines == null) return new List<string>();
            if (lines.Count <= keep) return lines;
            return lines.GetRange(lines.Count - keep, keep);
        }
    }
}
