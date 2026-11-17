using System;
using System.Globalization;

namespace Forja.Core
{
    // pt-BR text for values shown in the window and the tray. Pure functions.
    public static class Display
    {
        public const string AppName = "SenseMonitor";
        public const string Unavailable = "indisponível";
        // NotifyIcon.Text throws above 63 characters.
        public const int TooltipMax = 63;

        private const double GiB = 1024.0 * 1024.0 * 1024.0;
        private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

        // Whole degrees. Null, NaN, infinity and <= 0 (firmware "no reading") are unavailable.
        public static string Temp(double? c)
        {
            if (!ValidTemp(c)) return Unavailable;
            return Round(c.Value).ToString(PtBr) + " °C";
        }

        public static bool ValidTemp(double? c)
        {
            if (!c.HasValue) return false;
            double v = c.Value;
            return !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;
        }

        public static string Percent(double? p)
        {
            if (!p.HasValue || double.IsNaN(p.Value) || double.IsInfinity(p.Value) || p.Value < 0) return Unavailable;
            double v = p.Value > 100 ? 100 : p.Value;
            return Round(v).ToString(PtBr) + "%";
        }

        public static string Rpm(int? rpm)
        {
            if (!rpm.HasValue || rpm.Value < 0) return Unavailable;
            return rpm.Value.ToString(PtBr) + " RPM";
        }

        // "12,4" (GiB, one decimal, pt-BR comma).
        public static string Gib(long bytes)
        {
            return (bytes / GiB).ToString("0.0", PtBr);
        }

        public static string Ram(long used, long total)
        {
            if (RamFraction(used, total) == null) return Unavailable;
            return Gib(used) + " / " + Gib(total) + " GB";
        }

        public static double? RamFraction(long used, long total)
        {
            if (total <= 0 || used < 0 || used > total) return null;
            return (double)used / total;
        }

        // Result of "Limpar RAM": the drop of Task Manager's "Em uso" and the standby cache
        // purged. Parts under 0,1 GB are left out; standbyCleared < 0 = unknown.
        public static string CleanReport(long inUseDrop, long standbyCleared)
        {
            string text = "";
            if (Shown(inUseDrop)) text = Gib(inUseDrop) + " GB a menos em uso";
            if (Shown(standbyCleared))
            {
                text += text.Length > 0 ? " · cache em espera limpo: " : "Cache em espera limpo: ";
                text += Gib(standbyCleared) + " GB";
            }
            return text.Length > 0 ? text : "Nada a liberar agora";
        }

        public static string CleanResultText(RamCleanResult r)
        {
            if (r == null || r.AllFailed) return "Não foi possível limpar a RAM";
            return CleanReport(r.InUseDrop, r.StandbyCleared);
        }

        // At least 0,1 GB after rounding.
        private static bool Shown(long bytes)
        {
            return bytes >= 0.05 * GiB;
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        public static string TrayTooltip(double? cpuTempC)
        {
            return Truncate(AppName + " — CPU " + Temp(cpuTempC), TooltipMax);
        }

        private static long Round(double v)
        {
            return (long)Math.Round(v, MidpointRounding.AwayFromZero);
        }
    }
}
