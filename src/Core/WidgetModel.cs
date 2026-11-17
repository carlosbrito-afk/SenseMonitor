using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;

namespace Forja.Core
{
    // What the monitoring widget shows, as ready-made pt-BR text plus the temperature states for the
    // colors. Built from the same data the Monitor page uses (SensorReading from the firmware tick,
    // TelemetryView from the telemetry layer): the widget starts no sampling of its own.
    public sealed class WidgetData
    {
        public string CpuTemp = InstrumentMath.Dash;    // "62 °C"
        public string CpuUsage = InstrumentMath.Dash;   // "38 %"
        public string GpuTemp = InstrumentMath.Dash;
        public string GpuUsage = InstrumentMath.Dash;   // "12 %" or "repouso"
        public string Fan = InstrumentMath.Dash;        // "2.800 / 3.100 rpm"
        public string Mode = InstrumentMath.Dash;       // "Dia a dia"
        public string Ram = InstrumentMath.Dash;        // "12,4 / 15,8 GB"
        public TempState CpuTempState = TempState.Unavailable;
        public TempState GpuTempState = TempState.Unavailable;
        // Used / total, for the little bar; null when unknown.
        public double? RamFraction;
    }

    public static class WidgetModel
    {
        public static WidgetData Build(SensorReading r, TelemetryView v, string modeLabel, long ramUsed, long ramTotal)
        {
            WidgetData d = new WidgetData();
            SensorReading s = r ?? new SensorReading();
            d.CpuTemp = Temp(s.CpuTempC);
            d.CpuTempState = InstrumentMath.StateFor(s.CpuTempC, false);
            bool gpuResting = v != null && v.GpuState == GpuState.Resting;
            // The firmware reads 0 for a GPU that is off: that is "repouso", not a temperature.
            d.GpuTemp = Temp(s.GpuTempC);
            d.GpuTempState = InstrumentMath.StateFor(s.GpuTempC, gpuResting && v != null && v.GpuFirmwareOff);
            if (v != null)
            {
                d.CpuUsage = TelemetryViewBuilder.Percent(v.CpuLoadPct);
                d.GpuUsage = gpuResting && !v.GpuUsagePct.HasValue ? "repouso" : v.GpuUsageText;
            }
            d.Fan = Fan(s.CpuFanRpm, s.GpuFanRpm);
            d.Mode = string.IsNullOrEmpty(modeLabel) ? InstrumentMath.Dash : modeLabel;
            double? f = Display.RamFraction(ramUsed, ramTotal);
            if (f.HasValue)
            {
                d.RamFraction = f;
                d.Ram = Display.Gib(ramUsed) + " / " + Display.Gib(ramTotal) + " GB";
            }
            return d;
        }

        public static string Temp(double? c)
        {
            if (!InstrumentMath.IsValidTemp(c)) return InstrumentMath.Dash;
            return InstrumentMath.RoundTemp(c.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) + " °C";
        }

        // "2.800 / 3.100 rpm"; a fan that reports nothing shows a dash; none at all: a single dash.
        public static string Fan(int? cpu, int? gpu)
        {
            string a = Rpm(cpu);
            string b = Rpm(gpu);
            if (a == null && b == null) return InstrumentMath.Dash;
            return (a ?? InstrumentMath.Dash) + " / " + (b ?? InstrumentMath.Dash) + " rpm";
        }

        private static string Rpm(int? rpm)
        {
            if (!rpm.HasValue || rpm.Value < 0) return null;
            return InstrumentMath.FormatGrouped(rpm.Value);
        }
    }

