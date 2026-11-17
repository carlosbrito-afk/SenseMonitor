using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.App.Controls;
using Forja.Platform;

namespace Forja.App
{
    // "Saúde" page: blue screens, battery, disk and temperature history. Strictly read-only for the
    // system. Data comes from the four providers passed to the constructor and is loaded on
    // background threads (event log, powercfg and CIM can take seconds), then marshalled back.
    //
    // Providers (all called on thread-pool threads, each may take seconds):
    //   crashes: null means "could not read the log"; an empty list means no failures.
    //   battery: null means no battery or powercfg failed.
    //   disks:   null means the Storage provider could not be queried.
    //   temps:   DaySummary[] newest first (TempLogStore.Summaries).
    // Use HealthPage.CreateDefault(store) to wire the real providers.
    public class HealthPage : UserControl
    {
        public const int CrashDays = Stability.CrashWindowDays;
        public const int TempDays = 7;
        public static readonly string EmptyCrashText = "Nenhuma tela azul nos últimos " + CrashDays + " dias";
        private const int MaxCrashRows = 40;
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

        // The one palette (Controls.Palette), under the short names this page uses.
        private static class Pal
        {
            public static readonly Color Bg = Palette.Bg;
            public static readonly Color Panel = Palette.Panel;
            public static readonly Color Line = Palette.Line;
            public static readonly Color Text = Palette.Text;
            public static readonly Color Sec = Palette.Sec;
            public static readonly Color Mute = Palette.Mute;
            public static readonly Color Accent = Palette.Accent;
            public static readonly Color Warn = Palette.Warn;
            public static readonly Color Hot = Palette.Hot;
            public static readonly Color Ok = Palette.Ok;
            public static readonly Color Hover = Palette.Hover;
        }

        private enum LoadState
        {
            Loading,
            Ready,
            Failed
        }

        private readonly Func<List<CrashEntry>> crashProvider;
        private readonly Func<BatteryInfo> batteryProvider;
        private readonly Func<DiskInfo[]> diskProvider;
        private readonly Func<DaySummary[]> tempProvider;

        private LoadState crashState = LoadState.Loading;
        private LoadState batteryState = LoadState.Loading;
        private LoadState diskState = LoadState.Loading;
        private LoadState tempState = LoadState.Loading;
        private List<CrashEntry> crashes;
        private BatteryInfo battery;
        private DiskInfo[] disks;
        private DaySummary[] temps;

        private int generation;
        private int outstanding;
        private bool busy;
        private bool loadRequested;
        private DateTime lastLoaded = DateTime.MinValue;

        private readonly PlainLabel title;
        private readonly PlainLabel status;
        private readonly Button refresh;
        private readonly ScrollHost host;
        private readonly ContentView view;

        private int fontDpi;
        private Font fUi;
        private Font fSmall;
        private Font fCaption;
        private Font fMono;
        private Font fBig;
        private int hUi;
        private int hSmall;
        private int hMono;
        private int hBig;

        private static readonly TextFormatFlags MeasureFlags =
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        private static readonly TextFormatFlags DrawFlags =
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

        public HealthPage(Func<List<CrashEntry>> crashes, Func<BatteryInfo> battery, Func<DiskInfo[]> disks, Func<DaySummary[]> temps)
        {
            if (crashes == null) throw new ArgumentNullException("crashes");
            if (battery == null) throw new ArgumentNullException("battery");
            if (disks == null) throw new ArgumentNullException("disks");
            if (temps == null) throw new ArgumentNullException("temps");
            crashProvider = crashes;
            batteryProvider = battery;
            diskProvider = disks;
            tempProvider = temps;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Pal.Bg;
            ForeColor = Pal.Text;
            EnsureFonts(CurrentDpi());

            title = new PlainLabel();
            title.Text = "SAÚDE DO PC";
            title.ForeColor = Pal.Text;
            title.ThemePoints = 9f; // same heading face as the other pages
            title.ThemeSemibold = true;
            title.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(title);

            status = new PlainLabel();
            status.ForeColor = Pal.Sec;
            status.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(status);

            refresh = new Button();
            refresh.Text = "Atualizar";
            refresh.FlatStyle = FlatStyle.Flat;
            refresh.UseVisualStyleBackColor = false;
            refresh.BackColor = Pal.Panel;
            refresh.ForeColor = Pal.Accent;
            refresh.FlatAppearance.BorderColor = Pal.Line;
            refresh.FlatAppearance.BorderSize = 1;
            refresh.FlatAppearance.MouseOverBackColor = Pal.Hover;
            refresh.FlatAppearance.MouseDownBackColor = Pal.Hover;
            refresh.UseMnemonic = false;
            refresh.TabStop = true;
            refresh.Click += delegate { Reload(); };
            Controls.Add(refresh);

            host = new ScrollHost();
            host.BackColor = Pal.Bg;
            Controls.Add(host);

            view = new ContentView(this);
            view.Dock = DockStyle.Top;
            view.Height = 10;
            host.Controls.Add(view);

            ApplyFonts();
            UpdateHeader();
            Size = new Size(640, 480);
        }

