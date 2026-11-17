using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // Modes page: three large buttons (Silencioso / Dia a dia / Desempenho).
    // Self-contained: the shell supplies delegates; call RefreshState() when the page is
    // shown and whenever the firmware mode may have changed.
    public class ModesPage : UserControl
    {
        public const string WarningText = "Este modo pode deixar o PC instável. Se voltar a dar tela azul, volte para Dia a dia.";
        public const string FailureText = "Não foi possível trocar o modo";

        private readonly Func<PerfMode?> getMode;
        private readonly Func<PerfMode, bool> setMode;
        private readonly Func<bool> perfWarningShown;
        private readonly Action markPerfWarningShown;

        private readonly ModeCard[] cards = new ModeCard[3];
        private readonly PlainLabel title;
        private readonly PlainLabel message;
        private readonly Font titleFont;
        private readonly Font messageFont;
        private bool busy;

        // Confirms the one-time Desempenho warning. Default: a modal OK/Cancel box.
        // Return true to proceed (and mark the warning as shown).
        public Func<string, bool> ConfirmWarning;

        public ModesPage(Func<PerfMode?> getMode, Func<PerfMode, bool> setMode,
            Func<bool> perfWarningShown, Action markPerfWarningShown)
        {
            if (getMode == null) throw new ArgumentNullException("getMode");
            if (setMode == null) throw new ArgumentNullException("setMode");
            if (perfWarningShown == null) throw new ArgumentNullException("perfWarningShown");
            if (markPerfWarningShown == null) throw new ArgumentNullException("markPerfWarningShown");
            this.getMode = getMode;
            this.setMode = setMode;
            this.perfWarningShown = perfWarningShown;
            this.markPerfWarningShown = markPerfWarningShown;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = FanPageColors.Bg;
            ForeColor = FanPageColors.Text;
            titleFont = FanPageColors.Ui(9f, FontStyle.Bold);
            messageFont = FanPageColors.Ui(9.5f, FontStyle.Regular);

            title = new PlainLabel();
            title.Text = "MODO DE DESEMPENHO";
            title.Font = titleFont;
            title.ThemePoints = 9f; // same heading face as the other pages
            title.ThemeSemibold = true;
            title.ForeColor = Theme.Text;
            Controls.Add(title);

            message = new PlainLabel();
            message.Font = messageFont;
            message.ForeColor = FanPageColors.Sec;
            Controls.Add(message);

            string[] names = { "Silencioso", "Dia a dia", "Desempenho (Gaming)" };
            string[] descs =
            {
                "Ventoinhas baixas e menos ruído, para tarefas leves.",
                "Equilíbrio entre desempenho e ruído para o uso normal.",
                "Máximo desempenho, com mais ruído e calor."
            };
            PerfMode[] modes = { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };
            for (int i = 0; i < 3; i++)
            {
                ModeCard c = new ModeCard(modes[i], names[i], descs[i]);
                c.Click += OnCardClick;
                cards[i] = c;
                Controls.Add(c);
            }

            Padding = new Padding(0);
            Size = new Size(640, 420);
            RefreshState();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                messageFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private int S(int v)
        {
            return (int)Math.Round(v * DeviceDpi / 96.0);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // Called while the constructor is still adding controls.
            if (cards[2] == null) return;
            int pad = S(14);
            int w = Math.Max(10, ClientSize.Width - pad * 2);
            int y = pad;
            int th = TextRenderer.MeasureText("Ag", titleFont).Height + S(4);
            title.SetBounds(pad, y, w, th);
            y += th + S(4);
            int mh = TextRenderer.MeasureText("Ag", messageFont).Height + S(4);
            message.SetBounds(pad, y, w, mh);
            y += mh + S(14);

            int gap = S(12);
            int cw = (w - gap * 2) / 3;
            int ch = Math.Min(S(190), Math.Max(S(120), ClientSize.Height - y - pad));
            for (int i = 0; i < 3; i++)
            {
                cards[i].SetBounds(pad + i * (cw + gap), y, cw, ch);
            }
        }

        // Re-reads the firmware mode and repaints.
        public void RefreshState()
        {
            PerfMode? cur = null;
            try { cur = getMode(); }
            catch (Exception) { cur = null; }
            foreach (ModeCard c in cards)
            {
                c.Active = cur.HasValue && cur.Value == c.Mode;
            }
            if (cur.HasValue) SetMessage("Modo atual: " + NameOf(cur.Value), FanPageColors.Sec);
            else SetMessage("Modo atual indisponível", FanPageColors.Mute);
        }

        private static string NameOf(PerfMode m)
        {
            if (m == PerfMode.Silencioso) return "Silencioso";
            if (m == PerfMode.Desempenho) return "Desempenho (Gaming)";
            return "Dia a dia";
        }

        private void SetMessage(string text, Color color)
        {
            message.Text = text;
            message.ForeColor = color;
        }

        private void OnCardClick(object sender, EventArgs e)
        {
            ModeCard c = sender as ModeCard;
            if (c == null) return;
            Choose(c.Mode);
        }

        // Also callable from the shell (e.g. a tray menu): same rules as a click.
        public void Choose(PerfMode mode)
        {
            if (busy) return;
            busy = true;
            try
            {
                if (mode == PerfMode.Desempenho && !perfWarningShown())
                {
                    bool go = ConfirmWarning != null ? ConfirmWarning(WarningText) : DefaultConfirm(WarningText);
                    if (!go)
                    {
                        RefreshState();
                        return;
                    }
                    markPerfWarningShown();
                }
                bool ok;
                try { ok = setMode(mode); }
                catch (Exception) { ok = false; }
                RefreshState();
                if (!ok) SetMessage(FailureText, FanPageColors.Warn);
            }
            finally
            {
                busy = false;
            }
        }

        private bool DefaultConfirm(string text)
        {
            IWin32Window owner = FindForm();
            DialogResult r = MessageBox.Show(owner, text, "Modo Desempenho",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return r == DialogResult.OK;
        }

        // Large owner-drawn button for one performance mode.
        private class ModeCard : Control
        {
            public readonly PerfMode Mode;
            private readonly string name;
            private readonly string desc;
            private readonly Font nameFont;
            private readonly Font descFont;
            private readonly Font tagFont;
            private bool active;
            private bool hover;
            private bool pressed;

            public ModeCard(PerfMode mode, string name, string desc)
            {
                Mode = mode;
                this.name = name;
                this.desc = desc;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable
                    | ControlStyles.StandardClick, true);
                nameFont = FanPageColors.Ui(13f, FontStyle.Bold);
                descFont = FanPageColors.Ui(9.5f, FontStyle.Regular);
                tagFont = FanPageColors.Ui(8.5f, FontStyle.Bold);
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleName = name;
                AccessibleRole = AccessibleRole.PushButton;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    nameFont.Dispose();
                    descFont.Dispose();
                    tagFont.Dispose();
                }
                base.Dispose(disposing);
            }

            public bool Active
            {
                get { return active; }
                set { if (active != value) { active = value; Invalidate(); } }
            }

            private int S(int v)
            {
                return (int)Math.Round(v * DeviceDpi / 96.0);
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); }
                base.OnMouseDown(e);
            }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    e.Handled = true;
                    OnClick(EventArgs.Empty);
                    return;
                }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(FanPageColors.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rc = new Rectangle(0, 0, Width - 1, Height - 1);
                Color fill = FanPageColors.Panel;
                Color border = FanPageColors.Line;
                if (active) { fill = FanPageColors.AccentBg; border = FanPageColors.Accent; }
                else if (hover || pressed) border = FanPageColors.Line2;
                using (GraphicsPath path = Round(rc, S(6)))
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
                }

                TextFormatFlags tf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                int pad = S(16);
                int w = Width - pad * 2;
                Color nameColor = FanPageColors.Text;
                Color descColor = FanPageColors.Sec;

                int y = pad;
                // Tag row: "ATIVO" on the current mode.
                if (active)
                {
                    TextRenderer.DrawText(g, "ATIVO", tagFont, new Rectangle(pad, y, w, tagFont.Height + S(2)), FanPageColors.Accent,
                        tf | TextFormatFlags.SingleLine);
                }
                y += tagFont.Height + S(10);

                // One line, always: the name shrinks (down to 11 pt) to fit the card, then ellipsizes.
                Font nf = nameFont;
                bool ownFont = false;
                TextFormatFlags nameFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                for (float pt = 13f; TextRenderer.MeasureText(g, name, nf, new Size(int.MaxValue, 100), nameFlags).Width > w && pt > 11f; )
                {
                    pt -= 0.5f;
                    if (ownFont) nf.Dispose();
                    nf = FanPageColors.Ui(pt * nameFont.SizeInPoints / 13f, FontStyle.Bold);
                    ownFont = true;
                }
                int nh = TextRenderer.MeasureText(g, "Ag", nameFont, new Size(w, 100), nameFlags).Height;
                TextRenderer.DrawText(g, name, nf, new Rectangle(pad, y, w, nh), nameColor, tf | TextFormatFlags.SingleLine | TextFormatFlags.Bottom); // shared bottom edge, so a shrunk name keeps the baseline
                if (ownFont) nf.Dispose();
                y += nh + S(8);

                int dh = Math.Max(descFont.Height, Height - y - pad);
                TextRenderer.DrawText(g, desc, descFont, new Rectangle(pad, y, w, dh), descColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

                if (Focused && ShowFocusCues)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    using (Pen f = new Pen(FanPageColors.Accent, 2f))
                    {
                        g.DrawRectangle(f, 1, 1, Width - 3, Height - 3);
                    }
                }
            }

            private static GraphicsPath Round(Rectangle r, int radius)
            {
                int d = radius * 2;
                GraphicsPath p = new GraphicsPath();
                p.AddArc(r.Left, r.Top, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