    // What the overlays (message, widget) do next to a full-screen game. The state is shell32's
    // SHQueryUserNotificationState (see UserNotifyMap): 1 not present (locked / screen saver),
    // 2 a full-screen app, 3 a Direct3D full-screen app, 4 presentation mode, 5 normal, 6 quiet time,
    // 7 a Store app in full screen. Nothing here touches another process.
    public static class OverlayPolicy
    {
        // The widget never draws over exclusive (Direct3D) full screen or a presentation, and is
        // pointless on a locked screen. A borderless-window game reports state 2 and keeps the widget
        // (a window that is topmost over it, no injection).
        public static bool HideWidget(int state)
        {
            return state == 1 || state == 3 || state == 4;
        }

        // The short on-screen message is shown over everything except a presentation and a locked
        // screen; it never takes focus.
        public static bool HideOsd(int state)
        {
            return state == 1 || state == 4;
        }
    }

    // Where the widget sits on one monitor (relative to the monitor's top-left, physical pixels).
    [DataContract]
    public sealed class WidgetPosition
    {
        [DataMember] public string Monitor;
        [DataMember] public int X;
        [DataMember] public int Y;
    }

    // widget.json: what the user chose for the monitoring widget.
    [DataContract]
    public sealed class WidgetSettings
    {
        public const int MinOpacity = 30;
        public const int MaxOpacity = 100;
        private const int MaxPositions = 8;

        // Shown (also after a restart of the app).
        [DataMember] public bool Visible;
        [DataMember] public int OpacityPct;
        // "sempre visível": above other windows. Off = it behaves like a normal window.
        [DataMember] public bool AlwaysOnTop;
        // Clicks go through the widget to what is below it (it then cannot be dragged: turn it off in
        // Configurações, the tray menu or with the key gesture).
        [DataMember] public bool ClickThrough;
        [DataMember] public string LastMonitor;
        [DataMember] public List<WidgetPosition> Positions;

        public WidgetSettings()
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
            Visible = false;
            OpacityPct = 90;
            AlwaysOnTop = true;
            ClickThrough = false;
            LastMonitor = null;
            Positions = new List<WidgetPosition>();
        }

        public WidgetSettings Copy()
        {
            WidgetSettings c = new WidgetSettings();
            c.Visible = Visible;
            c.OpacityPct = OpacityPct;
            c.AlwaysOnTop = AlwaysOnTop;
            c.ClickThrough = ClickThrough;
            c.LastMonitor = LastMonitor;
            c.Positions = new List<WidgetPosition>();
            if (Positions != null)
            {
                foreach (WidgetPosition p in Positions)
                {
                    if (p == null) continue;
                    WidgetPosition q = new WidgetPosition();
                    q.Monitor = p.Monitor;
                    q.X = p.X;
                    q.Y = p.Y;
                    c.Positions.Add(q);
                }
            }
            return c;
        }

        // Hand-edited or damaged values go back into range. Returns this.
        public WidgetSettings Normalize()
        {
            if (OpacityPct < MinOpacity) OpacityPct = MinOpacity;
            if (OpacityPct > MaxOpacity) OpacityPct = MaxOpacity;
            if (Positions == null) Positions = new List<WidgetPosition>();
            for (int i = Positions.Count - 1; i >= 0; i--)
            {
                if (Positions[i] == null || string.IsNullOrEmpty(Positions[i].Monitor)) Positions.RemoveAt(i);
            }
            while (Positions.Count > MaxPositions) Positions.RemoveAt(0);
            return this;
        }

        public WidgetPosition PositionOn(string monitorId)
        {
            if (Positions == null || monitorId == null) return null;
            foreach (WidgetPosition p in Positions)
            {
                if (p != null && p.Monitor == monitorId) return p;
            }
            return null;
        }

