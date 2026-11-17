using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Forja.Core;
using Forja.App.Controls;

namespace Forja.App
{
    // Private palette of the Modes/Fan/Curve pages (mockup D). The shell will unify it
    // with the shared Theme at integration time.
    // Fonts for the fan/modes pages; colors are the one palette (Controls.Palette).
    internal static class FanPageColors
    {
        public static readonly Color Bg = Palette.Bg;
        public static readonly Color Panel = Palette.Panel;
        public static readonly Color Line = Palette.Line;
        public static readonly Color Line2 = Palette.Line2;
        public static readonly Color Text = Palette.Text;
        public static readonly Color Sec = Palette.Sec;
        public static readonly Color Mute = Palette.Mute;
        public static readonly Color Accent = Palette.Accent;
        public static readonly Color AccentBg = Palette.AccentBg;
        public static readonly Color Warn = Palette.Warn;
        public static readonly Color Hot = Palette.Hot;

        public static Font Mono(float pt, FontStyle style)
        {
            return new Font(Palette.MonoFamily, pt, style, GraphicsUnit.Point);
        }

        public static Font Ui(float pt, FontStyle style)
        {
            return new Font(Palette.UiFamily, pt, style, GraphicsUnit.Point);
        }
    }

    // Custom-drawn editor of the fan curve: temperature (30-100 C) x percent (30-100).
    // Drag a point to move it, double-click empty space to add one, right-click a point
    // to remove it (never below 2 points). Geometry/rules live in CurveEditMath.
    public class CurveEditor : Control
    {
        private FanCurve curve = FanCurve.Default();
        private double? currentTemp;
        private int dragIndex = -1;
        private int hoverIndex = -1;
        private readonly Font axisFont;
        private readonly Font tipFont;

        // Fired on every edit (including during a drag) with a normalized curve.
        public event EventHandler CurveChanged;
        // Fired once when an edit is finished (mouse released, point added/removed).
        public event EventHandler CurveCommitted;

