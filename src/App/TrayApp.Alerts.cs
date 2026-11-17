using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // B12: alerts in two channels (tray balloon + tray icon state) and the live state of the window's
    // strip. The decisions are in Core (AlertEngine); this file feeds it with what the app sees and
    // shows what it decides. Everything runs on the UI thread; the engine is not thread-safe.
    //
    // Sources: firmware readings (sustained heat, sensor lost), the fan controller's status text
    // (thermal protection, fan did not answer), the app's own journal and the sentinel's lines
    // (watchdog / guardian / start-up recovery, read from diario.log), and the Windows crash log
    // (a restart since the app last ran).
    public sealed partial class TrayApp
    {
        private const int JournalPollMs = 15 * 1000;
        private static readonly DateTime alertEpoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private AlertEngine alerts;
        private AlertSettings alertSettings = new AlertSettings();
        private AlertState alertState = new AlertState();
        private JournalTail journalTail;
        private string cpuName;
        private Icon warnIcon;
        private Icon critIcon;
        private TrayAlertState shownIconState = TrayAlertState.Normal;
        private string iconTip;
        private int lastJournalPoll;
        private bool journalPrimed;
        private FanCondition lastFanCondition;
        private string balloonPage;
        // One router for every balloon click: the LAST balloon shown owns the click.
        private enum BalloonKind { None, Alert, Env }
        private BalloonKind lastBalloon;
        private Action envBalloonAction;
        private AlertContext lastContext = AlertContext.Clear();
        private List<CrashEntry> crashEntries;
        private bool crashLogRead;

        // Wall clock (UTC), not a stopwatch: a suspended PC still advances it, so the engine sees the
        // pause as a gap between samples instead of continuous heat.
        private static long AlertNow { get { return (long)(DateTime.UtcNow - alertEpoch).TotalMilliseconds; } }

        // Before the window and the tray exist (the strip asks the engine as soon as it is built).
        private void InitAlerts()
        {
            alertSettings = AlertStore.LoadSettings();
            alertState = AlertStore.LoadState();
            alerts = new AlertEngine(alertSettings, ThermalLimits.CpuDefault(null), ThermalLimits.EffectiveGpu(alertSettings));
            journalTail = new JournalTail(System.IO.Path.Combine(Safety.Dir, "diario.log"));
            // The CPU model decides the default heat limit; WMI is slow, so off the UI thread.
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string name = CpuInfo.ReadName();
                PostToUi(delegate
                {
                    cpuName = name;
                    ReconfigureAlerts();
                });
            });
        }

        // After the tray icon exists.
        private void InitAlertIcons()
        {
            tray.BalloonTipClicked += delegate { OnBalloonClicked(); };
            warnIcon = AlertIcons.WithDot(smallIcon, Palette.Warn);
            critIcon = AlertIcons.WithDot(smallIcon, Palette.Hot);
        }

        private void DisposeAlerts()
        {
            if (warnIcon != null) warnIcon.Dispose();
            if (critIcon != null) critIcon.Dispose();
            warnIcon = null;
            critIcon = null;
        }

        private void ReconfigureAlerts()
        {
            if (alerts == null) return;
            alerts.Configure(alertSettings, ThermalLimits.EffectiveCpu(alertSettings, cpuName), ThermalLimits.EffectiveGpu(alertSettings));
        }

        // ---- settings (Settings page) -------------------------------------------------------------

        public AlertSettings AlertSettingsCopy()
        {
            return alertSettings.Copy();
        }

        public void ApplyAlertSettings(AlertSettings s)
        {
            alertSettings = (s ?? new AlertSettings()).Copy().Normalize();
            AlertStore.SaveSettings(alertSettings);
            ReconfigureAlerts();
            Safety.Journal.Write(JournalKind.Config, "alertas: " + (alertSettings.Enabled ? "ligados" : "desligados")
                + " (temperatura " + OnOff(alertSettings.TempAlerts) + ", proteção " + OnOff(alertSettings.ProtectionAlerts)
                + ", reinício " + OnOff(alertSettings.RestartAlerts) + ", ventoinha " + OnOff(alertSettings.FanAlerts)
                + ", sensor " + OnOff(alertSettings.SensorAlerts) + ")");
            RefreshAlertIcon();
        }

        private static string OnOff(bool b) { return b ? "sim" : "não"; }

        // Limit actually in force for the CPU / GPU (the Settings page shows it next to "automático").
        public int EffectiveCpuLimit { get { return ThermalLimits.EffectiveCpu(alertSettings, cpuName); } }
        public int EffectiveGpuLimit { get { return ThermalLimits.EffectiveGpu(alertSettings); } }

        // View-model for the alert settings (limits, ranges, texts); the future screen binds to it.
        public AlertSettingsModel AlertSettingsModel { get { return new AlertSettingsModel(alertSettings, cpuName); } }

        // ---- the strip -----------------------------------------------------------------------------

        public bool NeedsAttention { get { return alerts != null && alerts.NeedsAttention(AlertNow); } }
        public StripState StripState { get { return alerts == null ? StripState.Reading : alerts.State(AlertNow); } }
        public bool StripReadingGrace { get { return alerts == null || alerts.ReadingGrace(AlertNow); } }
        public bool AttentionIsCritical { get { return alerts != null && alerts.AttentionIsCritical(AlertNow); } }
        public string AttentionReason { get { return alerts == null ? null : alerts.AttentionReason(AlertNow); } }

        // The strip was clicked: open the page that explains why, and count it as seen.
        public void OnStripClicked()
        {
            if (alerts == null) return;
            string page = alerts.AttentionPage(AlertNow);
            if (page == null) return;
            ShowPage(page);
            alerts.Acknowledge(page);
            RefreshAlertIcon();
        }

        // A page became the active one of a visible window: one-off events pointing to it are seen, and
        // looking at Saúde marks the unseen restarts as seen (persisted by incident id, so the strip
        // stays on "Requer sua atenção" across app restarts until then).
        public void AlertsPageShown(string key)
        {
            if (alerts == null) return;
            alerts.Acknowledge(key);
            if (key == "Saude") MarkRestartsSeen();
            RefreshAlertIcon();
            if (form != null && !form.IsDisposed) form.UpdateStrip();
        }

        private void MarkRestartsSeen()
        {
            List<CrashEntry> unseen = RestartIncidents.Unseen(crashEntries, alertState.SeenIncidents, DateTime.Now);
            if (unseen.Count == 0) return;
            if (RestartIncidents.MarkSeen(alertState.SeenIncidents, unseen)) AlertStore.SaveState(alertState);
            alerts.SetUnseenRestarts(0);
        }

        // Restarts of the last 72 h that were not looked at: the strip's reason, from the persisted list.
        private void RefreshUnseenRestarts()
        {
            if (alerts == null || !crashLogRead) return;
            alerts.SetUnseenRestarts(RestartIncidents.Unseen(crashEntries, alertState.SeenIncidents, DateTime.Now).Count);
        }

        // A tray command (mode, fan) did not take effect: balloon + icon, like the other safety events.
        public void OnTrayCommandFailed(string what, string page)
        {
            if (alerts == null || exiting) return;
            DateTime now = DateTime.Now;
            Show(alerts.Raise(AlertText.CommandFailed(what, page, now), AlertNow, now, UserNotifyState.Query()));
            Safety.Journal.Write(JournalKind.Action, "comando da bandeja sem efeito: " + what);
            RefreshAlertIcon();
            if (form != null && !form.IsDisposed) form.UpdateStrip();
        }

        // ---- feeding the engine (UI thread, every firmware tick) -----------------------------------

        private void AlertsTick(SensorReading r)
        {
            if (alerts == null || exiting) return;
            lastContext = UserNotifyState.Query();
            DateTime now = DateTime.Now;

            FanCondition fc = FanStatusClassifier.Classify(fan.Status);
            AlertSample s = new AlertSample();
            s.NowMs = AlertNow;
            s.Local = now;
            s.CpuC = r == null ? null : r.CpuTempC;
            s.GpuC = r == null ? null : r.GpuTempC;
            s.ThermalProtection = fc == FanCondition.ThermalProtection;
            s.FanStatus = fan.Status;
            Show(alerts.Tick(s, lastContext));

            // "The fan did not answer" is an edge of the controller's status; the watchdog / guardian
            // come from the journal below (one source each, so nothing is told twice).
            if (fc == FanCondition.NoResponse && lastFanCondition != FanCondition.NoResponse)
            {
                Show(alerts.Raise(AlertText.FanNoResponse(now), AlertNow, now, lastContext));
            }
            lastFanCondition = fc;

            int tick = Environment.TickCount;
            if (!journalPrimed || unchecked(tick - lastJournalPoll) >= JournalPollMs)
            {
                lastJournalPoll = tick;
                PollJournalForAlerts(now);
            }
            RefreshAlertIcon();
        }

        // Fan lines of diario.log written since the last announcement. The first read after start
        // takes the lines of the last 15 minutes (a restart after an abnormal exit shows the
        // recovery line the previous run's sentinel or this start wrote).
        private void PollJournalForAlerts(DateTime now)
        {
            List<JournalEntry> lines = journalTail.ReadNew();
            DateTime after = alertState.LastJournalTicks > 0 ? new DateTime(alertState.LastJournalTicks) : DateTime.MinValue;
            if (!journalPrimed)
            {
                journalPrimed = true;
                DateTime floor = now.AddMinutes(-15);
                if (floor > after) after = floor;
            }
            List<Alert> found = JournalAlertClassifier.Select(lines, after);
            if (found.Count == 0) return;
            DateTime newest = after;
            foreach (Alert a in found)
            {
                if (a.Local > newest) newest = a.Local;
                Show(alerts.Raise(a, AlertNow, now, lastContext));
            }
            alertState.LastJournalTicks = newest.Ticks;
            AlertStore.SaveState(alertState);
        }

        // The crash log was read (start, then every 30 min): a restart newer than the last one told
        // is announced once, gently. The mark moves on even when the alert is switched off.
        private void OnCrashesRead(List<CrashEntry> entries, bool ok)
        {
            crashLogRead = ok;
            crashEntries = entries;
            if (alerts == null) return;
            // "Estável" needs a readable restart log; an unreadable one adds no attention but no assumption either.
            alerts.SetRestartLogKnown(ok && entries != null, AlertNow);
            if (!ok || entries == null)
            {
                if (form != null && !form.IsDisposed) form.UpdateStrip();
                return;
            }
            RefreshUnseenRestarts();
            DateTime now = DateTime.Now;
            List<CrashEntry> fresh = NewCrashDetector.Select(entries, alertState.LastCrashTicks, now);
            if (fresh.Count == 0)
            {
                RefreshAlertIcon();
                if (form != null && !form.IsDisposed) form.UpdateStrip();
                return;
            }
            alertState.LastCrashTicks = NewCrashDetector.NewestTicks(entries, alertState.LastCrashTicks);
            AlertStore.SaveState(alertState);
            Show(alerts.Raise(AlertText.UnexpectedRestart(fresh, now), AlertNow, now, UserNotifyState.Query()));
            RefreshAlertIcon();
            if (form != null && !form.IsDisposed) form.UpdateStrip();
        }

        // ---- showing -------------------------------------------------------------------------------

        private void Show(AlertOutput o)
        {
            if (o == null) return;
            foreach (Alert a in o.Balloons)
            {
                try
                {
                    balloonPage = a.Page;
                    lastBalloon = BalloonKind.Alert;
                    ToolTipIcon icon = a.Severity == AlertSeverity.Critical ? ToolTipIcon.Error
                        : (a.Severity == AlertSeverity.Warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    tray.ShowBalloonTip(8000, a.Title, a.Text, icon);
                    Safety.Journal.Write(JournalKind.Action, "alerta: " + a.Title + (a.Summary ? " (resumo)" : ""));
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                }
            }
        }

        // The environment-test balloons (changed notebook, never tested, tests left for later): a click opens the wizard.
        // Neither kind of balloon clears or acknowledges the other's state.
        private void ShowEnvBalloon(string text, ToolTipIcon icon)
        {
            envBalloonAction = OpenEnvWizard;
            lastBalloon = BalloonKind.Env;
            tray.ShowBalloonTip(8000, Display.AppName, text, icon);
        }

        private void OnBalloonClicked()
        {
            BalloonKind kind = lastBalloon;
            lastBalloon = BalloonKind.None;
            if (kind == BalloonKind.Env)
            {
                Action a = envBalloonAction;
                if (a != null) a();
                return;
            }
            if (kind != BalloonKind.Alert) return;
            string page = balloonPage;
            if (page == null) return;
            ShowPage(page);
            if (alerts != null) alerts.Acknowledge(page);
            RefreshAlertIcon();
        }

        // The fans could not be confirmed in Automático (a failing write that backs off): through the alert engine.
        private void RaiseFanUnconfirmed()
        {
            if (alerts == null || exiting) return;
            DateTime now = DateTime.Now;
            Show(alerts.Raise(AlertText.FanUnconfirmed(now), AlertNow, now, UserNotifyState.Query()));
            RefreshAlertIcon();
            if (form != null && !form.IsDisposed) form.UpdateStrip();
        }

        // Tray icon: normal / amber dot / red dot, with the reason as its tooltip.
        private void RefreshAlertIcon()
        {
            if (alerts == null || tray == null || exiting) return;
            TrayAlertState st = alerts.IconState(AlertNow);
            string tip = alerts.IconText(AlertNow);
            if (st != shownIconState)
            {
                shownIconState = st;
                Icon ic = st == TrayAlertState.Critical ? critIcon : (st == TrayAlertState.Warning ? warnIcon : smallIcon);
                tray.Icon = ic ?? smallIcon ?? SystemIcons.Application;
            }
            if (tip != iconTip)
            {
                iconTip = tip;
                lastTip = null; // the next tick rewrites the tooltip
                if (tip != null) tray.Text = tip;
            }
        }

        // The tooltip the tick wants to set: the alert reason wins while the icon is not normal.
        private string AlertAwareTip(string normal)
        {
            return iconTip != null ? iconTip : normal;
        }
    }

    // Tray icon with a colored dot in the bottom-right corner (the second alert channel).
    internal static class AlertIcons
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        public static Icon WithDot(Icon baseIcon, Color dot)
        {
            try
            {
                Size sz = SystemInformation.SmallIconSize;
                int w = Math.Max(16, sz.Width);
                int h = Math.Max(16, sz.Height);
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        Icon src = baseIcon ?? SystemIcons.Application;
                        g.DrawIcon(src, new Rectangle(0, 0, w, h));
                        int d = (int)Math.Round(w * 0.56);
                        Rectangle r = new Rectangle(w - d, h - d, d - 1, d - 1);
                        using (SolidBrush ring = new SolidBrush(Color.White)) g.FillEllipse(ring, Rectangle.Inflate(r, 1, 1));
                        using (SolidBrush fill = new SolidBrush(dot)) g.FillEllipse(fill, r);
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
                    finally { DestroyIcon(hIcon); }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
