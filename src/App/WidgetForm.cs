using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;

namespace Forja.App
{
    // Paints the widget into any Graphics (the form below, and the screenshot harness). Sizes follow
    // the DPI given; the layout is in 96-DPI units. Mockup D colors, opaque, no glow.
    internal static class WidgetRenderer
    {
        public const int BaseWidth = 236;
        public const int BaseHeight = 168;

        public static Size SizeFor(int dpi)
        {
            return new Size(S(BaseWidth, dpi), S(BaseHeight, dpi));
        }

        private static int S(int v, int dpi)
        {
            return (int)Math.Round(v * dpi / 96.0);
        }

        private static Font F(float points, bool semibold, int dpi)
        {
            return new Font(semibold ? Palette.UiSemiboldFamily : Palette.UiFamily, points * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static void Paint(Graphics g, Size size, WidgetData d, int dpi)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            g.Clear(Palette.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen edge = new Pen(Palette.Line2, Math.Max(1f, dpi / 96f)))
            {
                g.DrawRectangle(edge, 0, 0, size.Width - 1, size.Height - 1);
            }
            g.SmoothingMode = SmoothingMode.None;
            int pad = S(14, dpi);
            int w = size.Width - pad * 2;
            using (Font small = F(8.5f, true, dpi))
            using (Font label = F(9f, false, dpi))
            using (Font value = F(10.5f, true, dpi))
            using (Font fanFont = F(9.5f, false, dpi))
            using (Font modeFont = F(9f, true, dpi))
            {
                // header: product + current mode
                int hy = S(8, dpi);
                int hh = S(20, dpi);
                int modeW = S(100, dpi);
                TextRenderer.DrawText(g, "SENSEMONITOR", small, new Rectangle(pad, hy, w - modeW, hh), Palette.Mute, flags);
                TextRenderer.DrawText(g, d.Mode, modeFont, new Rectangle(pad + w - modeW, hy, modeW, hh), Palette.Accent, flags | TextFormatFlags.Right);
                using (Pen line = new Pen(Palette.Line))
                {
                    g.DrawLine(line, pad, S(33, dpi), size.Width - pad, S(33, dpi));
                }

                int rowH = S(26, dpi);
                int y = S(38, dpi);
                int labelW = S(44, dpi);
                int tempW = S(70, dpi);
                // CPU and GPU: temperature (colored by its state, never color alone: the number is there) and usage
                Row(g, "CPU", d.CpuTemp, Palette.ForState(d.CpuTempState), d.CpuUsage, pad, y, w, rowH, labelW, tempW, label, value, flags);
                y += rowH;
                Row(g, "GPU", d.GpuTemp, Palette.ForState(d.GpuTempState), d.GpuUsage, pad, y, w, rowH, labelW, tempW, label, value, flags);
                y += rowH;
                TextRenderer.DrawText(g, "Vent.", label, new Rectangle(pad, y, labelW, rowH), Palette.Mute, flags);
                TextRenderer.DrawText(g, d.Fan, fanFont, new Rectangle(pad + labelW, y, w - labelW, rowH), Palette.Text, flags | TextFormatFlags.Right);
                y += rowH;
                TextRenderer.DrawText(g, "RAM", label, new Rectangle(pad, y, labelW, rowH), Palette.Mute, flags);
                TextRenderer.DrawText(g, d.Ram, fanFont, new Rectangle(pad + labelW, y, w - labelW, rowH), Palette.Text, flags | TextFormatFlags.Right);
                // memory bar
                int by = y + rowH + S(1, dpi);
                int bh = S(4, dpi);
                Rectangle track = new Rectangle(pad, by, w, bh);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, Palette.Line2, track, bh / 2);
                if (d.RamFraction.HasValue && d.RamFraction.Value > 0)
                {
                    int fw = Math.Max(bh, (int)Math.Round(w * Math.Min(1.0, d.RamFraction.Value)));
                    Color c = d.RamFraction.Value >= 0.9 ? Palette.Warn : Palette.Accent;
                    Theme.FillRound(g, c, new Rectangle(pad, by, fw, bh), bh / 2);
                }
            }
        }

