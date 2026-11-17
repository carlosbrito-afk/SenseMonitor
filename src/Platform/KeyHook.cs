using System;
using System.Runtime.InteropServices;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // Listen-only low-level keyboard hook. It lives on its own background thread with its
    // own message loop so UI work can never starve it (Windows skips events and may remove
    // hooks that exceed LowLevelHooksTimeout). Never swallows keys; only recognized gestures of the
    // target key (NitroKey: tap, double/triple tap, long press) are posted to the UI thread. A small
    // timer polls the recognizer only while a press or tap sequence is undecided (a lone tap is final
    // only when no second tap follows), so an idle hook costs nothing. Construct on the UI thread
    // (the SynchronizationContext is captured there; throws InvalidOperationException if there is none).
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

        private const int PollMs = 40;

        private readonly NitroKey filter;
        private readonly Action onPress;
        private readonly Action<NitroGesture> onGesture;
        private readonly bool gestureMode; // false: one onPress per physical press (NitroKey.OnKey); true: gestures
        private readonly SynchronizationContext ui;
        private readonly Timer pollTimer;
        private readonly PollGate gate;
        private readonly bool real;   // false: test instance (no thread, no system hook)
        private volatile bool disposed;
        private readonly HookProc proc; // field keeps the delegate alive for the GC
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Thread thread;
        private uint threadId;
        private IntPtr handle; // owned by the hook thread
        private volatile bool installed;

        // onPress runs on the UI thread once per physical press (the 700 ms debounce of NitroKey.OnKey):
        // what the environment wizard's key test counts.
        public KeyHook(NitroKey filter, Action onPress) : this(filter, onPress, null)
        {
        }

        // Key gestures (tap, double tap, triple tap, long press): onGesture runs on the UI thread once
        // per recognized gesture. A small timer polls the recognizer only while a sequence is undecided.
        public static KeyHook ForGestures(NitroKey filter, Action<NitroGesture> onGesture)
        {
            if (onGesture == null) throw new ArgumentNullException("onGesture");
            return new KeyHook(filter, delegate { }, onGesture);
        }

        // A hook that is NOT installed: events come in through Process, gestures go out through the same
        // timer and dispatch path as the real one. For tests; needs no desktop session.
        internal static KeyHook ForTests(NitroKey filter, Action<NitroGesture> onGesture, SynchronizationContext ui)
        {
            if (onGesture == null) throw new ArgumentNullException("onGesture");
            if (ui == null) throw new ArgumentNullException("ui");
            return new KeyHook(filter, delegate { }, onGesture, ui, false);
        }

        private KeyHook(NitroKey filter, Action onPress, Action<NitroGesture> onGesture)
            : this(filter, onPress, onGesture, SynchronizationContext.Current, true)
        {
        }

        private KeyHook(NitroKey filter, Action onPress, Action<NitroGesture> onGesture, SynchronizationContext context, bool install)
        {
            if (filter == null) throw new ArgumentNullException("filter");
            if (onPress == null) throw new ArgumentNullException("onPress");
            this.onGesture = onGesture;
            gestureMode = onGesture != null;
            ui = context;
            if (ui == null) throw new InvalidOperationException("KeyHook must be constructed on a thread with a SynchronizationContext (the UI thread).");
            this.filter = filter;
            this.onPress = onPress;
            real = install;
            proc = Callback;
            pollTimer = new Timer(PollTick, null, Timeout.Infinite, Timeout.Infinite);
            gate = new PollGate(delegate { return filter.Busy; }, SetPollTimer);
            if (!install) return;

            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "Forja.KeyHook";
            thread.Start();
            ready.WaitOne(5000);
        }

        public bool IsInstalled { get { return installed; } }

        // The gesture poll timer is on (tests).
        internal bool PollingRunning { get { return gate.Running; } }

        // Forget a held target key; call on PowerEvents.Resumed and on session unlock,
        // because a keyup released during sleep/lock never reaches the hook.
        public void Reset()
        {
            filter.Reset();
        }

        // The gesture recognizer's options (long press, double/triple tap) live on the filter.
        public NitroKey Filter { get { return filter; } }

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
                    Process(wParam.ToInt32(), k.vkCode, k.scanCode, k.flags, k.time);
                }
                catch { }
            }
            return CallNextHookEx(handle, nCode, wParam, lParam);
        }

        // One keyboard event, on the hook thread (or a test's). Everything the callback does after it has
        // read the event: filter, dispatch to the UI thread, keep the poll timer running while undecided.
        internal void Process(int msg, int vk, int scan, int flags, int time)
        {
            if (gestureMode)
            {
                NitroGesture g = filter.OnKeyGesture(msg, vk, scan, flags, time);
                if (g != NitroGesture.None) Deliver(g);
                if (filter.Busy) gate.Ensure();
            }
            else if (filter.OnKey(msg, vk, scan, flags, time))
            {
                ui.Post(Invoke, NitroGesture.None);
            }
        }

        private void Deliver(NitroGesture g)
        {
            if (disposed) return;
            ui.Post(Invoke, g);
        }

        private void Invoke(object state)
        {
            if (disposed) return;
            if (gestureMode)
            {
                try { onGesture((NitroGesture)state); }
                catch { }
                return;
            }
            try { onPress(); }
            catch { }
        }

        private void SetPollTimer(bool on)
        {
            if (disposed) return;
            try { pollTimer.Change(on ? PollMs : Timeout.Infinite, on ? PollMs : Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        // Pool thread: asks the recognizer whether time alone decided something; the gate stops the timer
        // when nothing is left to decide (see PollGate for the ordering that keeps it from sticking off).
        private void PollTick(object state)
        {
            try
            {
                if (disposed) return;
                gate.Tick(delegate
                {
                    NitroGesture g = filter.Poll();
                    if (g != NitroGesture.None) Deliver(g);
                });
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (!real)
            {
                disposed = true;
                pollTimer.Dispose();
                return;
            }
            Thread t = thread;
            thread = null;
            if (t == null) return;
            disposed = true;
            pollTimer.Dispose();
            if (installed) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            // The hook thread clears `installed` itself right after UnhookWindowsHookEx; only claim
            // "removed" here when it really ended (a join that timed out leaves it reported as installed).
            if (t.Join(3000)) installed = false;
            ready.Close();
        }
    }
}