        public CurveEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            axisFont = FanPageColors.Mono(8.5f, FontStyle.Regular);
            tipFont = FanPageColors.Mono(9f, FontStyle.Regular);
            BackColor = FanPageColors.Panel;
            ForeColor = FanPageColors.Text;
            TabStop = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                axisFont.Dispose();
                tipFont.Dispose();
            }
            base.Dispose(disposing);
        }

        public bool IsDragging { get { return dragIndex >= 0; } }

        // Always returns/stores a normalized copy.
        public FanCurve Curve
        {
            get { return curve; }
            set
            {
                curve = (value == null ? FanCurve.Default() : value.Normalize());
                dragIndex = -1;
                Invalidate();
            }
        }

        // Live marker; null or invalid hides it.
        public double? CurrentTempC
        {
            get { return currentTemp; }
            set
            {
                double? v = value;
                if (v.HasValue && (double.IsNaN(v.Value) || double.IsInfinity(v.Value) || v.Value <= 0)) v = null;
                if (v.HasValue != currentTemp.HasValue || (v.HasValue && v.Value != currentTemp.Value))
                {
                    currentTemp = v;
                    Invalidate();
                }
            }
        }

        private int S(int v)
        {
            return (int)Math.Round(v * DeviceDpi / 96.0);
        }

        // 1 px lines stay 1 device px up to 199 % (like the instrument controls)
        private float Hair()
        {
            return Math.Max(1, (int)Math.Floor(DeviceDpi / 96.0));
        }

        private PlotRect Plot()
        {
            double l = S(40);
            double t = S(10);
            double r = S(18); // room for half of the last x label ("100°")
            double b = S(24);
            return new PlotRect(l, t, Math.Max(10, Width - l - r), Math.Max(10, Height - t - b));
        }

        private double HitRadius()
        {
            return S(11);
        }

        private void RaiseChanged()
        {
            EventHandler h = CurveChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void RaiseCommitted()
        {
            EventHandler h = CurveCommitted;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            Focus();
            int idx = CurveEditMath.HitTest(curve, e.X, e.Y, Plot(), HitRadius());
            if (e.Button == MouseButtons.Left)
            {
                if (idx >= 0)
                {
                    dragIndex = idx;
                    Capture = true;
                }
            }
            else if (e.Button == MouseButtons.Right && idx >= 0)
            {
                FanCurve next = CurveEditMath.Remove(curve, idx);
                if (next.Points.Count != curve.Points.Count)
                {
                    curve = next;
                    hoverIndex = -1;
                    Invalidate();
                    RaiseChanged();
                    RaiseCommitted();
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PlotRect r = Plot();
            if (dragIndex >= 0)
            {
                // Button released outside our sight (e.g. over another window): finish the drag.
                if ((e.Button & MouseButtons.Left) == 0)
                {
                    EndDrag();
                    return;
                }
                FanCurve next = CurveEditMath.Move(curve, dragIndex, CurveEditMath.XToTemp(e.X, r), CurveEditMath.YToPct(e.Y, r));
                curve = next;
                Invalidate();
                RaiseChanged();
                return;
            }
            int idx = Enabled ? CurveEditMath.HitTest(curve, e.X, e.Y, r, HitRadius()) : -1;
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EndDrag();
        }

        // Ends a drag (if any) and commits the edit; safe to call repeatedly.
        private void EndDrag()
        {
            if (dragIndex < 0) return;
            dragIndex = -1;
            if (Capture) Capture = false;
            Invalidate();
            RaiseCommitted();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragIndex >= 0 && !Capture) EndDrag();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIndex != -1 && dragIndex < 0)
            {
                hoverIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            PlotRect r = Plot();
            if (CurveEditMath.HitTest(curve, e.X, e.Y, r, HitRadius()) >= 0) return;
            if (e.X < r.Left || e.X > r.Left + r.Width || e.Y < r.Top || e.Y > r.Top + r.Height) return;
            curve = CurveEditMath.Add(curve, CurveEditMath.XToTemp(e.X, r), CurveEditMath.YToPct(e.Y, r));
            Invalidate();
            RaiseChanged();
            RaiseCommitted();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PlotRect r = Plot();
            float left = (float)r.Left;
            float top = (float)r.Top;
            float right = (float)(r.Left + r.Width);
            float bottom = (float)(r.Top + r.Height);

            // Grid: hairlines every 10 C and every 10 %.
            using (Pen grid = new Pen(FanPageColors.Line, Hair()))
            {
                for (int t = 30; t <= 100; t += 10)
                {
                    float x = (float)Math.Round(CurveEditMath.TempToX(t, r)) + 0.5f;
                    g.DrawLine(grid, x, top, x, bottom);
                }
                for (int p = 30; p <= 100; p += 10)
                {
                    float y = (float)Math.Round(CurveEditMath.PctToY(p, r)) + 0.5f;
                    g.DrawLine(grid, left, y, right, y);
                }
            }

            // Axis labels.
            TextFormatFlags tf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            for (int t = 30; t <= 100; t += 10)
            {
                string s = t + "°";
                Size sz = TextRenderer.MeasureText(g, s, axisFont, new Size(100, 40), tf);
                int x = (int)Math.Round(CurveEditMath.TempToX(t, r)) - sz.Width / 2;
                if (x + sz.Width > Width) x = Width - sz.Width;
                if (x < 0) x = 0;
                TextRenderer.DrawText(g, s, axisFont, new Point(x, (int)bottom + S(5)), FanPageColors.Mute, tf);
            }
            for (int p = 30; p <= 100; p += 10)
            {
                string s = p + "%";
                Size sz = TextRenderer.MeasureText(g, s, axisFont, new Size(100, 40), tf);
                int y = (int)Math.Round(CurveEditMath.PctToY(p, r)) - sz.Height / 2;
                TextRenderer.DrawText(g, s, axisFont, new Point((int)left - sz.Width - S(6), y), FanPageColors.Mute, tf);
            }

            // Marker of the current temperature.
            if (currentTemp.HasValue && currentTemp.Value >= CurveEditMath.MinTemp && currentTemp.Value <= CurveEditMath.MaxTemp)
            {
                float mx = (float)CurveEditMath.TempToX(currentTemp.Value, r);
                using (Pen mp = new Pen(FanPageColors.Hot, Hair()))
                {
                    mp.DashStyle = DashStyle.Dash;
                    g.DrawLine(mp, mx, top, mx, bottom);
                }
                int pct = curve.Evaluate(currentTemp.Value);
                string label = Math.Round(currentTemp.Value) + "°C → " + pct + "%";
                Size ls = TextRenderer.MeasureText(g, label, tipFont, new Size(400, 40), tf);
                int lx = (int)mx + S(6);
                if (lx + ls.Width > right) lx = (int)mx - S(6) - ls.Width;
                TextRenderer.DrawText(g, label, tipFont, new Point(lx, (int)top + S(2)), FanPageColors.Hot, tf);
            }

            // Curve: dotted extension to the edges, solid between points.
            int n = curve.Points.Count;
            if (n > 0)
            {
                PointF[] pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    pts[i] = new PointF((float)CurveEditMath.TempToX(curve.Points[i].TempC, r),
                        (float)CurveEditMath.PctToY(curve.Points[i].Pct, r));
                }
                using (Pen ext = new Pen(FanPageColors.Line2, 1.5f * DeviceDpi / 96f))
                {
                    ext.DashStyle = DashStyle.Dot;
                    g.DrawLine(ext, left, pts[0].Y, pts[0].X, pts[0].Y);
                    g.DrawLine(ext, pts[n - 1].X, pts[n - 1].Y, right, pts[n - 1].Y);
                }
                using (Pen cp = new Pen(FanPageColors.Accent, 2f * DeviceDpi / 96f))
                {
                    cp.LineJoin = LineJoin.Round;
                    if (n > 1) g.DrawLines(cp, pts);
                }
                float rad = S(5);
                for (int i = 0; i < n; i++)
                {
                    bool active = (i == dragIndex || i == hoverIndex);
                    float rr = active ? rad + S(1) : rad;
                    RectangleF rc = new RectangleF(pts[i].X - rr, pts[i].Y - rr, rr * 2, rr * 2);
                    using (SolidBrush fill = new SolidBrush(active ? FanPageColors.Accent : FanPageColors.Panel))
                    {
                        g.FillEllipse(fill, rc);
                    }
                    using (Pen outline = new Pen(FanPageColors.Accent, 2f * DeviceDpi / 96f))
                    {
                        g.DrawEllipse(outline, rc);
                    }
                }

                // Value tip while dragging or hovering.
                int tipIdx = dragIndex >= 0 ? dragIndex : hoverIndex;
                if (tipIdx >= 0 && tipIdx < n)
                {
                    string tip = curve.Points[tipIdx].TempC + "°C · " + curve.Points[tipIdx].Pct + "%";
                    Size ts = TextRenderer.MeasureText(g, tip, tipFont, new Size(400, 40), tf);
                    int tx = (int)pts[tipIdx].X - ts.Width / 2;
                    if (tx < (int)left) tx = (int)left;
                    if (tx + ts.Width > (int)right) tx = (int)right - ts.Width;
                    int ty = (int)pts[tipIdx].Y - ts.Height - S(10);
                    if (ty < 0) ty = (int)pts[tipIdx].Y + S(10);
                    TextRenderer.DrawText(g, tip, tipFont, new Point(tx, ty), FanPageColors.Text, tf);
                }
            }

            if (Focused && ShowFocusCues)
            {
                using (Pen f = new Pen(FanPageColors.Accent, 1f))
                {
                    g.SmoothingMode = SmoothingMode.None;
                    g.DrawRectangle(f, 0, 0, Width - 1, Height - 1);
                }
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            EndDrag();
            Invalidate();
        }
    }
}