        private static void Row(Graphics g, string name, string temp, Color tempColor, string usage, int x, int y, int w, int h,
            int labelW, int tempW, Font label, Font value, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, name, label, new Rectangle(x, y, labelW, h), Palette.Mute, flags);
            TextRenderer.DrawText(g, temp, value, new Rectangle(x + labelW, y, tempW, h), tempColor, flags);
            TextRenderer.DrawText(g, usage, value, new Rectangle(x + labelW + tempW, y, w - labelW - tempW, h), Palette.Text, flags | TextFormatFlags.Right);
        }
    }

    // The monitoring widget: a small borderless window you can drag anywhere (position remembered per
    // monitor), optionally always on top, translucent, and optionally click-through. It never takes
    // the focus, is not in Alt+Tab, and draws only its own pixels: nothing is injected into any game.
    // Fed by WidgetController at 1 Hz while visible. UI thread only.
    internal sealed class WidgetForm : Form
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int GWL_EXSTYLE = -20;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;
        private const uint LWA_ALPHA = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private WidgetData data = new WidgetData();
        private string shownSignature = "";
        private int dpi = 96;
        private int opacityPct = 90;
        private bool clickThrough;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem miTop;
        private readonly ToolStripMenuItem miThrough;
        private readonly ToolStripMenuItem[] miOpacity = new ToolStripMenuItem[4];
        private static readonly int[] OpacityChoices = { 40, 60, 80, 100 };

        // The window was dropped somewhere new (end of a drag, or moved back on screen).
        public event Action PositionCommitted;
        // The DPI changed (moved to a monitor with another scale) and the window was resized.
        public event Action Rescaled;
        public event Action HideRequested;
        public event Action<bool> AlwaysOnTopChosen;
        public event Action<bool> ClickThroughChosen;
        public event Action<int> OpacityChosen;

        public WidgetForm()
        {
            Text = Display.AppName + " (widget)";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Palette.Panel;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            menu.Renderer = new DarkMenuRenderer();
            miTop = new ToolStripMenuItem("Sempre visível", null, delegate { Raise(AlwaysOnTopChosen, !TopMost); });
            miThrough = new ToolStripMenuItem("Clique atravessa o widget", null, delegate { Raise(ClickThroughChosen, !clickThrough); });
            ToolStripMenuItem miOp = new ToolStripMenuItem("Opacidade");
            for (int i = 0; i < OpacityChoices.Length; i++)
            {
                int pct = OpacityChoices[i];
                miOpacity[i] = new ToolStripMenuItem(pct + " %", null, delegate { Raise(OpacityChosen, pct); });
                miOp.DropDownItems.Add(miOpacity[i]);
            }
            ToolStripDropDownMenu dd = miOp.DropDown as ToolStripDropDownMenu;
            if (dd != null)
            {
                dd.Renderer = new DarkMenuRenderer();
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
            }
            ToolStripMenuItem miHide = new ToolStripMenuItem("Esconder o widget", null, delegate { Action h = HideRequested; if (h != null) h(); });
            menu.Items.AddRange(new ToolStripItem[] { miTop, miThrough, miOp, new ToolStripSeparator(), miHide });
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Opening += delegate
            {
                miTop.Checked = TopMost;
                miThrough.Checked = clickThrough;
                for (int i = 0; i < OpacityChoices.Length; i++) miOpacity[i].Checked = OpacityChoices[i] == opacityPct;
            };
            ContextMenuStrip = menu;
            ClientSize = WidgetRenderer.SizeFor(96);
        }

        private static void Raise<T>(Action<T> h, T v)
        {
            if (h != null) h(v);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Layered (opacity, click-through), a tool window (not in Alt+Tab or the taskbar) and
                // never activated by a click.
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public int Dpi { get { return dpi; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { dpi = (int)GetDpiForWindow(Handle); }
            catch (Exception) { dpi = DeviceDpi; }
            if (dpi < 48) dpi = 96;
            ClientSize = WidgetRenderer.SizeFor(dpi);
            try
            {
                int round = 2; // DWMWCP_ROUND: Windows 11 rounded corners; ignored where unsupported
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (Exception) { }
            ApplyOpacity();
            ApplyClickThrough();
        }

        public int OpacityPct
        {
            get { return opacityPct; }
            set { opacityPct = value; if (IsHandleCreated) ApplyOpacity(); }
        }

        public bool ClickThrough
        {
            get { return clickThrough; }
            set { clickThrough = value; if (IsHandleCreated) ApplyClickThrough(); }
        }

        private void ApplyOpacity()
        {
            int pct = Math.Max(WidgetSettings.MinOpacity, Math.Min(WidgetSettings.MaxOpacity, opacityPct));
            SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(pct * 255 / 100.0), LWA_ALPHA);
        }

        private void ApplyClickThrough()
        {
            int ex = GetWindowLong32(Handle, GWL_EXSTYLE);
            int want = clickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            if (want != ex) SetWindowLong32(Handle, GWL_EXSTYLE, want);
        }

        // New numbers: repaint only when something on screen would change.
        public void SetData(WidgetData d)
        {
            if (d == null) return;
            string sig = d.CpuTemp + "|" + d.CpuUsage + "|" + d.GpuTemp + "|" + d.GpuUsage + "|" + d.Fan + "|" + d.Mode + "|" + d.Ram
                + "|" + d.CpuTempState + "|" + d.GpuTempState + "|" + (d.RamFraction.HasValue ? ((int)(d.RamFraction.Value * 200)).ToString() : "-");
            data = d;
            if (sig == shownSignature) return;
            shownSignature = sig;
            if (Visible) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            WidgetRenderer.Paint(e.Graphics, ClientSize, data, dpi);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero); // the system runs the drag
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                Action h = PositionCommitted;
                if (h != null) h();
            }
            else if (m.Msg == WM_DPICHANGED)
            {
                dpi = m.WParam.ToInt32() & 0xFFFF;
                int left = Marshal.ReadInt32(m.LParam, 0);
                int top = Marshal.ReadInt32(m.LParam, 4);
                Size s = WidgetRenderer.SizeFor(dpi);
                ClientSize = s;
                Location = new Point(left, top);
                Invalidate();
                Action r = Rescaled;
                if (r != null) r();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }
    }
}
