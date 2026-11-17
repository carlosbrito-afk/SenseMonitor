using System;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // The tray app's side of the silent updater (all the logic is in UpdateController and Core).
    public sealed partial class TrayApp
    {
        private UpdateController updater;

        private void StartUpdater()
        {
            try { updater = new UpdateController(this, config); }
            catch (Exception ex) { Program.LogError(ex); }
        }

        private void StopUpdater()
        {
            try { if (updater != null) updater.Dispose(); }
            catch (Exception ex) { Program.LogError(ex); }
        }

        // Settings page: one line about the updater.
        public string UpdateStatus { get { return updater == null ? "" : updater.Status; } }

        // Settings "Procurar agora". Null = a check started (watch UpdateStatus); otherwise why not.
        internal string CheckUpdatesNow()
        {
            if (updater == null) return "O atualizador não está ativo.";
            try { return updater.CheckNow(); }
            catch (Exception ex) { Program.LogError(ex); return "Não foi possível verificar agora."; }
        }

        internal bool IsExiting { get { return exiting || fansStopping; } }

        internal bool WindowVisible
        {
            get { return !form.IsDisposed && form.Visible && form.WindowState != FormWindowState.Minimized; }
        }

        // What "is this a good moment to restart" needs to know.
        internal IdleFacts CollectIdleFacts(long hiddenForMs)
        {
            IdleFacts f = new IdleFacts();
            f.Exiting = IsExiting;
            f.WindowVisible = WindowVisible;
            f.HiddenForMs = hiddenForMs;
            f.FanCustomActive = UserDrivesFans;
            f.MaintenanceBusy = !form.IsDisposed && form.MaintenanceBusy;
            f.Cleaning = cleaning;
            f.ToolRunning = Program.Validating || Program.PerfTesting || Program.Unattended;
            return f;
        }

        internal void ShowTrayBalloon(string text)
        {
            try { tray.ShowBalloonTip(5000, Display.AppName, text, ToolTipIcon.None); }
            catch (Exception) { }
        }
    }
}