        // Wires the real providers: System log (30 days), powercfg, CIM Storage and the temperature log.
        public static HealthPage CreateDefault(TempLogStore store)
        {
            if (store == null) store = new TempLogStore();
            return new HealthPage(
                delegate
                {
                    List<CrashEntry> list;
                    return CrashHistory.TryRead(CrashDays, out list) ? list : null;
                },
                delegate { return BatteryHealth.Read(); },
                delegate { return DiskHealth.Read(); },
                delegate { return store.Summaries(TempDays); });
        }

        public bool IsLoading
        {
            get { return busy; }
        }

        // Reloads every section in the background. Safe to call from the UI thread at any time;
        // ignored while a load is running. Before the handle exists the load starts with it.
        public void Reload()
        {
            if (!IsHandleCreated)
            {
                loadRequested = true;
                return;
            }
            if (busy) return;
            busy = true;
            outstanding = 4;
            int gen = ++generation;
            UpdateHeader();

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<CrashEntry> r = null;
                LoadState st = LoadState.Failed;
                try { r = crashProvider(); st = r == null ? LoadState.Failed : LoadState.Ready; }
                catch (Exception) { st = LoadState.Failed; }
                Post(delegate { if (gen == generation) { crashes = r; crashState = st; SectionDone(); } });
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                BatteryInfo r = null;
                LoadState st = LoadState.Failed;
                try { r = batteryProvider(); st = r == null ? LoadState.Failed : LoadState.Ready; }
                catch (Exception) { st = LoadState.Failed; }
                Post(delegate { if (gen == generation) { battery = r; batteryState = st; SectionDone(); } });
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                DiskInfo[] r = null;
                LoadState st = LoadState.Failed;
                try { r = diskProvider(); st = r == null ? LoadState.Failed : LoadState.Ready; }
                catch (Exception) { st = LoadState.Failed; }
                Post(delegate { if (gen == generation) { disks = r; diskState = st; SectionDone(); } });
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                DaySummary[] r = null;
                LoadState st = LoadState.Failed;
                try { r = tempProvider(); st = r == null ? LoadState.Failed : LoadState.Ready; }
                catch (Exception) { st = LoadState.Failed; }
                Post(delegate { if (gen == generation) { temps = r; tempState = st; SectionDone(); } });
            });
        }

        private void Post(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(a);
            }
            catch (InvalidOperationException)
            {
                // Includes ObjectDisposedException: the page went away while a load was running.
            }
        }

        private void SectionDone()
        {
            outstanding--;
            if (outstanding <= 0)
            {
                busy = false;
                lastLoaded = DateTime.Now;
            }
            UpdateHeader();
            view.Relayout();
            view.Invalidate();
        }

        private void UpdateHeader()
        {
            if (busy)
            {
                bool first = crashState == LoadState.Loading || batteryState == LoadState.Loading ||
                             diskState == LoadState.Loading || tempState == LoadState.Loading;
                status.Text = first ? "Carregando…" : "Atualizando…";
            }
            else if (lastLoaded != DateTime.MinValue)
            {
                status.Text = "Atualizado às " + lastLoaded.ToString("HH:mm");
            }
            else
            {
                status.Text = "";
            }
            refresh.Enabled = !busy;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (loadRequested || (lastLoaded == DateTime.MinValue && !busy))
            {
                loadRequested = false;
                // Defer one tick so the control is fully parented before results are marshalled.
                BeginInvoke(new Action(Reload));
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated && !busy && lastLoaded != DateTime.MinValue &&
                DateTime.Now - lastLoaded > StaleAfter)
            {
                Reload();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                generation++;
            }
            base.Dispose(disposing);
            if (disposing) DisposeFonts();
        }

        // ---- fonts and DPI -------------------------------------------------------------

        private int CurrentDpi()
        {
            int d = DeviceDpi;
            return d > 0 ? d : 96;
        }

        private int S(int v)
        {
            return (int)Math.Round(v * CurrentDpi() / 96.0);
        }

        private static string MonoFamily()
        {
            return Palette.MonoFamily;
        }

        private void DisposeFonts()
        {
            if (fUi != null) fUi.Dispose();
            if (fSmall != null) fSmall.Dispose();
            if (fCaption != null) fCaption.Dispose();
            if (fMono != null) fMono.Dispose();
            if (fBig != null) fBig.Dispose();
            fUi = fSmall = fCaption = fMono = fBig = null;
        }

        // Fonts are sized in pixels from the DPI so layout and text always scale together.
        private void EnsureFonts(int dpi)
        {
            if (fUi != null && fontDpi == dpi) return;
            Font oldUi = fUi, oldSmall = fSmall, oldCaption = fCaption, oldMono = fMono, oldBig = fBig;
            fontDpi = dpi;
            float k = dpi / 72f;
            fUi = new Font("Segoe UI", 9.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 8.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fCaption = new Font("Segoe UI", 8.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fMono = new Font(MonoFamily(), 9.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fBig = new Font(MonoFamily(), 24f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            hUi = LineH(fUi);
            hSmall = LineH(fSmall);
            hMono = LineH(fMono);
            hBig = LineH(fBig);
            if (oldUi != null) oldUi.Dispose();
            if (oldSmall != null) oldSmall.Dispose();
            if (oldCaption != null) oldCaption.Dispose();
            if (oldMono != null) oldMono.Dispose();
            if (oldBig != null) oldBig.Dispose();
        }

        private static int LineH(Font f)
        {
            return TextRenderer.MeasureText("Ag", f, new Size(int.MaxValue, int.MaxValue), MeasureFlags).Height;
        }

        private static int TextW(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), MeasureFlags).Width;
        }

        private void ApplyFonts()
        {
            status.Font = fSmall;
            refresh.Font = fUi;
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            RefreshMetrics();
        }

        private void RefreshMetrics()
        {
            EnsureFonts(CurrentDpi());
            ApplyFonts();
            PerformLayout();
            view.Relayout();
            view.Invalidate();
        }

        // ---- header layout ----------------------------------------------------------------

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (view == null) return;
            EnsureFonts(CurrentDpi());
            ApplyFonts();
            int pad = S(14);
            int bh = hUi + S(10);
            int bw = TextW(refresh.Text, fUi) + S(28);
            int headerH = bh + pad * 2 - S(4);
            int w = Math.Max(10, ClientSize.Width);
            // The title is top-aligned at the page gutter like every other heading; the status and
            // the button are centered on the title's text row.
            int headH = TextRenderer.MeasureText("Ag", Theme.Font(9f, true), new Size(int.MaxValue, int.MaxValue), MeasureFlags).Height;
            int rowMid = pad + headH / 2;
            // the button's right edge is the cards' right edge: the content width without the scrollbar
            int contentW = host.ClientSize.Width > S(100) && host.ClientSize.Width <= w ? host.ClientSize.Width : w;
            refresh.SetBounds(contentW - pad - bw, Math.Max(S(4), rowMid - bh / 2), bw, bh);
            int tw = TextW(title.Text, Theme.Font(9f, true)) + S(6);
            title.TextAlign = ContentAlignment.TopLeft;
            title.SetBounds(pad, pad, tw, headH);
            int sx = pad + tw + S(12);
            int sw = Math.Max(10, refresh.Left - S(12) - sx);
            status.SetBounds(sx, rowMid - hSmall / 2, sw, hSmall + S(2));
            host.SetBounds(0, headerH, w, Math.Max(10, ClientSize.Height - headerH));
        }

        // ---- scrolling container --------------------------------------------------------------

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        private class ScrollHost : Panel
        {
            public ScrollHost()
            {
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
                AutoScroll = true;
                DoubleBuffered = true;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try { SetWindowTheme(Handle, "DarkMode_Explorer", null); }
                catch (Exception) { }
            }

            // The wheel reaches the control under the cursor (the content view), which hands it
            // over here; hovering never takes the keyboard focus.
            public void ForwardWheel(MouseEventArgs e)
            {
                OnMouseWheel(e);
            }
        }

        // ---- content view ------------------------------------------------------------------------

        private class ContentView : Control
        {
            private readonly HealthPage owner;

            public ContentView(HealthPage owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Pal.Bg;
                AccessibleName = "Resumo de saúde do PC";
                AccessibleRole = AccessibleRole.Pane;
            }

            public void Relayout()
            {
                if (Width <= 0) return;
                owner.EnsureFonts(owner.CurrentDpi());
                int h = owner.Render(null, Width, false, Rectangle.Empty);
                if (Height != h) Height = h;
                if (Parent != null)
                {
                    ScrollableControl p = Parent as ScrollableControl;
                    if (p != null)
                    {
                        int before = p.ClientSize.Width;
                        p.AutoScrollMinSize = new Size(0, h);
                        // the scrollbar appeared or went away: the header follows the new content width
                        if (p.ClientSize.Width != before) owner.PerformLayout();
                    }
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                Relayout();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                ScrollHost h = Parent as ScrollHost;
                if (h != null)
                {
                    h.ForwardWheel(e);
                    HandledMouseEventArgs he = e as HandledMouseEventArgs;
                    if (he != null) he.Handled = true;
                    return;
                }
                base.OnMouseWheel(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                owner.EnsureFonts(owner.CurrentDpi());
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                owner.Render(e.Graphics, Width, true, e.ClipRectangle);
            }
        }

        // ---- rendering ------------------------------------------------------------------------------

        // Lays the page out for the given width and, when g is not null, paints it. Returns the content
        // height. Text is measured with TextRenderer and drawn with an end ellipsis, never wrapped.
        private int Render(Graphics g, int width, bool draw, Rectangle clip)
        {
            int pad = S(14);
            int cardPad = S(14);
            int gap = S(12);
            int cw = Math.Max(S(120), width - pad * 2);
            int y = pad;
            for (int i = 0; i < 4; i++)
            {
                Rectangle inner = new Rectangle(pad + cardPad, y + cardPad, Math.Max(10, cw - cardPad * 2), 0);
                int h = Section(i, null, inner, false);
                int cardH = h + cardPad * 2;
                if (draw && g != null)
                {
                    Rectangle card = new Rectangle(pad, y, cw, cardH);
                    if (clip.IsEmpty || clip.IntersectsWith(Rectangle.Inflate(card, 2, 2)))
                    {
                        DrawCard(g, card);
                        Section(i, g, inner, true);
                    }
                }
                y += cardH + gap;
            }
            return y - gap + pad;
        }

        private void DrawCard(Graphics g, Rectangle r)
        {
            int rad = S(6);
            Rectangle b = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (GraphicsPath path = RoundRect(b, rad))
            using (SolidBrush fill = new SolidBrush(Pal.Panel))
            using (Pen line = new Pen(Pal.Line, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(line, path);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private int Section(int index, Graphics g, Rectangle r, bool draw)
        {
            switch (index)
            {
                case 0: return SectionCrash(g, r, draw);
                case 1: return SectionBattery(g, r, draw);
                case 2: return SectionDisk(g, r, draw);
                default: return SectionTemp(g, r, draw);
            }
        }

        // Draws (when draw) or just measures one single-line text. Texts wider than the box get an
        // ellipsis.
        private void Txt(Graphics g, bool draw, string text, Font f, Color c, Rectangle box, bool right)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!draw) return;
            TextFormatFlags fl = DrawFlags | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(g, text, f, box, c, fl);
        }

        private void Dot(Graphics g, bool draw, Color c, int x, int cy)
        {
            if (!draw) return;
            int d = S(8);
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillEllipse(b, x, cy - d / 2, d, d);
            }
        }

        private void Hairline(Graphics g, bool draw, int x, int y, int w)
        {
            if (!draw) return;
            using (Pen p = new Pen(Pal.Line, 1f))
            {
                g.DrawLine(p, x, y, x + w, y);
            }
        }

        // Caption row of a card; returns the height consumed.
        private int Caption(Graphics g, bool draw, Rectangle r, string left, string right)
        {
            int h = hSmall + S(2);
            int rw = string.IsNullOrEmpty(right) ? 0 : Math.Min(r.Width / 2, TextW(right, fSmall) + S(2));
            Txt(g, draw, left, fCaption, Pal.Mute, new Rectangle(r.X, r.Y, Math.Max(10, r.Width - rw - S(12)), h), false);
            if (rw > 0) Txt(g, draw, right, fSmall, Pal.Mute, new Rectangle(r.Right - rw, r.Y, rw, h), true);
            return h + S(10);
        }

        // One plain message line (loading / failure / empty); returns its height.
        private int Message(Graphics g, bool draw, Rectangle r, int y, string text, Color c)
        {
            int h = hUi + S(4);
            Txt(g, draw, text, fUi, c, new Rectangle(r.X, y, r.Width, h), false);
            return h;
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + " · " + b;
        }

        // ---- section: blue screens ------------------------------------------------------------------------

        private int SectionCrash(Graphics g, Rectangle r, bool draw)
        {
            int y = r.Y;
            y += Caption(g, draw, r, "TELAS AZUIS", "últimos " + CrashDays + " dias");
            if (crashState == LoadState.Loading)
            {
                y += Message(g, draw, r, y, "Carregando…", Pal.Sec);
                return y - r.Y;
            }
            if (crashState == LoadState.Failed || crashes == null)
            {
                y += Message(g, draw, r, y, "Não foi possível ler o registro de eventos do Windows.", Pal.Warn);
                return y - r.Y;
            }

            int blue = 0;
            int noCode = 0;
            foreach (CrashEntry e in crashes)
            {
                if (e.Code.HasValue) blue++;
                else noCode++;
            }

            // Summary line: status dot, count, and the shutdowns without a code.
            int lineH = hUi + S(6);
            int dot = S(8);
            int tx = r.X + dot + S(8);
            Color dotColor = blue > 0 ? Pal.Warn : Pal.Ok;
            Dot(g, draw, dotColor, r.X, y + lineH / 2);
            string main = blue > 0 ? HealthFormat.BlueScreens(blue) + " nos últimos " + CrashDays + " dias" : EmptyCrashText;
            int mw = Math.Min(r.Right - tx, TextW(main, fUi) + S(2));
            Txt(g, draw, main, fUi, Pal.Text, new Rectangle(tx, y, mw, lineH), false);
            if (noCode > 0)
            {
                int sx = tx + mw + S(6);
                Txt(g, draw, "· " + HealthFormat.UnexpectedShutdowns(noCode), fUi, Pal.Sec,
                    new Rectangle(sx, y, Math.Max(10, r.Right - sx), lineH), false);
            }
            y += lineH;
            if (crashes.Count == 0) return y - r.Y;

            y += S(8);
            int colGap = S(12);
            int dateW = TextW("00/00 00:00", fMono);
            int codeW = TextW("0x1000007E", fMono);
            int dumpW = Math.Max(TextW("Minidump: sim", fSmall), TextW("Minidump: não", fSmall));
            int dateX = r.X;
            int codeX = dateX + dateW + colGap;
            int nameX = codeX + codeW + colGap;
            int dumpX = r.Right - dumpW;
            int nameW = Math.Max(10, r.Right - nameX);
            int l1 = hUi + S(2);
            int l2 = hSmall + S(2);
            int rowPad = S(6);
            int rowH = rowPad * 2 + l1 + l2;

            int shown = Math.Min(crashes.Count, MaxCrashRows);
            for (int i = 0; i < shown; i++)
            {
                CrashEntry e = crashes[i];
                Hairline(g, draw, r.X, y, r.Width);
                int ry = y + rowPad;
                Txt(g, draw, e.When.ToString("dd/MM HH:mm"), fMono, Pal.Text, new Rectangle(dateX, ry, dateW, l1), false);
                string yes = e.HasDump ? "Minidump: sim" : "Minidump: não";
                Txt(g, draw, yes, fSmall, e.HasDump ? Pal.Ok : Pal.Mute, new Rectangle(dumpX, ry + l1, dumpW, l2), true);
                if (e.Code.HasValue)
                {
                    CrashCodeInfo info = e.Info;
                    Txt(g, draw, e.CodeHex, fMono, Pal.Warn, new Rectangle(codeX, ry, codeW, l1), false);
                    string name = info.Symbol != null ? info.Symbol : "Código sem nome conhecido";
                    int nw = Math.Min(nameW, TextW(name, fUi) + S(2));
                    Txt(g, draw, name, fUi, Pal.Text, new Rectangle(nameX, ry, nw, l1), false);
                    if (info.Modified)
                    {
                        // Only drawn when it fits whole; the hex code above already shows the exact value.
                        string variant = "variante de " + CrashParse.FormatCode(e.Code.Value & 0x0FFFFFFFL);
                        int vx = nameX + nw + S(8);
                        if (TextW(variant, fSmall) <= r.Right - vx)
                        {
                            Txt(g, draw, variant, fSmall, Pal.Mute, new Rectangle(vx, ry, r.Right - vx, l1), false);
                        }
                    }
                    string hint = info.Hint;
                    Txt(g, draw, hint, fSmall, Pal.Mute, new Rectangle(codeX, ry + l1, Math.Max(10, dumpX - colGap - codeX), l2), false);
                }
                else
                {
                    Txt(g, draw, "Desligamento inesperado sem código", fUi, Pal.Sec,
                        new Rectangle(codeX, ry, Math.Max(10, r.Right - codeX), l1), false);
                    Txt(g, draw, "Sem código registrado: queda de energia ou travamento", fSmall, Pal.Mute,
                        new Rectangle(codeX, ry + l1, Math.Max(10, dumpX - colGap - codeX), l2), false);
                }
                y += rowH;
            }
            if (crashes.Count > shown)
            {
                Hairline(g, draw, r.X, y, r.Width);
                y += rowPad;
                int more = crashes.Count - shown;
                Txt(g, draw, "e mais " + more + " registro" + (more == 1 ? "" : "s") + " mais antigo" + (more == 1 ? "" : "s"),
                    fSmall, Pal.Mute, new Rectangle(r.X, y, r.Width, l2), false);
                y += l2;
            }
            return y - r.Y;
        }

        // ---- section: battery ----------------------------------------------------------------------------------

        private int SectionBattery(Graphics g, Rectangle r, bool draw)
        {
            int y = r.Y;
            y += Caption(g, draw, r, "BATERIA", null);
            if (batteryState == LoadState.Loading)
            {
                y += Message(g, draw, r, y, "Carregando…", Pal.Sec);
                return y - r.Y;
            }
            if (batteryState == LoadState.Failed || battery == null)
            {
                y += Message(g, draw, r, y, "Não foi possível ler a bateria (o PC pode estar sem bateria).", Pal.Warn);
                return y - r.Y;
            }

            double health = battery.HealthPct;
            Color hc = health >= 80.0 ? Pal.Ok : (health >= 60.0 ? Pal.Warn : Pal.Hot);
            string big = HealthFormat.Percent(health);
            string cap = "saúde da bateria";
            int leftW = Math.Max(TextW("100%", fBig), TextW(cap, fSmall)) + S(2);
            Txt(g, draw, big, fBig, hc, new Rectangle(r.X, y, leftW, hBig), false);
            Txt(g, draw, cap, fSmall, Pal.Mute, new Rectangle(r.X, y + hBig, leftW, hSmall + S(2)), false);
            int leftH = hBig + hSmall + S(2);

            string[] keys = { "Desgaste", "Ciclos de carga", "Capacidade de fábrica", "Capacidade atual" };
            string[] vals =
            {
                HealthFormat.Percent(battery.WearPct),
                battery.CycleCount.HasValue ? HealthFormat.Count(battery.CycleCount.Value) : HealthFormat.NotInformed,
                HealthFormat.Wh(battery.DesignMwh),
                HealthFormat.Wh(battery.FullChargeMwh)
            };
            int valW = 0;
            int keyW = 0;
            for (int i = 0; i < vals.Length; i++)
            {
                valW = Math.Max(valW, TextW(vals[i], ValFont(vals[i])));
                keyW = Math.Max(keyW, TextW(keys[i], fUi));
            }
            int gridW = keyW + S(24) + valW;
            int gx = r.X + leftW + S(32);
            int gy = y;
            if (gx + gridW > r.Right)
            {
                // Too narrow to sit beside the big number: stack the grid below it.
                gx = r.X;
                gy = y + leftH + S(10);
            }
            int rowH = hUi + S(6);
            int gw = Math.Max(10, r.Right - gx);
            for (int i = 0; i < keys.Length; i++)
            {
                int ry = gy + i * rowH;
                int kw = Math.Max(10, gw - valW - S(12));
                Txt(g, draw, keys[i], fUi, Pal.Sec, new Rectangle(gx, ry, kw, rowH), false);
                bool missing = vals[i] == HealthFormat.NotInformed;
                Txt(g, draw, vals[i], ValFont(vals[i]), missing ? Pal.Mute : Pal.Text, new Rectangle(gx + gw - valW, ry, valW, rowH), true);
            }
            int gridH = gy - y + rowH * keys.Length;
            return y - r.Y + Math.Max(leftH, gridH);
        }

        // ---- section: disk -----------------------------------------------------------------------------------------

        private int SectionDisk(Graphics g, Rectangle r, bool draw)
        {
            int y = r.Y;
            y += Caption(g, draw, r, "DISCO", null);
            if (diskState == LoadState.Loading)
            {
                y += Message(g, draw, r, y, "Carregando…", Pal.Sec);
                return y - r.Y;
            }
            if (diskState == LoadState.Failed || disks == null)
            {
                y += Message(g, draw, r, y, "Não foi possível consultar os discos.", Pal.Warn);
                return y - r.Y;
            }
            if (disks.Length == 0)
            {
                y += Message(g, draw, r, y, "Nenhum disco encontrado.", Pal.Sec);
                return y - r.Y;
            }

            for (int i = 0; i < disks.Length; i++)
            {
                DiskInfo d = disks[i];
                if (i > 0)
                {
                    y += S(10);
                    Hairline(g, draw, r.X, y, r.Width);
                    y += S(10);
                }

                // Name on the left, health on the right.
                Color hc = Pal.Mute;
                switch (d.Level)
                {
                    case DiskHealthLevel.Ok: hc = Pal.Ok; break;
                    case DiskHealthLevel.Warning: hc = Pal.Warn; break;
                    case DiskHealthLevel.Bad: hc = Pal.Hot; break;
                }
                int lineH = hUi + S(4);
                string health = d.HealthText;
                int dot = S(8);
                int hw = Math.Min(r.Width / 2, TextW(health, fUi) + S(2));
                int hx = r.Right - hw;
                Dot(g, draw, hc, hx - dot - S(8), y + lineH / 2);
                Txt(g, draw, health, fUi, hc, new Rectangle(hx, y, hw, lineH), true);
                int nameW = Math.Max(10, hx - dot - S(8) - S(12) - r.X);
                Txt(g, draw, d.Name, fUi, Pal.Text, new Rectangle(r.X, y, nameW, lineH), false);
                y += lineH;

                bool opKnown = !string.IsNullOrEmpty(d.OperationalText) && d.OperationalText != DiskMap.Unknown;
                string info = Join(Join(Join(d.MediaType, d.BusType), d.SizeBytes.HasValue ? HealthFormat.DiskSize(d.SizeBytes) : null),
                    opKnown ? "estado: " + d.OperationalText : null);
                if (!string.IsNullOrEmpty(info))
                {
                    Txt(g, draw, info, fSmall, Pal.Sec, new Rectangle(r.X, y, r.Width, hSmall + S(2)), false);
                    y += hSmall + S(2);
                }
                y += S(10);

                string[] keys = { "Temperatura", "Temp. máxima", "Desgaste", "Horas ligado", "Erros de leitura", "Erros de gravação" };
                string[] vals =
                {
                    d.TempC.HasValue ? HealthFormat.TempC(d.TempC.Value, HealthFormat.NotInformed) : HealthFormat.NotInformed,
                    d.TempMaxC.HasValue ? HealthFormat.TempC(d.TempMaxC.Value, HealthFormat.NotInformed) : HealthFormat.NotInformed,
                    HealthFormat.WearText(d.WearPct),
                    HealthFormat.Hours(d.PowerOnHours),
                    HealthFormat.Count(d.ReadErrors),
                    HealthFormat.Count(d.WriteErrors)
                };
                y += Cells(g, draw, r, y, keys, vals);

                if (!d.HasReliability)
                {
                    y += S(4);
                    Txt(g, draw, "Sem temperatura, desgaste ou erros informados (alguns dados exigem administrador).",
                        fSmall, Pal.Mute, new Rectangle(r.X, y, r.Width, hSmall + S(2)), false);
                    y += hSmall + S(2);
                }
            }
            return y - r.Y;
        }

        // Key/value cells flowed into as many columns as fit; returns the height used.
        private int Cells(Graphics g, bool draw, Rectangle r, int y, string[] keys, string[] vals)
        {
            int natural = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                natural = Math.Max(natural, Math.Max(TextW(keys[i], fSmall), TextW(vals[i], ValFont(vals[i]))));
            }
            natural += S(18);
            int cols = Math.Max(1, Math.Min(keys.Length, r.Width / natural));
            int cellW = r.Width / cols;
            int rows = (keys.Length + cols - 1) / cols;
            int keyH = hSmall + S(2);
            int valH = hMono + S(2);
            int cellH = keyH + valH + S(10);
            for (int i = 0; i < keys.Length; i++)
            {
                int cx = r.X + (i % cols) * cellW;
                int cy = y + (i / cols) * cellH;
                int tw = Math.Max(10, cellW - S(10));
                Txt(g, draw, keys[i], fSmall, Pal.Mute, new Rectangle(cx, cy, tw, keyH), false);
                bool missing = vals[i] == HealthFormat.NotInformed;
                Txt(g, draw, vals[i], ValFont(vals[i]), missing ? Pal.Mute : Pal.Text, new Rectangle(cx, cy + keyH, tw, valH), false);
            }
            return rows * cellH - S(10);
        }

        // ---- section: temperature -------------------------------------------------------------------------------

        private int SectionTemp(Graphics g, Rectangle r, bool draw)
        {
            int y = r.Y;
            y += Caption(g, draw, r, "TEMPERATURA", "últimos " + TempDays + " dias");
            if (tempState == LoadState.Loading)
            {
                y += Message(g, draw, r, y, "Carregando…", Pal.Sec);
                return y - r.Y;
            }
            if (tempState == LoadState.Failed || temps == null)
            {
                y += Message(g, draw, r, y, "Não foi possível ler o registro de temperatura.", Pal.Warn);
                return y - r.Y;
            }
            if (temps.Length == 0)
            {
                y += Message(g, draw, r, y, "Ainda sem registros. O SenseMonitor grava uma linha por minuto enquanto está aberto.", Pal.Sec);
                return y - r.Y;
            }

            string[] heads = { "Dia", "CPU máx", "CPU méd", "GPU máx", "GPU méd", "> 80 °C", "> 90 °C" };
            int n = temps.Length;
            string[][] cells = new string[n][];
            Color[][] colors = new Color[n][];
            for (int i = 0; i < n; i++)
            {
                DaySummary d = temps[i];
                cells[i] = new string[]
                {
                    HealthFormat.DayLabel(d.Day),
                    HealthFormat.TempC(d.CpuMax, "—"),
                    HealthFormat.TempC(d.CpuAvg, "—"),
                    HealthFormat.TempC(d.GpuMax, "—"),
                    HealthFormat.TempC(d.GpuAvg, "—"),
                    HealthFormat.Minutes(d.MinutesAbove80),
                    HealthFormat.Minutes(d.MinutesAbove90)
                };
                colors[i] = new Color[]
                {
                    Pal.Sec,
                    TempColor(d.CpuMax),
                    Pal.Text,
                    TempColor(d.GpuMax),
                    Pal.Text,
                    d.MinutesAbove80 > 0 ? Pal.Warn : Pal.Mute,
                    d.MinutesAbove90 > 0 ? Pal.Hot : Pal.Mute
                };
            }

            // Column widths: natural width from the header and the widest value, then the spare
            // room is shared out (or everything is squeezed in proportion when it does not fit).
            int colGap = S(14);
            int[] natural = new int[heads.Length];
            int naturalSum = 0;
            for (int c = 0; c < heads.Length; c++)
            {
                int m = TextW(heads[c], fSmall);
                for (int i = 0; i < n; i++)
                {
                    m = Math.Max(m, TextW(cells[i][c], c == 0 ? fUi : fMono));
                }
                natural[c] = m;
                naturalSum += m;
            }
            // Tighten the gaps first; only squeeze (ellipsis) when even that does not fit.
            if (naturalSum + colGap * heads.Length > r.Width) colGap = S(6);
            int[] w = new int[heads.Length];
            int total = 0;
            for (int c = 0; c < heads.Length; c++)
            {
                w[c] = natural[c] + colGap;
                total += w[c];
            }
            if (total > r.Width)
            {
                for (int c = 0; c < w.Length; c++) w[c] = Math.Max(S(20), (int)((long)w[c] * r.Width / total));
            }
            else
            {
                int extra = (r.Width - total) / w.Length;
                for (int c = 0; c < w.Length; c++) w[c] += extra;
            }
            int[] x = new int[w.Length];
            int acc = r.X;
            for (int c = 0; c < w.Length; c++)
            {
                x[c] = acc;
                acc += w[c];
            }

            int headH = hSmall + S(4);
            for (int c = 0; c < heads.Length; c++)
            {
                Rectangle box = new Rectangle(c == 0 ? x[c] : x[c] + colGap, y, Math.Max(10, w[c] - colGap), headH);
                Txt(g, draw, heads[c], fSmall, Pal.Mute, box, c != 0);
            }
            y += headH + S(4);

            int rowH = hUi + S(10);
            for (int i = 0; i < n; i++)
            {
                Hairline(g, draw, r.X, y, r.Width);
                for (int c = 0; c < heads.Length; c++)
                {
                    Rectangle box = new Rectangle(c == 0 ? x[c] : x[c] + colGap, y + S(1), Math.Max(10, w[c] - colGap), rowH - S(1));
                    Txt(g, draw, cells[i][c], c == 0 ? fUi : fMono, colors[i][c], box, c != 0);
                }
                y += rowH;
            }
            y += S(6);
            Txt(g, draw, "As colunas > 80 °C e > 90 °C contam os minutos com CPU ou GPU acima do limite.",
                fSmall, Pal.Mute, new Rectangle(r.X, y, r.Width, hSmall + S(2)), false);
            y += hSmall + S(2);
            return y - r.Y;
        }

        // Numbers use the mono face; the "não informado" placeholder uses the regular one.
        private Font ValFont(string value)
        {
            return value == HealthFormat.NotInformed ? fUi : fMono;
        }

        private static Color TempColor(double? c)
        {
            if (!c.HasValue) return Pal.Mute;
            if (c.Value > 90.0) return Pal.Hot;
            if (c.Value > 80.0) return Pal.Warn;
            return Pal.Text;
        }
    }
}
