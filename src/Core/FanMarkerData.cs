using System;
using System.Globalization;
using System.Text;

namespace Forja.Core
{
    // What the fan marker records besides "a Custom write was started": WHICH fans were written (so the hand-back
    // uses the same mask, never a both-fan one on a one-fan notebook), on which machine (a marker that roamed to
    // another notebook is never replayed) and how the recovery is going (failed attempts, next try, alert
    // already shown), so a recovery that keeps failing backs off across starts instead of repeating at every one.
    public sealed class FanMarkerData
    {
        public string Layout;        // "cpu", "gpu", "both"; null = not recorded
        public string Fingerprint;   // MachineFingerprint of the machine that wrote it; null = not recorded
        public int Fails;
        public long NextUtcTicks;
        public bool Alerted;

        public static string LayoutText(FanLayout l)
        {
            if (l == null) return null;
            if (l == FanLayout.CpuOnly) return "cpu";
            if (l == FanLayout.GpuOnly) return "gpu";
            return "both";
        }

        // The mask that covers both: null + x = x, equal = same, different = both fans.
        public static string Union(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
            if (string.IsNullOrEmpty(b)) return a;
            return a == b ? a : "both";
        }

        // null = not recorded or unreadable.
        public FanLayout GetLayout()
        {
            if (Layout == "cpu") return FanLayout.CpuOnly;
            if (Layout == "gpu") return FanLayout.GpuOnly;
            if (Layout == "both") return FanLayout.Both;
            return null;
        }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("layout=").Append(Layout ?? "").Append("\r\n");
            sb.Append("fp=").Append(Clean(Fingerprint)).Append("\r\n");
            sb.Append("fails=").Append(Fails.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("next=").Append(NextUtcTicks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("alerted=").Append(Alerted ? "1" : "0").Append("\r\n");
            return sb.ToString();
        }

        // Tolerant: a marker written by an older version (a timestamp line only) parses as "nothing recorded".
        public static FanMarkerData Parse(string text)
        {
            FanMarkerData d = new FanMarkerData();
            if (string.IsNullOrEmpty(text)) return d;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq);
                string v = line.Substring(eq + 1).Trim();
                if (k == "layout") d.Layout = (v == "cpu" || v == "gpu" || v == "both") ? v : null;
                else if (k == "fp") d.Fingerprint = v.Length == 0 ? null : v;
                else if (k == "fails") { int n; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n < 1000) d.Fails = n; }
                else if (k == "next") { long n; if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0) d.NextUtcTicks = n; }
                else if (k == "alerted") d.Alerted = v == "1";
            }
            return d;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", "").Replace("\n", "");
        }

        // Same back-off as the fan controller: 2 s, 5 s, 30 s, then every 5 minutes.
        public static long BackoffTicks(int fails)
        {
            int[] ms = { 2000, 5000, 30000, 300000 };
            int i = Math.Max(1, fails) - 1;
            return ms[Math.Min(i, ms.Length - 1)] * TimeSpan.TicksPerMillisecond;
        }
    }

    // The fan marker as a store that also keeps FanMarkerData. A plain IFlag still works everywhere (older callers, tests).
    public interface IFanMarker : IFlag
    {
        // Empty data when the marker is absent or unreadable.
        FanMarkerData Read();
        // Raises the marker recording the fans written and the machine; true when it exists afterwards.
        bool Set(FanLayout layout, string fingerprint);
        // Rewrites the data of an existing marker (the recovery bookkeeping).
        bool Update(FanMarkerData data);
    }

    // Reading the marker can fail for a moment while another thread's Write holds it (a truncated file reads as nothing).
    // An empty answer is read once more before it is believed.
    public static class FanMarkerReads
    {
        // read: one attempt at the marker (never throws; null or an empty record = nothing readable). pause: a short
        // wait between the two attempts (null = none). At most two reads; the result is never null.
        public static FanMarkerData ReadWithRetry(Func<FanMarkerData> read, Action pause)
        {
            FanMarkerData d = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { d = read(); }
                catch (Exception) { d = null; }
                if (d != null && d.GetLayout() != null) return d;
                if (attempt == 0 && pause != null)
                {
                    try { pause(); }
                    catch (Exception) { }
                }
            }
            return d ?? new FanMarkerData();
        }
    }
}
