using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Exit paths, fan hand-back and system events (power, session). Fans go back to the
    // firmware (Auto) before anything else on every way out.
    public sealed partial class TrayApp
    {
        // Fans back to the firmware before anything else on every exit path. They count as
        // handed back only after a confirmed Auto, so a failed attempt is retried by Cleanup.
        private void ShutdownFans()
        {
            if (fansHandedBack) return;
            fansStopping = true;
            watchdog.Pause(); // no more ticks from here on: that is not a stall
            fwTimer.Stop();
            fansHandedBack = HandBack(fan.Shutdown);
            if (!fansHandedBack) Program.LogLine("Ventoinhas: modo automático não confirmado ao sair.");
        }

        // Normal write through the controller first. If it is not confirmed (COM refuses
        // outgoing calls while the UI thread handles a sent message: logoff, suspend), the
        // isolated writer sets Auto from its own MTA thread and the controller is told.
        private bool HandBack(Action controllerStep)
        {
            return FanHandback.Run(
                delegate
                {
                    controllerStep();
                    return fan.FirmwareAutoConfirmed;
                },
                delegate
                {
                    bool ok = FanSafety.SetAutoIsolated(IsolatedTimeoutMs);
                    if (ok) fan.NotifyFirmwareAuto();
                    return ok;
                }).Confirmed;
        }

        private bool OnUiThread
        {
            get { return Thread.CurrentThread.ManagedThreadId == uiThreadId; }
        }

        // ---- system events (may arrive on the SystemEvents thread) ---------------------------

        // Must finish before the machine sleeps, so it runs synchronously. SystemEvents was
        // subscribed on the UI thread, so this normally arrives there, inside the sent
        // WM_POWERBROADCAST; HandBack covers the COM restriction with the isolated writer.
        private void OnSuspending()
        {
            watchdog.Pause(); // ticks stop while asleep; the next tick re-arms it
            try
            {
                if (OnUiThread)
                {
                    if (fansStopping) return;
                    if (!HandBack(fan.OnSuspend)) Program.LogLine("Ventoinhas: modo automático não confirmado ao suspender.");
                }
                else
                {
                    // Off the UI thread the controller must not be touched: isolated write now,
                    // controller state on the UI thread afterwards.
                    bool ok = FanSafety.SetAutoIsolated(IsolatedTimeoutMs);
                    if (!ok) Program.LogLine("Ventoinhas: modo automático não confirmado ao suspender.");
                    PostToUi(delegate
                    {
                        if (fansStopping) return;
                        fan.OnSuspend();
                        if (ok && !fan.FirmwareAutoConfirmed) fan.NotifyFirmwareAuto();
                    });
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // Not urgent: posted so it runs from the message queue, outside the sent message.
        private void OnResumed()
        {
            PostToUi(delegate
            {
                if (!fansStopping) fan.OnResume();
                if (keyHook != null) keyHook.Reset();
            });
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock) PostToUi(delegate { if (keyHook != null) keyHook.Reset(); });
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try
            {
                if (OnUiThread)
                {
                    RequestExit(); // fans handed back synchronously (isolated writer if needed)
                }
                else
                {
                    if (!FanSafety.SetAutoIsolated(IsolatedTimeoutMs)) Program.LogLine("Ventoinhas: modo automático não confirmado no logoff.");
                    PostToUi(RequestExit);
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        private void OnApplicationExit(object sender, EventArgs e)
        {
            Cleanup();
        }

        private void PostToUi(Action a)
        {
            try { ui.Post(SafeRun, a); }
            catch (Exception ex) { Program.LogError(ex); }
        }

        private static void SafeRun(object state)
        {
            try { ((Action)state)(); }
            catch (Exception ex) { Program.LogError(ex); }
        }

        // ---- exit --------------------------------------------------------------------------

        // Logoff/shutdown or an external close: fans to Auto synchronously, then exit.
        public void RequestExit()
        {
            ShutdownFans();
            // Posted through the main window's own handle: taskkill/"End task" also send
            // WM_CLOSE to helper windows, so the window that received the close is the
            // surest route back into the message loop.
            try
            {
                if (form.IsHandleCreated && !form.IsDisposed)
                {
                    form.BeginInvoke((MethodInvoker)ExitApp);
                    return;
                }
            }
            catch (Exception)
            {
                // fall through
            }
            ui.Post(delegate { ExitApp(); }, null);
        }

        // "Sair".
        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            Cleanup();
            ExitThread();
        }

        // "Sair" from the tray: asks first while a repair or cleanup runs.
        public void RequestQuit()
        {
            if (exiting) return;
            if (!form.IsDisposed && form.MaintenanceBusy)
            {
                // During DISM the text says it keeps running on its own (it is never killed).
                DialogResult r = form.Ask(RepairParse.QuitPrompt(form.DismRunning),
                    Display.AppName, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }
            ExitApp();
        }

        // Every exit path, right after the fans are handed back: cancel SFC/cleanup and wait
        // briefly so they are not orphaned. DISM is left running on its own (logged).
        private void StopMaintenance(int waitMs)
        {
            try { if (!form.IsDisposed) form.StopMaintenance(waitMs); }
            catch (Exception ex) { Program.LogError(ex); }
        }

        // WM_ENDSESSION (the session really ends; the process may be killed right after):
        // make sure the fans are handed back and the tray icon is gone.
        public void EndSessionNow()
        {
            try
            {
                ShutdownFans();
                StopMaintenance(2000);
                tray.Visible = false;
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // Crash path. The UI thread's firmware objects are used only from the UI thread;
        // any other thread uses the isolated writer.
        public void EmergencyFansAuto()
        {
            try
            {
                if (OnUiThread) ShutdownFans();
                else FanSafety.SetAutoIsolated(IsolatedTimeoutMs);
            }
            catch (Exception)
            {
            }
        }

        private void Cleanup()
        {
            if (cleanedUp) return;
            cleanedUp = true;
            exiting = true;
            ShutdownFans();
            if (watchdogThread != null) watchdogThread.Dispose();
            StopMaintenance(3000);
            fwTimer.Dispose();
            trimTimer.Dispose();
            try { if (!form.IsDisposed) form.CloseForExit(); }
            catch (Exception ex) { Program.LogError(ex); }
            if (keyHook != null) keyHook.Dispose();
            if (power != null) power.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            Application.ApplicationExit -= OnApplicationExit;
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            SingleInstance.Release();
            if (smallIcon != null) smallIcon.Dispose();
            if (largeIcon != null) largeIcon.Dispose();
        }

        private void LoadIcons()
        {
            try
            {
                IntPtr[] large = new IntPtr[1];
                IntPtr[] small = new IntPtr[1];
                if (ExtractIconEx(Application.ExecutablePath, 0, large, small, 1) == 0) return;
                if (large[0] != IntPtr.Zero)
                {
                    largeIcon = (Icon)Icon.FromHandle(large[0]).Clone();
                    DestroyIcon(large[0]);
                }
                if (small[0] != IntPtr.Zero)
                {
                    smallIcon = (Icon)Icon.FromHandle(small[0]).Clone();
                    DestroyIcon(small[0]);
                }
            }
            catch (Exception)
            {
                // fall back to the system icon
            }
        }
    }
}
