using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // "Manutencao" page: Inicializacao, Limpeza, Reparo do Windows and Historico.
    // Self-contained: the shell passes the services and shows the control. Everything that can
    // take time runs on the thread pool; results come back through BeginInvoke, so the window
    // never freezes. Nothing changes the system without a confirmation dialog that says exactly
    // what will happen.
    public class MaintenancePage : UserControl
    {
        // ---- confirmation hook (the default shows a dark dialog; tests can replace it) ----

        public class ConfirmRequest
        {
            public string Title;
            public List<string> Lines = new List<string>(); // an empty entry is a paragraph break
            public string ConfirmText = "Confirmar";
            public bool OfferRestorePoint;
            public bool RestorePointDefault = true;
        }

        public class ConfirmResult
        {
            public bool Accepted;
            public bool CreateRestorePoint;
        }

        public Func<ConfirmRequest, ConfirmResult> ConfirmHandler;

        private const int MaxHistoryRows = 50;

        private readonly IStartupService startup;
        private readonly ICleanupService cleanup;
        private readonly IRepairService repair;
        private readonly IRestorePointService restore;
        private readonly ChangeLog log;
        private readonly Skin skin;

        private readonly ScrollPanel scroller;
        private readonly Panel canvas;

        private readonly Card startupCard, cleanCard, repairCard, historyCard;

        // Inicializacao
        private readonly TextBlock startupTitle, startupDesc, startupStatus, tasksTitle, tasksStatus;
        private readonly RowList startupList, tasksList;
        private readonly DarkButton tasksToggle;
        // Limpeza
        private readonly TextBlock cleanTitle, cleanDesc, cleanStatus, cleanTotal, cleanResult;
        private readonly DarkButton analyzeBtn, cleanBtn, cleanCancelBtn;
        private readonly DarkProgress cleanProgress;
        private readonly RowList cleanList;
        // Reparo
        private readonly TextBlock repairTitle, repairDesc, repairPhase, repairLine, repairResultTitle, repairResultText;
        private readonly DarkButton repairBtn, repairCancelBtn;
        private readonly DarkProgress repairProgress;
        // Historico
        private readonly TextBlock histTitle, histDesc, histStatus;
        private readonly RowList histList;

        private readonly System.Windows.Forms.Timer elapsedTimer;

        private List<StartupItem> startupItems = new List<StartupItem>();
        private List<LogonTask> tasks = new List<LogonTask>();
        private bool showSystemTasks;
        private bool tasksLoaded;
        private bool startupRead;
        private CleanupPlan plan;
        private CancellationTokenSource cleanCts;
        private CancellationTokenSource repairCts;
        // Tool of the running repair step: DISM cannot be cancelled (never killed), SFC can.
        private RepairTool? repairTool;
        private string busyWhat;            // null when idle
        private bool loadedOnce;
        private bool inLayout;
        private DateTime repairStartedUtc;
        private int repairOverall;          // 0..100 across both steps
        private string repairPhaseText;

        public MaintenancePage(IStartupService startup, ICleanupService cleanup, IRepairService repair,
            IRestorePointService restore, ChangeLog log)
        {
            if (startup == null) throw new ArgumentNullException("startup");
            if (cleanup == null) throw new ArgumentNullException("cleanup");
            if (repair == null) throw new ArgumentNullException("repair");
            if (log == null) throw new ArgumentNullException("log");
            this.startup = startup;
            this.cleanup = cleanup;
            this.repair = repair;
            this.restore = restore;
            this.log = log;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = MaintColors.Bg;
            ForeColor = MaintColors.Text;
            skin = new Skin(DeviceDpi, DeviceDpi);

            scroller = new ScrollPanel();
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            scroller.BackColor = MaintColors.Bg;
            Controls.Add(scroller);
            canvas = new Panel();
            canvas.BackColor = MaintColors.Bg;
            scroller.Controls.Add(canvas);

            // Inicializacao
            startupCard = AddCard();
            startupTitle = AddText(startupCard, TextKind.Section, MaintColors.Text, false, "INICIALIZAÇÃO");
            startupDesc = AddText(startupCard, TextKind.Body, MaintColors.Sec, true,
                "Programas que abrem junto com o Windows. Desativar não apaga nada: o programa só deixa de abrir sozinho e você pode reativar aqui.");
            startupStatus = AddText(startupCard, TextKind.Body, MaintColors.Sec, true, "Lendo os itens de inicialização…");
            startupList = new RowList(skin, this);
            startupList.AccessibleName = "Programas de inicialização";
            startupList.RowActivated += OnStartupRow;
            startupCard.Controls.Add(startupList);
            tasksTitle = AddText(startupCard, TextKind.SmallBold, MaintColors.Mute, false, "TAREFAS AGENDADAS AO ENTRAR NO WINDOWS (SOMENTE LEITURA)");
            tasksStatus = AddText(startupCard, TextKind.Small, MaintColors.Mute, true, "Lendo as tarefas agendadas. Isso leva alguns segundos.");
            tasksList = new RowList(skin, this);
            tasksList.AccessibleName = "Tarefas agendadas ao entrar no Windows";
            startupCard.Controls.Add(tasksList);
            tasksToggle = new DarkButton(skin, this, "Mostrar também as do Windows", false);
            tasksToggle.Click += delegate { showSystemTasks = !showSystemTasks; BindTasks(); LayoutAll(); };
            startupCard.Controls.Add(tasksToggle);

            // Limpeza
            cleanCard = AddCard();
            cleanTitle = AddText(cleanCard, TextKind.Section, MaintColors.Text, false, "LIMPEZA");
            cleanDesc = AddText(cleanCard, TextKind.Body, MaintColors.Sec, true,
                "Apaga arquivos temporários, caches e logs antigos que o Windows e os programas deixam para trás. Nada é apagado antes de você ver o tamanho e confirmar. "
                + "Arquivos em uso e das últimas 24 horas ficam. A pasta de dumps de tela azul (Minidump) e seus documentos nunca são tocados.");
            analyzeBtn = new DarkButton(skin, this, "Analisar", true);
            analyzeBtn.Click += delegate { OnAnalyze(); };
            cleanCard.Controls.Add(analyzeBtn);
            cleanBtn = new DarkButton(skin, this, "Limpar selecionados…", false);
            cleanBtn.Click += delegate { OnCleanClick(); };
            cleanCard.Controls.Add(cleanBtn);
            cleanCancelBtn = new DarkButton(skin, this, "Cancelar", false);
            cleanCancelBtn.Click += delegate { CancelCleanup(); };
            cleanCard.Controls.Add(cleanCancelBtn);
            cleanStatus = AddText(cleanCard, TextKind.Body, MaintColors.Sec, true, "");
            cleanProgress = new DarkProgress(skin);
            cleanCard.Controls.Add(cleanProgress);
            cleanList = new RowList(skin, this);
            cleanList.AccessibleName = "Locais de limpeza";
            cleanList.RowActivated += OnCleanRow;
            cleanCard.Controls.Add(cleanList);
            cleanTotal = AddText(cleanCard, TextKind.Body, MaintColors.Text, true, "");
            cleanResult = AddText(cleanCard, TextKind.Body, MaintColors.Ok, true, "");

            // Reparo
            repairCard = AddCard();
            repairTitle = AddText(repairCard, TextKind.Section, MaintColors.Text, false, "REPARO DO WINDOWS");
            repairDesc = AddText(repairCard, TextKind.Body, MaintColors.Sec, true,
                "Verifica e conserta arquivos do sistema com as ferramentas do próprio Windows: primeiro o DISM, depois o SFC. "
                + "Leva de 10 a 40 minutos ou mais. Deixe o notebook na tomada e não o desligue durante o reparo. Pode cancelar a qualquer momento.");
            repairBtn = new DarkButton(skin, this, "Iniciar reparo…", true);
            repairBtn.Click += delegate { OnRepairClick(); };
            repairCard.Controls.Add(repairBtn);
            repairCancelBtn = new DarkButton(skin, this, "Cancelar", false);
            repairCancelBtn.Click += delegate { CancelRepair(); };
            repairCard.Controls.Add(repairCancelBtn);
            repairPhase = AddText(repairCard, TextKind.Body, MaintColors.Text, true, "");
            repairProgress = new DarkProgress(skin);
            repairCard.Controls.Add(repairProgress);
            repairLine = AddText(repairCard, TextKind.MonoSmall, MaintColors.Mute, false, "");
            repairResultTitle = AddText(repairCard, TextKind.UiBold, MaintColors.Ok, true, "");
            repairResultText = AddText(repairCard, TextKind.Body, MaintColors.Sec, true, "");

            // Historico
            historyCard = AddCard();
            histTitle = AddText(historyCard, TextKind.Section, MaintColors.Text, false, "HISTÓRICO");
            histDesc = AddText(historyCard, TextKind.Body, MaintColors.Sec, true,
                "Tudo o que o app alterou neste PC. Mudanças de inicialização podem ser desfeitas; limpeza e reparo não têm como voltar.");
            histStatus = AddText(historyCard, TextKind.Body, MaintColors.Sec, true, "");
            histList = new RowList(skin, this);
            histList.AccessibleName = "Histórico de alterações";
            histList.RowActivated += OnHistoryRow;
            historyCard.Controls.Add(histList);

            elapsedTimer = new System.Windows.Forms.Timer();
            elapsedTimer.Interval = 1000;
            elapsedTimer.Tick += delegate { UpdateRepairText(); };

            log.Changed += OnLogChanged;
            Size = new Size(760, 640);
            BindStartup();
            BindTasks();
            BindClean();
            BindHistory();
            UpdateControlsState();
            LayoutAll();
        }

        // ---- public API for the shell ----

        // True while a cleanup, analysis or repair runs. The shell should ask before exiting.
        public bool IsBusy
        {
            get { return busyWhat != null; }
        }

        // True while the repair is in its DISM step (exit leaves DISM running on its own).
        public bool DismRunning
        {
            get { return busyWhat == "reparo" && repairTool.HasValue && repairTool.Value == RepairTool.Dism; }
        }

        // Re-reads startup items and history. Call when the page becomes visible.
        public void RefreshState()
        {
            if (busyWhat == null) LoadStartup();
            BindHistory();
        }

        // Cancels whatever runs (used when the app exits).
        public void CancelAll()
        {
            CancelCleanup();
            CancelRepair();
        }

        // After CancelAll on exit: blocks the calling (UI) thread, without pumping messages, until
        // the cleanup/repair workers have returned (they only post results back, so they can
        // finish meanwhile) or the timeout passes. SFC is killed; DISM is left running on its own
        // (the runner returns at once). True = idle.
        public bool WaitForWorkers(int timeoutMs)
        {
            int t0 = Environment.TickCount;
            while (Interlocked.CompareExchange(ref workers, 0, 0) > 0)
            {
                if (unchecked(Environment.TickCount - t0) >= timeoutMs) return false;
                Thread.Sleep(50);
            }
            return true;
        }

        private int workers;

        // Long-running cancellable work (cleanup, repair): counted for WaitForWorkers.
        private void RunWorker(WaitCallback work)
        {
            Interlocked.Increment(ref workers);
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { work(state); }
                finally { Interlocked.Decrement(ref workers); }
            });
        }

        // ---- lifecycle ----

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!loadedOnce)
            {
                loadedOnce = true;
                LoadStartup();
            }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpi();
        }

        private void ApplyDpi()
        {
            skin.Apply(DeviceDpi, DeviceDpi);
            BindStartup();
            BindTasks();
            BindClean();
            BindHistory();
            LayoutAll();
            canvas.Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (canvas != null) LayoutAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                log.Changed -= OnLogChanged;
                try { if (cleanCts != null) cleanCts.Cancel(); }
                catch (Exception) { }
                try { if (repairCts != null) repairCts.Cancel(); }
                catch (Exception) { }
                elapsedTimer.Stop();
                elapsedTimer.Dispose();
                skin.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- small helpers ----

        private int S(int v)
        {
            return skin.S(v);
        }

        private Card AddCard()
        {
            Card c = new Card(skin);
            canvas.Controls.Add(c);
            return c;
        }

        private TextBlock AddText(Control parent, TextKind kind, Color color, bool wrap, string text)
        {
            TextBlock t = new TextBlock(skin, this, kind, color, wrap);
            t.Text = text;
            parent.Controls.Add(t);
            return t;
        }

        // The history file could not be saved: the change happened, but the user must know.
        private string WithSaveWarning(string text)
        {
            string w = log.SaveWarning;
            return w == null ? text : text + " " + w;
        }

        private bool HasSaveWarning
        {
            get { return log.SaveWarning != null; }
        }

        private void Post(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) a(); });
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void SetText(TextBlock b, string text, Color? color)
        {
            if (color.HasValue) b.ForeColor = color.Value;
            if (b.Text == text) return;
            b.Text = text ?? "";
            LayoutAll();
        }

        private void ScrollBy(int wheelDelta)
        {
            Point p = scroller.AutoScrollPosition;
            int step = S(60) * (wheelDelta > 0 ? -1 : 1) * Math.Max(1, Math.Abs(wheelDelta) / 120);
            scroller.AutoScrollPosition = new Point(-p.X, -p.Y + step);
        }

        // ---- layout ----

        private int Put(Control c, int x, int y, int w)
        {
            TextBlock t = c as TextBlock;
            bool show = t == null || t.Text.Length > 0;
            if (c.Visible != show) c.Visible = show;
            if (!show) return y;
            int h = t != null ? t.MeasureHeight(w) : c.Height;
            c.SetBounds(x, y, w, h);
            return y + h;
        }

        // Buttons flow left to right and wrap when the row is full. Returns the new y.
        private int PutButtons(int x, int y, int w, params DarkButton[] buttons)
        {
            int cx = x;
            int rowH = S(32);
            bool any = false;
            foreach (DarkButton b in buttons)
            {
                if (!b.Visible) continue;
                int bw = Math.Min(w, b.PreferredWidth());
                if (cx > x && cx + bw > x + w)
                {
                    cx = x;
                    y += rowH + S(8);
                }
                b.SetBounds(cx, y, bw, rowH);
                cx += bw + S(8);
                any = true;
            }
            return any ? y + rowH : y;
        }

        private void LayoutAll()
        {
            if (inLayout || canvas == null || skin == null) return;
            inLayout = true;
            try
            {
                int total = 0;
                bool needV = false;
                for (int pass = 0; pass < 2; pass++)
                {
                    int vbar = needV && !scroller.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
                    int cw = Math.Max(S(300), scroller.ClientSize.Width - vbar);
                    total = DoLayout(cw);
                    bool nowNeedV = total > scroller.ClientSize.Height;
                    if (nowNeedV == needV) break;
                    needV = nowNeedV;
                }
            }
            finally
            {
                inLayout = false;
            }
        }

        private int DoLayout(int canvasWidth)
        {
            int pad = S(14); // the same gutter as every other page
            int w = Math.Max(S(268), canvasWidth - pad * 2);
            int y = pad;
            y = LayoutStartup(pad, y, w) + S(14);
            y = LayoutClean(pad, y, w) + S(14);
            y = LayoutRepair(pad, y, w) + S(14);
            y = LayoutHistory(pad, y, w) + S(8);
            canvas.Size = new Size(canvasWidth, y);
            return y;
        }

        private int LayoutStartup(int x, int y, int w)
        {
            int ix = S(16);
            int iw = w - ix * 2;
            int cy = S(14);
            cy = Put(startupTitle, ix, cy, iw) + S(4);
            cy = Put(startupDesc, ix, cy, iw) + S(8);
            cy = Put(startupStatus, ix, cy, iw);
            cy += startupStatus.Visible ? S(10) : S(2);
            startupList.Visible = startupList.Rows.Count > 0;
            if (startupList.Visible)
            {
                int h = startupList.PreferredHeight(w);
                startupList.SetBounds(0, cy, w, h);
                cy += h + S(14);
            }
            cy = Put(tasksTitle, ix, cy, iw) + S(4);
            cy = Put(tasksStatus, ix, cy, iw);
            cy += tasksStatus.Visible ? S(8) : S(2);
            tasksList.Visible = tasksList.Rows.Count > 0;
            if (tasksList.Visible)
            {
                int h = tasksList.PreferredHeight(w);
                tasksList.SetBounds(0, cy, w, h);
                cy += h + S(10);
            }
            tasksToggle.Visible = tasksLoaded && SystemTaskCount() > 0;
            if (tasksToggle.Visible)
            {
                tasksToggle.Text = showSystemTasks ? "Ocultar as tarefas do Windows" : "Mostrar também as do Windows (" + SystemTaskCount() + ")";
                cy = PutButtons(ix, cy, iw, tasksToggle) + S(10);
            }
            startupCard.SetBounds(x, y, w, cy + S(4));
            return y + startupCard.Height;
        }

        private int LayoutClean(int x, int y, int w)
        {
            int ix = S(16);
            int iw = w - ix * 2;
            int cy = S(14);
            cy = Put(cleanTitle, ix, cy, iw) + S(4);
            cy = Put(cleanDesc, ix, cy, iw) + S(12);
            bool running = busyWhat == "analisando" || busyWhat == "limpando";
            cleanCancelBtn.Visible = running;
            cy = PutButtons(ix, cy, iw, analyzeBtn, cleanBtn, cleanCancelBtn) + S(10);
            cy = Put(cleanStatus, ix, cy, iw);
            cy += cleanStatus.Visible ? S(8) : 0;
            cleanProgress.Visible = running;
            if (running)
            {
                cleanProgress.SetBounds(ix, cy, iw, S(6));
                cy += S(6) + S(12);
            }
            cleanList.Visible = cleanList.Rows.Count > 0;
            if (cleanList.Visible)
            {
                int h = cleanList.PreferredHeight(w);
                cleanList.SetBounds(0, cy, w, h);
                cy += h + S(10);
            }
            cy = Put(cleanTotal, ix, cy, iw);
            cy += cleanTotal.Visible ? S(8) : 0;
            cy = Put(cleanResult, ix, cy, iw);
            cy += cleanResult.Visible ? S(8) : 0;
            cleanCard.SetBounds(x, y, w, cy + S(6));
            return y + cleanCard.Height;
        }

        private int LayoutRepair(int x, int y, int w)
        {
            int ix = S(16);
            int iw = w - ix * 2;
            int cy = S(14);
            cy = Put(repairTitle, ix, cy, iw) + S(4);
            cy = Put(repairDesc, ix, cy, iw) + S(12);
            bool running = busyWhat == "reparo";
            repairCancelBtn.Visible = running;
            cy = PutButtons(ix, cy, iw, repairBtn, repairCancelBtn) + S(12);
            cy = Put(repairPhase, ix, cy, iw);
            cy += repairPhase.Visible ? S(8) : 0;
            repairProgress.Visible = running;
            if (running)
            {
                repairProgress.SetBounds(ix, cy, iw, S(6));
                cy += S(6) + S(8);
            }
            cy = Put(repairLine, ix, cy, iw);
            cy += repairLine.Visible ? S(8) : 0;
            cy = Put(repairResultTitle, ix, cy, iw);
            cy += repairResultTitle.Visible ? S(4) : 0;
            cy = Put(repairResultText, ix, cy, iw);
            cy += repairResultText.Visible ? S(8) : 0;
            repairCard.SetBounds(x, y, w, cy + S(6));
            return y + repairCard.Height;
        }

        private int LayoutHistory(int x, int y, int w)
        {
            int ix = S(16);
            int iw = w - ix * 2;
            int cy = S(14);
            cy = Put(histTitle, ix, cy, iw) + S(4);
            cy = Put(histDesc, ix, cy, iw) + S(8);
            cy = Put(histStatus, ix, cy, iw);
            cy += histStatus.Visible ? S(10) : S(2);
            histList.Visible = histList.Rows.Count > 0;
            if (histList.Visible)
            {
                int h = histList.PreferredHeight(w);
                histList.SetBounds(0, cy, w, h);
                cy += h + S(6);
            }
            historyCard.SetBounds(x, y, w, cy + S(6));
            return y + historyCard.Height;
        }

        // ---- busy state ----

        private void SetBusy(string what)
        {
            busyWhat = what;
            UpdateControlsState();
            LayoutAll();
        }

        private void ClearBusy()
        {
            busyWhat = null;
            UpdateControlsState();
            LayoutAll();
        }

        private void UpdateControlsState()
        {
            bool idle = busyWhat == null;
            analyzeBtn.Enabled = idle;
            cleanBtn.Enabled = idle && plan != null && plan.CanClean;
            repairBtn.Enabled = idle;
            tasksToggle.Enabled = true;
            foreach (Row r in startupList.Rows) r.LeadEnabled = idle;
            foreach (Row r in histList.Rows) r.ActionEnabled = idle;
            startupList.Invalidate();
            histList.Invalidate();
            cleanProgress.Marquee = busyWhat == "analisando" || busyWhat == "limpando";
        }

        // ---- confirmation ----

        private ConfirmResult Confirm(ConfirmRequest req)
        {
            if (restore == null) req.OfferRestorePoint = false;
            if (ConfirmHandler != null) return ConfirmHandler(req);
            using (ConfirmDialog d = new ConfirmDialog(skin, req))
            {
                DialogResult r = d.ShowDialog(FindForm());
                ConfirmResult res = new ConfirmResult();
                res.Accepted = r == DialogResult.OK;
                res.CreateRestorePoint = d.RestorePointChecked;
                return res;
            }
        }

        // Optionally creates a restore point first (off the UI thread), then runs the action.
        // If the point cannot be made, asks again instead of going on silently.
        private void WithRestorePoint(bool create, string action, TextBlock status, Action proceed)
        {
            if (!create || restore == null)
            {
                proceed();
                return;
            }
            SetBusy("ponto");
            SetText(status, "Criando o ponto de restauração. Pode levar alguns segundos…", MaintColors.Sec);
            ThreadPool.QueueUserWorkItem(delegate
            {
                RestorePointResult r;
                try { r = restore.Create(action); }
                catch (Exception ex) { r = new RestorePointResult(RestorePointStatus.Failed, 0, ex.Message); }
                Post(delegate
                {
                    ClearBusy();
                    if (r.Ok || r.Status == RestorePointStatus.FrequencyLimit)
                    {
                        SetText(status, r.UserMessage, MaintColors.Sec);
                        proceed();
                        return;
                    }
                    ConfirmRequest again = new ConfirmRequest();
                    again.Title = "Continuar sem ponto de restauração?";
                    again.Lines.Add(r.UserMessage);
                    again.Lines.Add("");
                    again.Lines.Add("Se você continuar, a ação roda mesmo assim. Se cancelar, nada muda.");
                    again.ConfirmText = "Continuar sem ponto";
                    ConfirmResult c = Confirm(again);
                    if (c != null && c.Accepted) proceed();
                    else SetText(status, "Cancelado. Nada foi alterado.", MaintColors.Sec);
                });
            });
        }

        // ================= Inicializacao =================

        private void LoadStartup()
        {
            SetText(startupStatus, "Lendo os itens de inicialização…", MaintColors.Sec);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<StartupItem> items = null;
                string err = null;
                try { items = startup.List(); }
                catch (Exception ex) { err = ex.Message; }
                Post(delegate
                {
                    if (items == null)
                    {
                        SetText(startupStatus, "Não foi possível ler os itens de inicialização (" + err + "). Feche e abra a página para tentar de novo.", MaintColors.Warn);
                        return;
                    }
                    startupItems = items;
                    startupRead = true;
                    BindStartup();
                    LayoutAll();
                });
            });
            if (!tasksLoaded)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    List<LogonTask> t = null;
                    try { t = startup.ListLogonTasks(); }
                    catch (Exception) { t = null; }
                    Post(delegate
                    {
                        tasksLoaded = true;
                        tasks = t ?? new List<LogonTask>();
                        BindTasks();
                        LayoutAll();
                    });
                });
            }
        }

        private static string FileOf(string command)
        {
            string exe = StartupModel.ExtractExePath(command);
            if (string.IsNullOrEmpty(exe)) return null;
            try { return System.IO.Path.GetFileName(exe); }
            catch (ArgumentException) { return null; }
        }

        private void BindStartup()
        {
            startupList.Rows.Clear();
            int disabled = 0;
            foreach (StartupItem it in startupItems)
            {
                Row r = new Row();
                r.Lead = LeadKind.Toggle;
                r.On = it.Enabled;
                r.LeadEnabled = busyWhat == null;
                r.Primary = it.DisplayName;
                r.Secondary = (string.IsNullOrEmpty(it.Publisher) ? "Fabricante não informado" : it.Publisher)
                    + " · " + StartupModel.DescribeSource(it.Source);
                if (it.IsVendor)
                {
                    r.Tag = "Microsoft ou driver";
                    r.TagColor = MaintColors.Warn;
                }
                r.Right = it.Enabled ? "Ativo" : "Desativado";
                r.RightColor = it.Enabled ? MaintColors.Ok : MaintColors.Mute;
                r.Data = it;
                if (!it.Enabled) disabled++;
                startupList.Rows.Add(r);
            }
            if (startupItems.Count == 0 && startupRead)
            {
                startupStatus.Text = "Nenhum programa está configurado para abrir com o Windows neste PC.";
                startupStatus.ForeColor = MaintColors.Sec;
            }
            else if (startupItems.Count > 0)
            {
                startupStatus.Text = startupItems.Count + (startupItems.Count == 1 ? " item" : " itens") + " · " + disabled
                    + (disabled == 1 ? " desativado" : " desativados") + ". Use a chave para ativar ou desativar.";
                startupStatus.ForeColor = MaintColors.Sec;
            }
            startupList.Invalidate();
        }

        private int SystemTaskCount()
        {
            int n = 0;
            foreach (LogonTask t in tasks) if (t.IsSystem) n++;
            return n;
        }

        private void BindTasks()
        {
            tasksList.Rows.Clear();
            if (tasksLoaded)
            {
                int shown = 0;
                foreach (LogonTask t in tasks)
                {
                    if (t.IsSystem && !showSystemTasks) continue;
                    Row r = new Row();
                    r.Lead = LeadKind.None;
                    string name = t.Name ?? "";
                    int slash = name.LastIndexOf('\\');
                    r.Primary = slash >= 0 && slash < name.Length - 1 ? name.Substring(slash + 1) : name;
                    string folder = slash > 0 ? name.Substring(0, slash) : null;
                    string file = FileOf(t.Command);
                    r.Secondary = (folder ?? "") + (folder != null && file != null ? " · " : "") + (file ?? "");
                    r.Right = t.Enabled ? "Ativa" : "Desativada";
                    r.RightColor = t.Enabled ? MaintColors.Sec : MaintColors.Mute;
                    tasksList.Rows.Add(r);
                    shown++;
                }
                tasksStatus.Text = shown == 0
                    ? "Nenhuma tarefa de terceiros roda ao entrar no Windows. Esta versão só lista as tarefas; não liga nem desliga."
                    : shown + (shown == 1 ? " tarefa" : " tarefas") + ". Esta versão só lista as tarefas; não liga nem desliga.";
            }
            tasksList.Invalidate();
        }

        private void OnStartupRow(object sender, RowEventArgs e)
        {
            if (e.Hit != RowHit.Lead || busyWhat != null) return;
            StartupItem item = startupList.Rows[e.Index].Data as StartupItem;
            if (item == null) return;
            bool enable = !item.Enabled;
            string name = item.DisplayName;
            ConfirmRequest req = new ConfirmRequest();
            req.Title = enable ? "Reativar “" + name + "” na inicialização?" : "Desativar “" + name + "” na inicialização?";
            req.Lines.Add("O que vai acontecer:");
            string exe = FileOf(item.Command);
            req.Lines.Add("• " + (enable ? "O Windows volta a abrir " : "O Windows deixa de abrir ") + (exe != null ? exe : name) + " ao entrar.");
            req.Lines.Add("• O programa continua instalado e nenhuma entrada é apagada. Só o interruptor de inicialização muda, o mesmo do Gerenciador de Tarefas.");
            if (item.RequiresAdmin) req.Lines.Add("• É um item de todo o computador, por isso precisa de administrador.");
            req.Lines.Add("• Fica no Histórico com o botão Desfazer, que volta ao estado exato de antes. Se o item mudar de novo depois, o Desfazer recusa para não apagar a mudança mais recente.");
            if (item.IsVendor)
            {
                req.Lines.Add("");
                req.Lines.Add("Atenção: este item é da Microsoft ou de um driver (" + item.Publisher + "). Desativar pode tirar recursos do Windows ou da placa.");
            }
            req.ConfirmText = enable ? "Reativar" : "Desativar";
            req.OfferRestorePoint = true;
            ConfirmResult res = Confirm(req);
            if (res == null || !res.Accepted) return;
            WithRestorePoint(res.CreateRestorePoint, (enable ? "reativar " : "desativar ") + name + " na inicialização", startupStatus,
                delegate { DoToggle(item, enable); });
        }

        private void DoToggle(StartupItem item, bool enable)
        {
            SetBusy("inicializacao");
            ThreadPool.QueueUserWorkItem(delegate
            {
                StartupChangeResult r;
                try { r = startup.SetEnabled(item, enable); }
                catch (Exception ex) { r = new StartupChangeResult(false, "Não foi possível alterar (" + ex.Message + "). Nada foi mudado."); }
                Post(delegate
                {
                    ClearBusy();
                    string msg = WithSaveWarning(r.Message);
                    bool good = r.Ok && !HasSaveWarning;
                    SetText(startupStatus, msg, good ? MaintColors.Ok : MaintColors.Warn);
                    LoadStartupAfterChange(msg, good);
                });
            });
        }

        // Re-reads the list but keeps the result message on screen.
        private void LoadStartupAfterChange(string message, bool ok)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<StartupItem> items = null;
                try { items = startup.List(); }
                catch (Exception) { items = null; }
                Post(delegate
                {
                    if (items != null) startupItems = items;
                    BindStartup();
                    startupStatus.Text = message;
                    startupStatus.ForeColor = ok ? MaintColors.Ok : MaintColors.Warn;
                    LayoutAll();
                });
            });
        }

        // ================= Limpeza =================

        private void BindClean()
        {
            cleanList.Rows.Clear();
            if (plan != null)
            {
                string group = null;
                foreach (CleanupTarget t in plan.Targets)
                {
                    if (!string.IsNullOrEmpty(t.Group) && t.Group != group)
                    {
                        // a heading row: no checkbox, no data, so a click on it does nothing
                        group = t.Group;
                        Row h = new Row();
                        h.Primary = t.Group;
                        h.PrimaryColor = MaintColors.Sec;
                        cleanList.Rows.Add(h);
                    }
                    Row r = new Row();
                    r.Lead = LeadKind.Check;
                    r.On = t.Selected && t.Available;
                    r.LeadEnabled = t.Available;
                    r.Primary = t.Name;
                    if (t.Available)
                    {
                        string d = t.Description;
                        if (t.SkippedRecent > 0) d += " " + t.SkippedRecent + (t.SkippedRecent == 1 ? " arquivo recente fica." : " arquivos recentes ficam.");
                        if (!string.IsNullOrEmpty(t.Note)) d += " " + t.Note;
                        r.Secondary = d;
                        r.Right = t.Analyzed ? (t.Summary ?? SizeFormat.Format(t.Bytes)) : "?";
                        r.RightColor = MaintColors.Text;
                    }
                    else
                    {
                        r.Secondary = t.Note;
                        r.SecondaryColor = MaintColors.Warn;
                        r.Right = "—";
                        r.RightColor = MaintColors.Mute;
                        r.PrimaryColor = MaintColors.Mute;
                    }
                    if (t.Optional)
                    {
                        r.Tag = "Opcional";
                        r.TagColor = MaintColors.Mute;
                    }
                    r.Data = t;
                    cleanList.Rows.Add(r);
                }
            }
            UpdateCleanTotal();
            cleanList.Invalidate();
        }

        private void UpdateCleanTotal()
        {
            if (plan == null || plan.Targets.Count == 0)
            {
                cleanTotal.Text = "";
                return;
            }
            cleanTotal.Text = plan.SelectionSummary();
            cleanTotal.ForeColor = MaintColors.Text;
        }

        private void OnCleanRow(object sender, RowEventArgs e)
        {
            if (e.Hit != RowHit.Lead || busyWhat != null) return;
            CleanupTarget t = cleanList.Rows[e.Index].Data as CleanupTarget;
            if (t == null || !t.Available) return;
            t.Selected = !t.Selected;
            cleanList.Rows[e.Index].On = t.Selected;
            UpdateCleanTotal();
            UpdateControlsState();
            LayoutAll();
            cleanList.Invalidate();
        }

        private void OnAnalyze()
        {
            if (busyWhat != null) return;
            plan = null;
            cleanResult.Text = "";
            BindClean();
            SetBusy("analisando");
            SetText(cleanStatus, "Analisando as pastas… pode levar alguns segundos.", MaintColors.Sec);
            CancellationTokenSource cts = new CancellationTokenSource();
            cleanCts = cts;
            RunWorker(delegate
            {
                CleanupPlan p = null;
                string err = null;
                try
                {
                    p = cleanup.Analyze(delegate(CleanupProgress pr) { Post(delegate { ShowCleanProgress(pr); }); }, cts.Token);
                }
                catch (Exception ex) { err = ex.Message; }
                Post(delegate { OnAnalyzeDone(p, err); });
            });
        }

        private void ShowCleanProgress(CleanupProgress p)
        {
            if (busyWhat != "analisando" && busyWhat != "limpando") return;
            SetText(cleanStatus, p.Phase + " (" + p.TargetIndex + " de " + p.TargetCount + "): " + p.TargetName
                + (p.IsAction ? "" : " · " + p.Files.ToString("N0", new CultureInfo("pt-BR")) + " arquivos"), MaintColors.Sec);
        }

        private void OnAnalyzeDone(CleanupPlan p, string err)
        {
            cleanCts = null;
            plan = p;
            BindClean();
            ClearBusy();
            if (p == null)
            {
                SetText(cleanStatus, "Não foi possível analisar (" + err + "). Tente de novo; se repetir, feche programas que usam muitos arquivos temporários.", MaintColors.Warn);
                return;
            }
            if (p.Cancelled)
            {
                SetText(cleanStatus, "Análise cancelada. Os tamanhos ficaram incompletos; analise de novo para poder limpar.", MaintColors.Warn);
                return;
            }
            SetText(cleanStatus, "Análise concluída: até " + SizeFormat.Format(p.SelectedBytes) + " podem ser liberados. Marque o que quiser limpar.", MaintColors.Sec);
        }

        private void CancelCleanup()
        {
            CancellationTokenSource c = cleanCts;
            if (c != null)
            {
                try { c.Cancel(); }
                catch (Exception) { }
                SetText(cleanStatus, "Cancelando…", MaintColors.Sec);
            }
        }

        private void OnCleanClick()
        {
            if (busyWhat != null || plan == null) return;
            ConfirmRequest req = new ConfirmRequest();
            req.Title = "Limpar os arquivos selecionados?";
            req.Lines.Add("O que vai acontecer:");
            bool eventLogs = false;
            foreach (CleanupTarget t in plan.Targets)
            {
                if (!t.Selected || !t.Available) continue;
                if (t.Kind == CleanupKind.Action)
                {
                    req.Lines.Add("• " + t.Name + ": " + (t.Summary ?? "ação") + " (não apaga arquivos do disco)");
                    if (t.Id == CleanupTargetId.EventLogs) eventLogs = true;
                }
                else
                {
                    req.Lines.Add("• " + t.Name + ": " + SizeFormat.Format(t.Bytes) + " (" + t.Files + (t.Files == 1 ? " arquivo)" : " arquivos)"));
                }
            }
            if (plan.SelectedFolders > 0) req.Lines.Add("• Total estimado: " + SizeFormat.Format(plan.SelectedBytes) + ".");
            if (eventLogs) req.Lines.Add("• Os registros System, Application e Security do Windows não são limpos.");
            req.Lines.Add("• Arquivos em uso e criados nas últimas 24 horas ficam onde estão.");
            req.Lines.Add("• A pasta Minidump (telas azuis) e seus documentos não são tocados.");
            req.Lines.Add("");
            req.Lines.Add("Isto NÃO pode ser desfeito: os arquivos apagados não vão para a Lixeira. Fica registrado no Histórico, sem botão Desfazer.");
            req.ConfirmText = "Limpar agora";
            req.OfferRestorePoint = true;
            ConfirmResult res = Confirm(req);
            if (res == null || !res.Accepted) return;
            CleanupPlan snapshot = plan;
            WithRestorePoint(res.CreateRestorePoint, "limpeza de arquivos temporários", cleanStatus, delegate { DoClean(snapshot); });
        }

        private void DoClean(CleanupPlan p)
        {
            SetBusy("limpando");
            cleanResult.Text = "";
            SetText(cleanStatus, "Limpando…", MaintColors.Sec);
            CancellationTokenSource cts = new CancellationTokenSource();
            cleanCts = cts;
            RunWorker(delegate
            {
                CleanupResult r = null;
                string err = null;
                try
                {
                    r = cleanup.Clean(p, delegate(CleanupProgress pr) { Post(delegate { ShowCleanProgress(pr); }); }, cts.Token);
                }
                catch (Exception ex) { err = ex.Message; }
                Post(delegate { OnCleanDone(r, err); });
            });
        }

        private void OnCleanDone(CleanupResult r, string err)
        {
            cleanCts = null;
            plan = null;
            BindClean();
            ClearBusy();
            if (r == null)
            {
                SetText(cleanStatus, "A limpeza parou com um erro (" + err + "). Analise de novo para ver o que sobrou.", MaintColors.Warn);
                return;
            }
            SetText(cleanStatus, r.Cancelled ? "Limpeza cancelada. Analise de novo para ver o que sobrou." : "Analise de novo para ver o que sobrou.", MaintColors.Sec);
            string text = (r.Cancelled ? "Cancelada: " : "") + SizeFormat.Format(r.FreedBytes) + " liberados (" + r.DeletedFiles
                + (r.DeletedFiles == 1 ? " arquivo" : " arquivos") + "). " + r.SkippedCount
                + (r.SkippedCount == 1 ? " item ficou" : " itens ficaram") + " por estar em uso ou ser recente.";
            foreach (string n in r.Done) text += " " + n;
            foreach (string n in r.Notes) text += " " + n;
            text = WithSaveWarning(text);
            cleanResult.ForeColor = r.Cancelled || r.Notes.Count > 0 || HasSaveWarning ? MaintColors.Warn : MaintColors.Ok;
            SetText(cleanResult, text, null);
        }

        // ================= Reparo =================

        private void OnRepairClick()
        {
            if (busyWhat != null) return;
            ConfirmRequest req = new ConfirmRequest();
            req.Title = "Iniciar o reparo do Windows?";
            req.Lines.Add("O que vai acontecer:");
            req.Lines.Add("• Roda o DISM (/RestoreHealth) e depois o SFC (/scannow), um de cada vez, sem janelas.");
            req.Lines.Add("• Pode levar de 10 a 40 minutos ou mais, e o PC fica mais lento nesse tempo.");
            req.Lines.Add("• Deixe o notebook ligado na tomada e não o desligue nem suspenda até terminar.");
            req.Lines.Add("• Conserta arquivos do sistema do Windows. Seus arquivos pessoais não são tocados.");
            req.Lines.Add("• O DISM não pode ser cancelado no meio (interromper pode danificar o Windows); se o app fechar, ele continua sozinho até terminar. O SFC pode ser cancelado.");
            req.Lines.Add("• O resultado fica no Histórico, sem botão Desfazer.");
            req.ConfirmText = "Iniciar reparo";
            ConfirmResult res = Confirm(req);
            if (res == null || !res.Accepted) return;
            DoRepair();
        }

        private void DoRepair()
        {
            SetBusy("reparo");
            repairResultTitle.Text = "";
            repairResultText.Text = "";
            repairLine.Text = "";
            repairOverall = 0;
            repairPhaseText = "Iniciando…";
            repairStartedUtc = DateTime.UtcNow;
            repairTool = null;
            repairCancelBtn.Enabled = false;
            repairProgress.Marquee = true;
            UpdateRepairText();
            elapsedTimer.Start();
            CancellationTokenSource cts = new CancellationTokenSource();
            repairCts = cts;
            RunWorker(delegate
            {
                RepairResult r = null;
                string err = null;
                try { r = repair.Run(delegate(RepairProgress p) { Post(delegate { OnRepairProgress(p); }); }, cts.Token); }
                catch (Exception ex) { err = ex.Message; }
                Post(delegate { OnRepairDone(r, err); });
            });
        }

        private void OnRepairProgress(RepairProgress p)
        {
            if (busyWhat != "reparo") return;
            if (repairTool != p.Tool)
            {
                repairTool = p.Tool;
                // Cancel only during SFC; DISM is never interrupted.
                repairCancelBtn.Enabled = p.Tool != RepairTool.Dism;
            }
            if (p.Percent >= 0)
            {
                repairOverall = (int)Math.Round(((p.Step - 1) * 100.0 + p.Percent) / Math.Max(1, p.StepCount));
                repairProgress.Marquee = false;
                repairProgress.Value = repairOverall;
            }
            repairPhaseText = "Etapa " + p.Step + " de " + p.StepCount + " · " + p.Phase + (p.Percent >= 0 ? " · " + p.Percent + "%" : "")
                + (p.Tool == RepairTool.Dism ? " · o DISM não pode ser cancelado" : "");
            if (!string.IsNullOrEmpty(p.LastLine) && RepairParse.ParsePercent(p.LastLine) < 0) repairLine.Text = p.LastLine;
            UpdateRepairText();
        }

        private void UpdateRepairText()
        {
            if (busyWhat != "reparo") return;
            TimeSpan t = DateTime.UtcNow - repairStartedUtc;
            string elapsed = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            SetText(repairPhase, repairPhaseText + " · " + elapsed, MaintColors.Text);
        }

        private void CancelRepair()
        {
            CancellationTokenSource c = repairCts;
            if (c != null)
            {
                try { c.Cancel(); }
                catch (Exception) { }
                repairPhaseText = repairTool.HasValue && repairTool.Value == RepairTool.Dism
                    ? "O DISM continua em segundo plano até terminar…"
                    : "Cancelando e encerrando o SFC…";
                UpdateRepairText();
            }
        }

        private void OnRepairDone(RepairResult r, string err)
        {
            elapsedTimer.Stop();
            repairCts = null;
            repairTool = null;
            repairCancelBtn.Enabled = true;
            repairProgress.Marquee = false;
            ClearBusy();
            repairPhase.Text = "";
            repairLine.Text = "";
            if (r == null)
            {
                repairResultTitle.ForeColor = MaintColors.Warn;
                repairResultTitle.Text = "O reparo não terminou";
                repairResultText.Text = "Aconteceu um erro (" + err + "). Feche instaladores abertos, abra o app como administrador e tente de novo.";
            }
            else
            {
                Color c = MaintColors.Ok;
                if (r.Outcome == RepairOutcome.NaoFoiPossivelReparar || r.Outcome == RepairOutcome.Erro) c = MaintColors.Hot;
                else if (r.Outcome == RepairOutcome.Cancelado) c = MaintColors.Warn;
                if (HasSaveWarning && c == MaintColors.Ok) c = MaintColors.Warn;
                repairResultTitle.ForeColor = c;
                repairResultTitle.Text = r.Title;
                repairResultText.Text = WithSaveWarning(r.Message);
            }
            LayoutAll();
        }

        // ================= Historico =================

        private void OnLogChanged(object sender, EventArgs e)
        {
            Post(delegate { BindHistory(); LayoutAll(); });
        }

        private static string KindName(ChangeKind k)
        {
            switch (k)
            {
                case ChangeKind.Inicializacao: return "Inicialização";
                case ChangeKind.Limpeza: return "Limpeza";
                case ChangeKind.Reparo: return "Reparo";
                default: return "Ponto de restauração";
            }
        }

        private void BindHistory()
        {
            List<ChangeEntry> all = log.List();
            Dictionary<string, bool> pending = StartupModel.UndoableEntryIds(all);
            histList.Rows.Clear();
            int n = 0;
            foreach (ChangeEntry e in all)
            {
                if (n >= MaxHistoryRows) break;
                n++;
                Row r = new Row();
                r.Lead = LeadKind.None;
                r.Primary = e.Description;
                r.Secondary = e.WhenUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", new CultureInfo("pt-BR")) + " · " + KindName(e.Kind);
                bool undoable = pending.ContainsKey(e.Id);
                if (undoable)
                {
                    r.Action = "Desfazer";
                    r.ActionEnabled = busyWhat == null;
                }
                else if (e.Undone)
                {
                    r.Tag = "Desfeito";
                    r.TagColor = MaintColors.Mute;
                }
                else if (e.CanUndo && e.Kind == ChangeKind.Inicializacao)
                {
                    r.Tag = "Mudou depois";
                    r.TagColor = MaintColors.Mute;
                }
                else if (e.Kind == ChangeKind.PontoRestauracao)
                {
                    r.Tag = "Registro";
                    r.TagColor = MaintColors.Mute;
                }
                else
                {
                    r.Tag = "Sem reversão";
                    r.TagColor = MaintColors.Mute;
                }
                r.Data = e;
                histList.Rows.Add(r);
            }
            if (all.Count == 0) histStatus.Text = "Nada alterado ainda. O que o app mudar neste PC aparece aqui.";
            else if (all.Count > MaxHistoryRows) histStatus.Text = "Mostrando as " + MaxHistoryRows + " mais recentes de " + all.Count + ".";
            else if (histStatus.ForeColor == MaintColors.Sec || histStatus.Text.StartsWith("Nada alterado") || histStatus.Text.StartsWith("Mostrando")) histStatus.Text = "";
            histList.Invalidate();
        }

        private void OnHistoryRow(object sender, RowEventArgs e)
        {
            if (e.Hit != RowHit.Action || busyWhat != null) return;
            ChangeEntry entry = histList.Rows[e.Index].Data as ChangeEntry;
            if (entry == null || !entry.CanUndo) return;
            ConfirmRequest req = new ConfirmRequest();
            req.Title = "Desfazer esta alteração?";
            req.Lines.Add(entry.Description);
            req.Lines.Add("");
            req.Lines.Add("O que vai acontecer:");
            req.Lines.Add("• O item de inicialização volta exatamente ao estado de antes da alteração.");
            req.Lines.Add("• Se o item tiver sido mudado depois, o app recusa e avisa, para não apagar a mudança mais recente.");
            req.Lines.Add("• Nada é apagado nem instalado.");
            req.ConfirmText = "Desfazer";
            ConfirmResult res = Confirm(req);
            if (res == null || !res.Accepted) return;
            SetBusy("desfazer");
            ThreadPool.QueueUserWorkItem(delegate
            {
                StartupChangeResult r;
                try { r = startup.Undo(entry); }
                catch (Exception ex) { r = new StartupChangeResult(false, "Não foi possível desfazer (" + ex.Message + "). Nada foi mudado."); }
                Post(delegate
                {
                    ClearBusy();
                    BindHistory();
                    histStatus.Text = WithSaveWarning(r.Message);
                    histStatus.ForeColor = r.Ok && !HasSaveWarning ? MaintColors.Ok : MaintColors.Warn;
                    LayoutAll();
                    if (r.Ok) LoadStartupAfterChange(r.Message, !HasSaveWarning);
                });
            });
        }

        // =====================================================================
        // Look and feel
        // =====================================================================

        // The one palette (Controls.Palette), under the names this page uses.
        private static class MaintColors
        {
            public static readonly Color Bg = Palette.Bg;
            public static readonly Color Panel = Palette.Panel;
            public static readonly Color Line = Palette.Line;
            public static readonly Color Line2 = Palette.Line2;
            public static readonly Color Text = Palette.Text;
            public static readonly Color Sec = Palette.Sec;
            public static readonly Color Mute = Palette.Mute;
            public static readonly Color Accent = Palette.Accent;
            public static readonly Color AccentHover = Palette.AccentHover;
            public static readonly Color OnAccent = Palette.OnAccent;
            public static readonly Color Warn = Palette.Warn;
            public static readonly Color Hot = Palette.Hot;
            public static readonly Color Ok = Palette.Ok;
            public static readonly Color RowHover = Palette.Hover;
        }

        private enum TextKind
        {
            Section,
            Body,
            Small,
            SmallBold,
            UiBold,
            MonoSmall
        }

        private enum LeadKind
        {
            None,
            Toggle,
            Check
        }

        private enum RowHit
        {
            None,
            Lead,
            Action,
            Body
        }

        private sealed class RowEventArgs : EventArgs
        {
            public readonly int Index;
            public readonly RowHit Hit;

            public RowEventArgs(int index, RowHit hit)
            {
                Index = index;
                Hit = hit;
            }
        }

        private sealed class Row
        {
            public LeadKind Lead;
            public bool On;
            public bool LeadEnabled = true;
            public string Primary;
            public Color PrimaryColor = MaintColors.Text;
            public string Secondary;
            public Color SecondaryColor = MaintColors.Sec;
            public string Tag;
            public Color TagColor = MaintColors.Mute;
            public string Right;
            public Color RightColor = MaintColors.Sec;
            public string Action;
            public bool ActionEnabled = true;
            public object Data;
        }

        // Fonts and pixel sizes for one DPI. Rebuilt in place when the DPI changes.
        private sealed class Skin : IDisposable
        {

            public int Dpi = 96;
            public Font Ui, UiBold, Small, SmallBold, Section, Mono, MonoSmall;

            public Skin(int dpi, int deviceDpi)
            {
                Apply(dpi, deviceDpi);
            }

            public int S(int v)
            {
                return (int)Math.Round(v * Dpi / 96.0);
            }

            // dpi: the scale to draw at; deviceDpi: what the window really runs at. Their ratio
            // corrects the font size, so a test can render a 150 percent layout on a 100 percent screen.
            public void Apply(int dpi, int deviceDpi)
            {
                Dpi = dpi <= 0 ? 96 : dpi;
                float k = deviceDpi > 0 ? (float)Dpi / deviceDpi : 1f;
                DisposeFonts();
                Ui = new Font("Segoe UI", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point);
                UiBold = new Font("Segoe UI", 9.5f * k, FontStyle.Bold, GraphicsUnit.Point);
                Small = new Font("Segoe UI", 8.5f * k, FontStyle.Regular, GraphicsUnit.Point);
                SmallBold = new Font("Segoe UI", 8f * k, FontStyle.Bold, GraphicsUnit.Point);
                Section = new Font("Segoe UI", 9f * k, FontStyle.Bold, GraphicsUnit.Point);
                Mono = new Font(MonoName(), 9f * k, FontStyle.Regular, GraphicsUnit.Point);
                MonoSmall = new Font(MonoName(), 8.5f * k, FontStyle.Regular, GraphicsUnit.Point);
            }

            private static string MonoName()
            {
                return Palette.MonoFamily;
            }

            private void DisposeFonts()
            {
                Font[] all = { Ui, UiBold, Small, SmallBold, Section, Mono, MonoSmall };
                foreach (Font f in all)
                {
                    if (f != null) f.Dispose();
                }
            }

            public void Dispose()
            {
                DisposeFonts();
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private sealed class ScrollPanel : Panel
        {
            public ScrollPanel()
            {
                SetStyle(ControlStyles.Selectable, true);
                TabStop = false;
            }
        }

        // The rounded section background. Children that are transparent paint through it.
        private sealed class Card : Panel
        {
            private readonly Skin skin;

            public Card(Skin skin)
            {
                this.skin = skin;
                BackColor = MaintColors.Panel;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(MaintColors.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rc = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = RoundRect(rc, skin.S(6)))
                {
                    using (SolidBrush b = new SolidBrush(MaintColors.Panel)) g.FillPath(b, path);
                    using (Pen p = new Pen(MaintColors.Line, 1f)) g.DrawPath(p, path);
                }
            }
        }

        private sealed class TextBlock : Control
        {
            private readonly Skin skin;
            private readonly MaintenancePage owner;
            private readonly TextKind kind;
            private readonly bool wrap;

            public TextBlock(Skin skin, MaintenancePage owner, TextKind kind, Color color, bool wrap)
            {
                this.skin = skin;
                this.owner = owner;
                this.kind = kind;
                this.wrap = wrap;
                SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Transparent;
                ForeColor = color;
                TabStop = false;
            }

            private Font GetFont()
            {
                switch (kind)
                {
                    case TextKind.Section: return skin.Section;
                    case TextKind.Small: return skin.Small;
                    case TextKind.SmallBold: return skin.SmallBold;
                    case TextKind.UiBold: return skin.UiBold;
                    case TextKind.MonoSmall: return skin.MonoSmall;
                    default: return skin.Ui;
                }
            }

            private TextFormatFlags Flags()
            {
                TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
                if (wrap) f |= TextFormatFlags.WordBreak;
                else f |= TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                return f;
            }

            // Height of the text when laid out at the given width, with a little room for descenders.
            public int MeasureHeight(int width)
            {
                if (string.IsNullOrEmpty(Text)) return 0;
                Font f = GetFont();
                if (!wrap) return f.Height + skin.S(2);
                Size s = TextRenderer.MeasureText(Text, f, new Size(Math.Max(10, width), 100000), Flags());
                return s.Height + skin.S(2);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (string.IsNullOrEmpty(Text)) return;
                TextRenderer.DrawText(e.Graphics, Text, GetFont(), ClientRectangle, ForeColor, Flags());
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (owner != null) owner.ScrollBy(e.Delta);
            }
        }

        private sealed class DarkButton : Control
        {
            private readonly Skin skin;
            private readonly MaintenancePage owner;
            private readonly bool primary;
            private bool hover;
            private bool pressed;

            public DarkButton(Skin skin, MaintenancePage owner, string text, bool primary)
            {
                this.skin = skin;
                this.owner = owner;
                this.primary = primary;
                Text = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                    | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.PushButton;
            }

            public int PreferredWidth()
            {
                Size s = TextRenderer.MeasureText(Text, skin.UiBold, new Size(10000, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                return s.Width + skin.S(32);
            }

            protected override void OnTextChanged(EventArgs e)
            {
                AccessibleName = Text;
                base.OnTextChanged(e);
                Invalidate();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                Cursor = Enabled ? Cursors.Hand : Cursors.Default;
                Invalidate();
                base.OnEnabledChanged(e);
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
            protected override void OnMouseWheel(MouseEventArgs e) { if (owner != null) owner.ScrollBy(e.Delta); }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (Enabled && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space))
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
                g.Clear(MaintColors.Panel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rc = new Rectangle(0, 0, Width - 1, Height - 1);
                Color fill, border, fore;
                if (!Enabled)
                {
                    fill = primary ? MaintColors.Line : MaintColors.Panel;
                    border = MaintColors.Line;
                    fore = MaintColors.Mute;
                }
                else if (primary)
                {
                    fill = hover || pressed ? MaintColors.AccentHover : MaintColors.Accent;
                    border = fill;
                    fore = MaintColors.OnAccent;
                }
                else
                {
                    fill = pressed ? MaintColors.Line : MaintColors.Panel;
                    border = hover || pressed ? MaintColors.Sec : MaintColors.Line2;
                    fore = MaintColors.Text;
                }
                using (GraphicsPath path = RoundRect(rc, skin.S(5)))
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, Text, skin.UiBold, new Rectangle(skin.S(8), 0, Width - skin.S(16), Height), fore,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (Focused && ShowFocusCues)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    using (Pen f = new Pen(primary ? MaintColors.Text : MaintColors.Accent, 2f))
                    {
                        g.DrawRectangle(f, 1, 1, Width - 3, Height - 3);
                    }
                }
            }
        }

        private sealed class DarkProgress : Control
        {
            private readonly Skin skin;
            private readonly System.Windows.Forms.Timer timer;
            private bool marquee;
            private int value;
            private int phase;

            public DarkProgress(Skin skin)
            {
                this.skin = skin;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 30;
                timer.Tick += delegate { phase = (phase + 6) % 200; Invalidate(); };
                AccessibleRole = AccessibleRole.ProgressBar;
            }

            public bool Marquee
            {
                get { return marquee; }
                set
                {
                    if (marquee == value) return;
                    marquee = value;
                    if (value && Visible) timer.Start();
                    else timer.Stop();
                    Invalidate();
                }
            }

            public int Value
            {
                get { return value; }
                set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (marquee && Visible) timer.Start();
                else timer.Stop();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    timer.Stop();
                    timer.Dispose();
                }
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(MaintColors.Panel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath p = RoundRect(track, Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(MaintColors.Line2)) g.FillPath(b, p);
                }
                Rectangle bar;
                if (marquee)
                {
                    int segW = Math.Max(10, Width * 3 / 10);
                    int travel = Width + segW;
                    int x = (int)((long)travel * phase / 200) - segW;
                    int left = Math.Max(0, x);
                    int right = Math.Min(Width - 1, x + segW);
                    if (right <= left) return;
                    bar = new Rectangle(left, 0, right - left, Height - 1);
                }
                else
                {
                    int w = (int)((Width - 1) * (long)value / 100);
                    if (w <= 0) return;
                    bar = new Rectangle(0, 0, w, Height - 1);
                }
                using (GraphicsPath p = RoundRect(bar, Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(MaintColors.Accent)) g.FillPath(b, p);
                }
            }
        }

        private sealed class DarkCheck : Control
        {
            private readonly Skin skin;
            private bool isChecked;

            public DarkCheck(Skin skin, string text, bool isChecked)
            {
                this.skin = skin;
                this.isChecked = isChecked;
                Text = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                    | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.CheckButton;
                AccessibleName = text;
            }

            public bool Checked
            {
                get { return isChecked; }
                set { isChecked = value; Invalidate(); }
            }

            protected override void OnClick(EventArgs e)
            {
                isChecked = !isChecked;
                Invalidate();
                base.OnClick(e);
            }

            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Space || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space)
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
                g.Clear(MaintColors.Panel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int box = skin.S(16);
                Rectangle b = new Rectangle(0, (Height - box) / 2, box, box);
                DrawCheckBox(g, skin, b, isChecked, true);
                Rectangle t = new Rectangle(box + skin.S(10), 0, Width - box - skin.S(10), Height);
                TextRenderer.DrawText(g, Text, skin.Ui, t, MaintColors.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                if (Focused && ShowFocusCues)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    using (Pen f = new Pen(MaintColors.Accent, 2f)) g.DrawRectangle(f, 1, 1, Width - 3, Height - 3);
                }
            }
        }

        private static void DrawCheckBox(Graphics g, Skin skin, Rectangle b, bool on, bool enabled)
        {
            Color border = enabled ? (on ? MaintColors.Accent : MaintColors.Mute) : MaintColors.Line2;
            using (GraphicsPath p = RoundRect(b, skin.S(3)))
            {
                if (on && enabled)
                {
                    using (SolidBrush br = new SolidBrush(MaintColors.Accent)) g.FillPath(br, p);
                }
                using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
            }
            if (on)
            {
                using (Pen tick = new Pen(enabled ? MaintColors.OnAccent : MaintColors.Mute, Math.Max(2f, skin.S(2))))
                {
                    tick.StartCap = LineCap.Round;
                    tick.EndCap = LineCap.Round;
                    tick.LineJoin = LineJoin.Round;
                    g.DrawLines(tick, new Point[]
                    {
                        new Point(b.Left + b.Width * 22 / 100, b.Top + b.Height * 52 / 100),
                        new Point(b.Left + b.Width * 43 / 100, b.Top + b.Height * 72 / 100),
                        new Point(b.Left + b.Width * 78 / 100, b.Top + b.Height * 30 / 100)
                    });
                }
            }
        }

        // Owner-drawn list: one row per item with an optional switch or checkbox on the left,
        // two lines of text, an optional tag, a right-aligned value and an optional action
        // button. The control is as tall as its rows, so the page scrolls instead of the list.
        private sealed class RowList : Control
        {
            private readonly Skin skin;
            private readonly MaintenancePage owner;
            private readonly ToolTip tip = new ToolTip();
            private int hoverRow = -1;
            private RowHit hoverHit = RowHit.None;
            private int focusRow = -1;
            private string tipText;

            public readonly List<Row> Rows = new List<Row>();
            public int SecondaryLines = 1;

            public event EventHandler<RowEventArgs> RowActivated;

            public RowList(Skin skin, MaintenancePage owner)
            {
                this.skin = skin;
                this.owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                    | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
                BackColor = MaintColors.Panel;
                AccessibleRole = AccessibleRole.List;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) tip.Dispose();
                base.Dispose(disposing);
            }

            // Number of lines the second text line gets in every row (1 to 4), decided in
            // PreferredHeight from the longest text at the width the list will have.
            private int curLines = 1;

            private int RowHeightFor(int lines)
            {
                return Math.Max(skin.S(48), skin.S(20) + skin.Ui.Height + lines * skin.Small.Height + skin.S(2));
            }

            private int RowHeight
            {
                get { return RowHeightFor(curLines); }
            }

            private int LinesFor(int width)
            {
                int lines = Math.Max(1, SecondaryLines);
                for (int i = 0; i < Rows.Count; i++)
                {
                    Geom g = GetGeomAt(i, width, 0);
                    string sec = SecondaryOf(Rows[i], g);
                    if (string.IsNullOrEmpty(sec)) continue;
                    Size s = TextRenderer.MeasureText(sec, skin.Small, new Size(Math.Max(10, g.Text.Width), 100000),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                    int n = (s.Height + skin.Small.Height - 1) / skin.Small.Height;
                    lines = Math.Max(lines, Math.Min(4, n));
                }
                return lines;
            }

            public int PreferredHeight(int width)
            {
                curLines = LinesFor(width);
                return Rows.Count * RowHeight;
            }

            private struct Geom
            {
                public Rectangle Row, Lead, Text, Tag, Right, Action;
                public bool TagInline; // no room beside the text: the tag is written before the second line
            }

            private string SecondaryOf(Row r, Geom g)
            {
                if (g.TagInline && !string.IsNullOrEmpty(r.Tag))
                    return string.IsNullOrEmpty(r.Secondary) ? r.Tag : r.Tag + " · " + r.Secondary;
                return r.Secondary;
            }

            private int Measure(string text, Font f)
            {
                return TextRenderer.MeasureText(text, f, new Size(10000, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            }

            private Geom GetGeom(int i)
            {
                return GetGeomAt(i, Width, RowHeight);
            }

            private Geom GetGeomAt(int i, int width, int h)
            {
                Row r = Rows[i];
                int y = i * h;
                int pad = skin.S(16);
                Geom g = new Geom();
                g.Row = new Rectangle(0, y, width, h);
                int x = pad;
                int rightX = width - pad;
                if (!string.IsNullOrEmpty(r.Action))
                {
                    int w = Measure(r.Action, skin.UiBold) + skin.S(28);
                    int bh = skin.S(28);
                    g.Action = new Rectangle(rightX - w, y + (h - bh) / 2, w, bh);
                    rightX = g.Action.Left - skin.S(12);
                }
                if (!string.IsNullOrEmpty(r.Right))
                {
                    int w = Measure(r.Right, skin.Mono);
                    g.Right = new Rectangle(rightX - w, y, w, h);
                    rightX = g.Right.Left - skin.S(14);
                }
                int tagW = 0;
                if (!string.IsNullOrEmpty(r.Tag)) tagW = Measure(r.Tag, skin.SmallBold) + skin.S(16);
                if (r.Lead == LeadKind.Toggle)
                {
                    int lw = skin.S(36), lh = skin.S(20);
                    g.Lead = new Rectangle(x, y + (h - lh) / 2, lw, lh);
                    x += lw + skin.S(14);
                }
                else if (r.Lead == LeadKind.Check)
                {
                    int s = skin.S(18);
                    g.Lead = new Rectangle(x, y + (h - s) / 2, s, s);
                    x += s + skin.S(14);
                }
                if (tagW > 0)
                {
                    if (rightX - x - tagW - skin.S(12) >= skin.S(260))
                    {
                        int th = skin.SmallBold.Height + skin.S(6);
                        g.Tag = new Rectangle(rightX - tagW, y + (h - th) / 2, tagW, th);
                        rightX = g.Tag.Left - skin.S(12);
                    }
                    else
                    {
                        g.TagInline = true;
                    }
                }
                g.Text = new Rectangle(x, y, Math.Max(skin.S(40), rightX - x), h);
                return g;
            }

            private bool LeadHit(Row r, Geom g, Point p)
            {
                if (r.Lead == LeadKind.None) return false;
                Rectangle hit = g.Lead;
                hit.Inflate(skin.S(8), skin.S(8));
                return hit.Contains(p);
            }

            private void HitTest(Point p, out int index, out RowHit hit)
            {
                index = -1;
                hit = RowHit.None;
                int h = RowHeight;
                if (p.Y < 0 || h <= 0) return;
                int i = p.Y / h;
                if (i < 0 || i >= Rows.Count) return;
                index = i;
                Row r = Rows[i];
                Geom g = GetGeom(i);
                if (!string.IsNullOrEmpty(r.Action) && g.Action.Contains(p)) hit = RowHit.Action;
                else if (LeadHit(r, g, p)) hit = RowHit.Lead;
                else hit = RowHit.Body;
            }

            private bool Clickable(int index, RowHit hit)
            {
                if (index < 0) return false;
                Row r = Rows[index];
                if (hit == RowHit.Action) return r.ActionEnabled;
                if (hit == RowHit.Lead) return r.LeadEnabled;
                return false;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int i; RowHit hit;
                HitTest(e.Location, out i, out hit);
                if (i != hoverRow || hit != hoverHit)
                {
                    hoverRow = i;
                    hoverHit = hit;
                    Invalidate();
                    Cursor = Clickable(i, hit) ? Cursors.Hand : Cursors.Default;
                    string t = null;
                    if (i >= 0)
                    {
                        Row r = Rows[i];
                        t = r.Primary + (string.IsNullOrEmpty(r.Secondary) ? "" : "\r\n" + r.Secondary);
                    }
                    if (t != tipText)
                    {
                        tipText = t;
                        tip.SetToolTip(this, t);
                    }
                }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hoverRow = -1;
                hoverHit = RowHit.None;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                int i; RowHit hit;
                HitTest(e.Location, out i, out hit);
                if (i >= 0) { focusRow = i; Invalidate(); }
                if (e.Button == MouseButtons.Left && Clickable(i, hit)) Raise(i, hit);
                base.OnMouseDown(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                owner.ScrollBy(e.Delta);
            }

            private void Raise(int index, RowHit hit)
            {
                EventHandler<RowEventArgs> h = RowActivated;
                if (h != null) h(this, new RowEventArgs(index, hit));
            }

            protected override void OnGotFocus(EventArgs e)
            {
                if (focusRow < 0 && Rows.Count > 0) focusRow = 0;
                Invalidate();
                base.OnGotFocus(e);
            }

            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

            protected override bool IsInputKey(Keys keyData)
            {
                Keys k = keyData & Keys.KeyCode;
                return k == Keys.Up || k == Keys.Down || k == Keys.Space || k == Keys.Enter || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (Rows.Count == 0) { base.OnKeyDown(e); return; }
                if (e.KeyCode == Keys.Down) { focusRow = Math.Min(Rows.Count - 1, focusRow + 1); e.Handled = true; Invalidate(); EnsureVisible(); return; }
                if (e.KeyCode == Keys.Up) { focusRow = Math.Max(0, focusRow - 1); e.Handled = true; Invalidate(); EnsureVisible(); return; }
                if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && focusRow >= 0 && focusRow < Rows.Count)
                {
                    Row r = Rows[focusRow];
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(r.Action) && r.ActionEnabled) Raise(focusRow, RowHit.Action);
                    else if (r.Lead != LeadKind.None && r.LeadEnabled) Raise(focusRow, RowHit.Lead);
                    return;
                }
                base.OnKeyDown(e);
            }

            private void EnsureVisible()
            {
                if (focusRow < 0 || Parent == null) return;
                Panel scroll = null;
                for (Control c = Parent; c != null; c = c.Parent)
                {
                    ScrollPanel sp = c as ScrollPanel;
                    if (sp != null) { scroll = sp; break; }
                }
                if (scroll == null) return;
                Point inScroll = scroll.PointToClient(PointToScreen(new Point(0, focusRow * RowHeight)));
                int h = RowHeight;
                if (inScroll.Y < 0) scroll.AutoScrollPosition = new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y + inScroll.Y);
                else if (inScroll.Y + h > scroll.ClientSize.Height)
                    scroll.AutoScrollPosition = new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y + inScroll.Y + h - scroll.ClientSize.Height);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(MaintColors.Panel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                int h = RowHeight;
                for (int i = 0; i < Rows.Count; i++)
                {
                    Geom geo = GetGeom(i);
                    if (geo.Row.Bottom < e.ClipRectangle.Top || geo.Row.Top > e.ClipRectangle.Bottom) continue;
                    Row r = Rows[i];
                    if (i == hoverRow && hoverHit != RowHit.Action)
                    {
                        using (SolidBrush hb = new SolidBrush(MaintColors.RowHover)) g.FillRectangle(hb, 0, geo.Row.Top + 1, Width, h - 1);
                    }
                    using (Pen line = new Pen(MaintColors.Line, 1f)) g.DrawLine(line, 0, geo.Row.Top, Width, geo.Row.Top);

                    if (r.Lead == LeadKind.Toggle) DrawToggle(g, geo.Lead, r.On, r.LeadEnabled);
                    else if (r.Lead == LeadKind.Check) DrawCheckBox(g, skin, geo.Lead, r.On, r.LeadEnabled);

                    // text column
                    Color pc = r.PrimaryColor;
                    string sec = SecondaryOf(r, geo);
                    bool hasSec = !string.IsNullOrEmpty(sec);
                    int lines = curLines;
                    int blockH = skin.Ui.Height + (hasSec ? skin.S(2) + lines * skin.Small.Height : 0);
                    int ty = geo.Text.Top + (h - blockH) / 2;
                    TextRenderer.DrawText(g, r.Primary ?? "", skin.Ui, new Rectangle(geo.Text.Left, ty, geo.Text.Width, skin.Ui.Height + skin.S(1)), pc, one);
                    if (hasSec)
                    {
                        TextFormatFlags sf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                        if (lines > 1) sf |= TextFormatFlags.WordBreak; else sf |= TextFormatFlags.SingleLine;
                        Color secColor = geo.TagInline && r.TagColor != MaintColors.Mute ? r.TagColor : r.SecondaryColor;
                        TextRenderer.DrawText(g, sec, skin.Small, new Rectangle(geo.Text.Left, ty + skin.Ui.Height + skin.S(2), geo.Text.Width, lines * skin.Small.Height + skin.S(1)),
                            secColor, sf);
                    }
                    if (!string.IsNullOrEmpty(r.Right))
                    {
                        TextRenderer.DrawText(g, r.Right, skin.Mono, geo.Right, r.RightColor,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    }
                    if (!string.IsNullOrEmpty(r.Tag) && !geo.TagInline)
                    {
                        using (GraphicsPath tp = RoundRect(geo.Tag, skin.S(4)))
                        using (Pen tpen = new Pen(r.TagColor, 1f))
                        {
                            g.DrawPath(tpen, tp);
                        }
                        TextRenderer.DrawText(g, r.Tag, skin.SmallBold, geo.Tag, r.TagColor,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    if (!string.IsNullOrEmpty(r.Action))
                    {
                        bool hot = i == hoverRow && hoverHit == RowHit.Action && r.ActionEnabled;
                        Color c = r.ActionEnabled ? MaintColors.Accent : MaintColors.Mute;
                        using (GraphicsPath ap = RoundRect(geo.Action, skin.S(5)))
                        {
                            if (hot) using (SolidBrush ab = new SolidBrush(MaintColors.RowHover)) g.FillPath(ab, ap);
                            using (Pen apen = new Pen(hot ? MaintColors.Accent : MaintColors.Line2, 1f)) g.DrawPath(apen, ap);
                        }
                        TextRenderer.DrawText(g, r.Action, skin.UiBold, geo.Action, c,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    if (Focused && ShowFocusCues && i == focusRow)
                    {
                        g.SmoothingMode = SmoothingMode.None;
                        using (Pen f = new Pen(MaintColors.Accent, 2f)) g.DrawRectangle(f, 1, geo.Row.Top + 1, Width - 3, h - 3);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                    }
                }
            }

            private void DrawToggle(Graphics g, Rectangle r, bool on, bool enabled)
            {
                Color track, knob, edge;
                if (on)
                {
                    track = enabled ? MaintColors.Accent : MaintColors.Line2;
                    edge = track;
                    knob = enabled ? MaintColors.Text : MaintColors.Mute;
                }
                else
                {
                    track = MaintColors.Panel;
                    edge = enabled ? MaintColors.Mute : MaintColors.Line2;
                    knob = enabled ? MaintColors.Sec : MaintColors.Line2;
                }
                Rectangle rc = new Rectangle(r.Left, r.Top, r.Width - 1, r.Height - 1);
                using (GraphicsPath p = RoundRect(rc, rc.Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(track)) g.FillPath(b, p);
                    using (Pen pen = new Pen(edge, 1f)) g.DrawPath(pen, p);
                }
                int d = rc.Height - skin.S(6);
                int kx = on ? rc.Right - d - skin.S(3) : rc.Left + skin.S(3);
                using (SolidBrush kb = new SolidBrush(knob)) g.FillEllipse(kb, kx, rc.Top + skin.S(3), d, d);
            }
        }

        // Dark confirmation dialog. Default button is Cancel; Esc cancels.
        private sealed class ConfirmDialog : Form
        {
            [DllImport("dwmapi.dll")]
            private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

            private readonly Skin skin;
            private readonly DarkCheck restoreBox;

            public ConfirmDialog(Skin skin, ConfirmRequest req)
            {
                this.skin = skin;
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = MaintColors.Panel;
                ForeColor = MaintColors.Text;
                Text = "Confirmar ação";

                int m = skin.S(22);
                int w = skin.S(540);
                int iw = w - m * 2;
                int y = m;

                TextBlock title = new TextBlock(skin, null, TextKind.UiBold, MaintColors.Text, true);
                title.Text = req.Title;
                Controls.Add(title);
                int th = title.MeasureHeight(iw);
                title.SetBounds(m, y, iw, th);
                y += th + skin.S(12);

                TextBlock body = new TextBlock(skin, null, TextKind.Body, MaintColors.Sec, true);
                body.Text = string.Join("\r\n", req.Lines.ToArray());
                Controls.Add(body);
                int bh = body.MeasureHeight(iw);
                body.SetBounds(m, y, iw, bh);
                y += bh + skin.S(16);

                if (req.OfferRestorePoint)
                {
                    restoreBox = new DarkCheck(skin, "Criar um ponto de restauração antes (recomendado)", req.RestorePointDefault);
                    Controls.Add(restoreBox);
                    restoreBox.SetBounds(m, y, iw, skin.S(24));
                    y += skin.S(24) + skin.S(4);
                    TextBlock note = new TextBlock(skin, null, TextKind.Small, MaintColors.Mute, true);
                    note.Text = "O Windows só cria um ponto a cada 24 horas; se já houver um recente, ele continua valendo.";
                    Controls.Add(note);
                    int nh = note.MeasureHeight(iw - skin.S(26));
                    note.SetBounds(m + skin.S(26), y, iw - skin.S(26), nh);
                    y += nh + skin.S(16);
                }

                DarkButton cancel = new DarkButton(skin, null, "Cancelar", false);
                DarkButton ok = new DarkButton(skin, null, req.ConfirmText, true);
                cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
                ok.Click += delegate { DialogResult = DialogResult.OK; };
                int bw1 = cancel.PreferredWidth();
                int bw2 = ok.PreferredWidth();
                int btnH = skin.S(34);
                ok.SetBounds(w - m - bw2, y, bw2, btnH);
                cancel.SetBounds(w - m - bw2 - skin.S(10) - bw1, y, bw1, btnH);
                Controls.Add(cancel);
                Controls.Add(ok);
                y += btnH + m;

                ClientSize = new Size(w, y);
                ActiveControl = cancel;
            }

            public bool RestorePointChecked
            {
                get { return restoreBox != null && restoreBox.Checked; }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try
                {
                    int dark = 1;
                    DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                }
                catch (Exception)
                {
                }
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }
    }
}
