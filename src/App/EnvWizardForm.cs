using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // The host of the "Teste de ambiente": the window, the step indicator, the footer buttons, the worker
    // threads and the cancel path. It knows nothing about the individual steps (IWizardStep): the standalone
    // wizard gives it the six environment steps (EnvWizardSteps.Standard); an installer host gives it its own
    // "Bem-vindo" and "Instalando" steps in front of the same six and a "finished" handler that keeps the
    // EnvOutcome until the app is installed.
    //
    // Every step that writes to the firmware can be postponed ("Fazer depois": the capability stays locked and
    // the app reminds on its first open), and Cancel at any moment puts the fans back to Automático and the
    // profile to Dia a dia. Nothing is handed to the "finished" handler before the last step's primary button.
    //
    // Threading: this form lives on the UI thread. Blocking work runs on its own MTA threads (RunWorker) with
    // their own firmware objects (see EnvServices) and posts back.
    internal sealed class EnvWizardForm : Form, IWizardHost
    {
        public const int BaseWidth = 780;
        public const int BaseHeight = 620;
        internal const int HeaderHeight = 84;
        internal const int FooterHeight = 64;
        private const int WM_DPICHANGED = 0x02E0;

        // Harness / tests: when > 0 the form uses this DPI instead of the window's.
        internal static int ForceDpi { get { return forceDpi; } set { forceDpi = value; } }
        private static int forceDpi;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private readonly IEnvServices svc;
        private readonly EnvSession session;
        private readonly List<IWizardStep> all;
        private readonly Func<EnvOutcome, bool> onFinish;
        private readonly PaintSurface header;
        private readonly PaintSurface body;
        private readonly GhostButton btnCancel;
        private readonly GhostButton btnSkip;
        private readonly GhostButton btnA;
        private readonly GhostButton btnB;
        private readonly FlatButton btnNext;
        private readonly System.Windows.Forms.Timer tick;
        private readonly HashSet<IWizardStep> done = new HashSet<IWizardStep>();
        private readonly HashSet<IWizardStep> skipped = new HashSet<IWizardStep>();

        private int designWidth = BaseWidth;
        private string headerTitle = "TESTE DE AMBIENTE";
        private string cancelNote = "Cancelando: devolvendo as ventoinhas ao automático e o modo ao Dia a dia…";
        private string startKey;
        private bool showAllBeforeDetection;
        private IWizardStep current;
        private int workers;
        private bool cancelling;
        private bool closingForReal;
        private bool finishFailed;
        private bool aShown; // btnA.Visible is false while the form itself is not shown
        private double anim;
        private int animDir = 1;

        // steps: in order; each decides with Applies whether it is shown. onFinish gets the outcome when the
        // last step's primary button is pressed; true = it was kept/saved and the window closes (OK).
        public EnvWizardForm(IEnvServices services, IList<IWizardStep> steps, Func<EnvOutcome, bool> onFinish, string caption)
        {
            if (services == null) throw new ArgumentNullException("services");
            if (steps == null || steps.Count == 0) throw new ArgumentException("steps");
            svc = services;
            session = new EnvSession(services);
            all = new List<IWizardStep>(steps);
            this.onFinish = onFinish;
            SuspendLayout();
            Text = caption ?? ("Teste de ambiente — " + Display.AppName);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
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

            btnCancel = Ghost("Cancelar");
            btnCancel.Click += delegate { RequestCancel(); };
            btnSkip = Ghost("Fazer depois");
            btnSkip.Click += delegate { OnSkip(); };
            btnA = Ghost("");
            btnA.Click += delegate { if (!Busy && current != null) { current.ActionA(); Changed(); } };
            btnB = Ghost("");
            btnB.Click += delegate { if (!Busy && current != null) { current.ActionB(); Changed(); } };
            btnNext = new FlatButton();
            btnNext.Text = "Continuar";
            btnNext.Click += delegate { OnNext(); };
            Controls.Add(btnCancel);
            Controls.Add(btnSkip);
            Controls.Add(btnA);
            Controls.Add(btnB);
            Controls.Add(btnNext);

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 60;
            tick.Tick += delegate { OnTick(); };
            ResumeLayout(false);
            UpdateButtons();
        }

        // Hosts that put their own steps in front (the installer): the title in the header band, a wider window for the
        // longer step indicator, the step the window starts at (null = the first that applies) and what it says while
        // it is cancelling. Set before the window is shown.
        internal string HeaderTitle { get { return headerTitle; } set { headerTitle = value ?? ""; } }
        internal string CancelNote { get { return cancelNote; } set { cancelNote = value ?? ""; } }
        internal string StartKey { get { return startKey; } set { startKey = value; } }
        // Until the detection says which steps apply, the indicator lists every step (the installer's welcome screen
        // already shows the whole road: eight steps, not the three that need no detection).
        internal bool ShowAllBeforeDetection { get { return showAllBeforeDetection; } set { showAllBeforeDetection = value; } }

        internal int DesignWidth
        {
            get { return designWidth; }
            set
            {
                designWidth = value;
                ClientSize = new Size(Theme.S(designWidth), Theme.S(BaseHeight));
            }
        }

        private static GhostButton Ghost(string text)
        {
            GhostButton b = new GhostButton();
            b.Text = text;
            return b;
        }

        // ---- IWizardHost ----------------------------------------------------------------------------

        public EnvSession Session { get { return session; } }
        public bool Busy { get { return workers > 0 || session.Detecting; } }
        public bool Cancelling { get { return cancelling; } }
        public double Anim { get { return anim; } }
        public IWin32Window DialogOwner { get { return this; } }

        public void Post(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed) a();
                });
            }
            catch (Exception)
            {
            }
        }

        public void Changed()
        {
            UpdateButtons();
            body.Invalidate();
            header.Invalidate();
        }

        // The three below run after the click handler that asked for them has returned (the window may close, and the
        // handler still refreshes its buttons).

        // A step closes the window without the cancel path (the first-stage window after it started the elevated copy).
        public void CloseWindow()
        {
            Post(delegate { ForceClose(); });
        }

        // A step asks for the same thing as Cancelar (the installer's "Fechar" after a failed install).
        public void RequestCancelNow()
        {
            Post(delegate { RequestCancel(); });
        }

        // A step finishes the wizard now, as if its primary button had been the last one (the installer's "Concluir sem abrir").
        public void FinishNow()
        {
            Post(delegate
            {
                if (Busy || current == null) return;
                Finish();
                if (!closingForReal) Changed();
            });
        }

        public bool ConfirmOnUi(string text, string caption)
        {
            bool yes = false;
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    yes = MessageBox.Show(this, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                });
            }
            catch (Exception)
            {
                yes = false;
            }
            return yes;
        }

        public void RunWorker(string name, Action work, Action doneAction)
        {
            workers++;
            Thread t = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex) { Program.LogError(ex); }
                Post(delegate
                {
                    workers--;
                    if (doneAction != null) doneAction();
                    if (cancelling) FinishCancel();
                    Changed();
                });
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.EnvWizard." + name;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            Changed();
        }

        // ---- window plumbing -------------------------------------------------------------------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi = ForceDpi > 0 ? ForceDpi : 96;
            if (ForceDpi <= 0)
            {
                try { dpi = (int)GetDpiForWindow(Handle); }
                catch (Exception) { dpi = DeviceDpi; }
            }
            Theme.SetDpi(dpi);
            ClientSize = new Size(Theme.S(designWidth), Theme.S(BaseHeight));
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            tick.Start();
            IWizardStep first = null;
            if (startKey != null)
            {
                foreach (IWizardStep s in all)
                {
                    if (s.Key == startKey) { first = s; break; }
                }
            }
            GoTo(first ?? FirstVisible());
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (btnNext == null) return;
            Size cs = ClientSize;
            int headerH = Theme.S(HeaderHeight);
            int footerH = Theme.S(FooterHeight);
            header.SetBounds(0, 0, cs.Width, headerH);
            body.SetBounds(0, headerH, cs.Width, Math.Max(0, cs.Height - headerH - footerH));
            int bh = Theme.S(36);
            int fy = cs.Height - footerH + (footerH - bh) / 2;
            int pad = Theme.S(24);
            btnCancel.SetBounds(pad, fy, Theme.S(110), bh);
            int nextW = Theme.S(150);
            btnNext.SetBounds(cs.Width - pad - nextW, fy, nextW, bh);
            int skipW = Theme.S(150);
            btnSkip.SetBounds(cs.Width - pad - nextW - Theme.S(12) - skipW, fy, skipW, bh);
            int ay = headerH + body.Height - Theme.S(14) - bh;
            int aw = Math.Min(Theme.S(330), MeasureButton(btnA));
            btnA.SetBounds(pad, ay, aw, bh);
            int bw = Math.Min(Theme.S(330), MeasureButton(btnB));
            btnB.SetBounds(aShown ? pad + aw + Theme.S(12) : pad, ay, bw, bh);
        }

        private static int MeasureButton(GhostButton b)
        {
            return TextRenderer.MeasureText(b.Text ?? "", Theme.Font(9.5f, false), new Size(100000, 100000),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(40);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED && ForceDpi <= 0)
            {
                int dpi = m.WParam.ToInt32() & 0xFFFF;
                int left = Marshal.ReadInt32(m.LParam, 0);
                int top = Marshal.ReadInt32(m.LParam, 4);
                int right = Marshal.ReadInt32(m.LParam, 8);
                int bottom = Marshal.ReadInt32(m.LParam, 12);
                Theme.SetDpi(dpi);
                SuspendLayout();
                Bounds = new Rectangle(left, top, right - left, bottom - top);
                ClientSize = new Size(Theme.S(designWidth), Theme.S(BaseHeight));
                ResumeLayout(true);
                Invalidate(true);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingForReal)
            {
                // The window's X, Alt+F4 and Cancelar all mean the same: undo whatever a test changed.
                e.Cancel = true;
                RequestCancel();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Stop();
                tick.Dispose();
                if (current != null)
                {
                    try { current.Leave(); }
                    catch (Exception) { }
                }
            }
            base.Dispose(disposing);
        }

        // ---- navigation -------------------------------------------------------------------------------

        private List<IWizardStep> VisibleSteps()
        {
            List<IWizardStep> l = new List<IWizardStep>();
            foreach (IWizardStep s in all)
            {
                if (ReferenceEquals(s, current) || s.Applies(session) || (showAllBeforeDetection && !session.Detected)) l.Add(s);
            }
            return l;
        }

        private IWizardStep FirstVisible()
        {
            foreach (IWizardStep s in all)
            {
                if (s.Applies(session)) return s;
            }
            return all[0];
        }

        // The next step after the current one that applies (the last one stays).
        private IWizardStep NextStep()
        {
            int i = all.IndexOf(current);
            for (int j = i + 1; j < all.Count; j++)
            {
                if (all[j].Applies(session)) return all[j];
            }
            return current;
        }

        private void GoTo(IWizardStep s)
        {
            if (current != null && !ReferenceEquals(current, s))
            {
                try { current.Leave(); }
                catch (Exception) { }
            }
            current = s;
            try { s.Enter(this); }
            catch (Exception ex) { Program.LogError(ex); }
            PerformLayout();
            Changed();
        }

        // Harness: jumps straight to the step with this key (the detection must be done).
        internal void DebugGoTo(string key)
        {
            foreach (IWizardStep s in all)
            {
                if (s.Key == key)
                {
                    GoTo(s);
                    return;
                }
            }
        }

        // The app is exiting (or crashing): close without the cancel dialog; running work was already told to stop.
        internal void ForceClose()
        {
            closingForReal = true;
            Close();
        }

        // Harness: the Cancelar button.
        internal void DebugCancel()
        {
            RequestCancel();
        }

        // Harness: closes without the cancel path.
        internal void DebugClose()
        {
            ForceClose();
        }

        // Harness: presses a footer/action button ("a", "b", "next", "skip").
        internal void DebugClick(string which)
        {
            if (which == "a") { if (!Busy && current != null && btnA.Enabled) { current.ActionA(); Changed(); } }
            else if (which == "b") { if (!Busy && current != null && btnB.Enabled) { current.ActionB(); Changed(); } }
            else if (which == "next") OnNext();
            else if (which == "skip") OnSkip();
        }

        private void OnNext()
        {
            if (Busy || current == null) return;
            IWizardStep cur = current;
            bool advance;
            try { advance = cur.Next(); }
            catch (Exception ex) { Program.LogError(ex); advance = false; }
            if (!advance)
            {
                if (cur is SummaryStep) Finish();
                Changed();
                return;
            }
            done.Add(cur);
            GoTo(NextStep());
        }

        private void OnSkip()
        {
            if (Busy || current == null || !current.CanSkip) return;
            IWizardStep cur = current;
            skipped.Add(cur);
            cur.Skip();
            GoTo(NextStep());
        }

        private void Finish()
        {
            EnvOutcome o = session.ToOutcome();
            bool ok;
            try { ok = onFinish == null || onFinish(o); }
            catch (Exception ex) { Program.LogError(ex); ok = false; }
            finishFailed = !ok;
            session.SaveFailed = finishFailed;
            if (ok)
            {
                closingForReal = true;
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        // ---- cancel ---------------------------------------------------------------------------------------

        // Always safe: running work is asked to stop (it ends in Automático / Dia a dia by itself), then the
        // isolated writer makes sure of it, and nothing is handed to the finished handler.
        private void RequestCancel()
        {
            if (cancelling) return;
            cancelling = true;
            foreach (IWizardStep s in all)
            {
                try { s.Cancel(); }
                catch (Exception) { }
            }
            svc.CancelFanTest();
            svc.CancelModeTest();
            if (workers > 0)
            {
                Changed();
                return; // FinishCancel runs when the worker returns
            }
            FinishCancel();
        }

        private void FinishCancel()
        {
            if (workers > 0 || closingForReal) return;
            // The restore below can take a few seconds (firmware, services): paint the "Cancelando" note first.
            Changed();
            body.Update();
            header.Update();
            // Only what this run touched is put back (nothing at all when no writing step ever started).
            svc.RestoreTouched();
            svc.ReleaseFirmware();
            svc.Journal("teste de ambiente cancelado");
            closingForReal = true;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        // ---- buttons / tick ----------------------------------------------------------------------------------

        private void UpdateButtons()
        {
            if (btnNext == null) return;
            bool busy = Busy || cancelling;
            StepUi ui = current == null ? new StepUi() : current.Ui();
            btnNext.Text = ui.PrimaryText;
            btnNext.Enabled = !busy && ui.PrimaryEnabled;
            btnSkip.Visible = ui.SkipVisible && current != null && current.CanSkip;
            btnSkip.Text = ui.SkipText;
            btnSkip.Enabled = !busy && ui.SkipEnabled;
            aShown = ui.AVisible;
            btnA.Visible = ui.AVisible;
            btnA.Text = ui.AText;
            btnA.Enabled = !busy && ui.AEnabled;
            btnB.Visible = ui.BVisible;
            btnB.Text = ui.BText;
            btnB.Enabled = !busy && ui.BEnabled;
            btnCancel.Enabled = !cancelling;
            PerformLayout();
        }

        private void OnTick()
        {
            anim += 0.03 * animDir;
            if (anim >= 1) { anim = 1; animDir = -1; }
            if (anim <= 0) { anim = 0; animDir = 1; }
            if (current != null)
            {
                try { current.Tick(); }
                catch (Exception ex) { Program.LogError(ex); }
            }
            body.Invalidate();
        }

        // ---- drawing ---------------------------------------------------------------------------------------------

        private void PaintHeader(Graphics g, Rectangle r)
        {
            int pad = Theme.S(24);
            Theme.Draw(g, headerTitle, Theme.Font(9f, true), Theme.Text, new Rectangle(pad, Theme.S(14), r.Width / 2, Theme.S(20)), TextFormatFlags.EndEllipsis);
            List<IWizardStep> items = VisibleSteps();
            int idx = Math.Max(0, items.IndexOf(current));
            if (items.Count > 1)
            {
                Theme.Draw(g, "Passo " + (idx + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " de " + items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Theme.Font(9f, false), Theme.Muted, new Rectangle(r.Width / 2, Theme.S(14), r.Width / 2 - pad, Theme.S(20)),
                    TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            }

            Font lf = Theme.Font(9f, false);
            int d = Theme.S(18);
            int cy = Theme.S(56);
            int avail = r.Width - pad * 2;
            int total = 0;
            int[] lw = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                lw[i] = WizDraw.LineWidth(g, items[i].ShortName, lf);
                total += d + Theme.S(6) + lw[i];
            }
            // Too many steps for their names (the installer has up to eight): only the current step keeps its name.
            bool crowded = items.Count > 1 && total + (items.Count - 1) * Theme.S(12) > avail;
            if (crowded)
            {
                total = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    if (!ReferenceEquals(items[i], current)) lw[i] = -Theme.S(6);
                    total += d + Theme.S(6) + lw[i];
                }
            }
            int gap = items.Count > 1 ? Math.Max(Theme.S(12), (avail - total) / (items.Count - 1)) : 0;
            int x = pad;
            for (int i = 0; i < items.Count; i++)
            {
                IWizardStep s = items[i];
                bool isCurrent = ReferenceEquals(s, current);
                bool isSkipped = skipped.Contains(s);
                bool isDone = done.Contains(s) || (i < idx && !isSkipped);
                Rectangle c = new Rectangle(x, cy - d / 2, d, d);
                if (isCurrent)
                {
                    Theme.FillRound(g, Theme.Accent, c, d / 2);
                    Theme.FillRound(g, Theme.OnAccent, new Rectangle(c.X + d / 2 - Theme.S(3), c.Y + d / 2 - Theme.S(3), Theme.S(6), Theme.S(6)), Theme.S(3));
                }
                else if (isSkipped) WizDraw.Icon(g, WizIcon.Off, c);
                else if (isDone) WizDraw.Icon(g, WizIcon.Ok, c);
                else Theme.DrawRound(g, Forja.App.Controls.Palette.Line2, c, d / 2);
                if (lw[i] >= 0)
                    Theme.Draw(g, s.ShortName, lf, isCurrent ? Theme.Text : Theme.Muted,
                        new Rectangle(x + d + Theme.S(6), cy - Theme.S(10), lw[i] + Theme.S(4), Theme.S(20)), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                int endX = x + d + Theme.S(6) + lw[i];
                if (i < items.Count - 1)
                {
                    using (Pen p = new Pen(i < idx ? Forja.App.Controls.Palette.Line2 : Forja.App.Controls.Palette.Line, Math.Max(1, Theme.S(1))))
                    {
                        g.DrawLine(p, endX + Theme.S(6), cy, endX + gap - Theme.S(6), cy);
                    }
                }
                x = endX + gap;
            }
            using (Pen p = new Pen(Forja.App.Controls.Palette.Line, 1))
            {
                g.DrawLine(p, 0, r.Height - 1, r.Width, r.Height - 1);
            }
        }

        private void PaintBody(Graphics g, Rectangle r)
        {
            int pad = Theme.S(24);
            int w = r.Width - pad * 2;
            int y = Theme.S(14);
            int limit = r.Height - Theme.S(62);
            if (current != null) current.Paint(g, pad, y, w, limit);
            if (cancelling)
            {
                WizDraw.Para(g, cancelNote, Theme.Font(10f, true),
                    Forja.App.Controls.Palette.Warn, pad, limit - Theme.S(26), w);
            }
        }
    }
}
