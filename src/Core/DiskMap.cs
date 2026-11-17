using System.Collections.Generic;

namespace Forja.Core
{
    public enum DiskHealthLevel
    {
        Unknown,
        Ok,
        Warning,
        Bad
    }

    // One physical disk plus whatever reliability counters Windows gave us.
    // Every nullable field means "não informado" (never shown as OK or 0).
    public class DiskInfo
    {
        public string Name;
        public string MediaType;        // "SSD", "HDD" or null
        public string BusType;          // "NVMe", "SATA"... or null
        public long? SizeBytes;
        public int? HealthCode;
        public string OperationalText;
        public int? TempC;
        public int? TempMaxC;
        public int? WearPct;
        public long? ReadErrors;
        public long? WriteErrors;
        public long? PowerOnHours;

        public string HealthText
        {
            get { return DiskMap.HealthText(HealthCode); }
        }

        public DiskHealthLevel Level
        {
            get { return DiskMap.Level(HealthCode); }
        }

        public bool HasReliability
        {
            get
            {
                return TempC.HasValue || TempMaxC.HasValue || WearPct.HasValue || ReadErrors.HasValue
                    || WriteErrors.HasValue || PowerOnHours.HasValue;
            }
        }
    }

    public static class DiskMap
    {
        public const string Unknown = "Não informado";

        // MSFT_PhysicalDisk.HealthStatus: 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown.
        public static string HealthText(int? code)
        {
            switch (Level(code))
            {
                case DiskHealthLevel.Ok: return "Saudável";
                case DiskHealthLevel.Warning: return "Atenção";
                case DiskHealthLevel.Bad: return "Com problema";
                default: return Unknown;
            }
        }

        public static DiskHealthLevel Level(int? code)
        {
            if (!code.HasValue) return DiskHealthLevel.Unknown;
            switch (code.Value)
            {
                case 0: return DiskHealthLevel.Ok;
                case 1: return DiskHealthLevel.Warning;
                case 2: return DiskHealthLevel.Bad;
                default: return DiskHealthLevel.Unknown;
            }
        }

        // MSFT_PhysicalDisk.OperationalStatus is an array of codes.
        public static string OperationalText(int[] codes)
        {
            if (codes == null || codes.Length == 0) return Unknown;
            List<string> parts = new List<string>();
            foreach (int c in codes)
            {
                string t = OperationalOne(c);
                if (!parts.Contains(t)) parts.Add(t);
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string OperationalOne(int c)
        {
            switch (c)
            {
                case 2: return "OK";
                case 3: return "Degradado";
                case 4: return "Sob estresse";
                case 5: return "Falha prevista";
                case 6: return "Erro";
                case 7: return "Erro irrecuperável";
                case 8: return "Iniciando";
                case 9: return "Parando";
                case 10: return "Parado";
                case 11: return "Em serviço";
                case 12: return "Sem contato";
                case 13: return "Comunicação perdida";
                case 0xD010: return "Erro transitório";
                case 0xD011: return "Falha de leitura ou gravação";
                default: return "Desconhecido";
            }
        }

        // MSFT_PhysicalDisk.MediaType: 3 HDD, 4 SSD, 5 SCM.
        public static string MediaTypeText(int? code)
        {
            if (!code.HasValue) return null;
            switch (code.Value)
            {
                case 3: return "HDD";
                case 4: return "SSD";
                case 5: return "SCM";
                default: return null;
            }
        }

        // MSFT_PhysicalDisk.BusType.
        public static string BusTypeText(int? code)
        {
            if (!code.HasValue) return null;
            switch (code.Value)
            {
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 17: return "NVMe";
                default: return null;
            }
        }

        // Counters that are 0 or out of range are what drivers report for "no data".
        public static int? SanitizeTemp(int? c)
        {
            if (!c.HasValue || c.Value <= 0 || c.Value > 150) return null;
            return c;
        }

        public static int? SanitizeWear(int? pct)
        {
            if (!pct.HasValue || pct.Value < 0 || pct.Value > 100) return null;
            return pct;
        }

        public static long? SanitizeHours(long? h)
        {
            if (!h.HasValue || h.Value <= 0) return null;
            return h;
        }

        public static long? SanitizeCount(long? n)
        {
            if (!n.HasValue || n.Value < 0) return null;
            return n;
        }
    }
}
