using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // Text of the Settings "Estabilidade" card, built from the crash events (1001/41) the
    // caller read from the Windows log. Pure.
    public static class Stability
    {
        // One crash window for the verdict strip, Settings and the Saúde page.
        public const int CrashWindowDays = 30;

        // Real blue screens only, as the Saúde page counts them: entries WITH a bugcheck code.
        // Unexpected shutdowns without a code (power loss, hard reset) are left out. Entry
        // times are local; the result is UTC. Null in = null out (log unreadable).
        public static List<DateTime> BlueScreenTimesUtc(IEnumerable<CrashEntry> entries)
        {
            if (entries == null) return null;
            List<DateTime> list = new List<DateTime>();
            foreach (CrashEntry e in entries)
            {
                if (e == null || !e.Code.HasValue) continue;
                DateTime w = e.When;
                list.Add(w.Kind == DateTimeKind.Utc ? w : DateTime.SpecifyKind(w, DateTimeKind.Local).ToUniversalTime());
            }
            return list;
        }

        public static DateTime? LastCrash(IEnumerable<DateTime> crashesUtc)
        {
            if (crashesUtc == null) return null;
            DateTime? last = null;
            foreach (DateTime e in crashesUtc)
            {
                if (!last.HasValue || e > last.Value) last = e;
            }
            return last;
        }

        // logRead false or a null list = the log could not be read.
        public static string LastCrashLine(bool logRead, IEnumerable<DateTime> crashesUtc, DateTime nowUtc, TimeZoneInfo zone)
        {
            if (!logRead || crashesUtc == null) return "Não foi possível ler o registro de erros";
            DateTime? last = LastCrash(crashesUtc);
            if (!last.HasValue) return "Nenhuma tela azul registrada recentemente";
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(last.Value, DateTimeKind.Utc), zone);
            return "Última tela azul: " + local.ToString("dd/MM", CultureInfo.InvariantCulture) + " às "
                + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" + Ago(last.Value, nowUtc) + ")";
        }

        // Detail of the window's verdict strip: "Sem tela azul há 2 dias", "Nenhuma tela azul nos
        // últimos 30 dias" when the window read holds no crash, "Sem dados de tela azul" when unreadable.
        public static string VerdictDetail(bool logRead, IEnumerable<DateTime> crashesUtc, DateTime nowUtc, int windowDays)
        {
            if (!logRead || crashesUtc == null) return InstrumentMath.NoCrashSummary(null);
            DateTime? last = LastCrash(crashesUtc);
            if (!last.HasValue) return "Nenhuma tela azul nos últimos " + windowDays + " dias";
            double days = (nowUtc - last.Value).TotalDays;
            return InstrumentMath.NoCrashSummary(days <= 0 ? 0 : (int)Math.Floor(days));
        }

        // "há 2 dias" / "há 1 dia" / "há menos de 1 dia" (whole days, rounded down).
        public static string Ago(DateTime utc, DateTime nowUtc)
        {
            double days = (nowUtc - utc).TotalDays;
            int whole = days <= 0 ? 0 : (int)Math.Floor(days);
            if (whole == 0) return "há menos de 1 dia";
            return whole == 1 ? "há 1 dia" : "há " + whole + " dias";
        }
    }
}
