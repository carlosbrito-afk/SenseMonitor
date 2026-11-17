using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Forja.App.Controls
{
    // Flat panel from mockup D: panel fill, 1 px border, 6 px radius, painted over the parent's
    // background so the corners stay round. Meant for one level only (no nested panels).
    // Give children a Padding of at least 6 px so they never cover the rounded corners.
    public class PanelBox : Panel
    {
        public PanelBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Panel;
        }

        protected virtual int EffectiveDpi
        {
            get
            {
                int d = DeviceDpi;
                return d >= 48 ? d : 96;
            }
        }

        // Cached drawing objects: the outline path depends on size and DPI, the brush on BackColor,
        // the pen on the hairline width. Rebuilt only when those change.
        private GraphicsPath _path;
        private Size _pathSize;
        private int _pathDpi;
        private SolidBrush _fill;
        private Pen _border;

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            DropPath();
            Invalidate();
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            if (_fill != null)
            {
                _fill.Dispose();
                _fill = null;
            }
            base.OnBackColorChanged(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            DropPath();
            base.OnSizeChanged(e);
        }

        private void DropPath()
        {
            if (_path != null)
            {
                _path.Dispose();
                _path = null;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int dpi = EffectiveDpi;
            float f = dpi / 96f;
            int hair = Math.Max(1, (int)Math.Floor(f));
            float radius = 6f * f;
            g.Clear(Parent != null ? Parent.BackColor : Palette.Bg);
            RectangleF r = new RectangleF(hair / 2f, hair / 2f, Width - hair, Height - hair);
            if (r.Width < 2 * radius || r.Height < 2 * radius) return;

            if (_path == null || _pathSize != Size || _pathDpi != dpi)
            {
                DropPath();
                _path = RoundRect(r, radius);
                _pathSize = Size;
                _pathDpi = dpi;
            }
            if (_fill == null) _fill = new SolidBrush(BackColor);
            if (_border == null || (int)_border.Width != hair)
            {
                if (_border != null) _border.Dispose();
                _border = new Pen(Palette.Line, hair);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(_fill, _path);
            g.DrawPath(_border, _path);
            g.SmoothingMode = SmoothingMode.None;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DropPath();
                if (_fill != null) _fill.Dispose();
                if (_border != null) _border.Dispose();
                _fill = null;
                _border = null;
            }
            base.Dispose(disposing);
        }

        internal static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
