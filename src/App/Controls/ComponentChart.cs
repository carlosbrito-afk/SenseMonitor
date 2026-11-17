using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forja.Core;

namespace Forja.App.Controls
{
    // Last-10-minutes chart of one component (CPU or GPU): usage % as a quiet filled area with a
    // 1 px top line, temperature as the solid line, both on one shared unit-free 0-100 scale
    // (hairlines at 25/50/75), the 90 °C limit line with "90"/"limite", a vector legend, direct
    // end labels ("64 °C", "18 %") and "−10 min" / "agora". Null samples break the line and the
    // area. Call DataChanged() after adding to History; nothing repaints otherwise.
    public class ComponentChart : InstrumentControl
    {
        private const float HeaderH = 16f;
        private const float HeaderGap = 2f;
        private const float PlotL = 24f;
        private const float EndGap = 6f;
        private const float PlotT = 10f;
        private const float PlotB = 18f;

        // usage area: Sec at low strength over the panel, pre-blended (no transparency, no gradient)
        public static readonly Color UsageFill = Palette.Blend(Palette.Sec, Palette.Panel, 0.16);
        public static readonly Color UsageLine = Palette.Blend(Palette.Sec, Palette.Panel, 0.75);

        private Hardware _part = Hardware.Cpu;
        private TempHistory _history;
        private int _drawnVersion = -1;
        private readonly List<PointF> _segment = new List<PointF>(64);

        public ComponentChart()
        {
            AccessibleRole = System.Windows.Forms.AccessibleRole.Chart;
            UpdateAccessibleText();
        }

        protected override Size DefaultSize
        {
            get { return new Size(292, 188); }
        }

        public Hardware Part
        {
            get { return _part; }
            set
            {
                if (value == _part) return;
                _part = value;
                UpdateAccessibleText();
                Invalidate();
            }
        }

        public TempHistory History
        {
            get { return _history; }
            set
            {
                _history = value;
                _drawnVersion = -1;
                DataChanged();
            }
        }

        // Repaints (and refreshes the accessible description) only if the history changed.
        public void DataChanged()
        {
            int v = _history == null ? -2 : _history.Version;
            if (v == _drawnVersion) return;
            _drawnVersion = v;
            UpdateAccessibleText();
            Invalidate();
        }

        private void UpdateAccessibleText()
        {
            AccessibleName = InstrumentMath.HardwareName(_part) + ": uso e temperatura nos últimos 10 minutos";
            AccessibleDescription = InstrumentMath.ComponentChartText(_history, _part);
        }

