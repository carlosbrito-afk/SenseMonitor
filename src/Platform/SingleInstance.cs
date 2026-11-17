using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Forja.Platform
{
    public static class SingleInstance
    {
        public const string MutexName = @"Global\SenseMonitor.SingleInstance";
        public const string MessageName = "SenseMonitor.Show";

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private static Mutex mutex;
        private static int showMessageId;

        private const uint MSGFLT_ALLOW = 1;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Same id in every process; compare against it in the first instance WndProc.
        public static int ShowMessageId
        {
            get
            {
                if (showMessageId == 0) showMessageId = RegisterWindowMessage(MessageName);
                return showMessageId;
            }
        }

        // True = we are the first instance and now own the mutex.
        public static bool TryAcquire()
        {
            if (mutex != null) return true;
            Mutex m;
            if (!TryCreate(MutexName, out m)) return false;
            mutex = m;
            return true;
        }

        // First owner of the named mutex gets true and the Mutex; otherwise false and null.
        // An elevated first instance makes the open fail with UnauthorizedAccessException
        // for a non-elevated process: that also means "not first".
        public static bool TryCreate(string name, out Mutex result)
        {
            result = null;
            try
            {
                bool created;
                Mutex m = new Mutex(true, name, out created);
                if (!created)
                {
                    m.Close();
                    return false;
                }
                result = m;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Call with the main window handle so a non-elevated second instance can reach an
        // elevated first one (UIPI drops other posted messages).
        public static bool AllowShowMessage(IntPtr hwnd)
        {
            try { return ChangeWindowMessageFilterEx(hwnd, (uint)ShowMessageId, MSGFLT_ALLOW, IntPtr.Zero); }
            catch { return false; }
        }

        public static void SignalFirstInstance()
        {
            PostMessage(HWND_BROADCAST, ShowMessageId, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Release()
        {
            Mutex m = mutex;
            mutex = null;
            if (m == null) return;
            try { m.ReleaseMutex(); }
            catch (ApplicationException) { }
            m.Close();
        }
    }
}
