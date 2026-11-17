using System;
using System.Drawing;

namespace Forja.Core
{
    public enum OsdKind
    {
        Info,
        // A locked action or a failure: the window uses the warning color.
        Blocked
    }

    public enum OsdAnchor
    {
        BottomCenter,
        TopRight
    }

    // The life of the on-screen message: a short fade in, a steady part, a fade out. Pure.
    public static class OsdTimeline
    {
        public const int FadeInMs = 120;
        // Fully visible until here (about 1,5 s after it appeared).
        public const int HoldUntilMs = 1500;
        public const int FadeOutMs = 300;
        public const int TotalMs = HoldUntilMs + FadeOutMs;

        // 0..1 opacity this many ms after the message appeared.
        public static double Alpha(int elapsedMs)
        {
            if (elapsedMs <= 0) return 0.0;
            if (elapsedMs < FadeInMs) return (double)elapsedMs / FadeInMs;
            if (elapsedMs < HoldUntilMs) return 1.0;
            if (elapsedMs >= TotalMs) return 0.0;
            return 1.0 - (double)(elapsedMs - HoldUntilMs) / FadeOutMs;
        }

        public static bool IsDone(int elapsedMs)
        {
            return elapsedMs >= TotalMs;
        }

        // A new message replaces one still on screen: its clock starts where the opacity already is
        // (no flash back to transparent, a full restart of the hold).
        public static int StartOffsetFor(double currentAlpha)
        {
            if (currentAlpha <= 0.0) return 0;
            if (currentAlpha >= 1.0) return FadeInMs;
            return (int)Math.Round(currentAlpha * FadeInMs);
        }
    }

    // Size and place of the message. Pure; the shell passes the monitor's work area and its DPI.
    public static class OsdLayout
    {
        public const int PadX = 20;
        public const int PadY = 12;
        public const int MinWidth = 160;
        public const int EdgeMargin = 48;

        private static int S(int v, int dpi)
        {
            return (int)Math.Round(v * dpi / 96.0);
        }

        // Box around the measured text: padding, a minimum width, never wider than 80 % of the area.
        public static Size BoxSize(Size text, int dpi, Rectangle workArea)
        {
            int w = Math.Max(S(MinWidth, dpi), text.Width + S(PadX, dpi) * 2);
            int h = text.Height + S(PadY, dpi) * 2;
            int max = workArea.Width * 8 / 10;
            if (max > 0 && w > max) w = max;
            return new Size(w, h);
        }

        // Top-left corner of a box of `size` in the work area, always fully inside it.
        public static Point Place(Rectangle workArea, Size size, OsdAnchor anchor, int dpi)
        {
            int margin = S(EdgeMargin, dpi);
            int x;
            int y;
            if (anchor == OsdAnchor.TopRight)
            {
                x = workArea.Right - margin - size.Width;
                y = workArea.Top + margin;
            }
            else
            {
                x = workArea.Left + (workArea.Width - size.Width) / 2;
                y = workArea.Bottom - margin - size.Height;
            }
            if (x + size.Width > workArea.Right) x = workArea.Right - size.Width;
            if (y + size.Height > workArea.Bottom) y = workArea.Bottom - size.Height;
            if (x < workArea.Left) x = workArea.Left;
            if (y < workArea.Top) y = workArea.Top;
            return new Point(x, y);
        }
    }
}
