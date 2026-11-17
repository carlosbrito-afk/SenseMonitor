using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // The report of one unexpected restart (Saúde → click on a row). Deliberately a plain dialog over
    // the Core model: the report is built off the UI thread (event log + files), shown as text, and
    // "Copiar relatório" puts the same text, scrubbed of user names and personal paths, on the clipboard.
    // Only the System log and the app's own files are read; nothing is changed.
    public sealed class FailureReportForm : Form
    {
        private readonly CrashEntry entry;
        private readonly TextBox box = new TextBox();
        private readonly Button copy = new Button();
        private readonly Button openDumps = new Button();
        private readonly Button close = new Button();
        private readonly Label note = new Label();
        private FailureReport report;
        private bool closed;

        public FailureReportForm(CrashEntry entry)
        {
            this.entry = entry;
            Text = "Relatório do reinício inesperado";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            ClientSize = new Size(Theme.S(720), Theme.S(520));

            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.WordWrap = true;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.Panel;
            box.ForeColor = Theme.Text;
            box.Font = new Font(Forja.App.Controls.Palette.MonoFamily, 9f);
            box.Text = "Montando o relatório…";
            box.AccessibleName = "Relatório";
            Controls.Add(box);

            note.ForeColor = Theme.Muted;
            note.BackColor = Theme.Background;
            note.AutoSize = false;
            note.Font = Theme.Font(9f, false);
            Controls.Add(note);

            StyleButton(copy, "Copiar relatório");
            copy.Enabled = false;
            copy.Click += delegate { CopyReport(); };
            StyleButton(openDumps, "Abrir pasta de despejos");
            openDumps.Click += delegate
            {
                note.Text = FailureReportReader.OpenDumpFolder() ? "" : "A pasta de despejos não existe neste PC (o Windows não gravou nenhum).";
            };
            StyleButton(close, "Fechar");
            close.Click += delegate { Close(); };
            CancelButton = close;
            Controls.Add(copy);
            Controls.Add(openDumps);
            Controls.Add(close);
            LayoutControls();
        }

        private static void StyleButton(Button b, string text)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = Theme.Panel;
            b.ForeColor = Theme.Accent;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.Hover;
            b.Font = Theme.Font(9.5f, false);
            b.UseMnemonic = false;
        }

        private void LayoutControls()
        {
            int pad = Theme.S(14);
            int bh = Theme.S(32);
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int y = h - pad - bh;
            copy.SetBounds(pad, y, Theme.S(150), bh);
            openDumps.SetBounds(pad + Theme.S(160), y, Theme.S(190), bh);
            close.SetBounds(w - pad - Theme.S(100), y, Theme.S(100), bh);
            note.SetBounds(pad, y - Theme.S(24), w - pad * 2, Theme.S(20));
            box.SetBounds(pad, pad, w - pad * 2, y - Theme.S(30) - pad);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ThreadPool.QueueUserWorkItem(delegate
            {
                FailureReport r = null;
                try { r = FailureReportReader.Build(entry); }
                catch (Exception ex) { Program.LogError(ex); }
                try
                {
                    if (!closed && IsHandleCreated) BeginInvoke(new Action(delegate { Loaded(r); }));
                }
                catch (InvalidOperationException)
                {
                    // the dialog was closed while the report was being built
                }
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closed = true;
            base.OnFormClosed(e);
            if (box.Font != null) box.Font.Dispose();
        }

        private void Loaded(FailureReport r)
        {
            if (closed || IsDisposed) return;
            report = r;
            if (r == null)
            {
                box.Text = "Não foi possível montar o relatório.";
                return;
            }
            box.Text = r.ToText(Scrub).Replace("\r\n", "\n").Replace("\n", "\r\n");
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            copy.Enabled = true;
        }

        private static string Scrub(string text)
        {
            return LogText.ScrubForCurrentUser(text);
        }

        private void CopyReport()
        {
            if (report == null) return;
            try
            {
                Clipboard.SetText(report.ToText(Scrub));
                note.Text = "Relatório copiado (sem nome de usuário nem caminhos pessoais).";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                note.Text = "Não foi possível copiar agora: a área de transferência está em uso.";
            }
        }
    }
}
