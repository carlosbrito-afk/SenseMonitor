using System;
using System.Globalization;

namespace Forja.Core
{
    // pt-BR words for a failure kind and for "how long ago". Pure. Headlines say "reinício
    // inesperado"; the technical detail (error code and its meaning) belongs inside a report.
    public static class FailureText
    {
        // Lower-case noun phrase for sentences.
        public static string KindShort(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.BlueScreen: return "falha do sistema";
                case FailureKind.HangPowerButton: return "travamento";
                case FailureKind.PowerLoss: return "queda de energia";
                default: return "reinício inesperado";
            }
        }

        // Row title in the Saúde list.
        public static string KindTitle(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.BlueScreen: return "Falha do sistema";
                case FailureKind.HangPowerButton: return "Travamento";
                case FailureKind.PowerLoss: return "Queda de energia ou desligamento duro";
                default: return "Reinício inesperado";
            }
        }

        // Second line of a row in the Saúde list: what the log shows about this kind of restart. Never
        // the error code: that lives only inside the report.
        public static string RowDetail(CrashEntry e)
        {
            return KindLong(e.Kind);
        }

        // A full sentence for lists and reports. Says what the log shows, not more.
        public static string KindLong(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.BlueScreen:
                    return "Falha do sistema: o Windows parou de propósito ao detectar um erro grave e reiniciou";
                case FailureKind.HangPowerButton:
                    return "Travamento: o PC parou sem código de erro e o botão de energia foi usado antes do reinício";
                case FailureKind.PowerLoss:
                    return "Queda de energia ou desligamento duro: sem código de erro e sem uso do botão de energia";
                default:
                    return "Reinício inesperado: o registro não traz detalhes sobre o tipo";
            }
        }

        public static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.InvariantCulture) + s.Substring(1);
        }

        // "há menos de 1 min", "há 35 min", "há 9 h", "há 2 d". A negative span (clock skew) reads as "agora".
        public static string AgoShort(TimeSpan span)
        {
            if (span < TimeSpan.Zero) return "agora";
            if (span.TotalMinutes < 1) return "há menos de 1 min";
            if (span.TotalHours < 1) return "há " + (int)Math.Floor(span.TotalMinutes) + " min";
            if (span.TotalDays < 1) return "há " + (int)Math.Floor(span.TotalHours) + " h";
            return "há " + (int)Math.Floor(span.TotalDays) + " d";
        }

        // "17/11 às 01:35"
        public static string DayAndTime(DateTime local)
        {
            return local.ToString("dd/MM", CultureInfo.InvariantCulture) + " às " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        // "falha do sistema (0xEF)" / "travamento" for one entry.
        public static string Brief(CrashEntry e)
        {
            string s = KindShort(e.Kind);
            if (e.Code.HasValue) s += " (" + CrashParse.FormatCode(e.Code.Value) + ")";
            return s;
        }
    }

    // What the strip knows. "Estável" needs every input known-good (a valid temperature reading and a
    // readable restart log); until then the LED is neutral and the word is "Lendo…" for the first
    // seconds, then no word at all. Never "Estável" by assumption.
    public enum StripState
    {
        Reading,
        Stable,
        Attention
    }

    // The words next to the strip's LED: "Estável" or "Requer sua atenção", plus the neutral
    // "Lendo…" (first seconds only) while something is still unknown. No sub-text.
    public static class StripText
    {
        public const string Stable = "Estável";
        public const string Attention = "Requer sua atenção";
        public const string Reading = "Lendo…";

        public static string Label(StripState state, bool readingGrace)
        {
            if (state == StripState.Attention) return Attention;
            if (state == StripState.Stable) return Stable;
            return readingGrace ? Reading : "";
        }
    }
}
