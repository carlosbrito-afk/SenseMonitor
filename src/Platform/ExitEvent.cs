using System;
using System.Security.AccessControl;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The named exit event (see ExitSignal in Core): the app creates it, every closer signals it.
    public static class ExitEvent
    {
        // The app's side. The event is created with a protected DACL (ExitSignal.Sddl: Administrators and SYSTEM only).
        // null = not made (not elevated, or the name already exists: another process made it, so its access list is unknown and
        // it is NOT trusted); the closers then fall back to the window close. error says why, for the log.
        public static EventWaitHandle CreateForApp(out string error)
        {
            return Create(ExitSignal.EventName, ExitSignal.Sddl, out error);
        }

        // Another name or access list is for tests only (production always uses the two constants above).
        internal static EventWaitHandle Create(string name, string sddl, out string error)
        {
            error = null;
            EventWaitHandle h = null;
            try
            {
                EventWaitHandleSecurity security = new EventWaitHandleSecurity();
                security.SetSecurityDescriptorSddlForm(sddl);
                bool created;
                h = new EventWaitHandle(false, EventResetMode.AutoReset, name, out created, security);
                if (!created)
                {
                    h.Close();
                    error = "o evento de saída já existe (criado por outro processo): não usado";
                    return null;
                }
                return h;
            }
            catch (Exception ex)
            {
                if (h != null)
                {
                    try { h.Close(); }
                    catch (Exception) { }
                }
                error = "o evento de saída não pôde ser criado (" + ex.GetType().Name + ")";
                return null;
            }
        }

        // A closer's side: true = the event was there and is set (the app is listening). false = no such event (an app from
        // before this existed, or none running) or no access (not an administrator).
        public static bool Signal()
        {
            return Signal(ExitSignal.EventName);
        }

        internal static bool Signal(string name)
        {
            try
            {
                using (EventWaitHandle h = EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Modify))
                {
                    return h.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // The machine side of GracefulClose for the installed app's processes.
    public sealed class RealCloseEnv : ICloseEnv
    {
        private readonly int excludePid;
        private readonly bool includeSentinels;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        public RealCloseEnv(int excludePid, bool includeSentinels)
        {
            this.excludePid = excludePid;
            this.includeSentinels = includeSentinels;
        }

        public bool AppRunning() { return ProcessScan.AppIds(excludePid).Count > 0; }

        public bool Gone()
        {
            return ProcessScan.AppIds(excludePid).Count == 0 && (!includeSentinels || ProcessScan.SentinelCount() == 0);
        }

        public bool SignalExit() { return ExitEvent.Signal(); }

        public void RequestWindowClose() { AppProcesses.RequestClose(excludePid); }

        public void Sleep(int ms) { Thread.Sleep(ms); }

        public long NowMs() { return clock.ElapsedMilliseconds; }
    }
}
