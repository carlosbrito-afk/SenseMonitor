using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.Platform
{
    // Read-only questions about the screens and the window in front, for the on-screen message and
    // the monitoring widget. Nothing here opens or touches another process: the window in front is
    // only asked for its rectangle, and the full-screen state comes from the shell.
    public static class ScreenInfo
    {
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int NormalState = 5; // QUNS_ACCEPTS_NOTIFICATIONS

        // shell32's SHQueryUserNotificationState (see OverlayPolicy); "normal" when it cannot be read.
        public static int NotifyState()
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) != 0) return NormalState;
                return state;
            }
            catch (Exception)
            {
                return NormalState;
            }
        }

        public static List<MonitorInfo> Monitors()
        {
            List<MonitorInfo> list = new List<MonitorInfo>();
            try
            {
                foreach (Screen s in Screen.AllScreens)
                {
                    list.Add(new MonitorInfo(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary));
                }
            }
            catch (Exception)
            {
            }
            return list;
        }

        public static IntPtr ForegroundWindow()
        {
            try { return GetForegroundWindow(); }
            catch (Exception) { return IntPtr.Zero; }
        }

        // The monitor the window in front sits on (the primary one when there is none or it cannot be
        // read), so a message about a game appears on the game's screen.
        public static MonitorInfo ForegroundMonitor(IList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return null;
            try
            {
                IntPtr fg = GetForegroundWindow();
                RECT r;
                if (fg != IntPtr.Zero && GetWindowRect(fg, out r))
                {
                    Rectangle rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    MonitorInfo m = WidgetPlacement.MonitorOf(rect, monitors);
                    if (m != null) return m;
                }
            }
            catch (Exception)
            {
            }
            return WidgetPlacement.MonitorOf(Rectangle.Empty, monitors);
        }

        // Effective DPI of the monitor that holds this rectangle (96 when it cannot be read).
        public static int DpiOf(Rectangle bounds)
        {
            try
            {
                POINT p = new POINT();
                p.X = bounds.X + bounds.Width / 2;
                p.Y = bounds.Y + bounds.Height / 2;
                IntPtr mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
                uint x, y;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out x, out y) == 0 && x >= 48) return (int)x;
            }
            catch (Exception)
            {
            }
            return 96;
        }
    }
}
