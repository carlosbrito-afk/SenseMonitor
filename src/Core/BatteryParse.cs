using System;
using System.Globalization;
using System.Xml;

namespace Forja.Core
{
    public class BatteryInfo
    {
        public int DesignMwh;
        public int FullChargeMwh;
        public int? CycleCount;

        // Share of the design capacity the battery can still hold, 0..100.
        public double HealthPct
        {
            get
            {
                if (DesignMwh <= 0) return 0.0;
                double p = FullChargeMwh * 100.0 / DesignMwh;
                if (p > 100.0) p = 100.0;
                if (p < 0.0) p = 0.0;
                return p;
            }
        }

        public double WearPct
        {
            get { return 100.0 - HealthPct; }
        }
    }

    public static class BatteryParse
    {
        // Parses the XML written by "powercfg /batteryreport /xml". Only the
        // Batteries/Battery elements are read: RuntimeEstimates reuses the name DesignCapacity.
        // Returns null when no battery with a usable design capacity is found.
        public static BatteryInfo Parse(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                foreach (XmlNode batteries in doc.GetElementsByTagName("Batteries"))
                {
                    foreach (XmlNode battery in batteries.ChildNodes)
                    {
                        if (battery.NodeType != XmlNodeType.Element || battery.LocalName != "Battery") continue;
                        int design = ReadInt(battery, "DesignCapacity", -1);
                        int full = ReadInt(battery, "FullChargeCapacity", -1);
                        if (design <= 0 || full < 0) continue;
                        BatteryInfo info = new BatteryInfo();
                        info.DesignMwh = design;
                        info.FullChargeMwh = full;
                        int cycles = ReadInt(battery, "CycleCount", -1);
                        info.CycleCount = cycles >= 0 ? (int?)cycles : null;
                        return info;
                    }
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int ReadInt(XmlNode parent, string localName, int fallback)
        {
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element && child.LocalName == localName)
                {
                    int v;
                    if (int.TryParse(child.InnerText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v)) return v;
                    return fallback;
                }
            }
            return fallback;
        }
    }
}
