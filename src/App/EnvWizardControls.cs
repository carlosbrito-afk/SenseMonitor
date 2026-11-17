using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Forja.App.Controls;

namespace Forja.App
{
    // A drawing surface: the wizard form paints its header and body through these.
    internal sealed class PaintSurface : Painted
    {
        public Action<Graphics, Rectangle> Painter;
        // kind 0 = down, 1 = move, 2 = up
        public Action<Point, int> Mouse;

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Mouse != null && e.Button == MouseButtons.Left) Mouse(e.Location, 0); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Mouse != null && e.Button == MouseButtons.Left) Mouse(e.Location, 1); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (Mouse != null && e.Button == MouseButtons.Left) Mouse(e.Location, 2); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Background);
            if (Painter != null) Painter(e.Graphics, ClientRectangle);
        }
    }

    // Secondary button: panel fill, thin border (the accent FlatButton is the primary one).
    internal sealed class GhostButton : Painted
    {
        private bool hover;
        private bool pressed;

        public GhostButton()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = !Enabled ? Theme.Panel : pressed ? Palette.Selected : hover ? Palette.Hover : Theme.Panel;
            Color border = !Enabled ? Palette.Line : hover || pressed ? Palette.Line2 : Palette.Line;
            Theme.FillRound(g, fill, r, Theme.S(6));
            Theme.DrawRound(g, border, r, Theme.S(6));
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, Rectangle.Inflate(r, -Theme.S(2), -Theme.S(2)), Theme.S(5));
            Theme.Draw(g, Text, Theme.Font(9.5f, false), Enabled ? Theme.Text : Theme.Dim, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    internal enum WizIcon { Ok, Off, Warn, Wait, Fail }

    // Drawing helpers of the wizard: wrapped paragraphs that report their height (so nothing overlaps),
    // vector status icons, cards and bars. Everything scales with Theme's DPI.
    internal static class WizDraw
    {
        private static readonly Dictionary<string, Font> mono = new Dictionary<string, Font>();
        private static int monoDpi;

        private const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak
            | TextFormatFlags.Left | TextFormatFlags.Top;

        public static Font Mono(float points)
        {
            if (monoDpi != Theme.Dpi)
            {
                foreach (Font f in mono.Values) f.Dispose();
                mono.Clear();
                monoDpi = Theme.Dpi;
            }
            string key = points.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Font m;
            if (!mono.TryGetValue(key, out m))
            {
                m = new Font(Palette.MonoFamily, points * Theme.Dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                mono[key] = m;
            }
            return m;
        }

        public static int TextHeight(Graphics g, string text, Font f, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(g, text, f, new Size(Math.Max(10, width), 100000), WrapFlags).Height;
        }

        // Draws a wrapped paragraph and returns its height.
        public static int Para(Graphics g, string text, Font f, Color c, int x, int y, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int h = TextHeight(g, text, f, width);
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, Math.Max(10, width), h + 2), c, WrapFlags);
            return h;
        }

        // One line, ellipsized; returns the line height.
        public static int Line(Graphics g, string text, Font f, Color c, int x, int y, int width)
        {
            int h = f.Height;
            TextRenderer.DrawText(g, text ?? "", f, new Rectangle(x, y, Math.Max(10, width), h + 2), c,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top);
            return h;
        }

        public static int LineWidth(Graphics g, string text, Font f)
        {
            return TextRenderer.MeasureText(g, text ?? "", f, new Size(100000, 100000),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        }

        public static void Card(Graphics g, Rectangle r)
        {
            Theme.FillRound(g, Theme.Panel, r, Theme.S(8));
            Theme.DrawRound(g, Palette.Line, r, Theme.S(8));
        }

        public static void Icon(Graphics g, WizIcon icon, Rectangle box)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = icon == WizIcon.Ok ? Palette.Ok : icon == WizIcon.Warn ? Palette.Warn : icon == WizIcon.Fail ? Palette.Hot
                : icon == WizIcon.Wait ? Theme.Accent : Palette.Mute;
            float k = box.Width / 24f;
            using (Pen pen = new Pen(c, Math.Max(1.4f, 1.8f * k)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawEllipse(pen, box.X + 1.5f * k, box.Y + 1.5f * k, 21 * k, 21 * k);
                float ox = box.X, oy = box.Y;
                switch (icon)
                {
                    case WizIcon.Ok:
                        g.DrawLines(pen, new PointF[] { new PointF(ox + 7 * k, oy + 12.5f * k), new PointF(ox + 10.5f * k, oy + 16 * k), new PointF(ox + 17 * k, oy + 8.5f * k) });
                        break;
                    case WizIcon.Off:
                        g.DrawLine(pen, ox + 7.5f * k, oy + 12 * k, ox + 16.5f * k, oy + 12 * k);
                        break;
                    case WizIcon.Warn:
                        g.DrawLine(pen, ox + 12 * k, oy + 7 * k, ox + 12 * k, oy + 13 * k);
                        g.DrawLine(pen, ox + 12 * k, oy + 16.3f * k, ox + 12 * k, oy + 16.6f * k);
                        break;
                    case WizIcon.Fail:
                        g.DrawLine(pen, ox + 8 * k, oy + 8 * k, ox + 16 * k, oy + 16 * k);
                        g.DrawLine(pen, ox + 16 * k, oy + 8 * k, ox + 8 * k, oy + 16 * k);
                        break;
                    case WizIcon.Wait:
                        g.DrawLine(pen, ox + 12 * k, oy + 7 * k, ox + 12 * k, oy + 12 * k);
                        g.DrawLine(pen, ox + 12 * k, oy + 12 * k, ox + 15.5f * k, oy + 14 * k);
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        // Determinate bar when fraction is 0..1; an indeterminate sweep when it is negative (phase 0..1).
        public static void Bar(Graphics g, Rectangle r, double fraction, double phase, Color fill)
        {
            Theme.FillRound(g, Theme.Track, r, r.Height / 2);
            if (fraction >= 0)
            {
                if (fraction > 1) fraction = 1;
                int w = Math.Max(r.Height, (int)Math.Round(r.Width * fraction));
                if (fraction > 0) Theme.FillRound(g, fill, new Rectangle(r.X, r.Y, w, r.Height), r.Height / 2);
                return;
            }
            int seg = Math.Max(r.Height * 2, r.Width / 4);
            int x = r.X + (int)Math.Round((r.Width - seg) * phase);
            Theme.FillRound(g, fill, new Rectangle(x, r.Y, seg, r.Height), r.Height / 2);
        }
    }
}
