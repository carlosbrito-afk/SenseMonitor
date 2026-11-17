using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Autostart toggle, alerts (balloon + tray icon: master switch and one per kind), hardware key
    // status and the diagnostic read-only mode. schtasks is slow, so it runs on a pool thread and
    // posts back.
    public sealed class SettingsPage : Page
    {
        private static readonly string[] AlertLabels = new string[]
        {
            "Temperatura", "Proteção térmica", "Reinício inesperado", "Ventoinha", "Sensor"
        };

        private readonly TrayApp app;
        private readonly SynchronizationContext ui;
        private readonly ToggleSwitch autostart = new ToggleSwitch();
        private readonly ToggleSwitch readOnlyToggle = new ToggleSwitch();
        private Rectangle startCard;
        private Rectangle envCard;
        private Rectangle alertsCard;
        private readonly ToggleSwitch alertMaster = new ToggleSwitch();
        private readonly ToggleSwitch[] alertKinds = new ToggleSwitch[5];
        private readonly Rectangle[] alertLabelRects = new Rectangle[5];
        private Rectangle readOnlyCard;
        private readonly FlatButton redoButton = new FlatButton();
        private readonly GhostButton undoButton = new GhostButton();
        private readonly GhostButton uninstallButton = new GhostButton();
        private readonly bool installedCopy;
        private string envNote;
        private bool undoBusy;
        private readonly UpdateCard updateCard;
        // Geral / Tecla / Widget: the window is fixed-size, so the tecla and widget cards get their own view.
        private readonly PageSwitch switcher = new PageSwitch();
        private readonly GestureCard gestureCard;
        private readonly WidgetCard widgetCard;
        private int view;
        // RESERVED for the installer's "Desinstalar" button (Geral view, bottom right, under the update card,
        // on the same row as the config path note). The installer agent: add the button as a child control, set
        // its bounds to UninstallSlot in OnLayout, and show it only while view == 0 (see ApplyView).
        private Rectangle uninstallSlot;

        private string autostartNote = "verificando…";
        private bool autostartBusy;

        public SettingsPage(TrayApp app)
        {
            this.app = app;
            ui = SynchronizationContext.Current;
            Heading = "Configurações";
            redoButton.Text = "Refazer teste de ambiente";
            redoButton.Click += delegate { app.OpenEnvWizard(); };
            Controls.Add(redoButton);
            undoButton.Text = "Reativar serviços da Acer";
            undoButton.Click += delegate { UndoServices(); };
            Controls.Add(undoButton);
            // Only the installed copy can remove itself (a development build has nothing in Program Files to remove).
            uninstallButton.Text = "Desinstalar…";
            installedCopy = UpdatePolicy.IsInstalledCopy(Application.ExecutablePath, InstallPaths.InstalledExe);
            uninstallButton.Click += delegate
            {
                string problem = InstallTool.UninstallFromApp();
                if (problem != null) MessageBox.Show(problem, "SenseMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Controls.Add(uninstallButton);
            autostart.Enabled = false;
            autostart.AccessibleName = "Iniciar com o Windows";
            autostart.BackColor = Theme.Panel;
            autostart.UserToggled += OnAutostartToggled;
            Controls.Add(autostart);

            // Diagnostic read-only mode: saved in the config, effective at the next start.
            readOnlyToggle.AccessibleName = SafetyText.SettingTitle;
            readOnlyToggle.BackColor = Theme.Panel;
            readOnlyToggle.Checked = app.Config.DiagnosticReadOnly || app.ReadOnlyFromSwitch;
            readOnlyToggle.Enabled = !app.ReadOnlyFromSwitch;
            readOnlyToggle.UserToggled += OnReadOnlyToggled;
            Controls.Add(readOnlyToggle);
            UpdateEnvButtons();

            // Alerts: every change is saved at once (alertas.json) and takes effect immediately.
            AlertSettings a = app.AlertSettingsCopy();
            alertMaster.AccessibleName = "Alertas na bandeja";
            alertMaster.BackColor = Theme.Panel;
            alertMaster.Checked = a.Enabled;
            alertMaster.UserToggled += delegate(bool want) { OnAlertToggled(-1, want); };
            Controls.Add(alertMaster);
            for (int i = 0; i < alertKinds.Length; i++)
            {
                int index = i;
                ToggleSwitch t = new ToggleSwitch();
                t.AccessibleName = "Alerta: " + AlertLabels[i];
                t.BackColor = Theme.Panel;
                t.Checked = AlertFlag(a, i);
                t.Enabled = a.Enabled;
                t.UserToggled += delegate(bool want) { OnAlertToggled(index, want); };
                alertKinds[i] = t;
                Controls.Add(t);
            }
            updateCard = new UpdateCard(app);
            Controls.Add(updateCard);

            gestureCard = new GestureCard(app);
            widgetCard = new WidgetCard(app);
            Controls.Add(gestureCard);
            Controls.Add(widgetCard);
            switcher.Segments = new string[] { "Geral", "Tecla", "Widget" };
            switcher.Changed += delegate(int i) { view = i; ApplyView(); };
            Controls.Add(switcher);
            ApplyView();
        }

        // Shows the controls of the selected view only (the general cards are painted by this page,
        // their switches are children and follow Visible).
        private void ApplyView()
        {
            bool general = view == 0;
            autostart.Visible = general;
            readOnlyToggle.Visible = general;
            alertMaster.Visible = general;
            foreach (ToggleSwitch t in alertKinds) t.Visible = general;
            updateCard.Visible = general;
            redoButton.Visible = general;
            uninstallButton.Visible = general && installedCopy;
            UpdateEnvButtons();
            gestureCard.Visible = view == 1;
            widgetCard.Visible = view == 2;
            switcher.Selected = view;
            if (view == 1) gestureCard.Reload();
            if (view == 2) widgetCard.Reload();
            Invalidate();
        }

        public override void OnFirmwareTick()
        {
            updateCard.RefreshStatus();
        }

        private static bool AlertFlag(AlertSettings a, int i)
        {
            switch (i)
            {
                case 0: return a.TempAlerts;
                case 1: return a.ProtectionAlerts;
                case 2: return a.RestartAlerts;
                case 3: return a.FanAlerts;
                default: return a.SensorAlerts;
            }
        }

        private void OnAlertToggled(int index, bool want)
        {
            AlertSettings a = app.AlertSettingsCopy();
            switch (index)
            {
                case -1: a.Enabled = want; break;
                case 0: a.TempAlerts = want; break;
                case 1: a.ProtectionAlerts = want; break;
                case 2: a.RestartAlerts = want; break;
                case 3: a.FanAlerts = want; break;
                default: a.SensorAlerts = want; break;
            }
            app.ApplyAlertSettings(a);
            alertMaster.Checked = a.Enabled;
            for (int i = 0; i < alertKinds.Length; i++)
            {
                alertKinds[i].Checked = AlertFlag(a, i);
                alertKinds[i].Enabled = a.Enabled;
            }
            Invalidate(alertsCard);
        }

        private void OnReadOnlyToggled(bool want)
        {
            app.Config.DiagnosticReadOnly = want;
            app.SaveConfigQuietly();
            Safety.Journal.Write(JournalKind.Config, "modo somente leitura " + (want ? "ligado" : "desligado") + " nas configurações (vale no próximo início)");
            readOnlyToggle.Checked = want;
            Invalidate(readOnlyCard);
        }

        // The Acer services the wizard disabled on the user's click go back to what they were.
        private void UndoServices()
        {
            if (undoBusy || app.Config.ServiceUndo == null || app.Config.ServiceUndo.Count == 0) return;
            undoBusy = true;
            undoButton.Enabled = false;
            envNote = "Restaurando os serviços da Acer…";
            Invalidate(envCard);
            UndoOutcome result = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    result = ConflictServices.UndoAll(new WmiServiceControl(), app.Config, delegate { try { app.Config.Save(Config.DefaultPath); return true; } catch (Exception) { return false; } }, Safety.Journal);
                }
                catch (Exception ex) { Program.LogError(ex); }
                Post(delegate
                {
                    undoBusy = false;
                    undoButton.Enabled = true;
                    envNote = result == null ? "Não foi possível restaurar os serviços."
                        : result.Restored.ToString(System.Globalization.CultureInfo.InvariantCulture) + " serviço(s) restaurado(s)" + (result.Failed > 0 ? "; " + result.Failed + " não deu certo." : ".");
                    UpdateEnvButtons();
                    Invalidate();
                });
            });
        }

        private void UpdateEnvButtons()
        {
            undoButton.Visible = view == 0 && app.Config.ServiceUndo != null && app.Config.ServiceUndo.Count > 0;
        }

        // Where the "Desinstalar" button goes (Geral view). Empty until the first layout.
        internal Rectangle UninstallSlot { get { return uninstallSlot; } }

        public override void SetActive(bool active)
        {
            if (!active) return;
            UpdateEnvButtons();
            PerformLayout();
            QueryAutostart();
            updateCard.RefreshStatus();
            if (view == 1) gestureCard.Reload();
            if (view == 2) widgetCard.Reload();
        }

        // ---- autostart -------------------------------------------------------------------

        private void QueryAutostart()
        {
            if (autostartBusy) return;
            autostartBusy = true;
            autostart.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool on = Autostart.IsEnabled();
                Post(delegate { AutostartDone(on, null); });
            });
        }

        private void OnAutostartToggled(bool want)
        {
            if (autostartBusy) return;
            autostartBusy = true;
            autostart.Enabled = false;
            autostartNote = want ? "ligando…" : "desligando…";
            Invalidate(startCard);
            ThreadPool.QueueUserWorkItem(delegate
            {
                // The task always runs the installed copy under Program Files.
                string error = want ? Autostart.Enable() : (Autostart.Disable() ? null : "Não foi possível alterar a inicialização automática.");
                bool on = Autostart.IsEnabled();
                Post(delegate { AutostartDone(on, error); });
            });
        }

        private void AutostartDone(bool on, string error)
        {
            autostartBusy = false;
            autostart.Checked = on;
            autostart.Enabled = true;
            autostartNote = error ?? (on ? "Ligado: abre na bandeja ao entrar no Windows, sem pedir confirmação." : "Desligado.");
            Invalidate(startCard);
        }

        // ---- layout / paint ---------------------------------------------------------------

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // Called while the constructor is still adding controls (the alert switches come last).
            if (alertKinds[alertKinds.Length - 1] == null || updateCard == null || gestureCard == null || widgetCard == null) return;
            int x = Pad;
            int w = Width - Pad * 2;
            // Five cards in a fixed 860x526 window (page height 480): compact on purpose (the last is UpdateCard).
            int gap = Theme.S(8);
            startCard = new Rectangle(x, Top0, w, Theme.S(56));
            alertsCard = new Rectangle(x, startCard.Bottom + gap, w, Theme.S(88));
            envCard = new Rectangle(x, alertsCard.Bottom + gap, w, Theme.S(78));
            readOnlyCard = new Rectangle(x, envCard.Bottom + gap, w, Theme.S(56));
            gestureCard.SetBounds(x, Top0, w, Theme.S(GestureCard.BaseHeight));
            widgetCard.SetBounds(x, Top0, w, Theme.S(WidgetCard.BaseHeight));
            switcher.SetBounds(Width - Pad - Theme.S(252), Theme.S(8), Theme.S(252), Theme.S(28));
            updateCard.SetBounds(x, readOnlyCard.Bottom + gap, w, UpdateCard.PreferredHeight);
            uninstallSlot = new Rectangle(x + w - Theme.S(190), updateCard.Bottom + Theme.S(4), Theme.S(190), Theme.S(26));
            readOnlyToggle.SetBounds(readOnlyCard.Right - Theme.S(20) - Theme.S(46), readOnlyCard.Y + Theme.S(8), Theme.S(46), Theme.S(24));
            autostart.SetBounds(startCard.Right - Theme.S(20) - Theme.S(46), startCard.Y + Theme.S(10), Theme.S(46), Theme.S(24));
            alertMaster.SetBounds(alertsCard.Right - Theme.S(20) - Theme.S(46), alertsCard.Y + Theme.S(6), Theme.S(46), Theme.S(24));
            int cellW = (alertsCard.Width - Theme.S(40)) / alertKinds.Length;
            for (int i = 0; i < alertKinds.Length; i++)
            {
                int cx = alertsCard.X + Theme.S(20) + i * cellW;
                int cy = alertsCard.Y + Theme.S(58);
                alertKinds[i].SetBounds(cx, cy, Theme.S(38), Theme.S(22));
                alertLabelRects[i] = new Rectangle(cx + Theme.S(46), cy, cellW - Theme.S(52), Theme.S(22));
            }
            // The two environment buttons stack on the right of the card (the card is only 78 high).
            int bw = EnvButtonWidth();
            int bx = envCard.Right - Theme.S(20) - bw;
            redoButton.SetBounds(bx, envCard.Y + Theme.S(8), bw, Theme.S(28));
            undoButton.SetBounds(bx, redoButton.Bottom + Theme.S(6), bw, Theme.S(28));
            // The reserved slot of the footnote row (Geral view): the file note on the left, the uninstall button on the right.
            uninstallButton.SetBounds(uninstallSlot.X, uninstallSlot.Y, uninstallSlot.Width, uninstallSlot.Height);
            Invalidate();
        }

        private int EnvButtonWidth()
        {
            return Math.Min(Theme.S(230), Math.Max(Theme.S(170), Width / 4));
        }

        protected override void PaintPage(Graphics g)
        {
            if (view == 0) PaintGeneral(g);
        }

        private void PaintGeneral(Graphics g)
        {
            int r = Theme.S(10);
            int inX = Theme.S(20);

            Theme.FillRound(g, Theme.Panel, startCard, r);
            int tw = startCard.Width - inX * 2 - Theme.S(60);
            Theme.Draw(g, "Iniciar com o Windows", Theme.Font(11f, true), Theme.Text,
                new Rectangle(startCard.X + inX, startCard.Y + Theme.S(8), tw, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, autostartNote, Theme.Font(9f, false), Theme.Muted,
                new Rectangle(startCard.X + inX, startCard.Y + Theme.S(34), tw, Theme.S(22)), TextFormatFlags.EndEllipsis);

            Theme.FillRound(g, Theme.Panel, alertsCard, r);
            int aw = alertsCard.Width - inX * 2 - Theme.S(60);
            Theme.Draw(g, "Alertas na bandeja", Theme.Font(11f, true), Theme.Text,
                new Rectangle(alertsCard.X + inX, alertsCard.Y + Theme.S(6), aw, Theme.S(24)), TextFormatFlags.EndEllipsis);
            // one line, cut with an ellipsis at a word edge by the renderer: never clipped mid-glyph
            Theme.Draw(g, "Balão e ponto colorido no ícone, também em tela cheia. " + app.AlertSettingsModel.Summary(),
                Theme.Font(9f, false), Theme.Muted,
                new Rectangle(alertsCard.X + inX, alertsCard.Y + Theme.S(32), alertsCard.Width - inX * 2, Theme.S(20)),
                TextFormatFlags.EndEllipsis);
            for (int i = 0; i < alertKinds.Length; i++)
            {
                Theme.Draw(g, AlertLabels[i], Theme.Font(9f, false), alertKinds[i].Enabled ? Theme.Text : Theme.Dim,
                    alertLabelRects[i], TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            }

            Theme.FillRound(g, Theme.Panel, envCard, r);
            int btnW = EnvButtonWidth();
            int ew = envCard.Width - inX * 2 - btnW - Theme.S(16);
            Theme.Draw(g, "Teste de ambiente", Theme.Font(11f, true), Theme.Text,
                new Rectangle(envCard.X + inX, envCard.Y + Theme.S(6), ew, Theme.S(22)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, app.EnvStatusText, Theme.Font(10f, false), app.EnvStatusIsWarning ? Theme.Warn : Theme.Text,
                new Rectangle(envCard.X + inX, envCard.Y + Theme.S(28), ew, Theme.S(20)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, envNote ?? app.KeyStatusText, Theme.Font(9f, false), Theme.Muted,
                new Rectangle(envCard.X + inX, envCard.Y + Theme.S(50), ew, Theme.S(18)), TextFormatFlags.EndEllipsis);

            Theme.FillRound(g, Theme.Panel, readOnlyCard, r);
            int rw = readOnlyCard.Width - inX * 2 - Theme.S(60);
            Theme.Draw(g, SafetyText.SettingTitle, Theme.Font(11f, true), Theme.Text,
                new Rectangle(readOnlyCard.X + inX, readOnlyCard.Y + Theme.S(8), rw, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, SafetyText.SettingNote(app.ReadOnly, app.Config.DiagnosticReadOnly, app.ReadOnlyFromSwitch), Theme.Font(9f, false), Theme.Muted,
                new Rectangle(readOnlyCard.X + inX, readOnlyCard.Y + Theme.S(30), readOnlyCard.Width - inX * 2, Theme.S(26)), TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);

            Theme.Draw(g, "Configurações salvas em %APPDATA%\\SenseMonitor\\config.json", Theme.Font(8.5f, false), Theme.Dim,
                new Rectangle(Pad, updateCard.Bottom + Theme.S(8), Math.Max(0, uninstallSlot.X - Pad - Theme.S(8)), Theme.S(16)), TextFormatFlags.EndEllipsis);
        }

        private void Post(Action a)
        {
            if (ui == null) return;
            ui.Post(delegate
            {
                if (!IsDisposed) a();
            }, null);
        }
    }
}