        // Remembers where the widget is on this monitor (the most recent monitor goes last).
        public void RememberPosition(string monitorId, int x, int y)
        {
            if (string.IsNullOrEmpty(monitorId)) return;
            if (Positions == null) Positions = new List<WidgetPosition>();
            WidgetPosition p = PositionOn(monitorId);
            if (p == null)
            {
                p = new WidgetPosition();
                p.Monitor = monitorId;
            }
            else
            {
                Positions.Remove(p);
            }
            p.X = x;
            p.Y = y;
            Positions.Add(p);
            LastMonitor = monitorId;
            Normalize();
        }
    }

    public sealed class MonitorInfo
    {
        // Stable per physical output (the device name, e.g. \\.\DISPLAY2).
        public string Id;
        public Rectangle Bounds;
        public Rectangle WorkArea;
        public bool Primary;

        public MonitorInfo(string id, Rectangle bounds, Rectangle workArea, bool primary)
        {
            Id = id;
            Bounds = bounds;
            WorkArea = workArea;
            Primary = primary;
        }
    }

    public sealed class WidgetPlace
    {
        public string Monitor;
        public Point Location;
        // The size in physical pixels at the DPI of that monitor (what the clamp used).
        public Size Size;
    }

    public static class WidgetPlacement
    {
        public const int DefaultMargin = 16;

        // Where to open the widget: on the monitor it was last on (else the primary one, else the
        // first), at the position remembered for that monitor (else the top-right corner of its work
        // area), then moved so it lies fully inside the work area (a monitor that changed size or was
        // unplugged never leaves it off screen). null only when there are no monitors.
        public static WidgetPlace Resolve(WidgetSettings s, IList<MonitorInfo> monitors, Size size)
        {
            return Resolve(s, monitors, delegate(MonitorInfo m) { return size; });
        }

        // The widget's physical size depends on the DPI of the monitor it lands on (WidgetRenderer.SizeFor),
        // so the size is asked for the CHOSEN monitor before the position is clamped: at 150 % the widget
        // is 1,5 times as wide, and a position computed with the 96-DPI size would hang off the edge.
        public static WidgetPlace Resolve(WidgetSettings s, IList<MonitorInfo> monitors, Func<MonitorInfo, Size> sizeFor)
        {
            if (monitors == null || monitors.Count == 0 || sizeFor == null) return null;
            MonitorInfo m = Find(monitors, s == null ? null : s.LastMonitor);
            if (m == null) m = FindPrimary(monitors);
            Size size = sizeFor(m);
            Point p;
            WidgetPosition saved = s == null ? null : s.PositionOn(m.Id);
            if (saved != null) p = new Point(m.Bounds.X + saved.X, m.Bounds.Y + saved.Y);
            else p = new Point(m.WorkArea.Right - size.Width - DefaultMargin, m.WorkArea.Top + DefaultMargin);
            WidgetPlace w = new WidgetPlace();
            w.Monitor = m.Id;
            w.Location = Clamp(m.WorkArea, size, p);
            w.Size = size;
            return w;
        }

        // The monitor the widget is on: the one holding most of it (ties: the primary one).
        public static MonitorInfo MonitorOf(Rectangle widget, IList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return null;
            MonitorInfo best = null;
            long bestArea = -1;
            foreach (MonitorInfo m in monitors)
            {
                Rectangle i = Rectangle.Intersect(m.Bounds, widget);
                long area = i.IsEmpty ? 0 : (long)i.Width * i.Height;
                if (area > bestArea || (area == bestArea && m.Primary))
                {
                    best = m;
                    bestArea = area;
                }
            }
            return best;
        }

        public static Point Clamp(Rectangle area, Size size, Point p)
        {
            int x = p.X;
            int y = p.Y;
            if (x + size.Width > area.Right) x = area.Right - size.Width;
            if (y + size.Height > area.Bottom) y = area.Bottom - size.Height;
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            return new Point(x, y);
        }

        private static MonitorInfo Find(IList<MonitorInfo> monitors, string id)
        {
            if (id == null) return null;
            foreach (MonitorInfo m in monitors)
            {
                if (m.Id == id) return m;
            }
            return null;
        }

        private static MonitorInfo FindPrimary(IList<MonitorInfo> monitors)
        {
            foreach (MonitorInfo m in monitors)
            {
                if (m.Primary) return m;
            }
            return monitors[0];
        }
    }
}