        protected override void PrepareResources()
        {
            Ink.Prepare(Ui(10.5f));
            Ink.Prepare(Ui(12f));
            Ink.Prepare(Ui(10f));
            Ink.Prepare(Mono(10f));
            Ink.Prepare(Mono(11f));
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            int H = ClientSize.Height;
            DrawHeader(g, W);

            Font axis = Mono(10f);
            Font limitFont = Ui(10f);
            Font endFont = Mono(11f);

            // right margin sized for the widest end label this chart can show
            int endW = Math.Max(Ink.Width(InstrumentMath.WithUnit("100", "°C"), endFont),
                Ink.Width(InstrumentMath.WithUnit("100", "%"), endFont));
            float top = Px(HeaderH + HeaderGap);
            float l = Px(PlotL);
            float r = W - Px(EndGap) - endW;
            float t = top + Px(PlotT);
            float b = H - Px(PlotB);
            float pw = r - l;
            float ph = b - t;
            if (pw < Px(40f) || ph < Px(30f)) return;
            int hair = Hairline;

            // usage area first, so the grid stays visible on top of it
            float useY = float.NaN, tempY = float.NaN;
            double? useNow = null, tempNow = null;
            if (_history != null && _history.Count > 0)
            {
                Smooth(g);
                DrawUsageArea(g, l, pw, t, ph);
                Plain(g);
            }

            List<RectangleF> taken = new List<RectangleF>();
            float y90 = (float)InstrumentMath.ChartY(InstrumentMath.LimitC, t, ph);
            YLabel(g, "90", axis, Palette.LimitText, l, y90, taken);
            foreach (int v in InstrumentMath.ChartGrid)
            {
                float y = (float)Math.Round(InstrumentMath.ChartY(v, t, ph));
                g.FillRectangle(Res.Brush(Palette.Line), l, y, pw, hair);
                YLabel(g, v.ToString(), axis, Palette.Mute, l, y, taken);
            }

            Smooth(g);
            g.DrawLine(Res.Pen(Palette.Limit, Px(1.5f)), l, y90, r, y90);
            Plain(g);

            if (_history != null && _history.Count > 0)
            {
                Smooth(g);
                useY = DrawLine(g, Metric.Usage, Res.Pen(UsageLine, hair, true), UsageLine, l, pw, t, ph);
                tempY = DrawLine(g, Metric.Temp, Res.Pen(Palette.Text, Px(2f), true), Palette.Text, l, pw, t, ph);
                Plain(g);
                useNow = _history.Latest(_part, Metric.Usage);
                tempNow = _history.Latest(_part, Metric.Temp);
            }

            // "limite" sits at the right end of the 90 line, drawn over the data with no
            // knockout: nothing is ever hidden behind the word.
            float limBase = y90 - Px(4f);
            if (limBase - Ink.Ascent(limitFont) < top)
            {
                limBase = y90 + Px(4f) + Ink.CapHeight(limitFont); // no room above: under the line
            }
            Ink.Draw(g, "limite", limitFont, Palette.LimitText, r - Px(2f), limBase, InkAlign.Right);

            // end labels at the last drawn points, pushed apart when close
            float ty = float.IsNaN(tempY) ? t + ph / 2f : tempY;
            float uy = float.IsNaN(useY) ? b : useY;
            double a, bb;
            float gap = Ink.CapHeight(endFont) + Px(5f);
            float half = Ink.CapHeight(endFont) / 2f;
            InstrumentMath.SpreadLabels(ty, uy, gap, t + half, b - half, out a, out bb);
            float lx = r + Px(EndGap);
            Ink.Draw(g, InstrumentMath.WithUnit(InstrumentMath.FormatTemp(tempNow), "°C"), endFont, Palette.Text, lx, (float)a + half, InkAlign.Left);
            Ink.Draw(g, UsageLabel(useNow), endFont, Palette.Sec, lx, (float)bb + half, InkAlign.Left);

            // time axis
            float xb = H - Px(4f);
            int leftW = Ink.Draw(g, "−10 min", axis, Palette.Mute, l, xb, InkAlign.Left);
            if (Ink.Width("agora", axis) + leftW + Px(8f) <= pw)
            {
                Ink.Draw(g, "agora", axis, Palette.Mute, r, xb, InkAlign.Right);
            }
        }

        private static string UsageLabel(double? pct)
        {
            string s = InstrumentMath.FormatUsage(pct);
            return s == InstrumentMath.Dash ? InstrumentMath.WithUnit(s, "%") : s;
        }

