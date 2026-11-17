using System;
using System.Runtime.InteropServices;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // Listen-only low-level keyboard hook. It lives on its own background thread with its
    // own message loop so UI work can never starve it (Windows skips events and may remove
    // hooks that exceed LowLevelHooksTimeout). Never swallows keys; only the target key
    // press is posted to the UI thread. Construct on the UI thread (the SynchronizationContext
    // is captured there; throws InvalidOperationException if there is none).
    // The owner must keep the KeyHook in a field and Dispose it: there is no finalizer.
    public class KeyHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const uint WM_QUIT = 0x0012;
        private const uint PM_NOREMOVE = 0;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly NitroKey filter;
        private readonly Action onPress;
        private readonly SynchronizationContext ui;
        private readonly HookProc proc; // field keeps the delegate alive for the GC
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Thread thread;
        private uint threadId;
        private IntPtr handle; // owned by the hook thread
        private volatile bool installed;

        public KeyHook(NitroKey filter, Action onPress)
        {
            if (filter == null) throw new ArgumentNullException("filter");
            if (onPress == null) throw new ArgumentNullException("onPress");
            ui = SynchronizationContext.Current;
            if (ui == null) throw new InvalidOperationException("KeyHook must be constructed on a thread with a SynchronizationContext (the UI thread).");
            this.filter = filter;
            this.onPress = onPress;
            proc = Callback;

            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "Forja.KeyHook";
            thread.Start();
            ready.WaitOne(5000);
        }

        public bool IsInstalled { get { return installed; } }

        // Forget a held target key; call on PowerEvents.Resumed and on session unlock,
        // because a keyup released during sleep/lock never reaches the hook.
        public void Reset()
        {
            filter.Reset();
        }

        private void Run()
        {
            MSG m;
            PeekMessage(out m, IntPtr.Zero, 0, 0, PM_NOREMOVE); // force creation of the thread message queue
            threadId = GetCurrentThreadId();
            // Low-level hooks are not injected, so any valid module handle works;
            // user32 is always loaded (the main module handle fails on some runtimes).
            handle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle("user32.dll"), 0);
            installed = handle != IntPtr.Zero;
            ready.Set();
            if (!installed) return;

            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
            UnhookWindowsHookEx(handle);
            handle = IntPtr.Zero;
            installed = false;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    if (filter.OnKey(wParam.ToInt32(), k.vkCode, k.scanCode, k.flags, k.time)) ui.Post(Invoke, null);
                }
                catch { }
            }
            return CallNextHookEx(handle, nCode, wParam, lParam);
        }

        private void Invoke(object state)
        {
            try { onPress(); }
            catch { }
        }

        public void Dispose()
        {
            Thread t = thread;
            thread = null;
            if (t == null) return;
            if (installed) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            // The hook thread clears `installed` itself right after UnhookWindowsHookEx; only claim
            // "removed" here when it really ended (a join that timed out leaves it reported as installed).
            if (t.Join(3000)) installed = false;
            ready.Close();
        }
    }
}
