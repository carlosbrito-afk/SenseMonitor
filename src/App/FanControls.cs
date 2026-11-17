using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Forja.App
{
    // Owner-drawn flat button / segmented tab (selected = accent outline).
    internal class SegmentButton : Control
    {
        private readonly Font font;
        private bool selected;
        private bool locked;
        private bool hover;
        private bool pressed;

        public SegmentButton(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable
                | ControlStyles.StandardClick, true);
            Text = text;
            font = FanPageColors.Ui(10f, FontStyle.Regular);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) font.Dispose();
            base.Dispose(disposing);
        }

        public bool Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        // Locked buttons look dimmed and do not react (the owner also checks Locked).
        public bool Locked
        {
            get { return locked; }
            set
            {
                if (locked != value)
                {
                    locked = value;
                    Cursor = value ? Cursors.Default : Cursors.Hand;
                    Invalidate();
                }
            }
        }

        private int S(int v)
        {
            return (int)Math.Round(v * DeviceDpi / 96.0);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(FanPageColors.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rc = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = FanPageColors.Panel;
            Color border = FanPageColors.Line;
            Color text = locked ? FanPageColors.Mute : FanPageColors.Text;
            if (selected) { fill = FanPageColors.AccentBg; border = FanPageColors.Accent; }
            else if (!locked && (hover || pressed)) border = FanPageColors.Line2;
            int d = S(6) * 2;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(rc.Left, rc.Top, d, d, 180, 90);
                p.AddArc(rc.Right - d, rc.Top, d, d, 270, 90);
                p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
                p.AddArc(rc.Left, rc.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
            }
            Rectangle tr = new Rectangle(S(8), 0, Width - S(16), Height);
            TextRenderer.DrawText(g, Text, font, tr, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
            {
                g.SmoothingMode = SmoothingMode.None;
                using (Pen f = new Pen(FanPageColors.Accent, 2f))
                {
                    g.DrawRectangle(f, 1, 1, Width - 3, Height - 3);
                }
            }
        }
    }

    // Flat horizontal slider: mouse drag, arrows (+/-1), PageUp/PageDown (+/-10), Home/End.
    internal class FlatSlider : Control
    {
        private readonly int min;
        private readonly int max;
        private int value;
        private bool dragging;

        public event EventHandler ValueChanged;
        // Fired when the user finishes a change (mouse up or key press).
        public event EventHandler ValueCommitted;

        public FlatSlider(int min, int max)
        {
            this.min = min;
            this.max = max;
            value = min;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.Slider;
        }

        public int Value
        {
            get { return value; }
            set
            {
                int v = value < min ? min : (value > max ? max : value);
                if (v != this.value)
                {
                    this.value = v;
                    Invalidate();
                    EventHandler h = ValueChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }
        }

        private int S(int v)
        {
            return (int)Math.Round(v * DeviceDpi / 96.0);
        }

        private int TrackLeft() { return S(9); }
        private int TrackRight() { return Math.Max(TrackLeft() + 1, Width - S(9)); }

        private int XFromValue(int v)
        {
            return TrackLeft() + (int)Math.Round((double)(v - min) / (max - min) * (TrackRight() - TrackLeft()));
        }

        private int ValueFromX(int x)
        {
            double f = (double)(x - TrackLeft()) / (TrackRight() - TrackLeft());
            int v = min + (int)Math.Round(f * (max - min));
            return v < min ? min : (v > max ? max : v);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            Capture = true;
            Value = ValueFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) Value = ValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                Capture = false;
                Commit();
            }
        }

        private void Commit()
        {
            EventHandler h = ValueCommitted;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right || k == Keys.Home || k == Keys.End
                || k == Keys.PageUp || k == Keys.PageDown) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = 0;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) step = -1;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) step = 1;
            else if (e.KeyCode == Keys.PageDown) step = -10;
            else if (e.KeyCode == Keys.PageUp) step = 10;
            else if (e.KeyCode == Keys.Home) { e.Handled = true; Value = min; Commit(); return; }
            else if (e.KeyCode == Keys.End) { e.Handled = true; Value = max; Commit(); return; }
            if (step != 0)
            {
                e.Handled = true;
                Value = value + step;
                Commit();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(FanPageColors.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = Height / 2;
            int th = S(4);
            int l = TrackLeft();
            int r = TrackRight();
            int x = XFromValue(value);
            using (SolidBrush track = new SolidBrush(FanPageColors.Line2))
            {
                g.FillRectangle(track, l, cy - th / 2, r - l, th);
            }
            using (SolidBrush fill = new SolidBrush(FanPageColors.Accent))
            {
                g.FillRectangle(fill, l, cy - th / 2, Math.Max(0, x - l), th);
                int rad = S(7);
                g.FillEllipse(fill, x - rad, cy - rad, rad * 2, rad * 2);
            }
            if (Focused && ShowFocusCues)
            {
                int rad2 = S(10);
                using (Pen f = new Pen(FanPageColors.Accent, 1f))
                {
                    g.DrawEllipse(f, x - rad2, cy - rad2, rad2 * 2, rad2 * 2);
                }
            }
        }
    }
}