        private static void Plain(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        private void YLabel(Graphics g, string text, Font f, Color c, float l, float y, List<RectangleF> taken)
        {
            float cap = Ink.CapHeight(f);
            float baseline = (float)Math.Round(y + cap / 2f);
            int w = Ink.Width(text, f);
            RectangleF box = new RectangleF(l - Px(6f) - w, baseline - cap - Px(1f), w, cap + Px(2f));
            foreach (RectangleF o in taken)
            {
                if (o.IntersectsWith(box)) return;
            }
            taken.Add(box);
            Ink.Draw(g, text, f, c, l - Px(6f), baseline, InkAlign.Right);
        }

        // Filled usage area, one polygon per run of valid samples (gaps leave holes).
        private void DrawUsageArea(Graphics g, float l, float pw, float t, float ph)
        {
            float baseY = (float)InstrumentMath.ChartY(InstrumentMath.ChartMin, t, ph);
            Brush fill = Res.Brush(UsageFill);
            int n = _history.Count;
            int cap = _history.Capacity;
            _segment.Clear();
            for (int i = 0; i <= n; i++)
            {
                double? v = i < n ? _history.At(_part, Metric.Usage, i) : null;
                if (v.HasValue)
                {
                    _segment.Add(new PointF((float)InstrumentMath.ChartX(n - 1 - i, cap, l, pw),
                        (float)InstrumentMath.ChartY(v.Value, t, ph)));
                    continue;
                }
                if (_segment.Count >= 2)
                {
                    PointF[] poly = new PointF[_segment.Count + 2];
                    _segment.CopyTo(poly);
                    poly[_segment.Count] = new PointF(_segment[_segment.Count - 1].X, baseY);
                    poly[_segment.Count + 1] = new PointF(_segment[0].X, baseY);
                    g.FillPolygon(fill, poly);
                }
                _segment.Clear();
            }
        }

        // Polyline per run of valid samples; returns the y of the newest drawn point (NaN if none).
        private float DrawLine(Graphics g, Metric m, Pen pen, Color dotColor, float l, float pw, float t, float ph)
        {
            float lastY = float.NaN;
            int n = _history.Count;
            int cap = _history.Capacity;
            _segment.Clear();
            for (int i = 0; i < n; i++)
            {
                double? v = _history.At(_part, m, i);
                if (!v.HasValue)
                {
                    Flush(g, pen, dotColor);
                    continue;
                }
                float y = (float)InstrumentMath.ChartY(v.Value, t, ph);
                _segment.Add(new PointF((float)InstrumentMath.ChartX(n - 1 - i, cap, l, pw), y));
                lastY = y;
            }
            Flush(g, pen, dotColor);
            return lastY;
        }

        private void Flush(Graphics g, Pen pen, Color dotColor)
        {
            if (_segment.Count >= 2)
            {
                g.DrawLines(pen, _segment.ToArray());
            }
            else if (_segment.Count == 1)
            {
                // an isolated sample between gaps: a dot the size of the stroke
                float d = Math.Max(pen.Width, Px(2f));
                PointF p = _segment[0];
                g.FillEllipse(Res.Brush(dotColor), p.X - d / 2f, p.Y - d / 2f, d, d);
            }
            _segment.Clear();
        }

        private void DrawHeader(Graphics g, int W)
        {
            Font k = Ui(10.5f);
            Font lg = Ui(12f);
            float track = 10.5f * 0.09f * Factor;
            float mid = Px(HeaderH) / 2f;
            float kBase = (float)Math.Round(mid + Ink.CapHeight(k) / 2f);
            float lgBase = (float)Math.Round(mid + Ink.CapHeight(lg) / 2f);

            string name = InstrumentMath.HardwareName(_part);
            string longTitle = name + " · USO E TEMPERATURA · 10 MIN";
            string shortTitle = name + " · 10 MIN";

            int lineSw = PxI(16f);
            int areaSw = PxI(12f);
            int sGap = PxI(6f);
            int iGap = PxI(14f);
            string tempText = "temp " + "°C";
            string useText = "uso %";
            int tempW = Ink.Width(tempText, lg);
            int useW = Ink.Width(useText, lg);
            int legendW = lineSw + sGap + tempW + iGap + areaSw + sGap + useW;
            int gapTL = PxI(12f);

            // prefer the long title, then the short one; drop the legend before cutting the title
            bool legend;
            string title;
            if (Ink.TrackedWidth(longTitle, k, track) + gapTL + legendW <= W)
            {
                title = longTitle;
                legend = true;
            }
            else if (Ink.TrackedWidth(shortTitle, k, track) + gapTL + legendW <= W)
            {
                title = shortTitle;
                legend = true;
            }
            else
            {
                title = Ink.FitTracked(shortTitle, k, track, W);
                legend = false;
            }
            Ink.DrawTracked(g, title, k, Palette.Mute, 0, kBase, track, InkAlign.Left);
            if (!legend) return;

            float x = W - legendW;
            float ly = (float)Math.Round(mid);
            Smooth(g);
            g.DrawLine(Res.Pen(Palette.Text, Px(2f)), x + Px(1f), ly, x + lineSw - Px(1f), ly);
            Plain(g);
            Ink.Draw(g, tempText, lg, Palette.Sec, x + lineSw + sGap, lgBase, InkAlign.Left);

            float ax = x + lineSw + sGap + tempW + iGap;
            float sh = Px(8f);
            float sy = (float)Math.Round(mid - sh / 2f);
            g.FillRectangle(Res.Brush(UsageFill), ax, sy, areaSw, sh);
            g.FillRectangle(Res.Brush(UsageLine), ax, sy, areaSw, Hairline);
            Ink.Draw(g, useText, lg, Palette.Sec, ax + areaSw + sGap, lgBase, InkAlign.Left);
        }
    }
}
