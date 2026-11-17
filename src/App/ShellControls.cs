using System;
using System.Drawing;
using System.Windows.Forms;
using Forja.App.Controls;

namespace Forja.App
{
    // Base for owner-drawn controls: double-buffered, no background erase flicker.
    public class Painted : Control
    {
        public Painted()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
        }

        protected static bool Set(ref string field, string value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }
    }

    // Label that paints through TextRenderer with NoPadding, like every Theme.Draw heading, so its
    // text starts exactly at its left edge (a stock Label insets the text by a few px, which put the
    // page headings out of line with each other). Never wraps unless Wrap is set; an overlong
    // line is ellipsized. ThemePoints > 0 takes the font from Theme at paint time (follows DPI).
    internal class PlainLabel : Label
    {
        public float ThemePoints;
        public bool ThemeSemibold;
        public bool Wrap;

        public PlainLabel()
        {
            AutoSize = false;
            AutoEllipsis = true;
            UseMnemonic = false;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Font f = ThemePoints > 0 ? Theme.Font(ThemePoints, ThemeSemibold) : Font;
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            fl |= Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine;
            ContentAlignment a = TextAlign;
            if (a == ContentAlignment.MiddleLeft || a == ContentAlignment.MiddleCenter || a == ContentAlignment.MiddleRight)
                fl |= TextFormatFlags.VerticalCenter;
            else if (a == ContentAlignment.BottomLeft || a == ContentAlignment.BottomCenter || a == ContentAlignment.BottomRight)
                fl |= TextFormatFlags.Bottom;
            if (a == ContentAlignment.TopRight || a == ContentAlignment.MiddleRight || a == ContentAlignment.BottomRight)
                fl |= TextFormatFlags.Right;
            else if (a == ContentAlignment.TopCenter || a == ContentAlignment.MiddleCenter || a == ContentAlignment.BottomCenter)
                fl |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(e.Graphics, Text, f, ClientRectangle, Enabled ? ForeColor : Theme.Dim, fl);
        }
    }

    // Rounded accent button (dark text on blue for contrast), keyboard accessible.
    public class FlatButton : Painted
    {
        public NavIcon Icon; // optional vector icon before the label
        private bool hover;
        private bool pressed;

        public FlatButton()
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
            Color bg = !Enabled ? Theme.Track : pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent;
            Color fg = Enabled ? Theme.OnAccent : Theme.Dim;
            Rectangle r = ClientRectangle;
            Theme.FillRound(g, bg, r, Theme.S(6));
            if (Focused && ShowFocusCues)
            {
                Rectangle f = Rectangle.Inflate(r, -Theme.S(2), -Theme.S(2));
                Theme.DrawRound(g, Theme.OnAccent, f, Theme.S(5));
            }
            Font lf = Theme.Font(9.5f, true);
            if (Icon == NavIcon.None)
            {
                Theme.Draw(g, Text, lf, fg, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            // icon + label centered as one group; the label ellipsizes before the group leaves the button
            int isz = Theme.S(16);
            int gap = Theme.S(8);
            int avail = Math.Max(0, r.Width - Theme.S(20) - isz - gap);
            int tw = Math.Min(avail, TextRenderer.MeasureText(g, Text ?? "", lf, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
            int x0 = r.X + (r.Width - (isz + gap + tw)) / 2;
            NavIcons.Draw(g, Icon, new Rectangle(x0, r.Y + (r.Height - isz) / 2, isz, isz), fg);
            Theme.Draw(g, Text, lf, fg, new Rectangle(x0 + isz + gap, r.Y, tw, r.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Vector nav icons (the mockup's 24 x 24 outlines, 1.8 stroke), drawn with GDI+ so they stay
    // sharp at every DPI. No font glyphs.
    public enum NavIcon { None, Activity, Gauge, Fan, Heart, Wrench, Gear, Broom }

    internal static class NavIcons
    {
        public static void Draw(Graphics g, NavIcon icon, Rectangle box, Color color)
        {
            if (icon == NavIcon.None) return;
            float k = box.Width / 24f;
            System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using (Pen pen = new Pen(color, Math.Max(1.2f, 1.7f * k)))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                float ox = box.X, oy = box.Y;
                Func<float, float> X = delegate(float v) { return ox + v * k; };
                Func<float, float> Y = delegate(float v) { return oy + v * k; };
                switch (icon)
                {
                    case NavIcon.Activity:
                        g.DrawLines(pen, new PointF[] { new PointF(X(3), Y(12)), new PointF(X(7), Y(12)), new PointF(X(10), Y(4)),
                            new PointF(X(14), Y(20)), new PointF(X(17), Y(12)), new PointF(X(21), Y(12)) });
                        break;
                    case NavIcon.Gauge:
                        g.DrawArc(pen, X(4), Y(8), 16 * k, 16 * k, 180, 180);
                        g.DrawLine(pen, X(12), Y(16), X(16), Y(11));
                        break;
                    case NavIcon.Fan:
                        g.DrawEllipse(pen, X(10), Y(10), 4 * k, 4 * k);
                        FanBlade(g, pen, ox, oy, k, 12, 10, 12, 6, 13, 3, 16, 3, 19, 3, 18, 8, 14, 10);
                        FanBlade(g, pen, ox, oy, k, 14, 12, 18, 12, 21, 13, 21, 16, 21, 19, 16, 18, 14, 14);
                        FanBlade(g, pen, ox, oy, k, 12, 14, 12, 18, 11, 21, 8, 21, 5, 21, 6, 16, 10, 14);
                        FanBlade(g, pen, ox, oy, k, 10, 12, 6, 12, 3, 11, 3, 8, 3, 5, 8, 6, 10, 10);
                        break;
                    case NavIcon.Heart:
                        using (System.Drawing.Drawing2D.GraphicsPath hp = new System.Drawing.Drawing2D.GraphicsPath())
                        {
                            hp.AddBezier(X(12), Y(20), X(12), Y(20), X(4), Y(15), X(4), Y(10));
                            hp.AddBezier(X(4), Y(10), X(4), Y(7.5f), X(6), Y(6), X(8), Y(6));
                            hp.AddBezier(X(8), Y(6), X(10), Y(6), X(11.5f), Y(7), X(12), Y(8.5f));
                            hp.AddBezier(X(12), Y(8.5f), X(12.5f), Y(7), X(14), Y(6), X(16), Y(6));
                            hp.AddBezier(X(16), Y(6), X(18), Y(6), X(20), Y(7.5f), X(20), Y(10));
                            hp.AddBezier(X(20), Y(10), X(20), Y(15), X(12), Y(20), X(12), Y(20));
                            g.DrawPath(pen, hp);
                        }
                        break;
                    case NavIcon.Wrench:
                        g.DrawLine(pen, X(5), Y(19), X(12.5f), Y(11.5f));
                        g.DrawArc(pen, X(11.4f), Y(3.4f), 9 * k, 9 * k, -5, 280);
                        break;
                    case NavIcon.Broom:
                        g.DrawLine(pen, X(14), Y(3), X(10), Y(11));
                        g.DrawLines(pen, new PointF[] { new PointF(X(7), Y(11)), new PointF(X(15), Y(11)), new PointF(X(17), Y(20)),
                            new PointF(X(5), Y(20)), new PointF(X(7), Y(11)) });
                        g.DrawLine(pen, X(9), Y(15), X(9), Y(20)); g.DrawLine(pen, X(12), Y(15), X(12), Y(20));
                        break;
                    case NavIcon.Gear:
                        g.DrawEllipse(pen, X(9), Y(9), 6 * k, 6 * k);
                        g.DrawLine(pen, X(12), Y(2), X(12), Y(5)); g.DrawLine(pen, X(12), Y(19), X(12), Y(22));
                        g.DrawLine(pen, X(2), Y(12), X(5), Y(12)); g.DrawLine(pen, X(19), Y(12), X(22), Y(12));
                        g.DrawLine(pen, X(4.9f), Y(4.9f), X(7), Y(7)); g.DrawLine(pen, X(17), Y(17), X(19.1f), Y(19.1f));
                        g.DrawLine(pen, X(4.9f), Y(19.1f), X(7), Y(17)); g.DrawLine(pen, X(17), Y(7), X(19.1f), Y(4.9f));
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        private static void FanBlade(Graphics g, Pen pen, float ox, float oy, float k,
            float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3,
            float x4, float y4, float x5, float y5, float x6, float y6)
        {
            using (System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath())
            {
                p.AddBezier(ox + x0 * k, oy + y0 * k, ox + x1 * k, oy + y1 * k, ox + x2 * k, oy + y2 * k, ox + x3 * k, oy + y3 * k);
                p.AddBezier(ox + x3 * k, oy + y3 * k, ox + x4 * k, oy + y4 * k, ox + x5 * k, oy + y5 * k, ox + x6 * k, oy + y6 * k);
                g.DrawPath(pen, p);
            }
        }
    }

    // Left navigation entry.
    public class NavButton : Painted
    {
        public NavIcon Icon;

        private bool hover;
        private bool selected;

        public NavButton()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Nav;
            AccessibleRole = AccessibleRole.PageTab;
        }

        public string Key;

        public bool Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Nav);
            Rectangle r = ClientRectangle;
            if (selected || hover) Theme.FillRound(g, selected ? Palette.AccentBg : Theme.Hover, r, Theme.S(4));
            if (selected)
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                {
                    g.FillRectangle(b, r.X, r.Y, Theme.S(2), r.Height);
                }
            }
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Muted, r, Theme.S(4));
            int isz = Theme.S(18);
            NavIcons.Draw(g, Icon, new Rectangle(r.X + Theme.S(10), (r.Height - isz) / 2, isz, isz), selected ? Theme.Accent : Theme.Muted);
            Rectangle tr = new Rectangle(r.X + Theme.S(38), 0, Math.Max(0, r.Width - Theme.S(44)), Height);
            Theme.Draw(g, Text, Theme.Font(10f, false), selected ? Theme.Text : Theme.Muted, tr,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // On/off switch. A click raises UserToggled with the wanted state; the owner sets
    // Checked once the change really happened (the action may be slow or fail).
    public class ToggleSwitch : Painted
    {
        private bool on;

        public event Action<bool> UserToggled;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; AccessibleDescription = on ? "ligado" : "desligado"; Invalidate(); } }
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Action<bool> h = UserToggled;
            if (Enabled && h != null) h(!on);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor); // the owner sets it to the surface the switch sits on
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color track = !Enabled ? Theme.Border : on ? Theme.Accent : Theme.Track;
            Theme.FillRound(g, track, r, r.Height / 2);
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Text, r, r.Height / 2);
            int k = r.Height - Theme.S(6);
            int x = on ? r.Right - k - Theme.S(3) : r.X + Theme.S(3);
            Theme.FillRound(g, Enabled ? (on ? Theme.OnAccent : Theme.Text) : Theme.Dim,
                new Rectangle(x, r.Y + Theme.S(3), k, k), k / 2);
        }
    }
}
