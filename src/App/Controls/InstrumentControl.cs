using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Forja.App.Controls
{
    // Base for the custom-drawn instruments: double-buffered, not focusable, repaints only when
    // a setter changes something, scales with DeviceDpi and caches its GDI objects per DPI
    // (Res is rebuilt only when the DPI changes, never on resize).
    public abstract class InstrumentControl : Control
    {
        private GdiCache _res;
        private int _resDpi;

        protected InstrumentControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Palette.Panel;
            ForeColor = Palette.Text;
            AccessibleRole = AccessibleRole.StaticText;
        }

        // DPI used for drawing. Virtual so an offline renderer can force 125/150 % on a 100 % screen.
        protected virtual int EffectiveDpi
        {
            get
            {
                int d = DeviceDpi;
                return d >= 48 ? d : 96;
            }
        }

        protected float Factor
        {
            get { return EffectiveDpi / 96f; }
        }

        protected float Px(float logical)
        {
            return logical * EffectiveDpi / 96f;
        }

        protected int PxI(float logical)
        {
            return (int)Math.Round(logical * EffectiveDpi / 96f);
        }

        // 1 px lines stay 1 device px up to 199 % (like browsers), 2 px from 200 %.
        protected int Hairline
        {
            get { return Math.Max(1, (int)Math.Floor(Factor)); }
        }

        protected GdiCache Res
        {
            get
            {
                int dpi = EffectiveDpi;
                if (_res == null || dpi != _resDpi)
                {
                    DropResources();
                    _res = new GdiCache();
                    _resDpi = dpi;
                }
                return _res;
            }
        }

        protected Font Ui(float logicalPx)
        {
            return Res.Font(Palette.UiFamily, Px(logicalPx), FontStyle.Regular);
        }

        protected Font UiSemibold(float logicalPx)
        {
            return Res.Font(Palette.UiSemiboldFamily, Px(logicalPx), FontStyle.Regular);
        }

        protected Font Mono(float logicalPx)
        {
            return Res.Font(Palette.MonoFamily, Px(logicalPx), FontStyle.Regular);
        }

        protected void DropResources()
        {
            if (_res != null)
            {
                _res.Dispose();
                _res = null;
            }
        }

        // Creates the fonts a control paints with and measures their GDI metrics (Ink.Prepare),
        // so that work happens here instead of inside the first OnPaint. Called when the handle
        // is created and after a DPI change; overrides should only touch Res/fonts.
        protected virtual void PrepareResources()
        {
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PrepareResources();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            DropResources();
            PrepareResources();
            Invalidate();
        }

        protected static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DropResources();
            base.Dispose(disposing);
        }
    }

    // Owns the fonts/pens/brushes of one control; everything is disposed together.
    public sealed class GdiCache : IDisposable
    {
        private readonly Dictionary<string, IDisposable> _items = new Dictionary<string, IDisposable>();

        private static string F(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public Font Font(string family, float px, FontStyle style)
        {
            string key = "F|" + family + "|" + F(px) + "|" + (int)style;
            IDisposable o;
            if (!_items.TryGetValue(key, out o))
            {
                o = new Font(family, px, style, GraphicsUnit.Pixel);
                _items[key] = o;
            }
            return (Font)o;
        }

        public Pen Pen(Color c, float width)
        {
            return Pen(c, width, false);
        }

        public Pen Pen(Color c, float width, bool roundJoin)
        {
            string key = "P|" + c.ToArgb() + "|" + F(width) + "|" + roundJoin;
            IDisposable o;
            if (!_items.TryGetValue(key, out o))
            {
                Pen p = new Pen(c, width);
                if (roundJoin) p.LineJoin = LineJoin.Round;
                o = p;
                _items[key] = o;
            }
            return (Pen)o;
        }

        // dash and gap in device px (GDI+ wants them in multiples of the pen width)
        public Pen DashPen(Color c, float width, float dashPx, float gapPx, bool roundJoin)
        {
            string key = "D|" + c.ToArgb() + "|" + F(width) + "|" + F(dashPx) + "|" + F(gapPx) + "|" + roundJoin;
            IDisposable o;
            if (!_items.TryGetValue(key, out o))
            {
                Pen p = new Pen(c, width);
                p.DashPattern = new float[] { dashPx / width, gapPx / width };
                if (roundJoin) p.LineJoin = LineJoin.Round;
                o = p;
                _items[key] = o;
            }
            return (Pen)o;
        }

        public SolidBrush Brush(Color c)
        {
            string key = "B|" + c.ToArgb();
            IDisposable o;
            if (!_items.TryGetValue(key, out o))
            {
                o = new SolidBrush(c);
                _items[key] = o;
            }
            return (SolidBrush)o;
        }

        public void Dispose()
        {
            foreach (IDisposable d in _items.Values) d.Dispose();
            _items.Clear();
        }
    }

    public enum InkAlign
    {
        Left,
        Center,
        Right
    }

    // Text through TextRenderer (GDI), measured and drawn with the same engine, single line,
    // positioned by baseline. Strings that do not fit are shortened with an ellipsis, never wrapped.
    public static class Ink
    {
        public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping;

        public const string Ellipsis = "…";

        private static readonly Size Unbounded = new Size(int.MaxValue, int.MaxValue);

        private static Bitmap _measureBitmap;
        private static Graphics _measure;

        // The MeasureText overloads without a device context ignore NoPadding (documented
        // .NET Framework behavior) and add ~1/3 em, so measuring always goes through a DC.
        // Fonts are in pixel units, so a 96-dpi bitmap DC measures the same as the screen.
        private static Graphics MeasureDc
        {
            get
            {
                if (_measure == null)
                {
                    _measureBitmap = new Bitmap(1, 1);
                    _measure = Graphics.FromImage(_measureBitmap);
                }
                return _measure;
            }
        }

        public static int Width(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(MeasureDc, s, f, Unbounded, Flags).Width;
        }

        private struct GdiMetrics
        {
            public int Baseline; // top of the GDI text cell -> baseline
            public int Cap;      // ink height of "H"
        }

        // Bounded: a few fonts per control and DPI; 64 covers several DPIs without growing forever.
        public const int MetricsCapacity = 64;
        private static readonly Forja.Core.LruCache<string, GdiMetrics> MetricsCache =
            new Forja.Core.LruCache<string, GdiMetrics>(MetricsCapacity);

        // Measures a font's GDI metrics now (outside OnPaint) so the first paint does not pay for it.
        public static void Prepare(Font f)
        {
            Metrics(f);
        }

        // GDI's real baseline can differ from the GDI+ cell ascent by several px (e.g. Cascadia
        // Mono), so it is measured once per font: draw "H" at the cell origin into a small bitmap
        // and scan its rows (LockBits, one copy; no per-pixel GetPixel).
        private static GdiMetrics Metrics(Font f)
        {
            string key = f.Name + "|" + f.Size.ToString("R", CultureInfo.InvariantCulture) + "|" + (int)f.Style + "|" + (int)f.Unit;
            GdiMetrics m;
            if (MetricsCache.TryGet(key, out m)) return m;
            // "H" is narrower than 1 em; the cell is at most the line height
            int w = (int)Math.Ceiling(f.Size) + 8;
            int h = (int)Math.Ceiling(f.GetHeight(96f)) + 8;
            using (Bitmap b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Black);
                    TextRenderer.DrawText(g, "H", f, new Point(2, 0), Color.White, Flags);
                }
                int top = -1, bottom = -1;
                System.Drawing.Imaging.BitmapData data = b.LockBits(new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    int stride = Math.Abs(data.Stride);
                    byte[] buf = new byte[stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            if (buf[row + x * 3 + 1] > 127) // green byte of BGR
                            {
                                if (top < 0) top = y;
                                bottom = y;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    b.UnlockBits(data);
                }
                if (top < 0)
                {
                    FontFamily fam = f.FontFamily;
                    m.Baseline = (int)Math.Round(f.Size * fam.GetCellAscent(f.Style) / fam.GetEmHeight(f.Style));
                    m.Cap = (int)Math.Round(f.Size * 0.7f);
                }
                else
                {
                    m.Baseline = bottom + 1;
                    m.Cap = bottom + 1 - top;
                }
            }
            MetricsCache.Set(key, m);
            return m;
        }

        // Distance from the top of the GDI text cell to the baseline, in px (as rendered).
        public static float Ascent(Font f)
        {
            return Metrics(f).Baseline;
        }

        public static float Descent(Font f)
        {
            FontFamily fam = f.FontFamily;
            return f.Size * fam.GetCellDescent(f.Style) / fam.GetEmHeight(f.Style);
        }

        // Cap height as rendered, used to center text optically on a row.
        public static float CapHeight(Font f)
        {
            return Metrics(f).Cap;
        }

        public static int Draw(Graphics g, string s, Font f, Color c, float x, float baseline, InkAlign align)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int w = Width(s, f);
            float left = align == InkAlign.Left ? x : (align == InkAlign.Right ? x - w : x - w / 2f);
            int top = (int)Math.Round(baseline - Ascent(f));
            TextRenderer.DrawText(g, s, f, new Point((int)Math.Round(left), top), c, Flags);
            return w;
        }

        // Large numerals: grayscale antialiasing (TextRenderer takes the GDI font quality from the
        // Graphics' TextRenderingHint); ClearType fringes are most visible on big strokes.
        public static int DrawGray(Graphics g, string s, Font f, Color c, float x, float baseline, InkAlign align)
        {
            System.Drawing.Text.TextRenderingHint saved = g.TextRenderingHint;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            try
            {
                return Draw(g, s, f, c, x, baseline, align);
            }
            finally
            {
                g.TextRenderingHint = saved;
            }
        }

        // Shortest prefix + ellipsis that fits in maxWidth ("" if not even the ellipsis fits).
        public static string Fit(string s, Font f, int maxWidth)
        {
            if (string.IsNullOrEmpty(s) || Width(s, f) <= maxWidth) return s ?? "";
            for (int n = s.Length - 1; n > 0; n--)
            {
                string t = s.Substring(0, n).TrimEnd() + Ellipsis;
                if (Width(t, f) <= maxWidth) return t;
            }
            return Width(Ellipsis, f) <= maxWidth ? Ellipsis : "";
        }

        // ---- letter-spaced uppercase labels (CSS letter-spacing; GDI has none) -----------

        private static int CharWidth(char ch, Font f)
        {
            if (char.IsWhiteSpace(ch))
            {
                return Width("a" + ch + "a", f) - Width("aa", f);
            }
            return Width(ch.ToString(), f);
        }

        public static int TrackedWidth(string s, Font f, float trackPx)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            float w = 0;
            for (int i = 0; i < s.Length; i++)
            {
                w += CharWidth(s[i], f);
                if (i < s.Length - 1) w += trackPx;
            }
            return (int)Math.Ceiling(w);
        }

        public static string FitTracked(string s, Font f, float trackPx, int maxWidth)
        {
            if (string.IsNullOrEmpty(s) || TrackedWidth(s, f, trackPx) <= maxWidth) return s ?? "";
            for (int n = s.Length - 1; n > 0; n--)
            {
                string t = s.Substring(0, n).TrimEnd() + Ellipsis;
                if (TrackedWidth(t, f, trackPx) <= maxWidth) return t;
            }
            return "";
        }

        public static int DrawTracked(Graphics g, string s, Font f, Color c, float x, float baseline,
            float trackPx, InkAlign align)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int total = TrackedWidth(s, f, trackPx);
            float pen = align == InkAlign.Left ? x : (align == InkAlign.Right ? x - total : x - total / 2f);
            int top = (int)Math.Round(baseline - Ascent(f));
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (!char.IsWhiteSpace(ch))
                {
                    TextRenderer.DrawText(g, ch.ToString(), f, new Point((int)Math.Round(pen), top), c, Flags);
                }
                pen += CharWidth(ch, f) + trackPx;
            }
            return total;
        }
    }
}
