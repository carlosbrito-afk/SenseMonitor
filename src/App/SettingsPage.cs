using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Autostart toggle, stability (last blue screen from the Windows log) and hardware key
    // status. schtasks and the event log are slow, so they run on pool threads and post back.
    public sealed class SettingsPage : Page
    {
        private const int CrashWindowDays = Stability.CrashWindowDays;

        private readonly TrayApp app;
        private readonly SynchronizationContext ui;
        private readonly ToggleSwitch autostart = new ToggleSwitch();
        private Rectangle startCard;
        private Rectangle stabilityCard;
        private Rectangle keyCard;

        private string autostartNote = "verificando…";
        private bool autostartBusy;
        private string stabilityLine = "Verificando o registro de erros do Windows…";
        private bool checking;

        public SettingsPage(TrayApp app)
        {
            this.app = app;
            ui = SynchronizationContext.Current;
            Heading = "Configurações";
            Subheading = "Inicialização, estabilidade e tecla do teclado.";
            autostart.Enabled = false;
            autostart.AccessibleName = "Iniciar com o Windows";
            autostart.BackColor = Theme.Panel;
            autostart.UserToggled += OnAutostartToggled;
            Controls.Add(autostart);
        }

        public override void SetActive(bool active)
        {
            if (!active) return;
            QueryAutostart();
            CheckStability();
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

        // ---- stability ---------------------------------------------------------------------

        private void CheckStability()
        {
            if (checking) return;
            checking = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Blue screens only (entries with a bugcheck code), as on the Saúde page.
                List<CrashEntry> entries = null;
                bool ok = false;
                try { ok = CrashHistory.TryRead(CrashWindowDays, out entries); }
                catch (Exception) { ok = false; }
                List<DateTime> events = Stability.BlueScreenTimesUtc(entries);
                Post(delegate
                {
                    checking = false;
                    stabilityLine = Stability.LastCrashLine(ok, events, DateTime.UtcNow, TimeZoneInfo.Local);
                    Invalidate(stabilityCard);
                });
            });
        }

        // ---- layout / paint ---------------------------------------------------------------

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Pad;
            int w = Width - Pad * 2;
            int gap = Theme.S(12);
            startCard = new Rectangle(x, Top0, w, Theme.S(84));
            stabilityCard = new Rectangle(x, startCard.Bottom + gap, w, Theme.S(100));
            keyCard = new Rectangle(x, stabilityCard.Bottom + gap, w, Theme.S(76));
            autostart.SetBounds(startCard.Right - Theme.S(20) - Theme.S(46), startCard.Y + Theme.S(18), Theme.S(46), Theme.S(24));
            Invalidate();
        }

        protected override void PaintPage(Graphics g)
        {
            int r = Theme.S(10);
            int inX = Theme.S(20);

            Theme.FillRound(g, Theme.Panel, startCard, r);
            int tw = startCard.Width - inX * 2 - Theme.S(60);
            Theme.Draw(g, "Iniciar com o Windows", Theme.Font(11f, true), Theme.Text,
                new Rectangle(startCard.X + inX, startCard.Y + Theme.S(16), tw, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, autostartNote, Theme.Font(9f, false), Theme.Muted,
                new Rectangle(startCard.X + inX, startCard.Y + Theme.S(46), tw, Theme.S(22)), TextFormatFlags.EndEllipsis);

            Theme.FillRound(g, Theme.Panel, stabilityCard, r);
            int sw = stabilityCard.Width - inX * 2;
            Theme.Draw(g, "Estabilidade", Theme.Font(11f, true), Theme.Text,
                new Rectangle(stabilityCard.X + inX, stabilityCard.Y + Theme.S(16), sw, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, stabilityLine, Theme.Font(10f, false), Theme.Text,
                new Rectangle(stabilityCard.X + inX, stabilityCard.Y + Theme.S(44), sw, Theme.S(22)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, "Só telas azuis com código de erro (últimos " + CrashWindowDays + " dias); desligamentos inesperados aparecem em Saúde.",
                Theme.Font(9f, false), Theme.Muted,
                new Rectangle(stabilityCard.X + inX, stabilityCard.Y + Theme.S(70), sw, Theme.S(20)), TextFormatFlags.EndEllipsis);

            Theme.FillRound(g, Theme.Panel, keyCard, r);
            bool key = app.KeyAvailable;
            Theme.Draw(g, key ? "Tecla do teclado: ativa — abre esta janela" : "Tecla não disponível", Theme.Font(11f, true),
                key ? Theme.Text : Theme.Warn, new Rectangle(keyCard.X + inX, keyCard.Y + Theme.S(14), keyCard.Width - inX * 2, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, "Só essa tecla é observada; nenhuma outra tecla é registrada ou guardada.", Theme.Font(9f, false), Theme.Muted,
                new Rectangle(keyCard.X + inX, keyCard.Y + Theme.S(42), keyCard.Width - inX * 2, Theme.S(20)), TextFormatFlags.EndEllipsis);

            Theme.Draw(g, "Configurações salvas em %APPDATA%\\SenseMonitor\\config.json", Theme.Font(8.5f, false), Theme.Dim,
                new Rectangle(Pad, keyCard.Bottom + Theme.S(14), Width - Pad * 2, Theme.S(18)), TextFormatFlags.EndEllipsis);
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
