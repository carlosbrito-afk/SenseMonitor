using System;
using System.Drawing;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // The hand-off question with labelled buttons (a MessageBox cannot say "Instalar a versão antiga mesmo assim"). The words
    // and the default come from Core (HandOffPrompt). Enter and Esc never replace the install: Enter opens the installed
    // copy, Esc / the window's X cancel. Shown by the first stage (not elevated) and by the elevated route alike.
    internal sealed class HandOffDialog : Form
    {
        private HandOffAnswer answer = HandOffAnswer.Cancel;

        private HandOffDialog(HandOffPrompt prompt)
        {
            Text = "SenseMonitor";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(520, 190);

            Label text = new Label();
            text.Text = prompt.Text;
            text.AutoSize = false;
            text.Location = new Point(18, 16);
            text.Size = new Size(484, 112);
            Controls.Add(text);

            Button open = MakeButton(prompt.OpenText, HandOffAnswer.OpenInstalled);
            Button install = MakeButton(prompt.InstallText, HandOffAnswer.InstallThisFile);
            Button cancel = MakeButton(prompt.CancelText, HandOffAnswer.Cancel);
            int y = 140;
            // the long explicit button gets its own width so its text is never clipped
            open.SetBounds(18, y, 140, 32);
            install.SetBounds(166, y, 230, 32);
            cancel.SetBounds(404, y, 98, 32);
            Controls.Add(open);
            Controls.Add(install);
            Controls.Add(cancel);
            AcceptButton = open;
            CancelButton = cancel;
            ActiveControl = open;
        }

        private Button MakeButton(string text, HandOffAnswer result)
        {
            Button b = new Button();
            b.Text = text;
            b.UseVisualStyleBackColor = true;
            b.Click += delegate
            {
                answer = result;
                DialogResult = result == HandOffAnswer.Cancel ? DialogResult.Cancel : DialogResult.OK;
                Close();
            };
            return b;
        }

        internal static HandOffAnswer Ask(HandOffPrompt prompt)
        {
            using (HandOffDialog d = new HandOffDialog(prompt))
            {
                d.ShowDialog();
                return d.answer;
            }
        }
    }
}
