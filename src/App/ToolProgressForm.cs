using System;
using System.Drawing;
using System.Windows.Forms;

namespace Forja.App
{
    // Progress window of the command-line test tools: status text, time left and a Cancel
    // button. The only ways to close it are Done() (the routine returned) and the routine
    // ending by itself, so the test is never abandoned half way by a stray click.
    internal sealed class ToolProgressForm : Form
    {
        private readonly Label status = new Label();
        private readonly Label clock = new Label();
        private readonly Button cancel = new Button();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly DateTime started = DateTime.UtcNow;
        private readonly Action onCancel;
        private volatile string text;
        private volatile int remaining = -1;
        private bool done;

        private readonly bool unattended;

        // Unattended (--auto): the window must not steal the focus from whoever is typing, stays
        // out of the taskbar and is never on top.
        protected override bool ShowWithoutActivation
        {
            get { return unattended; }
        }

        public ToolProgressForm(string caption, string initialText, Action onCancel, bool unattended)
        {
            this.unattended = unattended;
            bool topMost = !unattended;
            ShowInTaskbar = !unattended;
            this.onCancel = onCancel;
            text = initialText;
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = topMost;
            ClientSize = new Size(520, 150);
            Font = new Font("Segoe UI", 10f);

            status.SetBounds(12, 12, 496, 56);
            status.TextAlign = ContentAlignment.MiddleCenter;
            clock.SetBounds(12, 72, 496, 24);
            clock.TextAlign = ContentAlignment.MiddleCenter;
            cancel.Text = "Cancelar";
            cancel.SetBounds(200, 108, 120, 30);
            cancel.Click += delegate { Cancel(); };
            Controls.Add(status);
            Controls.Add(clock);
            Controls.Add(cancel);

            timer.Interval = 500;
            timer.Tick += delegate { Refresh2(); };
            timer.Start();
            Refresh2();
        }

        // Any thread.
        public void SetStatus(string line, int remainingSec)
        {
            text = line;
            remaining = remainingSec;
        }

        private void Cancel()
        {
            cancel.Enabled = false;
            cancel.Text = "Cancelando…";
            Action c = onCancel;
            if (c != null) c();
        }

        private void Refresh2()
        {
            status.Text = text + "\nNão feche nem suspenda o computador.";
            int s = (int)(DateTime.UtcNow - started).TotalSeconds;
            string t = "Decorrido: " + Mmss(s);
            int r = remaining;
            if (r >= 0) t += "   ·   Faltam cerca de " + Mmss(r);
            clock.Text = t;
        }

        private static string Mmss(int sec)
        {
            return (sec / 60) + ":" + (sec % 60).ToString("00");
        }

        // UI thread: the routine returned.
        public void Done()
        {
            done = true;
            timer.Stop();
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Final window with a table: monospaced, selectable and scrollable.
    internal static class ToolResultDialog
    {
        public static void Show(string caption, string text)
        {
            using (Form f = new Form())
            using (Font mono = new Font("Consolas", 9.5f))
            using (TextBox box = new TextBox())
            using (Button ok = new Button())
            {
                f.Text = caption;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(980, 640);
                f.MinimizeBox = false;
                f.ShowInTaskbar = true;
                box.Multiline = true;
                box.ReadOnly = true;
                box.ScrollBars = ScrollBars.Both;
                box.WordWrap = false;
                box.Font = mono;
                box.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                box.Dock = DockStyle.Fill;
                ok.Text = "OK";
                ok.Height = 36;
                ok.Dock = DockStyle.Bottom;
                ok.DialogResult = DialogResult.OK;
                f.AcceptButton = ok;
                f.Controls.Add(box);
                f.Controls.Add(ok);
                f.Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; };
                f.ShowDialog();
            }
        }
    }
}
