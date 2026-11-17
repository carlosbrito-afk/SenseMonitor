using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;

namespace Forja.App
{
    internal enum UninstallChoice
    {
        Cancel,
        // "Manter suas configurações?" Sim
        Keep,
        // Não: the settings and histories go too
        Erase
    }

    // The uninstall window, in the style of the installer: what will be removed, what the uninstaller does on the way
    // and the one question, "Manter suas configurações?" (Sim / Não). With --manter-dados the question is already
    // answered and the window only asks to confirm.
    internal sealed class UninstallForm : Form
    {
        internal const int BaseWidth = 700;
        internal const int BaseHeight = 600;
        private const int HeaderHeight = 56;
        private const int FooterHeight = 64;
        private const int WM_DPICHANGED = 0x02E0;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private readonly UninstallSummary summary;
        private readonly bool answered;
        private readonly PaintSurface header;
        private readonly PaintSurface body;
        private readonly GhostButton btnCancel;
        private readonly GhostButton btnNo;
        private readonly FlatButton btnYes;
        private UninstallChoice choice = UninstallChoice.Cancel;

        public UninstallForm(UninstallSummary summary, bool alreadyKeeping)
        {
            this.summary = summary;
            answered = alreadyKeeping;
            SuspendLayout();
            Text = "Desinstalar o SenseMonitor";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(BaseWidth), Theme.S(BaseHeight));
            header = new PaintSurface();
            header.Painter = PaintHeader;
            body = new PaintSurface();
            body.Painter = PaintBody;
            Controls.Add(header);
            Controls.Add(body);
            btnCancel = new GhostButton();
            btnCancel.Text = "Cancelar";
            btnCancel.Click += delegate { Finish(UninstallChoice.Cancel); };
            btnNo = new GhostButton();
            btnNo.Text = "Não";
            btnNo.Visible = !answered;
            btnNo.Click += delegate { Finish(UninstallChoice.Erase); };
            btnYes = new FlatButton();
            btnYes.Text = answered ? "Desinstalar" : "Sim";
            btnYes.Click += delegate { Finish(UninstallChoice.Keep); };
            Controls.Add(btnCancel);
            Controls.Add(btnNo);
            Controls.Add(btnYes);
            CancelButton = null;
            ResumeLayout(false);
        }

        public UninstallChoice Choice { get { return choice; } }

        // Shows the window and returns what the user chose (the screenshot harness drives the same form).
        public static UninstallChoice Ask(UninstallSummary summary, bool alreadyKeeping)
        {
            using (UninstallForm f = new UninstallForm(summary, alreadyKeeping))
            {
                f.ShowDialog();
                return f.Choice;
            }
        }

