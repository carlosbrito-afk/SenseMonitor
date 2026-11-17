using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Forja.Core
{
    // Append-only journal of what the app (or its sentinel) did to the firmware: every write
    // (a line BEFORE and a line AFTER it), every mode / fan state change, start and exit.
    // This file holds the store-independent part: the sink interface and the line format that
    // the Health page will read later. No personal data is ever written (no user name, no paths).
    public interface IJournal
    {
        // Never throws.
        void Write(string kind, string text);
    }

    public sealed class NullJournal : IJournal
    {
        public static readonly NullJournal Instance = new NullJournal();

        public void Write(string kind, string text)
        {
        }
    }

    // Event kinds, ASCII so a line is easy to grep.
    public static class JournalKind
    {
        public const string Start = "INICIO";
        public const string Exit = "SAIDA";
        public const string WriteBefore = "ESCRITA-ANTES";
        public const string WriteAfter = "ESCRITA-DEPOIS";
        public const string State = "ESTADO";
        public const string Blocked = "BLOQUEADO";
        public const string Recovery = "RECUPERACAO";
        public const string Sentinel = "SENTINELA";
        public const string Config = "CONFIG";
        public const string Action = "ACAO";
    }

    public sealed class JournalEntry
    {
        public DateTime Time;
        public string Source;
        public string Kind;
        public string Text;
    }

    // One line = "yyyy-MM-dd HH:mm:ss.fff<TAB>source<TAB>KIND<TAB>text" (local time). Source is "app"
    // or "sentinela". Text never contains tabs or line breaks.
    public static class JournalFormat
    {
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
        public const int MaxTextLength = 300;

        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else if (c < ' ') continue;
                else sb.Append(c);
            }
            string s = sb.ToString().Trim();
            if (s.Length > MaxTextLength) s = s.Substring(0, MaxTextLength - 1) + "…";
            return s;
        }

        // Without the trailing line break.
        public static string Format(DateTime time, string source, string kind, string text)
        {
            return time.ToString(TimeFormat, CultureInfo.InvariantCulture)
                + "\t" + Sanitize(source) + "\t" + Sanitize(kind) + "\t" + Sanitize(text);
        }

        public static bool TryParse(string line, out JournalEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(line)) return false;
            string[] p = line.TrimEnd('\r', '\n').Split(new char[] { '\t' }, 4);
            if (p.Length < 3) return false;
            DateTime t;
            if (!DateTime.TryParseExact(p[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return false;
            JournalEntry e = new JournalEntry();
            e.Time = t;
            e.Source = p[1];
            e.Kind = p[2];
            e.Text = p.Length > 3 ? p[3] : "";
            entry = e;
            return true;
        }

        // Skips lines that do not parse (a torn last line after a crash, a foreign line).
        public static List<JournalEntry> ParseAll(IEnumerable<string> lines)
        {
            List<JournalEntry> list = new List<JournalEntry>();
            if (lines == null) return list;
            foreach (string line in lines)
            {
                JournalEntry e;
                if (TryParse(line, out e)) list.Add(e);
            }
            return list;
        }
    }

    // Two files of bounded size: diario.log and diario.log.1 (the previous one). When the
    // current file would grow past the cap it becomes the previous one.
    public static class JournalRotation
    {
        public const long DefaultMaxBytes = 256 * 1024;

        public static bool ShouldRotate(long currentLength, int nextLineBytes, long maxBytes)
        {
            if (maxBytes <= 0) return false;
            if (currentLength <= 0) return false; // an empty file never rotates (a huge line still goes in)
            return currentLength + nextLineBytes > maxBytes;
        }

        public static string PreviousPath(string path)
        {
            return path + ".1";
        }
    }
}
