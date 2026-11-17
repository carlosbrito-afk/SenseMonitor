using System;
using System.Drawing;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;

namespace Forja.App
{
    // What the two cards of Configurações (tecla e widget) need from the app. TrayApp implements it;
    // the screenshot harness uses a fake.
    internal interface IOverlayHost
    {
        bool KeyAvailable { get; }
        GestureSettings GestureSettingsCopy();
        void ApplyGestureSettings(GestureSettings s);
        // null while shortcuts are off or all registered; otherwise what went wrong.
        string HotkeyStatus { get; }
        WidgetSettings WidgetSettingsCopy();
        void ApplyWidgetSettings(WidgetSettings s);
        // The widget is wanted but hidden by a full-screen game / presentation.
        bool WidgetSuppressed { get; }
        // Raised on the UI thread when the widget was switched somewhere else (tray, key, widget menu).
        event Action OverlayChanged;
    }

    // A dropdown: current choice with a chevron; a click opens the dark menu with every option.
    internal sealed class PickerButton : Painted
    {
        private string[] options = new string[0];
        private int index;
        private bool hover;
        private bool pressed;

        public event Action<int> Changed;

        public PickerButton()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.ComboBox;
        }

        public string[] Options
        {
            get { return options; }
            set { options = value ?? new string[0]; Invalidate(); }
        }

