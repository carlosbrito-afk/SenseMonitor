using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Plain navigation background (the brand lives in the title bar).
    internal sealed class NavPanel : Painted
    {
        public NavPanel()
        {
            BackColor = Theme.Nav;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Nav);
        }
    }

    // Main window, mockup D: verdict strip across the top, navigation on the left
    // (Monitor · Modos · Ventoinha · Saúde · Manutenção · Configurações), the selected page
    // on the right.
    public class MainForm : Form
    {
        private const int BaseWidth = 860;
        private const int BaseHeight = 526;
        private const int NavWidth = 176;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;
        private const int WM_QUERYENDSESSION = 0x0011;
        private const int WM_ENDSESSION = 0x0016;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private readonly TrayApp app;
        private readonly VerdictStrip strip;
        private readonly NavPanel nav;
        private readonly List<NavButton> navButtons = new List<NavButton>();
        private readonly Dictionary<string, Page> pages = new Dictionary<string, Page>();
        private readonly MonitorPage monitor;
        private readonly ModesPage modes;
        private readonly FanPage fans;
        private readonly MaintenancePage maint;
        private string current;
        private Page activePage;
        private bool allowClose;
        private bool positioned;
        private bool userClose;
        private bool sessionEnding;

        public MainForm(TrayApp app)
        {
            this.app = app;
            SuspendLayout();
            Text = Display.AppName;
            if (app.LargeIcon != null) Icon = app.LargeIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            // DPI: no design-time AutoScaleDimensions, so WinForms does no load-time scaling.
            // Shell layout and fonts are scaled by Theme from the window's real DPI
            // (GetDpiForWindow) and WM_DPICHANGED is handled below (PerMonitorV2); the
            // instrument controls follow their own DeviceDpi.
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(BaseWidth, BaseHeight);

            strip = new VerdictStrip();
            strip.Clicked += delegate { app.OnStripClicked(); };
            Controls.Add(strip);
            nav = new NavPanel();
            Controls.Add(nav);

            monitor = new MonitorPage(app);
            AddPage("Monitor", "Monitor", monitor);

            // Modes: Mode is written only from a user action (card click or tray menu).
            modes = new ModesPage(app.ReadMode, app.SetMode,
                delegate { return app.Config.PerfWarningShown; },
                delegate
                {
                    app.Config.PerfWarningShown = true;
                    app.SaveConfigQuietly();
                });
            modes.ConfirmWarning = ConfirmPerfWarning;
            modes.LockedReason = delegate
            {
                if (app.ReadOnly) return SafetyText.ReadOnlyReason;
                return app.EnvOwnsFirmware ? "O teste de ambiente está usando o firmware: aguarde ele terminar." : null;
            };
            modes.ViewSource = app.Telemetry.BuildModesView;
            modes.SetOffers(app.OfferedModes(), app.ModesNote);
            AddPage("Modos", "Modos", new HostPage(modes,
                delegate(bool active)
                {
                    // Active: sample CPU/GPU so a mode switch can be proven (TelemetryHub).
                    if (active) { app.Telemetry.Acquire(modes, false); modes.RefreshState(); }
                    else app.Telemetry.Release(modes);
                },
                delegate(bool active) { if (active) modes.RefreshInsights(); }, null));

            // Fans: every delegate runs here on the UI thread, like fan.Tick. Manual/Curva/Máximo
            // only after the hardware validation passed (otherwise Automático only).
            fans = new FanPage(app.Fan, app.Config, delegate { app.SaveConfigQuietly(); }, app.HottestTemp,
                app.Config.ManualFanAllowed && !app.ReadOnly, app.SetFanMax, app.SetFanAuto, app.IsFanMax);
            fans.LockedNote = app.FanLockedNote;
            fans.BusyReason = delegate { return app.EnvOwnsFirmware ? "O teste de ambiente está usando as ventoinhas: aguarde ele terminar." : null; };
            fans.ModeChosen += app.OnFanModeChosen;
            AddPage("Ventoinha", "Ventoinha", new HostPage(fans,
                delegate(bool active) { if (active) fans.RefreshState(); },
                delegate(bool active) { if (active) fans.RefreshState(); else fans.RefreshHidden(); }, null));

            AddPage("Saude", "Saúde", new HostPage(HealthPage.CreateDefault(app.TempLog), null, null, null));

            // Maintenance: real services; every system change goes through its own confirmation.
            ChangeLog log = new ChangeLog(new ChangeLogStore(ChangeLogStore.DefaultPath));
            maint = new MaintenancePage(StartupManager.CreateDefault(log), Cleaner.CreateDefault(log),
                new RepairRunner(log), new RestorePoint(log), log);
            HostPage maintHost = new HostPage(maint,
                delegate(bool active) { if (active) maint.RefreshState(); }, null, maint.CancelAll);
            maintHost.Heading = "Manutenção";
            AddPage("Manutencao", "Manutenção", maintHost);
            AddPage("Configuracoes", "Configurações", new SettingsPage(app));
            ResumeLayout(false);

            string last = app.Config.LastPage;
            SelectPage(last != null && pages.ContainsKey(last) ? last : "Monitor", false);
            UpdateStrip();
        }

        private void AddPage(string key, string label, Page page)
        {
            NavButton b = new NavButton();
            b.Key = key;
            b.Text = label;
            b.Icon = key == "Monitor" ? NavIcon.Activity : key == "Modos" ? NavIcon.Gauge : key == "Ventoinha" ? NavIcon.Fan
                : key == "Saude" ? NavIcon.Heart : key == "Manutencao" ? NavIcon.Wrench : NavIcon.Gear;
            b.TabIndex = navButtons.Count;
            b.Click += delegate { SelectPage(key, true); };
            navButtons.Add(b);
            nav.Controls.Add(b);
            page.Visible = false;
            pages[key] = page;
            Controls.Add(page);
        }

        // ---- shared state for the tray menu ---------------------------------------------------

        public string FanModeLabel { get { return fans.ModeLabel; } }
        public bool FanManualAllowed { get { return fans.ManualAllowed; } }
        public bool MaintenanceBusy { get { return maint.IsBusy; } }
        public bool DismRunning { get { return maint.DismRunning; } }

        // Exit: cancel a running cleanup/repair and wait briefly for its worker. SFC and cleanup
        // stop; DISM is never killed: it keeps running on its own and finishes after the app exits.
        public void StopMaintenance(int waitMs)
        {
            if (maint.DismRunning) Program.LogLine("Manutenção: o DISM continua rodando em segundo plano depois da saída do app (não é interrompido).");
            maint.CancelAll();
            if (!maint.WaitForWorkers(waitMs)) Program.LogLine("Manutenção: trabalho ainda em andamento ao sair.");
        }
        public bool FanMaxActive { get { return fans.MaxActive; } }

        // The ownership of the firmware changed: the Modos page shows or drops its "wait" note.
        public void RefreshModes()
        {
            modes.RefreshState();
        }

        // After the environment test: the Modos page and the Ventoinha page follow the new profile.
        public void ApplyCapabilities()
        {
            modes.SetOffers(app.OfferedModes(), app.ModesNote);
            fans.ApplyCapabilities(app.Config.ManualFanAllowed && !app.ReadOnly, app.FanLockedNote, app.Config.EnvFanLayout);
            UpdateStrip();
        }

        // Tray "Modo" items: same path as the cards (one-time Desempenho warning included).
        public void ChooseMode(PerfMode m)
        {
            perfWarningDeclined = false;
            modes.Choose(m);
            UpdateStrip();
            // The tray has no message line: a mode that did not take effect is told by an alert. Nothing is
            // said when the user declined the warning or read-only mode blocked it on purpose.
            if (!app.ReadOnly && !perfWarningDeclined && app.CurrentMode != m)
            {
                app.OnTrayCommandFailed("trocar para o modo " + ModeName(m), "Modos");
            }
        }

        private static string ModeName(PerfMode m)
        {
            return ModeCatalog.Name(m);
        }

        // Tray "Ventoinha" items: same path as the page's tabs.
        public void ChooseFanAuto()
        {
            fans.ChooseAuto();
            UpdateStrip();
            app.UpdatePollRate();
            if (!app.Fan.FirmwareAutoConfirmed) app.OnTrayCommandFailed("voltar a ventoinha ao automático", "Ventoinha");
        }

        public void ChooseFanMax()
        {
            fans.ChooseMax();
            UpdateStrip();
            app.UpdatePollRate();
            if (app.Config.ManualFanAllowed && !app.ReadOnly && !FanMaxActive) app.OnTrayCommandFailed("ativar a ventoinha no máximo", "Ventoinha");
        }

        private bool perfWarningDeclined;

        private bool ConfirmPerfWarning(string text)
        {
            bool ok = Ask(text, "Modo Desempenho", MessageBoxButtons.OKCancel, MessageBoxDefaultButton.Button2) == DialogResult.OK;
            if (!ok) perfWarningDeclined = true;
            return ok;
        }

        // Warning box owned by this window when visible; when hidden (tray action) by a
        // throwaway topmost owner, so the box never opens behind other windows.
        public DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxDefaultButton def)
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                return MessageBox.Show(this, text, caption, buttons, MessageBoxIcon.Warning, def);
            }
            using (Form owner = new Form())
            {
                owner.FormBorderStyle = FormBorderStyle.None;
                owner.ShowInTaskbar = false;
                owner.StartPosition = FormStartPosition.Manual;
                owner.Location = new Point(-32000, -32000);
                owner.Size = new Size(1, 1);
                owner.TopMost = true;
                owner.Show();
                owner.Activate();
                return MessageBox.Show(owner, text, caption, buttons, MessageBoxIcon.Warning, def);
            }
        }

        // ---- pages ----------------------------------------------------------------------------

        public void SelectPage(string key, bool save)
        {
            Page page;
            if (!pages.TryGetValue(key, out page)) return;
            if (key != current)
            {
                SuspendLayout();
                foreach (KeyValuePair<string, Page> kv in pages)
                {
                    kv.Value.Visible = kv.Key == key;
                }
                foreach (NavButton b in navButtons)
                {
                    b.Selected = b.Key == key;
                }
                current = key;
                ResumeLayout(true);
                page.PerformLayout();
            }
            UpdateActive();
            if (save && app.Config.LastPage != key)
            {
                app.Config.LastPage = key;
                app.SaveConfigQuietly();
            }
        }

        // Starts/stops page timers: only the selected page of a visible, non-minimized window runs.
        private void UpdateActive()
        {
            Page want = null;
            if (Visible && WindowState != FormWindowState.Minimized && current != null) want = pages[current];
            if (want == activePage) return;
            if (activePage != null) activePage.SetActive(false);
            activePage = want;
            if (activePage != null)
            {
                activePage.SetActive(true);
                app.AlertsPageShown(current); // the page is on screen now: its alerts count as seen
            }
        }

        public void NotifyFirmwareTick()
        {
            foreach (Page p in pages.Values) p.OnFirmwareTick();
            UpdateStrip();
        }

        // "Estável" / "Requer sua atenção" and MODO / VENTOINHA telemetry (UI thread, cheap: cached values).
        // No crash or restart text here: that lives in Saúde and in the one-time alert.
        public void UpdateStrip()
        {
            strip.SetState(app.StripState, app.AttentionIsCritical, app.AttentionReason, app.StripReadingGrace);
            strip.Detail = app.ReadOnly ? SafetyText.ReadOnlyBanner : "";
            strip.SetTelemetry("Modo", app.ModeLabel, "Ventoinha", fans.ModeLabel);
        }

        // Idempotent: shows, restores and activates (never toggles).
        public void ShowAndActivate()
        {
            if (!positioned)
            {
                CenterOnScreen();
                positioned = true;
            }
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Program.ForceForeground(Handle);
            Activate();
        }

        private void CenterOnScreen()
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.X + Math.Max(0, (wa.Width - Width) / 2), wa.Y + Math.Max(0, (wa.Height - Height) / 2));
        }

        // Closing for real (Sair / logoff); stops page threads and lets the close through.
        public void CloseForExit()
        {
            allowClose = true;
            if (activePage != null) activePage.SetActive(false);
            activePage = null;
            foreach (Page p in pages.Values) p.Shutdown();
            if (!IsDisposed) Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi = 96;
            try { dpi = (int)GetDpiForWindow(Handle); }
            catch (Exception) { dpi = DeviceDpi; }
            Theme.SetDpi(dpi);
            ClientSize = new Size(Theme.S(BaseWidth), Theme.S(BaseHeight));
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (nav == null) return;
            Size cs = ClientSize;
            int stripH = Theme.S(VerdictStrip.LogicalHeight);
            int navW = Theme.S(NavWidth);
            strip.SetBounds(0, 0, cs.Width, stripH);
            nav.SetBounds(0, stripH, navW, Math.Max(0, cs.Height - stripH));
            int y = Theme.S(10);
            foreach (NavButton b in navButtons)
            {
                b.SetBounds(Theme.S(8), y, navW - Theme.S(16), Theme.S(36));
                y += Theme.S(38);
            }
            foreach (Page p in pages.Values)
            {
                p.SetBounds(navW, stripH, Math.Max(0, cs.Width - navW), Math.Max(0, cs.Height - stripH));
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateActive();
            app.UpdatePollRate();
            if (!Visible) app.ScheduleTrim();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateActive();
            app.UpdatePollRate(); // minimized counts as hidden
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // WinForms keeps the old CloseReason after a cancelled close, so the cause is
            // tracked from the window messages themselves.
            bool user = userClose;
            userClose = false;
            if (!allowClose)
            {
                if (sessionEnding || e.CloseReason == CloseReason.WindowsShutDown)
                {
                    // Never block logoff/shutdown: fans to Auto now, orderly exit right after.
                    app.RequestExit();
                }
                else if (user)
                {
                    // X / Alt+F4 / system menu: hide to the tray; only "Sair" exits.
                    e.Cancel = true;
                    Hide();
                }
                else
                {
                    // Task Manager "End task", taskkill without /F, a plain WM_CLOSE: orderly exit.
                    e.Cancel = true;
                    app.RequestExit();
                }
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == SC_CLOSE) userClose = true;
            if (m.Msg == WM_QUERYENDSESSION) sessionEnding = true; // the app exits on any logoff attempt
            if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero) app.EndSessionNow();
            int show = Forja.Platform.SingleInstance.ShowMessageId;
            if (show != 0 && m.Msg == show)
            {
                app.ShowMainWindow();
                return;
            }
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = m.WParam.ToInt32() & 0xFFFF;
                int left = Marshal.ReadInt32(m.LParam, 0);
                int top = Marshal.ReadInt32(m.LParam, 4);
                int right = Marshal.ReadInt32(m.LParam, 8);
                int bottom = Marshal.ReadInt32(m.LParam, 12);
                ApplyDpi(dpi, new Rectangle(left, top, right - left, bottom - top)); // suggested RECT
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private void ApplyDpi(int dpi, Rectangle suggested)
        {
            Theme.SetDpi(dpi);
            SuspendLayout();
            Bounds = suggested;
            ClientSize = new Size(Theme.S(BaseWidth), Theme.S(BaseHeight));
            ResumeLayout(true);
            foreach (Page p in pages.Values) p.PerformLayout();
            Invalidate(true);
        }
    }
}
