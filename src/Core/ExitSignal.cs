using System;
using System.Threading;

namespace Forja.Core
{
    // The robust way to ask the running app to leave: a named event. taskkill-without-/F (WM_CLOSE to the process's first
    // top-level window) is only a fallback: it twice failed to reach a tray app that had just been started hidden, and the
    // closers (installer, uninstaller, update helper, deploy.ps1) timed out after 30 s with the app still running.
    //
    // The app creates the event (elevated; Administrators and SYSTEM only may open it, so a user-level process cannot make
    // the app leave) and waits on it in its own thread, which posts the orderly exit (fans to Automático first) to the UI.
    // Every closer signals the event first and falls back to taskkill only if the app did not go.
    public static class ExitSignal
    {
        public const string EventName = @"Global\SenseMonitor.Sair";

        // Protected DACL, no inheritance: full access for BUILTIN\Administrators (BA) and LocalSystem (SY), nobody else.
        public const string Sddl = "D:P(A;;GA;;;BA)(A;;GA;;;SY)";

        // How long a closer waits after signalling before it also sends the window close. The app needs a few seconds to hand
        // the fans back; a stale event (nobody listening) must not cost more than this.
        public const int SignalGraceMs = 10000;
    }

    // The app's side: a thread that waits for the event and calls onSignal (which must only post to the UI thread). Pure
    // logic over a WaitHandle, so it is tested with a plain event.
    public sealed class ExitSignalListener : IDisposable
    {
        private readonly WaitHandle signal;
        private readonly Action onSignal;
        private readonly Action<Exception> onError;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private Thread thread;
        private bool disposed;

        public ExitSignalListener(WaitHandle signal, Action onSignal, Action<Exception> onError)
        {
            if (signal == null) throw new ArgumentNullException("signal");
            if (onSignal == null) throw new ArgumentNullException("onSignal");
            this.signal = signal;
            this.onSignal = onSignal;
            this.onError = onError;
        }

        public void Start()
        {
            if (thread != null || disposed) return;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "SenseMonitor.ExitSignal";
            thread.Start();
        }

        private void Loop()
        {
            WaitHandle[] both = new WaitHandle[] { signal, stop };
            while (true)
            {
                int which;
                try { which = WaitHandle.WaitAny(both); }
                catch (Exception ex)
                {
                    Report(ex); // the handle was closed under us: nothing more to wait for
                    return;
                }
                if (which != 0) return;
                try { onSignal(); }
                catch (Exception ex) { Report(ex); }
            }
        }

        private void Report(Exception ex)
        {
            Action<Exception> cb = onError;
            if (cb == null) return;
            try { cb(ex); }
            catch (Exception) { }
        }

        // Stops the thread and waits for it a moment (never from inside onSignal).
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { stop.Set(); }
            catch (Exception) { }
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
            try { stop.Close(); }
            catch (Exception) { }
        }
    }

    // What a closer needs from the machine; real in Platform, faked in the tests.
    public interface ICloseEnv
    {
        // A copy of the app (not the sentinel) is running.
        bool AppRunning();
        // Everything the caller waits for is gone (the app, and the sentinel when the caller asked to wait for it).
        bool Gone();
        // Signals the named event. true = the event was found and set (the app is listening).
        bool SignalExit();
        // taskkill WITHOUT /F: WM_CLOSE only.
        void RequestWindowClose();
        void Sleep(int ms);
        long NowMs();
    }

    // The order every closer follows: the event first; the window close only when the event could not be signalled, or the
    // app is still there after SignalGraceMs. Never a kill: the app hands the fans back on its way out.
    public static class GracefulClose
    {
        public const int PollMs = 500;

        // true = gone within totalMs.
        public static bool Run(ICloseEnv env, int totalMs)
        {
            if (env == null) throw new ArgumentNullException("env");
            long start = env.NowMs();
            bool asked = !env.AppRunning(); // nothing to ask: only the sentinel (if wanted) is waited for
            long windowCloseAt = 0;
            if (!asked)
            {
                bool signalled = false;
                try { signalled = env.SignalExit(); }
                catch (Exception) { signalled = false; }
                windowCloseAt = signalled ? ExitSignal.SignalGraceMs : 0;
            }
            while (true)
            {
                if (env.Gone()) return true;
                long elapsed = env.NowMs() - start;
                if (!asked && elapsed >= windowCloseAt)
                {
                    asked = true;
                    if (env.AppRunning()) env.RequestWindowClose();
                }
                if (elapsed >= totalMs) return false;
                env.Sleep(PollMs);
            }
        }
    }
}
