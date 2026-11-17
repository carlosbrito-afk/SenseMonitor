using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.Platform
{
    // Optional global shortcuts through RegisterHotKey: the system tells us when exactly that
    // combination is pressed. Nothing is hooked and no other key is ever seen, stored or logged.
    // A combination another program already owns simply fails to register (reported, never forced).
    // Create and Dispose on the UI thread (the window that receives WM_HOTKEY lives on it).
    public sealed class GlobalHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        private sealed class Sink : NativeWindow
        {
            private readonly GlobalHotkeys owner;

            public Sink(GlobalHotkeys owner)
            {
                this.owner = owner;
                CreateParams cp = new CreateParams();
                cp.Parent = HWND_MESSAGE; // message-only: no visible window, no taskbar button
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY) owner.Raise(m.WParam.ToInt32());
                else base.WndProc(ref m);
            }
        }

        private readonly Sink sink;
        private readonly List<HotkeyBinding> registered = new List<HotkeyBinding>();
        private bool disposed;

        // The id of the shortcut that was pressed.
        public event Action<int> Pressed;

        public GlobalHotkeys()
        {
            sink = new Sink(this);
        }

        public bool IsRegistered(int id)
        {
            foreach (HotkeyBinding b in registered)
            {
                if (b.Id == id) return true;
            }
            return false;
        }

        public bool Register(HotkeyBinding b)
        {
            if (disposed || b == null || sink.Handle == IntPtr.Zero) return false;
            if (IsRegistered(b.Id)) return true;
            bool ok;
            try
            {
                ok = RegisterHotKey(sink.Handle, b.Id, (uint)b.Modifiers, (uint)b.Vk);
                LastError = ok ? 0 : Marshal.GetLastWin32Error();
            }
            catch (Exception) { ok = false; LastError = -1; }
            if (ok) registered.Add(b);
            return ok;
        }

        // The Win32 error of the last Register that failed (0 after a success).
        public int LastError { get; private set; }

        // Registers every binding; returns one sentence for each that failed ("Ctrl+Alt+M: já em uso por
        // outro programa", or the error code when it is something else).
        public List<string> RegisterAll(IEnumerable<HotkeyBinding> bindings)
        {
            List<string> failed = new List<string>();
            foreach (HotkeyBinding b in bindings)
            {
                if (!Register(b)) failed.Add(Hotkeys.FailureText(b.Text, LastError));
            }
            return failed;
        }

        public void UnregisterAll()
        {
            if (sink.Handle != IntPtr.Zero)
            {
                foreach (HotkeyBinding b in registered)
                {
                    try { UnregisterHotKey(sink.Handle, b.Id); }
                    catch (Exception) { }
                }
            }
            registered.Clear();
        }

        private void Raise(int id)
        {
            Action<int> h = Pressed;
            if (h == null) return;
            try { h(id); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            UnregisterAll();
            disposed = true;
            sink.DestroyHandle();
        }
    }
}
