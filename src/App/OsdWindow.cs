using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Draws the on-screen message into a bitmap with a transparent background (also used by the
    // screenshot harness). Mockup D colors; no glow, no gradients. All sizes follow the DPI given.
    internal static class OsdRenderer
    {
        public static Bitmap Render(string text, OsdKind kind, int dpi, Rectangle workArea, out Size box)
        {
            float k = dpi / 96f;
            string t = string.IsNullOrEmpty(text) ? " " : text;
            using (Font font = new Font(Palette.UiSemiboldFamily, 17f * k, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                int barW = (int)Math.Round(5 * k);
                int gap = (int)Math.Round(12 * k);
                Size textSize;
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    SizeF m = pg.MeasureString(t, font, int.MaxValue, fmt);
                    textSize = new Size((int)Math.Ceiling(m.Width), (int)Math.Ceiling(font.GetHeight(pg)));
                }
                box = OsdLayout.BoxSize(new Size(textSize.Width + barW + gap, textSize.Height), dpi, workArea);
                Bitmap bmp = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // grayscale: keeps per-pixel alpha right
                    Rectangle r = new Rectangle(0, 0, box.Width - 1, box.Height - 1);
                    int radius = (int)Math.Round(14 * k);
                    using (GraphicsPath p = Theme.Round(r, radius))
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(0xF2, Palette.Panel)))
                    using (Pen edge = new Pen(Palette.Line2, Math.Max(1f, k)))
                    {
                        g.FillPath(fill, p);
                        g.DrawPath(edge, p);
                    }
                    Color accent = kind == OsdKind.Blocked ? Palette.Warn : Palette.Accent;
                    int padX = (int)Math.Round(OsdLayout.PadX * k);
                    int barH = Math.Max(textSize.Height, (int)Math.Round(20 * k));
                    Rectangle bar = new Rectangle(padX - (int)Math.Round(4 * k), (box.Height - barH) / 2, barW, barH);
                    using (GraphicsPath bp = Theme.Round(bar, barW / 2))
                    using (SolidBrush ab = new SolidBrush(accent))
                    {
                        g.FillPath(ab, bp);
                    }
                    float tx = bar.Right + gap;
                    RectangleF tr = new RectangleF(tx, (box.Height - textSize.Height) / 2f, box.Width - tx - padX, textSize.Height + 2);
                    using (SolidBrush tb = new SolidBrush(Palette.Text))
                    {
                        g.DrawString(t, font, tb, tr, fmt);
                    }
                }
                return bmp;
            }
        }
    }

    // The on-screen message ("Modo: Desempenho"): a small topmost window that never takes the focus
    // and lets every click through (WS_EX_TOOLWINDOW | TRANSPARENT | NOACTIVATE | TOPMOST | LAYERED,
    // per-pixel alpha through UpdateLayeredWindow). It appears near the bottom center of the screen
    // the window in front is on, fades in, stays about 1,5 s and fades out. UI thread only. Not
    // shown over a presentation or a locked screen (OverlayPolicy); it injects nothing anywhere.
    internal sealed class OsdWindow : NativeWindow, IDisposable
    {
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int HTTRANSPARENT = -1;
        private const int MA_NOACTIVATE = 3;
        private const int SW_HIDE = 0;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int W; public int H; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        private readonly Timer timer = new Timer();
        private Bitmap bitmap;
        private IntPtr hBitmap;       // the GDI copy of `bitmap`, one per message (not one per frame)
        private int lastByte = -1;    // the opacity last pushed: an unchanged one is not pushed again
        private Point location;
        private Size size;
        private int startTick;
        private int offsetMs;
        private bool shown;
        private double lastAlpha;

        // Off: nothing is ever shown (Configurações).
        public bool Enabled = true;

        public OsdWindow()
        {
            timer.Interval = 33; // runs only while a message is on screen
            timer.Tick += delegate { Step(); };
        }

        public bool IsShowing { get { return shown; } }

        // Shows (or replaces) the message. Never throws.
        public void Show(string text, OsdKind kind)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) return;
            try
            {
                if (OverlayPolicy.HideOsd(ScreenInfo.NotifyState())) return;
                System.Collections.Generic.List<MonitorInfo> monitors = ScreenInfo.Monitors();
                MonitorInfo m = ScreenInfo.ForegroundMonitor(monitors);
                Rectangle work = m == null ? Screen.PrimaryScreen.WorkingArea : m.WorkArea;
                int dpi = ScreenInfo.DpiOf(m == null ? work : m.Bounds);
                Size box;
                Bitmap bmp = OsdRenderer.Render(text, kind, dpi, work, out box);
                if (bitmap != null) bitmap.Dispose();
                bitmap = bmp;
                FreeHBitmap();
                lastByte = -1;
                size = box;
                location = OsdLayout.Place(work, box, OsdAnchor.BottomCenter, dpi);
                offsetMs = OsdTimeline.StartOffsetFor(shown ? lastAlpha : 0.0);
                startTick = Environment.TickCount;
                EnsureHandle();
                SetWindowPos(Handle, HWND_TOPMOST, location.X, location.Y, size.Width, size.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSENDCHANGING);
                shown = true;
                Step();
                timer.Start();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        public void HideNow()
        {
            timer.Stop();
            shown = false;
            lastAlpha = 0;
            if (Handle != IntPtr.Zero) ShowWindow(Handle, SW_HIDE);
        }

        private void Step()
        {
            int elapsed = offsetMs + unchecked(Environment.TickCount - startTick);
            if (OsdTimeline.IsDone(elapsed))
            {
                HideNow();
                return;
            }
            lastAlpha = OsdTimeline.Alpha(elapsed);
            Paint(lastAlpha);
        }

        private void EnsureHandle()
        {
            if (Handle != IntPtr.Zero) return;
            CreateParams cp = new CreateParams();
            cp.Style = WS_POPUP;
            cp.ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            cp.X = location.X;
            cp.Y = location.Y;
            cp.Width = size.Width;
            cp.Height = size.Height;
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTTRANSPARENT); return; }
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }

        // Pushes the bitmap with the given opacity (per-pixel alpha times a constant).
        private void Paint(double alpha)
        {
            if (bitmap == null || Handle == IntPtr.Zero) return;
            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(alpha * 255)));
            if (a == lastByte) return; // the steady part of the message repaints nothing
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                if (hBitmap == IntPtr.Zero) hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                old = SelectObject(memDc, hBitmap);
                POINT dst = new POINT();
                dst.X = location.X;
                dst.Y = location.Y;
                SIZE sz = new SIZE();
                sz.W = size.Width;
                sz.H = size.Height;
                POINT src = new POINT();
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = AC_SRC_OVER;
                bf.SourceConstantAlpha = a;
                bf.AlphaFormat = AC_SRC_ALPHA;
                if (UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref bf, ULW_ALPHA)) lastByte = a;
            }
            finally
            {
                // acquired inside the try: whatever failed, what was acquired is released
                if (memDc != IntPtr.Zero)
                {
                    if (old != IntPtr.Zero) SelectObject(memDc, old);
                    DeleteDC(memDc);
                }
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void FreeHBitmap()
        {
            if (hBitmap == IntPtr.Zero) return;
            DeleteObject(hBitmap);
            hBitmap = IntPtr.Zero;
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
            FreeHBitmap();
            if (bitmap != null)
            {
                bitmap.Dispose();
                bitmap = null;
            }
        }
    }
}
