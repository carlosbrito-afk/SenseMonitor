using System;
using System.Drawing;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // The "Atualizações" card of Configurações: version, what the updater is doing, and the two
    // switches (automatic updates, tray balloon after updating).
    internal sealed class UpdateCard : Painted
    {
        private readonly TrayApp app;
        private readonly ToggleSwitch auto = new ToggleSwitch();
        private readonly ToggleSwitch balloon = new ToggleSwitch();
        private string shownStatus = "";

        public UpdateCard(TrayApp app)
        {
            this.app = app;
            BackColor = Theme.Panel;
            auto.AccessibleName = "Atualizar automaticamente";
            auto.BackColor = Theme.Panel;
            auto.Checked = app.Config.AutoUpdate;
            auto.UserToggled += delegate(bool want)
            {
                app.Config.AutoUpdate = want;
                app.SaveConfigQuietly();
                auto.Checked = want;
                RefreshStatus();
                Invalidate();
            };
            balloon.AccessibleName = "Avisar na bandeja quando atualizar";
            balloon.BackColor = Theme.Panel;
            balloon.Checked = app.Config.UpdateBalloon;
            balloon.UserToggled += delegate(bool want)
            {
                app.Config.UpdateBalloon = want;
                app.SaveConfigQuietly();
                balloon.Checked = want;
            };
            Controls.Add(auto);
            Controls.Add(balloon);
        }

        public static int PreferredHeight { get { return Theme.S(96); } }

        public void RefreshStatus()
        {
            string s = app.UpdateStatus ?? "";
            if (s == shownStatus) return;
            shownStatus = s;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int sw = Theme.S(46), sh = Theme.S(24);
            auto.SetBounds(Theme.S(20) + Theme.S(206), Theme.S(58), sw, sh);
            balloon.SetBounds(Width - Theme.S(20) - sw, Theme.S(58), sw, sh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            Rectangle card = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, Theme.Panel, card, Theme.S(10));
            int inX = Theme.S(20);
            int w = Width - inX * 2;
            Theme.Draw(g, "Atualizações", Theme.Font(11f, true), Theme.Text, new Rectangle(inX, Theme.S(8), w / 2, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, "Versão " + AppVersion.Text, Theme.Font(10f, false), Theme.Text,
                new Rectangle(inX + w / 2, Theme.S(10), w - w / 2, Theme.S(22)), TextFormatFlags.EndEllipsis | TextFormatFlags.Right);
            Theme.Draw(g, shownStatus.Length == 0 ? (app.UpdateStatus ?? "") : shownStatus, Theme.Font(9f, false), Theme.Muted,
                new Rectangle(inX, Theme.S(32), w, Theme.S(22)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, "Atualizar automaticamente", Theme.Font(10f, false), Theme.Text,
                new Rectangle(inX, Theme.S(60), Theme.S(200), Theme.S(22)), TextFormatFlags.EndEllipsis);
            int half = Width / 2;
            Theme.Draw(g, "Avisar na bandeja ao atualizar", Theme.Font(10f, false), Theme.Text,
                new Rectangle(half + Theme.S(10), Theme.S(60), half - Theme.S(20) - Theme.S(46) - Theme.S(30), Theme.S(22)), TextFormatFlags.EndEllipsis);
        }
    }
}
