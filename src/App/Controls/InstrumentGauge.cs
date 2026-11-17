using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forja.Core;

namespace Forja.App.Controls
{
    // 270° temperature dial from mockup D: track, hatched 90-100 zone, ticks every 5/10 °C,
    // value arc colored by state, peak tick, mono number in the middle.
    // Unavailable = dashed arc + dash; Sleeping = dim arc + moon + dash.
    // Geometry is proportional to the square side (132 px at 100 %); hairlines follow DPI.
    public class InstrumentGauge : InstrumentControl
    {
        public const int LogicalSize = 132;

        private static readonly List<GaugeTick> TickList = InstrumentMath.Ticks();

        private string _sensorName = "CPU";
        private double? _value;
        private double? _peak;
        private bool _sleeping;

        public InstrumentGauge()
        {
            AccessibleDescription = "Medidor de temperatura de 30 a 100 graus; faixa de risco a partir de 90";
            UpdateAccessibleText();
        }

        protected override Size DefaultSize
        {
            get { return new Size(LogicalSize, LogicalSize); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int s = PxI(LogicalSize);
            return new Size(s, s);
        }

        // "CPU" / "GPU": used in the accessible text.
        public string SensorName
        {
            get { return _sensorName; }
            set
            {
                _sensorName = value ?? "";
                UpdateAccessibleText();
            }
        }

        // °C; null, ≤0, NaN and ±Infinity are shown as "no reading".
        public double? Value
        {
            get { return _value; }
            set { SetReading(value, _peak, _sleeping); }
        }

        public double? Peak
        {
            get { return _peak; }
            set { SetReading(_value, value, _sleeping); }
        }

        public bool Sleeping
        {
            get { return _sleeping; }
            set { SetReading(_value, _peak, value); }
        }

        public TempState State
        {
            get { return InstrumentMath.StateFor(_value, _sleeping); }
        }

        // Updates everything at once; repaints only if something changed.
        public void SetReading(double? value, double? peak, bool sleeping)
        {
            value = InstrumentMath.Sanitize(value);
            peak = InstrumentMath.Sanitize(peak);
            if (Nullable.Equals(value, _value) && Nullable.Equals(peak, _peak) && sleeping == _sleeping) return;
            _value = value;
            _peak = peak;
            _sleeping = sleeping;
            UpdateAccessibleText();
            Invalidate();
        }

        private void UpdateAccessibleText()
        {
            AccessibleName = InstrumentMath.GaugeAccessibleText(_sensorName, _value, _peak, _sleeping);
        }

        private static PointF Polar(float cx, float cy, float r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
        }

        private static float Sweep(double fromC, double toC)
        {
            return (float)(InstrumentMath.AngleForTemp(toC) - InstrumentMath.AngleForTemp(fromC));
        }

        // ---- resources proportional to the dial side ------------------------------------
        // Fonts (whole-pixel sizes) and the arc pens depend on the side, not on the DPI, so they
        // live in their own small cache that is replaced only when the side changes.

        private const float BigRatio = 0.25f;
        private const float UnitRatio = 0.085f;
        private const float NoReadingDashRatio = 0.2f;
        private const float SleepDashRatio = 0.11f;
        private const int MinSide = 24;

        private GdiCache _sideRes;
        private int _side = -1;

        private int Side
        {
            get { return Math.Min(ClientSize.Width, ClientSize.Height); }
        }

        private GdiCache SideRes(int side)
        {
            if (_sideRes == null || side != _side)
            {
                if (_sideRes != null) _sideRes.Dispose();
                _sideRes = new GdiCache();
                _side = side;
            }
            return _sideRes;
        }

        private Font SideFont(int side, float ratio)
        {
            float px = Math.Max(1f, (float)Math.Round(side * ratio));
            return SideRes(side).Font(Palette.MonoFamily, px, FontStyle.Regular);
        }

        protected override void PrepareResources()
        {
            int side = Side;
            if (side < MinSide) return;
            Ink.Prepare(SideFont(side, BigRatio));
            Ink.Prepare(SideFont(side, UnitRatio));
            Ink.Prepare(SideFont(side, NoReadingDashRatio));
            Ink.Prepare(SideFont(side, SleepDashRatio));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) PrepareResources();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _sideRes != null)
            {
                _sideRes.Dispose();
                _sideRes = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int side = Side;
            if (side < MinSide) return;
            float S = side;
            GdiCache sr = SideRes(side);
            float cx = ClientSize.Width / 2f;
            float cy = ClientSize.Height / 2f;
            float r = S * 0.38f;
            float w = S * 0.06f;
            RectangleF ring = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            float start = (float)InstrumentMath.ArcStartDeg;
            float full = (float)InstrumentMath.ArcSweepDeg;
            TempState state = State;

            Smooth(g);

            // track
            g.DrawArc(sr.Pen(Palette.Line, w), ring, start, full);
            DrawHatch(g, cx, cy, r, w);

            // ticks
            float tickFrom = r + w * 0.9f;
            foreach (GaugeTick t in TickList)
            {
                float len = t.Major ? S * 0.05f : S * 0.028f;
                Pen p = t.Major ? Res.Pen(Palette.Mute, Px(1.4f)) : Res.Pen(Palette.TickMinor, Px(1f));
                g.DrawLine(p, Polar(cx, cy, tickFrom, t.AngleDeg), Polar(cx, cy, tickFrom + len, t.AngleDeg));
            }

            Font dashFont;
            if (state == TempState.Unavailable)
            {
                g.DrawArc(Res.DashPen(Palette.Mute, Px(1.4f), Px(3f), Px(4f), false), ring, start, full);
                dashFont = SideFont(side, NoReadingDashRatio);
                Ink.DrawGray(g, InstrumentMath.Dash, dashFont, Palette.Sec, cx, cy + S * 0.053f, InkAlign.Center);
                return;
            }

            if (_value.HasValue)
            {
                double v = Math.Max(31.0, Math.Min(InstrumentMath.ScaleMaxC, _value.Value));
                g.DrawArc(sr.Pen(Palette.ForState(state), w), ring, start, Sweep(InstrumentMath.ScaleMinC, v));
            }

            if (state == TempState.Sleeping)
            {
                float box = S * 0.22f;
                DrawMoon(g, cx - S * 0.11f, cy - S * 0.17f, box, Palette.Mute);
                dashFont = SideFont(side, SleepDashRatio);
                Ink.Draw(g, InstrumentMath.Dash, dashFont, Palette.Mute, cx, cy + S * 0.2f, InkAlign.Center);
                return;
            }

            if (_peak.HasValue)
            {
                double a = InstrumentMath.AngleForTemp(_peak.Value);
                g.DrawLine(Res.Pen(Palette.PeakTick, Px(2f)), Polar(cx, cy, r - w * 0.75f, a), Polar(cx, cy, r + w * 0.75f, a));
            }

            Font big = SideFont(side, BigRatio);
            Font unit = SideFont(side, UnitRatio);
            Ink.DrawGray(g, InstrumentMath.FormatTemp(_value), big, Palette.Text, cx, cy + S * 0.09f, InkAlign.Center);
            Ink.Draw(g, "°C", unit, Palette.Mute, cx, cy + S * 0.22f, InkAlign.Center);
        }

