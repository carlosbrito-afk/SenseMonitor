using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // Modes page: one large button per mode the environment test confirmed (up to five).
    // Self-contained: the shell supplies delegates; call RefreshState() when the page is
    // shown and whenever the firmware mode may have changed.
    public class ModesPage : UserControl
    {
        public const string WarningText = "Este modo pode deixar o PC instável. Se isso acontecer, volte para Dia a dia.";
        public const string TurboWarningText = "O Turbo leva o notebook ao limite: muito mais barulho e calor. Use o carregador, com a bateria instalada. Se ficar instável, volte para Dia a dia.";
        public const string FailureText = "Não foi possível trocar o modo";

        private readonly Func<PerfMode?> getMode;
        private readonly Func<PerfMode, bool> setMode;
        private readonly Func<bool> perfWarningShown;
        private readonly Action markPerfWarningShown;

        // One card per mode the environment test confirmed (SetOffers); none when the notebook has not been tested.
        private readonly List<ModeCard> cards = new List<ModeCard>();
        private string offersNote;
        private readonly PlainLabel title;
        private readonly PlainLabel message;
        private readonly PlainLabel proof;
        private readonly PlainLabel caption;
        private readonly Font titleFont;
        private readonly Font messageFont;
        private bool busy;

        // Confirms the one-time Desempenho warning. Default: a modal OK/Cancel box.
        // Return true to proceed (and mark the warning as shown).
        public Func<string, bool> ConfirmWarning;

        // Non-null text = the cards are disabled and this is why (read-only mode). Null/absent = enabled.
        public Func<string> LockedReason;

        // Data under the cards (last teste-desempenho numbers per mode, proof of the last switch).
        // Null/absent = the cards show only their description.
        public Func<ModesView> ViewSource;

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

            proof = new PlainLabel();
            proof.Font = messageFont;
            proof.ForeColor = FanPageColors.Text;
            proof.Wrap = true;
            Controls.Add(proof);

            caption = new PlainLabel();
            caption.Font = messageFont;
            caption.ForeColor = FanPageColors.Mute;
            caption.Wrap = true;
            Controls.Add(caption);

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
            if (message == null) return;
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
            int n = cards.Count;
            if (n == 0) return;
            int cols = n <= 3 ? n : 3;
            int rows = (n + cols - 1) / cols;
            int cw = (w - gap * (cols - 1)) / cols;
            int ph = mh * 2 + S(2);
            // room below the cards for the proof of the last switch and the benchmark caption
            int reserve = ph * 2 + S(4) + S(14);
            int avail = ClientSize.Height - y - pad - reserve;
            int ch = Math.Min(S(190), Math.Max(S(96), (avail - gap * (rows - 1)) / rows));
            for (int i = 0; i < n; i++)
            {
                cards[i].SetBounds(pad + (i % cols) * (cw + gap), y + (i / cols) * (ch + gap), cw, ch);
            }
            y += rows * ch + (rows - 1) * gap + S(14);
            proof.SetBounds(pad, y, w, ph);
            y += ph + S(4);
            caption.SetBounds(pad, y, w, ph);
        }

        // The modes this notebook offers (confirmed by the environment test), in display order. note explains
        // why the list is empty (never tested, another BIOS, not an Acer, read-only mode).
        public void SetOffers(IList<PerfMode> offered, string note)
        {
            foreach (ModeCard c in cards)
            {
                Controls.Remove(c);
                c.Dispose();
            }
            cards.Clear();
            if (offered != null)
            {
                foreach (PerfMode m in offered)
                {
                    ModeCard c = new ModeCard(m, ModeCatalog.CardName(m), ModeCatalog.Description(m));
                    c.Click += OnCardClick;
                    cards.Add(c);
                    Controls.Add(c);
                }
            }
            offersNote = note;
            PerformLayout();
            RefreshState();
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
            string locked = Locked();
            foreach (ModeCard c in cards) c.Enabled = locked == null;
            string head = cur.HasValue ? "Modo atual: " + NameOf(cur.Value) : "Modo atual indisponível";
            if (cards.Count == 0) SetMessage(offersNote ?? "Nenhum modo liberado neste notebook.", FanPageColors.Warn);
            else if (locked != null) SetMessage(head + ". " + locked, FanPageColors.Warn);
            else if (cur.HasValue) SetMessage(head, FanPageColors.Sec);
            else SetMessage(head, FanPageColors.Mute);
            RefreshInsights();
        }

        // Benchmark numbers on the cards and the proof of the last switch; cheap, safe to call every tick.
        public void RefreshInsights()
        {
            ModesView v = null;
            Func<ModesView> src = ViewSource;
            if (src != null)
            {
                try { v = src(); }
                catch (Exception) { v = null; }
            }
            // each card shows ITS mode's line (the page has up to five cards, the view is keyed by mode)
            foreach (ModeCard c in cards) c.Detail = v == null ? null : v.LineFor(c.Mode);
            proof.Text = v == null ? "" : (v.EffectLine ?? "Última troca de modo: nenhuma nesta sessão.");
            caption.Text = v == null ? ""
                : "Teste de desempenho: " + v.Caption + (v.Caption == PerfScoreboard.NoMeasurement ? " (rode SenseMonitor.exe --teste-desempenho)" : "");
        }

        private static string NameOf(PerfMode m)
        {
            return ModeCatalog.CardName(m);
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
            if (!cards.Exists(delegate(ModeCard c) { return c.Mode == mode; }))
            {
                // Only the modes the environment test confirmed can be chosen (a stale tray menu, a hotkey).
                RefreshState();
                return;
            }
            if (Locked() != null)
            {
                RefreshState();
                return;
            }
            busy = true;
            try
            {
                // Desempenho warns once; Turbo warns EVERY time (it pushes the notebook to its limit).
                bool needsWarning = mode == PerfMode.Turbo || (mode == PerfMode.Desempenho && !perfWarningShown());
                if (needsWarning)
                {
                    string text = mode == PerfMode.Turbo ? TurboWarningText : WarningText;
                    bool go = ConfirmWarning != null ? ConfirmWarning(text) : DefaultConfirm(text);
                    if (!go)
                    {
                        RefreshState();
                        return;
                    }
                    if (mode == PerfMode.Desempenho) markPerfWarningShown();
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

        private string Locked()
        {
            Func<string> f = LockedReason;
            return f == null ? null : f();
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
            private readonly Font detailFont;
            private string detail;
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
                detailFont = FanPageColors.Ui(9f, FontStyle.Regular);
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
                    detailFont.Dispose();
                }
                base.Dispose(disposing);
            }

            // Measured data line (benchmark numbers), drawn under the description.
            public string Detail
            {
                get { return detail; }
                set { if (detail != value) { detail = value; Invalidate(); } }
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
                Color nameColor = Enabled ? FanPageColors.Text : FanPageColors.Mute;
                Color descColor = Enabled ? FanPageColors.Sec : FanPageColors.Mute;

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
                TextFormatFlags descFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(g, desc, descFont, new Rectangle(pad, y, w, dh), descColor, descFlags);
                if (!string.IsNullOrEmpty(detail))
                {
                    int used = Math.Min(dh, TextRenderer.MeasureText(g, desc, descFont, new Size(w, int.MaxValue), descFlags).Height);
                    int dy = y + used + S(8);
                    int remain = Height - dy - pad;
                    if (remain >= detailFont.Height)
                        TextRenderer.DrawText(g, detail, detailFont, new Rectangle(pad, dy, w, remain), Enabled ? FanPageColors.Text : FanPageColors.Mute, descFlags);
                }

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
