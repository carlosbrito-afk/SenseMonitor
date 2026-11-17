using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.App.Controls;

namespace Forja.App
{
    // DPI scaling, fonts and drawing helpers of the shell (colors come from Controls.Palette).
    // Everything here is used from the UI thread only.
    public static class Theme
    {
        // Shell names for the one palette (Controls.Palette, mockup D): every page draws from it.
        public static readonly Color Background = Palette.Bg;
        public static readonly Color Nav = Palette.Bg;
        public static readonly Color Panel = Palette.Panel;
        public static readonly Color Hover = Palette.Hover;
        public static readonly Color Selected = Palette.Selected;
        public static readonly Color Border = Palette.Line;
        public static readonly Color Track = Palette.Line2;
        public static readonly Color Text = Palette.Text;
        public static readonly Color Muted = Palette.Sec;
        public static readonly Color Dim = Palette.Mute;
        public static readonly Color Accent = Palette.Accent;
        public static readonly Color AccentHover = Palette.AccentHover;
        public static readonly Color AccentPressed = Palette.AccentPressed;
        public static readonly Color OnAccent = Palette.OnAccent;
        public static readonly Color Warn = Palette.Warn;

        private static int dpi = 96;
        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static int Dpi { get { return dpi; } }

        public static void SetDpi(int value)
        {
            if (value < 48) value = 96;
            if (value == dpi) return;
            dpi = value;
            foreach (Font f in fonts.Values) f.Dispose();
            fonts.Clear();
        }

        // Scales a 96-DPI layout value to the window's DPI.
        public static int S(int v)
        {
            return (int)Math.Round(v * dpi / 96.0);
        }

        // Fonts are created in pixels from the window DPI so they follow WM_DPICHANGED.
        public static Font Font(float points, bool semibold)
        {
            string key = points.ToString(System.Globalization.CultureInfo.InvariantCulture) + (semibold ? "s" : "r");
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", points * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static Color TempColor(double? c)
        {
            return c.HasValue && c.Value >= 85 ? Warn : Text;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, Rectangle r, int radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, p);
            }
            g.SmoothingMode = old;
        }

        public static void DrawRound(Graphics g, Color c, Rectangle r, int radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, Math.Max(1, S(1))))
            {
                g.DrawPath(pen, p);
            }
            g.SmoothingMode = old;
        }

        public static void Draw(Graphics g, string text, Font f, Color c, Rectangle r, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text ?? "", f, r, c,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | extra);
        }

        // Thin horizontal bar (track + fill). fraction outside 0..1 is clamped.
        public static void Bar(Graphics g, Rectangle r, double fraction, Color fill)
        {
            FillRound(g, Track, r, r.Height / 2);
            if (fraction <= 0) return;
            if (fraction > 1) fraction = 1;
            int w = Math.Max(r.Height, (int)Math.Round(r.Width * fraction));
            FillRound(g, fill, new Rectangle(r.X, r.Y, w, r.Height), r.Height / 2);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark title bar on Windows 10 20H1+ / 11, caption tinted to the background on 11.
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                int caption = Background.R | (Background.G << 8) | (Background.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            }
            catch (Exception)
            {
                // older Windows: keep the default title bar
            }
        }
    }
}