        // 45° stripes (period 5 px, stripe 2 px at 100 %) clipped to the 90-100 band of the ring.
        private void DrawHatch(Graphics g, float cx, float cy, float r, float w)
        {
            float a0 = (float)InstrumentMath.AngleForTemp(InstrumentMath.LimitC);
            float sweep = Sweep(InstrumentMath.LimitC, InstrumentMath.ScaleMaxC);
            float ro = r + w / 2f;
            float ri = r - w / 2f;
            using (GraphicsPath band = new GraphicsPath())
            {
                band.AddArc(cx - ro, cy - ro, 2 * ro, 2 * ro, a0, sweep);
                band.AddArc(cx - ri, cy - ri, 2 * ri, 2 * ri, a0 + sweep, -sweep);
                band.CloseFigure();
                RectangleF bb = band.GetBounds();
                Region saved = g.Clip;
                try
                {
                    g.SetClip(band, CombineMode.Intersect);
                    Pen stripe = Res.Pen(Palette.HatchStripe, Px(2f));
                    float step = Px(5f) * (float)Math.Sqrt(2.0);
                    // "/" stripes: lines x + y = k
                    float kMin = bb.Left + bb.Top - step;
                    float kMax = bb.Right + bb.Bottom + step;
                    for (float k = (float)Math.Floor(kMin / step) * step; k <= kMax; k += step)
                    {
                        g.DrawLine(stripe, k - bb.Bottom - 2, bb.Bottom + 2, k - bb.Top + 2, bb.Top - 2);
                    }
                }
                finally
                {
                    g.Clip = saved;
                    saved.Dispose();
                }
            }
        }

        // Crescent from the mockup's 24-unit icon: "M19 14A7 7 0 0 1 10 5a7 7 0 1 0 9 9z",
        // stroke 1.8, round joins. Arc centers solved from the SVG endpoints.
        internal static void DrawMoonPath(Graphics g, Pen pen, float x, float y, float box)
        {
            float k = box / 24f;
            using (GraphicsPath p = new GraphicsPath())
            {
                // inner bite: center (16.5616, 7.4384), from (19,14) clockwise to (10,5)
                p.AddArc(x + (16.5616f - 7f) * k, y + (7.4384f - 7f) * k, 14f * k, 14f * k, 69.62f, 130.76f);
                // outer body: center (12.4384, 11.5616), from (10,5) counter-clockwise to (19,14)
                p.AddArc(x + (12.4384f - 7f) * k, y + (11.5616f - 7f) * k, 14f * k, 14f * k, 249.62f, -229.24f);
                p.CloseFigure();
                g.DrawPath(pen, p);
            }
        }

        private void DrawMoon(Graphics g, float x, float y, float box, Color c)
        {
            Pen pen = SideRes(_side).Pen(c, 1.8f * box / 24f, true);
            DrawMoonPath(g, pen, x, y, box);
        }
    }
}