        public int Index
        {
            get { return index; }
            set { if (index != value) { index = value; Invalidate(); } }
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Enabled) Open();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)) Open();
        }

        private void Open()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            for (int i = 0; i < options.Length; i++)
            {
                int chosen = i;
                ToolStripMenuItem it = new ToolStripMenuItem(options[i], null, delegate
                {
                    Action<int> h = Changed;
                    if (h != null) h(chosen);
                });
                it.Checked = i == index;
                m.Items.Add(it);
            }
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(this, new Point(0, Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Panel);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = !Enabled ? Palette.Bg : pressed ? Palette.Selected : hover ? Palette.Hover : Palette.Bg;
            Theme.FillRound(g, fill, r, Theme.S(6));
            Theme.DrawRound(g, Focused && ShowFocusCues ? Theme.Accent : Theme.Border, r, Theme.S(6));
            string t = index >= 0 && index < options.Length ? options[index] : "";
            int pad = Theme.S(10);
            int chev = Theme.S(22);
            Theme.Draw(g, t, Theme.Font(9.5f, false), Enabled ? Theme.Text : Theme.Dim,
                new Rectangle(pad, 0, Width - pad - chev, Height), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            // chevron
            int cx = Width - Theme.S(14);
            int cy = Height / 2;
            int a = Theme.S(4);
            using (Pen p = new Pen(Enabled ? Theme.Muted : Theme.Dim, Math.Max(1.5f, Theme.S(2) * 0.8f)))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(p, new Point[] { new Point(cx - a, cy - a / 2), new Point(cx, cy + a / 2), new Point(cx + a, cy - a / 2) });
                g.SmoothingMode = old;
            }
        }
    }

    // Small segmented switch for the views of a page (Geral / Tecla / Widget).
    internal sealed class PageSwitch : Painted
    {
        private string[] segments = new string[0];
        private int selected;
        private int hover = -1;

        public event Action<int> Changed;

        public PageSwitch()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PageTabList;
        }

        public string[] Segments
        {
            get { return segments; }
            set { segments = value ?? new string[0]; Invalidate(); }
        }

        public int Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        private int IndexAt(int x)
        {
            if (segments.Length == 0 || Width <= 0) return -1;
            int i = x * segments.Length / Width;
            return i < 0 || i >= segments.Length ? -1 : i;
        }

        private void Choose(int i)
        {
            if (i < 0 || i >= segments.Length || i == selected) return;
            selected = i;
            Invalidate();
            Action<int> h = Changed;
            if (h != null) h(i);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.X); if (i != hover) { hover = i; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Choose(IndexAt(e.X)); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Choose(selected - 1);
            else if (e.KeyCode == Keys.Right) Choose(selected + 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            if (segments.Length == 0) return;
            Rectangle all = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, Palette.Panel, all, Theme.S(8));
            Theme.DrawRound(g, Focused && ShowFocusCues ? Theme.Accent : Theme.Border, all, Theme.S(8));
            int inset = Theme.S(3);
            int segW = (Width - inset * 2) / segments.Length;
            for (int i = 0; i < segments.Length; i++)
            {
                Rectangle r = new Rectangle(inset + i * segW, inset, segW, Height - inset * 2 - 1);
                bool on = i == selected;
                if (on) Theme.FillRound(g, Palette.Selected, r, Theme.S(6));
                else if (i == hover) Theme.FillRound(g, Palette.Hover, r, Theme.S(6));
                Theme.Draw(g, segments[i], Theme.Font(9.5f, on), on ? Theme.Text : Theme.Muted, r,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    // "Tecla NitroSense": what 1, 2 and 3 taps (and an optional hold) do, global shortcuts, the message.
    internal sealed class GestureCard : Painted
    {
        public const int BaseHeight = 322;

        private readonly IOverlayHost host;
        private readonly PickerButton[] pickers = new PickerButton[4];
        private readonly ToggleSwitch longToggle = new ToggleSwitch();
        private readonly ToggleSwitch hotkeyToggle = new ToggleSwitch();
        private readonly ToggleSwitch osdToggle = new ToggleSwitch();
        private readonly string[] labels;
        private bool built; // Controls.Add lays the card out before the last picker exists
        private bool longOn;

        public GestureCard(IOverlayHost host)
        {
            this.host = host;
            BackColor = Theme.Background;
            labels = new string[KeyActions.All.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = KeyActions.Label(KeyActions.All[i]);
            for (int i = 0; i < pickers.Length; i++)
            {
                NitroGesture g = KeyActions.Gestures[i];
                PickerButton p = new PickerButton();
                p.BackColor = Theme.Panel;
                p.Options = labels;
                p.AccessibleName = "Ação de " + KeyActions.GestureLabel(g);
                p.Changed += delegate(int chosen)
                {
                    GestureSettings s = host.GestureSettingsCopy();
                    s.Set(g, KeyActions.All[chosen]);
                    host.ApplyGestureSettings(s);
                    Reload();
                };
                pickers[i] = p;
                Controls.Add(p);
            }
            Setup(longToggle, "Segurar a tecla (experimental)", delegate(bool want) { Change(delegate(GestureSettings s) { s.LongPressEnabled = want; }); });
            Setup(hotkeyToggle, "Atalhos de teclado globais", delegate(bool want) { Change(delegate(GestureSettings s) { s.HotkeysEnabled = want; }); });
            Setup(osdToggle, "Aviso na tela", delegate(bool want) { Change(delegate(GestureSettings s) { s.OsdEnabled = want; }); });
            built = true;
            Reload();
        }

        private void Setup(ToggleSwitch t, string name, Action<bool> handler)
        {
            t.AccessibleName = name;
            t.BackColor = Theme.Panel;
            t.UserToggled += handler;
            Controls.Add(t);
        }

        private void Change(Action<GestureSettings> edit)
        {
            GestureSettings s = host.GestureSettingsCopy();
            edit(s);
            host.ApplyGestureSettings(s);
            Reload();
        }

        // Shows what is saved.
        public void Reload()
        {
            GestureSettings s = host.GestureSettingsCopy();
            for (int i = 0; i < pickers.Length; i++)
            {
                pickers[i].Index = Array.IndexOf(KeyActions.All, s.Stored(KeyActions.Gestures[i]));
            }
            longToggle.Checked = s.LongPressEnabled;
            longOn = s.LongPressEnabled;
            pickers[3].Enabled = s.LongPressEnabled;
            hotkeyToggle.Checked = s.HotkeysEnabled;
            osdToggle.Checked = s.OsdEnabled;
            Invalidate();
        }

        private int Row(int i) { return Theme.S(60) + i * Theme.S(34); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!built) return;
            int inX = Theme.S(20);
            int sw = Theme.S(46);
            int sh = Theme.S(24);
            for (int i = 0; i < pickers.Length; i++)
            {
                pickers[i].SetBounds(inX + Theme.S(104), Row(i), Theme.S(250), Theme.S(28));
            }
            longToggle.SetBounds(Width - inX - sw, Row(3) + Theme.S(2), sw, sh);
            hotkeyToggle.SetBounds(Width - inX - sw, Theme.S(236), sw, sh);
            osdToggle.SetBounds(Width - inX - sw, Theme.S(280), sw, sh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            Theme.FillRound(g, Theme.Panel, new Rectangle(0, 0, Width, Height), Theme.S(10));
            int inX = Theme.S(20);
            int w = Width - inX * 2;
            bool key = host.KeyAvailable;
            Theme.Draw(g, "Tecla NitroSense", Theme.Font(11f, true), Theme.Text, new Rectangle(inX, Theme.S(8), w / 2, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, key ? "ativa" : "não disponível", Theme.Font(9f, true), key ? Palette.Ok : Theme.Warn,
                new Rectangle(inX + w / 2, Theme.S(10), w - w / 2, Theme.S(22)), TextFormatFlags.EndEllipsis | TextFormatFlags.Right);
            Theme.Draw(g, "Só essa tecla é observada; nenhuma outra tecla é registrada ou guardada.", Theme.Font(9f, false), Theme.Muted,
                new Rectangle(inX, Theme.S(32), w, Theme.S(20)), TextFormatFlags.EndEllipsis);
            for (int i = 0; i < pickers.Length; i++)
            {
                Theme.Draw(g, KeyActions.GestureLabel(KeyActions.Gestures[i]), Theme.Font(10f, false), pickers[i].Enabled ? Theme.Text : Theme.Dim,
                    new Rectangle(inX, Row(i), Theme.S(100), Theme.S(28)), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            }
            // beside the hold row: why it is off by default
            int noteX = pickers[3].Right + Theme.S(12);
            Theme.Draw(g, "experimental", Theme.Font(9f, false), Theme.Muted,
                new Rectangle(noteX, Row(3), longToggle.Left - noteX - Theme.S(8), Theme.S(28)), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            Theme.Draw(g, longOn
                ? "Segurar a tecla por cerca de 1 s executa a ação. Experimental: o “soltar” da tecla chega de forma irregular."
                : "A tecla avisa o “soltar” de forma irregular, por isso segurar vem desligado (segurar não faz nada).",
                Theme.Font(8.5f, false), Theme.Dim, new Rectangle(inX, Row(3) + Theme.S(32), w, Theme.S(18)), TextFormatFlags.EndEllipsis);
            using (Pen line = new Pen(Theme.Border))
            {
                int ly = Theme.S(226);
                g.DrawLine(line, inX, ly, Width - inX, ly);
            }
            int textW = w - Theme.S(46) - Theme.S(16);
            Theme.Draw(g, "Atalhos de teclado globais", Theme.Font(10f, false), Theme.Text,
                new Rectangle(inX, Theme.S(234), textW, Theme.S(20)), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            string status = host.HotkeyStatus;
            Theme.Draw(g, status ?? Hotkeys.ShortSummary, Theme.Font(8.5f, false), status != null ? Theme.Warn : Theme.Dim,
                new Rectangle(inX, Theme.S(254), textW, Theme.S(16)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, "Aviso na tela", Theme.Font(10f, false), Theme.Text,
                new Rectangle(inX, Theme.S(278), textW, Theme.S(28)), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }
    }

    // "Widget de monitoramento": show, always on top, click-through, opacity.
    internal sealed class WidgetCard : Painted
    {
        public const int BaseHeight = 270;

        private readonly IOverlayHost host;
        private readonly ToggleSwitch visible = new ToggleSwitch();
        private readonly ToggleSwitch top = new ToggleSwitch();
        private readonly ToggleSwitch through = new ToggleSwitch();
        private readonly PickerButton opacity = new PickerButton();
        private static readonly int[] Steps = { 30, 40, 50, 60, 70, 80, 90, 100 };

        public WidgetCard(IOverlayHost host)
        {
            this.host = host;
            BackColor = Theme.Background;
            string[] names = new string[Steps.Length];
            for (int i = 0; i < Steps.Length; i++) names[i] = Steps[i] + " %";
            opacity.Options = names;
            opacity.BackColor = Theme.Panel;
            opacity.AccessibleName = "Opacidade do widget";
            opacity.Changed += delegate(int chosen) { Change(delegate(WidgetSettings s) { s.OpacityPct = Steps[chosen]; }); };
            Controls.Add(opacity);
            Setup(visible, "Mostrar o widget", delegate(bool want) { Change(delegate(WidgetSettings s) { s.Visible = want; }); });
            Setup(top, "Widget sempre visível", delegate(bool want) { Change(delegate(WidgetSettings s) { s.AlwaysOnTop = want; }); });
            Setup(through, "Clique atravessa o widget", delegate(bool want) { Change(delegate(WidgetSettings s) { s.ClickThrough = want; }); });
            host.OverlayChanged += OnHostChanged;
            Reload();
        }

        private void Setup(ToggleSwitch t, string name, Action<bool> handler)
        {
            t.AccessibleName = name;
            t.BackColor = Theme.Panel;
            t.UserToggled += handler;
            Controls.Add(t);
        }

        private void OnHostChanged()
        {
            if (!IsDisposed) Reload();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) host.OverlayChanged -= OnHostChanged;
            base.Dispose(disposing);
        }

        private void Change(Action<WidgetSettings> edit)
        {
            WidgetSettings s = host.WidgetSettingsCopy();
            edit(s);
            host.ApplyWidgetSettings(s);
            Reload();
        }

        public void Reload()
        {
            WidgetSettings s = host.WidgetSettingsCopy();
            visible.Checked = s.Visible;
            top.Checked = s.AlwaysOnTop;
            through.Checked = s.ClickThrough;
            int best = 0;
            for (int i = 0; i < Steps.Length; i++)
            {
                if (Math.Abs(Steps[i] - s.OpacityPct) < Math.Abs(Steps[best] - s.OpacityPct)) best = i;
            }
            opacity.Index = best;
            Invalidate();
        }

        private int Row(int i) { return Theme.S(92) + i * Theme.S(36); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int inX = Theme.S(20);
            int sw = Theme.S(46);
            int sh = Theme.S(24);
            visible.SetBounds(Width - inX - sw, Row(0) + Theme.S(2), sw, sh);
            top.SetBounds(Width - inX - sw, Row(1) + Theme.S(2), sw, sh);
            through.SetBounds(Width - inX - sw, Row(2) + Theme.S(2), sw, sh);
            opacity.SetBounds(Width - inX - Theme.S(96), Row(3), Theme.S(96), Theme.S(28));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            Theme.FillRound(g, Theme.Panel, new Rectangle(0, 0, Width, Height), Theme.S(10));
            int inX = Theme.S(20);
            int w = Width - inX * 2;
            Theme.Draw(g, "Widget de monitoramento", Theme.Font(11f, true), Theme.Text, new Rectangle(inX, Theme.S(8), w, Theme.S(24)), TextFormatFlags.EndEllipsis);
            Theme.Draw(g, "Janela pequena com temperaturas, uso, ventoinha, modo e RAM. Arraste para onde quiser. Some sobre jogo em tela cheia exclusiva (Direct3D); em jogo em janela sem borda continua visível. Não mexe em nenhum jogo.",
                Theme.Font(9f, false), Theme.Muted, new Rectangle(inX, Theme.S(32), w, Theme.S(54)), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            int textW = w - Theme.S(46) - Theme.S(16);
            string[] names = { "Mostrar o widget", "Sempre visível", "Clique atravessa o widget", "Opacidade" };
            for (int i = 0; i < names.Length; i++)
            {
                Theme.Draw(g, names[i], Theme.Font(10f, false), Theme.Text,
                    new Rectangle(inX, Row(i), i == 3 ? w - Theme.S(110) : textW, Theme.S(28)), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            }
            string note = host.WidgetSuppressed ? "Escondido agora: um jogo em tela cheia está na frente." : "Com o clique atravessando, só dá para mexer aqui, na bandeja ou pelo gesto.";
            Theme.Draw(g, note, Theme.Font(8.5f, false), host.WidgetSuppressed ? Theme.Warn : Theme.Dim,
                new Rectangle(inX, Row(3) + Theme.S(38), w, Theme.S(32)), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }
}
