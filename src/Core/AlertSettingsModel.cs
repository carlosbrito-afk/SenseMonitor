using System;
using System.Globalization;

namespace Forja.Core
{
    // What a settings screen needs for the alerts, with no UI in it: every value the user can change,
    // its allowed range, what "automático" means for this CPU, the sentence that describes the current
    // limits, and a way back to AlertSettings (always normalized). Any future UI binds to this; the
    // current Settings page only shows the toggles and Summary, and the numbers stay in alertas.json
    // until the screen is redesigned.
    public sealed class AlertSettingsModel
    {
        public const int CpuMinC = 70;
        public const int CpuMaxC = 105;
        public const int GpuMinC = 60;
        public const int GpuMaxC = 100;
        public const int SustainMinSeconds = 10;
        public const int SustainMaxSeconds = 300;
        public const int GapMinMinutes = 1;
        public const int GapMaxMinutes = 120;

        private readonly string cpuName;

        public bool Enabled;
        public bool TempAlerts;
        public bool ProtectionAlerts;
        public bool RestartAlerts;
        public bool FanAlerts;
        public bool SensorAlerts;
        // true: the limit follows the CPU model (CpuAutomaticLimitC); CpuManualLimitC is ignored.
        public bool CpuLimitAutomatic;
        public int CpuManualLimitC;
        public int GpuLimitC;
        public int SustainSeconds;
        public int BalloonGapMinutes;
        public bool QuietHoursEnabled;
        public int QuietStartHour;
        public int QuietEndHour;

        public AlertSettingsModel(AlertSettings s, string cpuName)
        {
            AlertSettings n = (s ?? new AlertSettings()).Copy().Normalize();
            this.cpuName = cpuName;
            Enabled = n.Enabled;
            TempAlerts = n.TempAlerts;
            ProtectionAlerts = n.ProtectionAlerts;
            RestartAlerts = n.RestartAlerts;
            FanAlerts = n.FanAlerts;
            SensorAlerts = n.SensorAlerts;
            CpuLimitAutomatic = n.CpuLimitC <= 0;
            CpuManualLimitC = n.CpuLimitC > 0 ? n.CpuLimitC : ThermalLimits.CpuDefault(cpuName);
            GpuLimitC = n.GpuLimitC;
            SustainSeconds = n.SustainSeconds;
            BalloonGapMinutes = n.BalloonGapMinutes;
            QuietHoursEnabled = n.QuietHoursEnabled;
            QuietStartHour = n.QuietStartHour;
            QuietEndHour = n.QuietEndHour;
        }

        // The limit "automático" gives for this CPU.
        public int CpuAutomaticLimitC { get { return ThermalLimits.CpuDefault(cpuName); } }

        // The limit in force.
        public int CpuLimitC
        {
            get { return CpuLimitAutomatic ? CpuAutomaticLimitC : Clamp(CpuManualLimitC, CpuMinC, CpuMaxC); }
        }

        public string CpuLimitText
        {
            get
            {
                return CpuLimitC.ToString(CultureInfo.InvariantCulture) + " °C"
                    + (CpuLimitAutomatic ? " (automático para este processador)" : " (definido por você)");
            }
        }

        // "CPU acima de 95 °C ou GPU acima de 87 °C por mais de 30 s."
        public string Summary()
        {
            return "CPU acima de " + CpuLimitC.ToString(CultureInfo.InvariantCulture) + " °C ou GPU acima de "
                + Clamp(GpuLimitC, GpuMinC, GpuMaxC).ToString(CultureInfo.InvariantCulture) + " °C por mais de "
                + Clamp(SustainSeconds, SustainMinSeconds, SustainMaxSeconds).ToString(CultureInfo.InvariantCulture) + " s.";
        }

        // "23:00 às 07:00" or "desligado".
        public string QuietHoursText
        {
            get
            {
                if (!QuietHoursEnabled || QuietStartHour == QuietEndHour) return "desligado";
                return QuietStartHour.ToString("00", CultureInfo.InvariantCulture) + ":00 às " + QuietEndHour.ToString("00", CultureInfo.InvariantCulture) + ":00";
            }
        }

        // Out-of-range values are brought into range (never rejected: a screen can set them freely).
        public AlertSettings ToSettings()
        {
            AlertSettings s = new AlertSettings();
            s.Enabled = Enabled;
            s.TempAlerts = TempAlerts;
            s.ProtectionAlerts = ProtectionAlerts;
            s.RestartAlerts = RestartAlerts;
            s.FanAlerts = FanAlerts;
            s.SensorAlerts = SensorAlerts;
            s.CpuLimitC = CpuLimitAutomatic ? 0 : Clamp(CpuManualLimitC, CpuMinC, CpuMaxC);
            s.GpuLimitC = GpuLimitC;
            s.SustainSeconds = SustainSeconds;
            s.BalloonGapMinutes = BalloonGapMinutes;
            s.QuietHoursEnabled = QuietHoursEnabled;
            s.QuietStartHour = QuietStartHour;
            s.QuietEndHour = QuietEndHour;
            return s.Normalize();
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