        private void Finish(UninstallChoice c)
        {
            choice = c;
            DialogResult = c == UninstallChoice.Cancel ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi = EnvWizardForm.ForceDpi > 0 ? EnvWizardForm.ForceDpi : 96;
            if (EnvWizardForm.ForceDpi <= 0)
            {
                try { dpi = (int)GetDpiForWindow(Handle); }
                catch (Exception) { dpi = DeviceDpi; }
            }
            Theme.SetDpi(dpi);
            ClientSize = new Size(Theme.S(BaseWidth), Theme.S(BaseHeight));
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (btnYes == null) return;
            Size cs = ClientSize;
            int headerH = Theme.S(HeaderHeight);
            int footerH = Theme.S(FooterHeight);
            header.SetBounds(0, 0, cs.Width, headerH);
            body.SetBounds(0, headerH, cs.Width, Math.Max(0, cs.Height - headerH - footerH));
            int bh = Theme.S(36);
            int fy = cs.Height - footerH + (footerH - bh) / 2;
            int pad = Theme.S(24);
            btnCancel.SetBounds(pad, fy, Theme.S(110), bh);
            int yesW = Theme.S(answered ? 150 : 110);
            btnYes.SetBounds(cs.Width - pad - yesW, fy, yesW, bh);
            btnNo.SetBounds(cs.Width - pad - yesW - Theme.S(12) - Theme.S(110), fy, Theme.S(110), bh);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED && EnvWizardForm.ForceDpi <= 0)
            {
                int dpi = m.WParam.ToInt32() & 0xFFFF;
                int left = Marshal.ReadInt32(m.LParam, 0);
                int top = Marshal.ReadInt32(m.LParam, 4);
                int right = Marshal.ReadInt32(m.LParam, 8);
                int bottom = Marshal.ReadInt32(m.LParam, 12);
                Theme.SetDpi(dpi);
                SuspendLayout();
                Bounds = new Rectangle(left, top, right - left, bottom - top);
                ClientSize = new Size(Theme.S(BaseWidth), Theme.S(BaseHeight));
                ResumeLayout(true);
                Invalidate(true);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private void PaintHeader(Graphics g, Rectangle r)
        {
            int pad = Theme.S(24);
            Theme.Draw(g, "DESINSTALAR O SENSEMONITOR", Theme.Font(9f, true), Theme.Text,
                new Rectangle(pad, Theme.S(18), r.Width - pad * 2, Theme.S(20)), TextFormatFlags.EndEllipsis);
            using (Pen p = new Pen(Palette.Line, 1))
            {
                g.DrawLine(p, 0, r.Height - 1, r.Width, r.Height - 1);
            }
        }

        private void PaintBody(Graphics g, Rectangle r)
        {
            int pad = Theme.S(24);
            int x = pad;
            int w = r.Width - pad * 2;
            int y = Theme.S(14);
            y += WizUi.Heading(g, x, y, w, "Remover o SenseMonitor deste computador?", "Confira o que será removido.");

            // what goes
            List<string> lines = summary.RemovedLines();
            int padIn = Theme.S(14);
            Font tf = Theme.Font(9f, true);
            Font lf = Theme.Font(9.5f, false);
            int textW = w - padIn * 2 - Theme.S(20);
            int h = padIn + tf.Height + Theme.S(8);
            int[] lh = new int[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                lh[i] = Math.Max(lf.Height, WizDraw.TextHeight(g, lines[i], lf, textW)) + Theme.S(5);
                h += lh[i];
            }
            h += padIn - Theme.S(5);
            WizDraw.Card(g, new Rectangle(x, y, w, h));
            int cy = y + padIn;
            Theme.Draw(g, "SERÁ REMOVIDO", tf, Theme.Muted, new Rectangle(x + padIn, cy, w - padIn * 2, tf.Height + 2), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            cy += tf.Height + Theme.S(8);
            for (int i = 0; i < lines.Count; i++)
            {
                int dot = Theme.S(5);
                Theme.FillRound(g, Palette.Mute, new Rectangle(x + padIn + Theme.S(2), cy + lf.Height / 2 - dot / 2, dot, dot), dot / 2);
                WizDraw.Para(g, lines[i], lf, Theme.Text, x + padIn + Theme.S(20), cy, textW);
                cy += lh[i];
            }
            y += h + Theme.S(10);

            // what the uninstaller also does
            foreach (string n in summary.Notes()) y += WizUi.Note(g, WizIcon.Ok, n, Theme.Text, x, y, w);
            y += Theme.S(6);

            // the question
            string q = UninstallSummary.QuestionText;
            string expl = answered ? summary.DataLine(true) : UninstallSummary.ExplanationText;
            Font qf = Theme.Font(11f, true);
            Font ef = Theme.Font(9.5f, false);
            int qh = padIn + qf.Height + Theme.S(6) + WizDraw.TextHeight(g, expl, ef, w - padIn * 2) + padIn;
            WizDraw.Card(g, new Rectangle(x, y, w, qh));
            WizDraw.Line(g, answered ? "Configurações e históricos" : q, qf, Theme.Text, x + padIn, y + padIn, w - padIn * 2);
            WizDraw.Para(g, expl, ef, Theme.Muted, x + padIn, y + padIn + qf.Height + Theme.S(6), w - padIn * 2);
        }
    }
}
