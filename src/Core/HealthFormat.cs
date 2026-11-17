using System;
using System.Globalization;

namespace Forja.Core
{
    // pt-BR text for the Saúde page. Pure, so it is covered by the test runner.
    public static class HealthFormat
    {
        public const string NotInformed = "não informado";

        private static readonly CultureInfo Br = new CultureInfo("pt-BR");

        public static string BlueScreens(int n)
        {
            return n == 1 ? "1 tela azul" : n.ToString(Br) + " telas azuis";
        }

        public static string UnexpectedShutdowns(int n)
        {
            return n == 1 ? "1 desligamento inesperado sem código" : n.ToString(Br) + " desligamentos inesperados sem código";
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
