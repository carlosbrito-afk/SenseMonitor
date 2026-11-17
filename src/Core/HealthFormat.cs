using System;
using System.Globalization;

namespace Forja.Core
{
    // pt-BR text for the Saúde page. Pure, so it is covered by the test runner.
    public static class HealthFormat
    {
        public const string NotInformed = "não informado";

        private static readonly CultureInfo Br = new CultureInfo("pt-BR");

        // "1 reinício inesperado" / "3 reinícios inesperados"
        public static string UnexpectedRestarts(int n)
        {
            return n == 1 ? "1 reinício inesperado" : n.ToString(Br) + " reinícios inesperados";
        }

        // "2 falhas do sistema, 1 travamento, 4 quedas de energia" (kinds with no entry are left out;
        // an entry whose kind the log could not tell counts as "sem detalhes").
        public static string RestartBreakdown(int system, int hang, int power, int unknown)
        {
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            if (system > 0) parts.Add(system == 1 ? "1 falha do sistema" : system.ToString(Br) + " falhas do sistema");
            if (hang > 0) parts.Add(hang == 1 ? "1 travamento" : hang.ToString(Br) + " travamentos");
            if (power > 0) parts.Add(power == 1 ? "1 queda de energia" : power.ToString(Br) + " quedas de energia");
            if (unknown > 0) parts.Add(unknown == 1 ? "1 sem detalhes" : unknown.ToString(Br) + " sem detalhes");
            return string.Join(", ", parts.ToArray());
        }

        public static string Wh(int mWh)
        {
            return (mWh / 1000.0).ToString("0.0", Br) + " Wh";
        }

        public static string Percent(double pct)
        {
            return Math.Round(pct, MidpointRounding.AwayFromZero).ToString("0", Br) + "%";
        }

        // Marketing (decimal) units: a 512 GB drive reads as 512 GB.
        public static string DiskSize(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value <= 0) return NotInformed;
            double gb = bytes.Value / 1e9;
            if (gb >= 1000.0)
            {
                return (gb / 1000.0).ToString("0.##", Br) + " TB";
            }
            return Math.Round(gb, MidpointRounding.AwayFromZero).ToString("0", Br) + " GB";
        }

        public static string TempC(double? c, string whenMissing)
        {
            if (!c.HasValue) return whenMissing;
            return Math.Round(c.Value, MidpointRounding.AwayFromZero).ToString("0", Br) + " °C";
        }

        public static string Hours(long? h)
        {
            if (!h.HasValue) return NotInformed;
            return h.Value.ToString("N0", Br) + " h";
        }

        public static string Count(long? n)
        {
            if (!n.HasValue) return NotInformed;
            return n.Value.ToString("N0", Br);
        }

        public static string WearText(int? pct)
        {
            if (!pct.HasValue) return NotInformed;
            return pct.Value.ToString("0", Br) + "%";
        }

        public static string Minutes(int minutes)
        {
            if (minutes < 60) return minutes.ToString(Br) + " min";
            return (minutes / 60).ToString(Br) + " h " + (minutes % 60).ToString("00", Br) + " min";
        }

        // "seg 16/11"
        public static string DayLabel(DateTime day)
        {
            string wd = Br.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek).TrimEnd('.');
            return wd + " " + day.ToString("dd/MM", Br);
        }
    }
}
