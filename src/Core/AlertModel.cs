using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace Forja.Core
{
    public enum AlertKind
    {
        CpuHot,
        GpuHot,
        ThermalProtection,
        UnexpectedRestart,
        FanWatchdog,
        FanNoResponse,
        SensorLost,
        // A tray menu command (mode, fan) did not take effect.
        CommandFailed
    }

    public enum AlertSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    // The tray icon's second channel (balloons are hidden while a game runs full screen).
    public enum TrayAlertState
    {
        Normal,
        Warning,
        Critical
    }

    // One thing worth telling the user. All text pt-BR; Title <= 63 and Text <= 255 characters
    // (the balloon limits).
    public sealed class Alert
    {
        public AlertKind Kind;
        public AlertSeverity Severity;
        public string Title;
        public string Text;
        public DateTime Local;
        // Page to open when the balloon is clicked: "Monitor", "Ventoinha" or "Saude".
        public string Page;
        // True for the one balloon that stands in for alerts held back while a game or quiet hours.
        public bool Summary;
    }

    // What the user is doing right now, as far as notifications are concerned.
    public sealed class AlertContext
    {
        // A full-screen game / D3D app / presentation is in front: a balloon would be hidden, and
        // may even pull the game out of full screen. Only the tray icon changes.
        public bool Fullscreen;
        // Windows focus assist ("quiet time") or a locked / screen-saver session.
        public bool OsQuiet;

        public static AlertContext Clear()
        {
            return new AlertContext();
        }
    }

    // What the engine hands back for one call.
    public sealed class AlertOutput
    {
        public readonly List<Alert> Balloons = new List<Alert>();
    }

    // alertas.json: what the user chose. Missing members load with the defaults.
    [DataContract]
    public sealed class AlertSettings
    {
        public const int DefaultSustainSeconds = 30;
        public const int DefaultGpuLimitC = 87;
        public const int DefaultBalloonGapMinutes = 5;

        // Master switch: off = no balloon and no icon change for anything.
        [DataMember] public bool Enabled;
        [DataMember] public bool TempAlerts;        // CPU / GPU hot for SustainSeconds
        [DataMember] public bool ProtectionAlerts;  // thermal protection engaged
        [DataMember] public bool RestartAlerts;       // Windows restarted unexpectedly since the app last ran
        [DataMember] public bool FanAlerts;         // fan watchdog / sentinel / fan did not answer
        [DataMember] public bool SensorAlerts;      // temperature sensor lost
        // 0 = automatic: the limit that fits the CPU model (ThermalLimits.CpuDefault).
        [DataMember] public int CpuLimitC;
        [DataMember] public int GpuLimitC;
        [DataMember] public int SustainSeconds;
        // At most one balloon per kind in this many minutes.
        [DataMember] public int BalloonGapMinutes;
        [DataMember] public bool QuietHoursEnabled;
        [DataMember] public int QuietStartHour;
        [DataMember] public int QuietEndHour;

        public AlertSettings()
        {
            SetDefaults();
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SetDefaults();
        }

        private void SetDefaults()
        {
            Enabled = true;
            TempAlerts = true;
            ProtectionAlerts = true;
            RestartAlerts = true;
            FanAlerts = true;
            SensorAlerts = true;
            CpuLimitC = 0;
            GpuLimitC = DefaultGpuLimitC;
            SustainSeconds = DefaultSustainSeconds;
            BalloonGapMinutes = DefaultBalloonGapMinutes;
            QuietHoursEnabled = false;
            QuietStartHour = 23;
            QuietEndHour = 7;
        }

        // Puts hand-edited values back into a sane range. Returns this.
        public AlertSettings Normalize()
        {
            if (CpuLimitC != 0) CpuLimitC = Clamp(CpuLimitC, 70, 105);
            GpuLimitC = Clamp(GpuLimitC <= 0 ? DefaultGpuLimitC : GpuLimitC, 60, 100);
            SustainSeconds = Clamp(SustainSeconds <= 0 ? DefaultSustainSeconds : SustainSeconds, 10, 300);
            BalloonGapMinutes = Clamp(BalloonGapMinutes <= 0 ? DefaultBalloonGapMinutes : BalloonGapMinutes, 1, 120);
            QuietStartHour = Clamp(QuietStartHour, 0, 23);
            QuietEndHour = Clamp(QuietEndHour, 0, 23);
            return this;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public bool Allows(AlertKind kind)
        {
            if (!Enabled) return false;
            switch (kind)
            {
                case AlertKind.CpuHot:
                case AlertKind.GpuHot:
                    return TempAlerts;
                case AlertKind.ThermalProtection:
                    return ProtectionAlerts;
                case AlertKind.UnexpectedRestart:
                    return RestartAlerts;
                case AlertKind.FanWatchdog:
                case AlertKind.FanNoResponse:
                case AlertKind.CommandFailed:
                    return FanAlerts;
                case AlertKind.SensorLost:
                    return SensorAlerts;
            }
            return false;
        }

        public AlertSettings Copy()
        {
            AlertSettings c = new AlertSettings();
            c.Enabled = Enabled;
            c.TempAlerts = TempAlerts;
            c.ProtectionAlerts = ProtectionAlerts;
            c.RestartAlerts = RestartAlerts;
            c.FanAlerts = FanAlerts;
            c.SensorAlerts = SensorAlerts;
            c.CpuLimitC = CpuLimitC;
            c.GpuLimitC = GpuLimitC;
            c.SustainSeconds = SustainSeconds;
            c.BalloonGapMinutes = BalloonGapMinutes;
            c.QuietHoursEnabled = QuietHoursEnabled;
            c.QuietStartHour = QuietStartHour;
            c.QuietEndHour = QuietEndHour;
            return c;
        }
    }

    // alertas-estado.json: what the app already told the user, so a restart never repeats it.
    [DataContract]
    public sealed class AlertState
    {
        // Log time (ticks, local) of the newest crash already announced.
        [DataMember] public long LastCrashTicks;
        // Journal time (ticks, local) of the newest fan line already announced.
        [DataMember] public long LastJournalTicks;
        // Incidents (RestartIncidents.IdOf) the user already looked at in Saúde.
        [DataMember] public List<string> SeenIncidents;

        public AlertState()
        {
            SeenIncidents = new List<string>();
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeenIncidents = new List<string>();
        }
    }

    // Heat limits. The default fits the CPU's own thermal limit: an alert at 85 C would fire in
    // every game on a gaming laptop (the CPU may run to its 100 C limit); five degrees under the
    // limit means "about to throttle".
    public static class ThermalLimits
    {
        public const int CpuUnknownC = 95;

        public static int CpuDefault(string cpuName)
        {
            if (string.IsNullOrEmpty(cpuName)) return CpuUnknownC;
            string n = cpuName.ToLowerInvariant();
            if (n.Contains("intel")) return 95;   // Core / Core Ultra: thermal limit 100 C
            if (n.Contains("ryzen") || n.Contains("amd")) return 90; // limit 90 to 95 C depending on the line
            return CpuUnknownC;
        }

        public static int EffectiveCpu(AlertSettings s, string cpuName)
        {
            if (s != null && s.CpuLimitC > 0) return s.CpuLimitC;
            return CpuDefault(cpuName);
        }

        public static int EffectiveGpu(AlertSettings s)
        {
            if (s != null && s.GpuLimitC > 0) return s.GpuLimitC;
            return AlertSettings.DefaultGpuLimitC;
        }
    }

    public static class QuietHours
    {
        // start == end means no window. A window may wrap midnight (23 to 7).
        public static bool IsQuiet(DateTime local, bool enabled, int startHour, int endHour)
        {
            if (!enabled || startHour == endHour) return false;
            int h = local.Hour;
            if (startHour < endHour) return h >= startHour && h < endHour;
            return h >= startHour || h < endHour;
        }
    }

    // pt-BR texts of every alert. Pure.
    public static class AlertText
    {
        public const int MaxTitle = 63;
        public const int MaxText = 255;

        private static Alert Make(AlertKind kind, AlertSeverity sev, string title, string text, DateTime local, string page)
        {
            Alert a = new Alert();
            a.Kind = kind;
            a.Severity = sev;
            a.Title = Cut(title, MaxTitle);
            a.Text = Cut(text, MaxText);
            a.Local = local;
            a.Page = page;
            return a;
        }

        public static string Cut(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static string C(double v)
        {
            return Math.Round(v, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + " °C";
        }

        public static Alert Hot(AlertKind kind, double tempC, int limitC, int seconds, DateTime local)
        {
            string part = kind == AlertKind.CpuHot ? "CPU" : "GPU";
            return Make(kind, AlertSeverity.Warning,
                part + " quente: " + C(tempC),
                "A " + part + " ficou em " + C(tempC) + ", acima de " + C(limitC) + ", por mais de " + seconds
                + " s. Se estiver jogando, confira a ventilação do PC.",
                local, "Monitor");
        }

        public static Alert Protection(string fanStatus, DateTime local)
        {
            bool full = fanStatus != null && fanStatus.IndexOf("100", StringComparison.Ordinal) >= 0;
            return Make(AlertKind.ThermalProtection, AlertSeverity.Critical,
                "Proteção térmica ativada",
                "A temperatura passou do limite seguro; "
                + (full ? "as ventoinhas foram para 100%" : "as ventoinhas voltaram ao automático")
                + " até o PC esfriar.",
                local, "Ventoinha");
        }

        public static Alert Watchdog(DateTime local)
        {
            return Make(AlertKind.FanWatchdog, AlertSeverity.Warning,
                "Ventoinha devolvida ao automático",
                "O app ficou sem responder (ou fechou de forma anormal) e a proteção devolveu a ventoinha ao automático.",
                local, "Ventoinha");
        }

        public static Alert FanNoResponse(DateTime local)
        {
            return Make(AlertKind.FanNoResponse, AlertSeverity.Warning,
                "A ventoinha não respondeu",
                "A ventoinha não obedeceu ao comando e voltou ao automático. Confira em Ventoinha.",
                local, "Ventoinha");
        }

        public static Alert FanUnconfirmed(DateTime local)
        {
            return Make(AlertKind.FanWatchdog, AlertSeverity.Warning,
                "Ventoinha sem confirmação do automático",
                "O firmware não confirmou que a ventoinha está em automático. Acompanhe a temperatura.",
                local, "Ventoinha");
        }

        public static Alert SensorLost(int seconds, DateTime local)
        {
            return Make(AlertKind.SensorLost, AlertSeverity.Warning,
                "Sem leitura de temperatura",
                "O sensor de temperatura da CPU está sem leitura há mais de " + (seconds >= 120 ? (seconds / 60) + " min" : seconds + " s")
                + ". Sem ele o app não consegue proteger o PC contra superaquecimento.",
                local, "Monitor");
        }

        // A tray command that did not take effect ("trocar para o modo Desempenho").
        public static Alert CommandFailed(string what, string page, DateTime local)
        {
            return Make(AlertKind.CommandFailed, AlertSeverity.Warning,
                "O comando não funcionou",
                "Não foi possível " + what + ". O firmware não confirmou a mudança.",
                local, page);
        }

        // One gentle line, once: "O Windows reiniciou inesperadamente. Ver detalhes". The details
        // (kind, code, what happened before) are in Saúde, in the report.
        public static Alert UnexpectedRestart(IList<CrashEntry> fresh, DateTime nowLocal)
        {
            string text = fresh.Count > 1
                ? "O Windows reiniciou inesperadamente (" + fresh.Count + " vezes). Ver detalhes"
                : "O Windows reiniciou inesperadamente. Ver detalhes";
            return Make(AlertKind.UnexpectedRestart, AlertSeverity.Info, "Reinício inesperado", text, nowLocal, "Saude");
        }

        // Held-back alerts are shown later; the text says when each one happened.
        public static string WithTime(string text, DateTime local)
        {
            return Cut("(" + local.ToString("HH:mm", CultureInfo.InvariantCulture) + ") " + text, MaxText);
        }
    }
}
