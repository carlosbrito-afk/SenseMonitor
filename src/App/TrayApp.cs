using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Owns the app's lifetime: config, firmware + fan controller, tray icon, main window,
    // hardware key and system events (exit paths in TrayApp.Lifecycle.cs, menu in TrayMenu.cs).
    //
    // Threading: WmiFirmware and FanController are touched ONLY on the UI thread (the
    // firmware WinForms timer, startup, exit, user actions and marshaled power events).
    // The only exceptions are the isolated emergency writers (FanSafety): exit/suspend paths
    // and the fan watchdog thread, each with its own WmiFirmware instance.
    // SystemStats lives on the Monitor page's poller thread; RamCleaner and the crash-log
    // query run on their own short-lived threads and post back.
    //
    // Firmware polling: every 2 s, or every 12 s while the window is hidden and the fans are
    // Automático (PollPolicy). The watchdog thread writes Auto if no tick completes for 10 s
    // while the user drives the fans (Manual/Curva/Máximo).
    public sealed partial class TrayApp : ApplicationContext
    {
        private const int IsolatedTimeoutMs = 2500;
        private const int ModeRefreshMs = 30 * 1000;       // re-read the firmware mode every 30 s
        private const int CrashRefreshMs = 30 * 60 * 1000; // re-read the crash log every 30 min
        private const int CrashWindowDays = Stability.CrashWindowDays;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_KEEP = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

        private readonly Config config;
        private readonly string configPath;
        private readonly SynchronizationContext ui;
        private readonly int uiThreadId;
        private readonly GuardedFirmware fw;
        private readonly FanController fan;
        private readonly TempLogStore tempLog = new TempLogStore();
        private readonly MainForm form;
        private readonly NotifyIcon tray;
        private readonly TrayMenu trayMenu;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer fwTimer;
        private readonly System.Windows.Forms.Timer trimTimer;
        private KeyHook keyHook;
        private EnvWizardForm wizardForm;
        private EnvServices wizardServices;
        private EnvStatus envStatus = EnvStatus.NeverTested;
        private bool recoveryAlertPending;
        private bool envOwns;
        private bool showMainAfterWizard;
        private readonly PowerEvents power;
        private readonly FanWatchdog watchdog = new FanWatchdog();
        private readonly FanWatchdogThread watchdogThread;
        private Icon smallIcon;
        private Icon largeIcon;

        private SensorReading latest = new SensorReading();
        private TelemetryHub telemetry;
        private string lastTip;
        private bool cleaning;
        private string lastCleanText;
        private bool cleanFromTray;
        private bool fansStopping;   // an exit path started: no more ticks or power handling
        private bool fansHandedBack; // Auto confirmed on exit (otherwise Cleanup retries)
        private bool cleanedUp;
        private bool exiting;
        private PerfMode? lastMode;
        private int lastModeRead;
        private int lastCrashQuery;
        private bool crashQueryBusy;

        public event Action CleaningChanged;

        public TrayApp(Config config, string configPath, bool firstRun)
        {
            this.config = config;
            this.configPath = configPath;
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            }
            ui = SynchronizationContext.Current;
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
            LoadIcons();
            EvaluateEnvironment(); // identity, migration of the old flags, stale check, fan layout: before any firmware object

            // Firmware at startup: the performance mode goes back to "Dia a dia" (confirmed by
            // read-back; a failure is logged, never thrown) and the fan controller asserts
            // Automático (its first tick sends one SetFanAuto). A saved Manual/Curva/Máximo is
            // not re-applied. Manual control exists only after the hardware validation passed;
            // its measured max RPM arms the controller's RPM-response guard.
            fw = InitSafety(config); // journal, read-only mode, recovery of a previous unclean exit (TrayApp.Safety.cs)
            fan = new FanController(fw);
            fan.SetFanLayout(config.EnvFanLayout);
            // Auto is asserted (start, resume, exit) only where the fan control of THIS notebook is confirmed; elsewhere
            // the firmware is left alone. Failed writes back off 2 s, 5 s, 30 s, 5 min and alert once.
            fan.AutoAssertEnabled = config.ManualFanAllowed && !readOnly;
            fan.BackoffEnabled = true;
            fan.CustomWritesValidated = config.ManualFanAllowed && !readOnly;
            fan.LockedNote = FanLockedNote;
            if (config.ManualFanAllowed) fan.SetRpmReference(config.FanMaxRpmCpu, config.FanMaxRpmGpu);
            config.FanMode = "Auto";
            lastMode = ApplyStartupMode();
            lastModeRead = Environment.TickCount;

            InitAlerts(); // the strip asks the alert engine as soon as the window is built
            telemetry = new TelemetryHub(this); // before the pages: they acquire it
            form = new MainForm(this);
            SingleInstance.AllowShowMessage(form.Handle); // creates the (hidden) window handle

            trayMenu = new TrayMenu(this, form);
            menu = trayMenu.Menu;
            menu.Closed += delegate { KeepFormFirst(); };

            tray = new NotifyIcon();
            tray.Icon = smallIcon ?? SystemIcons.Application;
            tray.Text = Display.AppName;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowMainWindow(); };
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
            InitAlertIcons();

            // Needs the UI SynchronizationContext installed above. What a gesture of the key does lives in
            // TrayApp.Gestures.cs (InstallKeyHook below builds the gesture hook).
            InstallKeyHook();
            InitGestures();

            power = new PowerEvents();
            power.Suspending += OnSuspending;
            power.Resumed += OnResumed;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            Application.ApplicationExit += OnApplicationExit;

            trimTimer = new System.Windows.Forms.Timer();
            trimTimer.Interval = 3000;
            trimTimer.Tick += delegate { TrimNow(); };
            fwTimer = new System.Windows.Forms.Timer();
            fwTimer.Interval = PollPolicy.FastMs;
            fwTimer.Tick += delegate { FirmwareTick(); };
            fwTimer.Start();
            FirmwareTick(); // first tick now: asserts fan Auto and fills the tooltip
            lastCrashQuery = Environment.TickCount;
            QueryCrashes();
            watchdogThread = new FanWatchdogThread(watchdog, IsolatedTimeoutMs, Program.LogLine);
            // Output files of a DISM that outlived an earlier run: drop those older than a day.
            ThreadPool.QueueUserWorkItem(delegate { RepairRunner.CleanOldDismLogs(InstallPaths.SecureTempDir(), DateTime.UtcNow); });

            StartUpdater();
            tray.Visible = true;
            // A failed start-up hand-back of the fans is announced whatever else the start does (first run, read-only mode,
            // a profile that needs no offer): independent of the environment offer.
            PostToUi(RaisePendingRecoveryAlert);
            if (firstRun)
            {
                // The first thing a new install does: the environment test (the main window follows it, also
                // when the wizard is cancelled).
                showMainAfterWizard = true;
                PostToUi(delegate { OpenEnvWizard(); });
            }
            else
            {
                KeepFormFirst();
                ScheduleTrim();
                PostToUi(OfferEnvironmentTest);
            }
        }

        // ---- environment test (profile of this notebook) -------------------------------------------------

        // At start: who is this notebook (model + BIOS), migrate the single-machine flags, mark the profile
        // stale when it belongs to another PC or BIOS, and tell the firmware layer which fans exist.
        private void EvaluateEnvironment()
        {
            DetectionRaw id = null;
            try { id = EnvProbe.ReadIdentity(); }
            catch (Exception ex) { Program.LogError(ex); }
            MergeKeptUndoRecords();
            // The machine identity goes into the markers (a marker is replayed only on the machine that wrote it).
            Safety.Fingerprint = MachineFingerprint.Make(id == null ? null : id.Model, id == null ? null : id.Bios);
            bool hadProfile = config.Env != null;
            envStatus = EnvStartup.Apply(config, id == null ? null : id.Model, id == null ? null : id.Bios, EnvSession.NowUtc());
            FanLayoutStore.Set(envStatus == EnvStatus.Stale ? FanLayout.Both : config.EnvFanLayout);
            if (!hadProfile && config.Env != null) SaveConfigQuietly(); // the migrated profile
        }

        // Undo records written under %ProgramData% by an installer run that was cancelled or failed after disabling Acer
        // services: they go into the config (so Settings can undo them) and the file is removed.
        private void MergeKeptUndoRecords()
        {
            try
            {
                UndoStore store = new UndoStore();
                List<ServiceUndo> kept = store.Load();
                if (kept.Count == 0) return;
                EnvApply.MergeServiceUndo(config, kept);
                config.Save(configPath);
                store.Clear();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // The environment wizard owns the firmware while a writing step runs: the fan controller hands the fans back to
        // Automático and then writes nothing (except Auto at 90 C), and mode changes are refused, until it lets go.
        public bool EnvOwnsFirmware { get { return envOwns; } }

        private void SetEnvOwnsFirmware(bool own)
        {
            if (envOwns == own) return;
            envOwns = own;
            if (own)
            {
                if (!form.IsDisposed) form.ChooseFanAuto(); // drops Manual / Curva / Máximo through the page
                fan.SetPaused(true);
            }
            else
            {
                fan.SetPaused(false);
            }
            Safety.Journal.Write(JournalKind.State, own ? "teste de ambiente assumiu o firmware: ventoinha e modo do app em pausa" : "teste de ambiente devolveu o firmware ao app");
            if (!form.IsDisposed) form.RefreshModes();
        }

        // Start-up recovery could not hand the fans back: the red alert, once, not at every start.
        private void RaisePendingRecoveryAlert()
        {
            if (exiting || !recoveryAlertPending) return;
            recoveryAlertPending = false;
            RaiseFanUnconfirmed();
        }

        // Not the first run: a notebook that changed (or was never tested) is offered the wizard through a
        // balloon; tests postponed earlier get the one-time reminder.
        private void OfferEnvironmentTest()
        {
            if (exiting) return;
            if (readOnly && envStatus != EnvStatus.Stale) return;
            if (envStatus == EnvStatus.Stale)
            {
                ShowEnvBalloon(EnvCheck.StaleNote, ToolTipIcon.Warning);
            }
            else if (envStatus == EnvStatus.NeverTested)
            {
                ShowEnvBalloon(EnvCheck.NeverNote, ToolTipIcon.Info);
            }
            else if (EnvReminder.Due(config))
            {
                config.EnvReminderShown = true;
                SaveConfigQuietly();
                ShowEnvBalloon(EnvReminder.Text, ToolTipIcon.Info);
            }
        }

        // Configurações → "Refazer teste de ambiente", the first run, the balloon.
        public void OpenEnvWizard()
        {
            if (exiting) return;
            if (wizardForm != null && !wizardForm.IsDisposed)
            {
                wizardForm.Activate();
                return;
            }
            EnvServices svc = new EnvServices(config, configPath, readOnly, OnEnvironmentChanged, SetEnvOwnsFirmware);
            wizardServices = svc;
            EnvWizardTool.Active = svc;
            EnvWizardForm f = new EnvWizardForm(svc, EnvWizardSteps.Standard(), svc.SaveOutcome, null);
            if (largeIcon != null) f.Icon = largeIcon;
            f.FormClosed += delegate
            {
                bool saved = f.DialogResult == DialogResult.OK;
                wizardForm = null;
                wizardServices = null;
                EnvWizardTool.Active = null;
                SetEnvOwnsFirmware(false);
                if (keyHook != null) keyHook.Reset();
                if ((saved || showMainAfterWizard) && !exiting) ShowMainWindow();
                showMainAfterWizard = false;
            };
            wizardForm = f;
            f.Show();
            f.Activate();
        }

        // A wizard run was saved: pages, tray menu, fan controller, firmware layout and key hook follow the profile.
        private void OnEnvironmentChanged()
        {
            // EnvApply decided: a profile saved without a fingerprint stays stale (nothing it confirms is unlocked).
            envStatus = config.EnvStale ? EnvStatus.Stale : EnvStatus.Fresh;
            fw.SetLayout(config.EnvFanLayout);
            fan.SetFanLayout(config.EnvFanLayout);
            fan.AutoAssertEnabled = config.ManualFanAllowed && !readOnly;
            fan.CustomWritesValidated = config.ManualFanAllowed && !readOnly;
            if (config.ManualFanAllowed) fan.SetRpmReference(config.FanMaxRpmCpu, config.FanMaxRpmGpu);
            else fan.SetRpmReference(0, 0);
            fan.LockedNote = FanLockedNote;
            InstallKeyHook();
            if (!form.IsDisposed) form.ApplyCapabilities();
            if (trayMenu != null) trayMenu.ApplyCapabilities();
            lastMode = SafeGetMode();
            lastModeRead = Environment.TickCount;
        }

        // The hook listens only for the key code(s) the profile confirmed (or the one the app always used
        // before the first test); none at all when the notebook has no such key.
        private void InstallKeyHook()
        {
            if (keyHook != null)
            {
                keyHook.Dispose();
                keyHook = null;
            }
            KeyCandidate[] cand = KeyCandidates.ForProfile(config.EnvStale ? null : config.Env);
            if (cand.Length == 0) return;
            keyHook = KeyHook.ForGestures(new NitroKey(cand), OnKeyGesture);
            ApplyRecognizerOptions(); // long press / double / triple follow gestos.json
        }

        // Modes the Modos page and the tray menu offer: only those the environment test confirmed.
        public List<PerfMode> OfferedModes()
        {
            if (config.EnvStale || config.Env == null) return new List<PerfMode>();
            return config.Env.ConfirmedModes();
        }

        // Why the list of modes is empty (null when there is something to choose).
        public string ModesNote
        {
            get
            {
                if (OfferedModes().Count > 0) return null;
                if (config.EnvStale) return EnvCheck.StaleNote;
                if (config.Env == null) return EnvCheck.NeverNote;
                string r = FirstSentence(config.Env.Reason(Cap.ModeBalanced));
                if (config.Env.State(Cap.ModeBalanced) == CapState.Unavailable && !string.IsNullOrEmpty(r)) return r;
                return "Nenhum modo liberado ainda: refaça o teste de ambiente em Configurações.";
            }
        }

        public string EnvStatusText
        {
            get
            {
                if (config.EnvStale) return EnvCheck.StaleNote;
                if (config.Env == null) return "Ainda não foi feito. Rode o teste para liberar modos e ventoinhas.";
                CapabilityProfile p = config.Env;
                int modes = p.ConfirmedModes().Count;
                string fans = p.Allows(Cap.FanControl) ? "controle de ventoinha liberado" : "controle de ventoinha bloqueado";
                return (string.IsNullOrEmpty(p.Model) ? "Este notebook" : p.Model) + " · " + modes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (modes == 1 ? " modo liberado" : " modos liberados") + " · " + fans;
            }
        }

        public bool EnvStatusIsWarning
        {
            get { return config.EnvStale || config.Env == null || config.Env.PendingTests().Count > 0; }
        }

        public string KeyStatusText
        {
            get
            {
                CapabilityProfile p = config.Env;
                if (p != null && p.State(Cap.Key) == CapState.Unavailable) return "Tecla do teclado: este notebook não tem. Nenhuma tecla é observada.";
                if (!KeyAvailable) return "Tecla do teclado: não disponível. Só o código dessa tecla seria observado.";
                return "Tecla do teclado: ativa (toques e atalhos em Configurações > Tecla). Só esse código é observado; nenhuma outra tecla é registrada.";
            }
        }

        // Exit / crash: a wizard test in flight is told to stop (it ends in Automático / Dia a dia) and the window goes away.
        private void CancelEnvWizard()
        {
            EnvWizardTool.CancelCurrent();
            EnvWizardTool.RestoreCurrent(); // only what the wizard touched; nothing touched = no write
            EnvWizardForm f = wizardForm;
            if (f != null && !f.IsDisposed)
            {
                try { f.ForceClose(); }
                catch (Exception) { }
            }
        }

        // ---- state for the pages ------------------------------------------------------------

        public Config Config { get { return config; } }
        public SensorReading Latest { get { return latest; } }
        internal TelemetryHub Telemetry { get { return telemetry; } }
        public FanController Fan { get { return fan; } }
        public TempLogStore TempLog { get { return tempLog; } }
        public string FanStatus { get { return fan.Status; } }
        public bool KeyAvailable { get { return keyHook != null && keyHook.IsInstalled; } }
        public bool Cleaning { get { return cleaning; } }
        public string LastCleanText { get { return lastCleanText; } }
        public Icon LargeIcon { get { return largeIcon; } }
        public PerfMode? CurrentMode { get { return lastMode; } }

        public string ModeLabel
        {
            get
            {
                if (!lastMode.HasValue) return InstrumentMath.Dash;
                return ModeCatalog.Name(lastMode.Value);
            }
        }

        // Hottest valid temperature (curve marker of the fan page), or null.
        public double? HottestTemp()
        {
            double? c = InstrumentMath.Sanitize(latest.CpuTempC);
            double? g = InstrumentMath.Sanitize(latest.GpuTempC);
            if (c.HasValue && g.HasValue) return Math.Max(c.Value, g.Value);
            return c.HasValue ? c : g;
        }

        // ---- firmware (UI thread only) ------------------------------------------------------

        private void FirmwareTick()
        {
            if (fansStopping) return;
            if (watchdog.TakePendingNotify())
            {
                // The watchdog handed the fans to the firmware while this thread was stuck.
                fan.NotifyWatchdogAuto();
                Program.LogLine("Ventoinhas: nenhuma leitura por 10 s com controle manual; o vigia voltou ao automático.");
                Safety.Journal.Write(JournalKind.State, "o vigia interno devolveu a ventoinha ao automático (nenhuma leitura por 10 s)");
            }
            SensorReading r = fw.ReadSensors();
            fan.Tick(r);
            if (fan.TakeAutoFailAlert())
            {
                Safety.Journal.Write(JournalKind.State, "não consegui confirmar a ventoinha em automático; novas tentativas em intervalos maiores");
                RaiseFanUnconfirmed(); // through the alert engine: quiet hours, de-duplication, icon dot
            }
            latest = r;
            AlertsTick(r); // alerts in two channels (TrayApp.Alerts.cs)
            telemetry.OnFirmwareTick(r);
            WidgetTick(); // a full-screen game in front hides the widget, and its end brings it back
            tempLog.Append(DateTime.Now, r); // one line per minute; never throws
            int now = Environment.TickCount;
            if (unchecked(now - lastModeRead) >= ModeRefreshMs)
            {
                lastModeRead = now;
                lastMode = SafeGetMode();
            }
            if (unchecked(now - lastCrashQuery) >= CrashRefreshMs)
            {
                lastCrashQuery = now;
                QueryCrashes();
            }
            string tip = AlertAwareTip(Display.TrayTooltip(r.CpuTempC));
            if (tip != lastTip)
            {
                tray.Text = tip;
                lastTip = tip;
            }
            form.NotifyFirmwareTick();
            watchdog.Beat(Environment.TickCount, UserDrivesFans);
            SafetyTick();
            UpdatePollRate();
        }

        // Manual/Curva (controller) or Máximo (fan page): the watchdog guards these.
        private bool UserDrivesFans
        {
            get { return fan.Mode != FanUserMode.Auto || (!form.IsDisposed && form.FanMaxActive); }
        }

        // A fan mode was just chosen (page tab or tray): arm the watchdog with the new state now
        // (Manual/Curva/Máximo picked while 12 s ticks were running) and switch the poll rate.
        public void OnFanModeChosen()
        {
            if (fansStopping || form == null || form.IsDisposed) return;
            watchdog.Beat(Environment.TickCount, UserDrivesFans);
            UpdatePollRate();
        }

        // 2 s or 12 s (PollPolicy). Called after each tick, on show/hide and on tray fan choices.
        public void UpdatePollRate()
        {
            if (fwTimer == null || form == null || form.IsDisposed || fansStopping) return;
            // The widget on screen also wants fresh readings (2 s), window hidden or not.
            bool visible = (form.Visible && form.WindowState != FormWindowState.Minimized) || WidgetShownNow;
            int want = PollPolicy.FirmwareIntervalMs(fan.Mode, form.FanMaxActive, visible, fan.FirmwareAutoConfirmed, telemetry.ProofActive);
            if (fwTimer.Interval != want) fwTimer.Interval = want;
        }

        private PerfMode? SafeGetMode()
        {
            try { return fw.GetMode(); }
            catch (Exception) { return null; }
        }

        // Modes page / tray: read the firmware mode (also refreshes the strip's MODO).
        public PerfMode? ReadMode()
        {
            lastMode = SafeGetMode();
            return lastMode;
        }

        // Modes page only (a user action): SetMode verifies by read-back.
        public bool SetMode(PerfMode m)
        {
            if (fansStopping) return false;
            if (envOwns || !config.ModeAllowed(m)) return false; // only what the environment test confirmed, never while the wizard owns the firmware
            bool ok;
            PerfMode? before = SafeGetMode(); // read before write: lastMode can be 30 s old (Fn key, other tools)
            try { ok = fw.SetMode(m); }
            catch (Exception) { ok = false; }
            lastMode = ok ? m : SafeGetMode();
            if (ok)
            {
                telemetry.OnModeSwitched(before, m); // starts the 60 s / 30-60 s comparison
                UpdatePollRate();                    // and keeps the tick at 2 s for it, window hidden or not
            }
            return ok;
        }

        // Fan page "Máximo" (a user action), outside the controller.
        public bool SetFanMax()
        {
            if (fansStopping || envOwns) return false;
            fan.MarkDirty(); // the fans leave Auto outside the controller: the hand-back must be written
            try { return fw.SetFanMax(); }
            catch (Exception) { return false; }
        }

        // Fan page leaving "Máximo": back to Auto, and the controller is told the truth.
        public bool SetFanAuto()
        {
            // The wizard owns the firmware: the controller is already in Auto and paused, nothing to hand back, no write.
            if (envOwns) return true;
            bool ok;
            try { ok = fw.SetFanAuto(); }
            catch (Exception) { ok = false; }
            if (ok) fan.NotifyFirmwareAuto();
            return ok;
        }

        public bool? IsFanMax()
        {
            try { return fw.IsFanMax(); }
            catch (Exception) { return null; }
        }

        // ---- crash log (pool thread, posted back) ------------------------------------------------

        private void QueryCrashes()
        {
            if (crashQueryBusy || exiting) return;
            crashQueryBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Same reader as the Saúde page. The result only feeds the one-time "restarted
                // unexpectedly" alert; the strip never writes crash text.
                System.Collections.Generic.List<CrashEntry> entries = null;
                bool ok = false;
                try { ok = CrashHistory.TryRead(CrashWindowDays, out entries); }
                catch (Exception) { ok = false; }
                PostToUi(delegate
                {
                    crashQueryBusy = false;
                    if (!exiting) OnCrashesRead(entries, ok);
                });
            });
        }

        // ---- window / tray -----------------------------------------------------------------

        // Hardware key, tray click, second launch: idempotent show + restore + activate.
        public void ShowMainWindow()
        {
            if (exiting || form.IsDisposed) return;
            trimTimer.Stop();
            form.ShowAndActivate();
        }

        public void ShowPage(string key)
        {
            if (exiting || form.IsDisposed) return;
            form.SelectPage(key, true);
            ShowMainWindow();
        }

        // Once the window is hidden: collect and trim the working set (the app idles in the tray).
        public void ScheduleTrim()
        {
            if (exiting) return;
            trimTimer.Stop();
            trimTimer.Start();
        }

        private void TrimNow()
        {
            trimTimer.Stop();
            if (exiting || form.Visible) return;
            KeepFormFirst();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        // taskkill without /F (and similar tools) post WM_CLOSE only to the process's first
        // top-level window in z-order. Keeping the hidden main window first makes that request
        // an orderly exit (fans to Auto) instead of silently destroying the tray icon's window.
        private void KeepFormFirst()
        {
            if (exiting || form.IsDisposed || form.Visible) return;
            SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_KEEP);
        }

        // ---- RAM cleaner -------------------------------------------------------------------

        public void CleanRam(bool fromTray)
        {
            if (cleaning || exiting) return;
            if (readOnly)
            {
                Safety.Journal.Write(JournalKind.Blocked, "limpar RAM: somente leitura, nada foi executado");
                return;
            }
            Safety.Journal.Write(JournalKind.Action, "limpar RAM: iniciada");
            cleaning = true;
            cleanFromTray = fromTray;
            RaiseCleaningChanged();
            Thread t = new Thread(delegate()
            {
                RamCleanResult result = null;
                try { result = RamCleaner.CleanDetailed(); }
                catch (Exception ex) { Program.LogError(ex); }
                ui.Post(delegate { CleanDone(result ?? new RamCleanResult()); }, null);
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.RamCleaner";
            t.Start();
        }

        private void CleanDone(RamCleanResult result)
        {
            if (cleanedUp) return;
            cleaning = false;
            lastCleanText = Display.CleanResultText(result);
            Safety.Journal.Write(JournalKind.Action, "limpar RAM: terminada" + (result.AllFailed ? " (nenhum comando aceito)" : ""));
            if (result.AllFailed) Program.LogLine("Limpar RAM: nenhum comando aceito (" + result.Steps + ")");
            RaiseCleaningChanged();
            if (cleanFromTray)
            {
                lastBalloon = BalloonKind.None; // a click on this balloon is not an answer to the previous one (alert / wizard offer)
                tray.ShowBalloonTip(4000, Display.AppName, lastCleanText, ToolTipIcon.None);
            }
        }

        private void RaiseCleaningChanged()
        {
            Action h = CleaningChanged;
            if (h != null) h();
        }

        // ---- config ------------------------------------------------------------------------

        public void SaveConfigQuietly()
        {
            try { config.Save(configPath); }
            catch (Exception) { }
        }
    }
}
