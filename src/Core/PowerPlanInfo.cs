using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // The Windows power plan and the "Modo de energia" slider (power mode overlay), for the
    // context line "is Windows holding the CPU back?". Plain data.
    public class PowerPlanInfo
    {
        public string SchemeGuid;
        public string SchemeName;
        public string OverlayGuid;
        public string OverlayName;
        public bool? OnBattery;

        // "Plano Equilibrado · Energia: Melhor desempenho · na tomada"; null when nothing is known.
        public string Line
        {
            get { return PowerPlanNames.Line(this); }
        }
    }

    public static class PowerPlanNames
    {
        public const string NoOverlay = "00000000-0000-0000-0000-000000000000";

        private static readonly Dictionary<string, string> Schemes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "381b4222-f694-41f0-9685-ff5bb260df2e", "Equilibrado" },
            { "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "Alto desempenho" },
            { "a1841308-3541-4fab-bc81-f71556f20b4a", "Economia de energia" },
            { "e9a42b02-d5df-448d-aa00-03f14749eb61", "Desempenho máximo" }
        };

        // Windows 10/11 power mode slider (PowerGetEffectiveOverlayScheme).
        private static readonly Dictionary<string, string> Overlays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "961cc777-2547-4f9d-8174-7d86181b8a7a", "Melhor eficiência de energia" },
            { NoOverlay, "Equilibrado" },
            { "3af9b8d9-7c97-431d-ad78-34a8bfea439f", "Melhor desempenho" },
            { "ded574b5-45a0-4f42-8737-46345c09c238", "Desempenho máximo" }
        };

        public static string SchemeName(string guid)
        {
            string n;
            if (!string.IsNullOrEmpty(guid) && Schemes.TryGetValue(guid, out n)) return n;
            return null;
        }

        public static string OverlayName(string guid)
        {
            string n;
            if (!string.IsNullOrEmpty(guid) && Overlays.TryGetValue(guid, out n)) return n;
            return null;
        }

        // The overlay that holds the CPU back: the power efficiency position of the slider.
        public static bool IsEfficiencyOverlay(string guid)
        {
            return string.Equals(guid, "961cc777-2547-4f9d-8174-7d86181b8a7a", StringComparison.OrdinalIgnoreCase);
        }

        public static string Line(PowerPlanInfo p)
        {
            if (p == null) return null;
            List<string> parts = new List<string>();
            string scheme = p.SchemeName ?? SchemeName(p.SchemeGuid);
            if (scheme != null) parts.Add("Plano " + scheme);
            else if (!string.IsNullOrEmpty(p.SchemeGuid)) parts.Add("Plano personalizado");
            string overlay = p.OverlayName ?? OverlayName(p.OverlayGuid);
            if (overlay != null) parts.Add("Energia: " + overlay);
            else if (!string.IsNullOrEmpty(p.OverlayGuid)) parts.Add("Energia personalizada");
            if (p.OnBattery.HasValue) parts.Add(p.OnBattery.Value ? "na bateria" : "na tomada");
            return parts.Count == 0 ? null : string.Join(" · ", parts.ToArray());
        }

        // Fills the known names from the GUIDs.
        public static PowerPlanInfo Named(string schemeGuid, string overlayGuid, bool? onBattery)
        {
            PowerPlanInfo p = new PowerPlanInfo();
            p.SchemeGuid = schemeGuid == null ? null : schemeGuid.ToLowerInvariant();
            p.OverlayGuid = overlayGuid == null ? null : overlayGuid.ToLowerInvariant();
            p.SchemeName = SchemeName(p.SchemeGuid);
            p.OverlayName = OverlayName(p.OverlayGuid);
            p.OnBattery = onBattery;
            return p;
        }
    }
}
