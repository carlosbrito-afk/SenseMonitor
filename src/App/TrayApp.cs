using System;
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
        private readonly WmiFirmware fw;
        private readonly FanController fan;
        private readonly TempLogStore tempLog = new TempLogStore();
        private readonly MainForm form;
        private readonly NotifyIcon tray;
        private readonly TrayMenu trayMenu;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer fwTimer;
        private readonly System.Windows.Forms.Timer trimTimer;
        private readonly KeyHook keyHook;
        private readonly PowerEvents power;
        private readonly FanWatchdog watchdog = new FanWatchdog();
        private readonly FanWatchdogThread watchdogThread;
        private Icon smallIcon;
        private Icon largeIcon;

        private SensorReading latest = new SensorReading();
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
        private string stabilityDetail = InstrumentMath.NoCrashSummary(null);
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

            // Firmware at startup: the performance mode goes back to "Dia a dia" (confirmed by
            // read-back; a failure is logged, never thrown) and the fan controller asserts
            // Automático (its first tick sends one SetFanAuto). A saved Manual/Curva/Máximo is
            // not re-applied. Manual control exists only after the hardware validation passed;
            // its measured max RPM arms the controller's RPM-response guard.
            fw = new WmiFirmware();
            fan = new FanController(fw);
            fan.CustomWritesValidated = config.ManualFanAllowed;
            if (config.ManualFanAllowed) fan.SetRpmReference(config.FanMaxRpmCpu, config.FanMaxRpmGpu);
            config.FanMode = "Auto";
            lastMode = StartupMode.Apply(fw, Program.LogLine);
            lastModeRead = Environment.TickCount;

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

            // Needs the UI SynchronizationContext installed above.
            keyHook = new KeyHook(new NitroKey(), ShowMainWindow);

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

            tray.Visible = true;
            if (firstRun) ShowMainWindow();
            else
            {
                KeepFormFirst();
                ScheduleTrim();
            }
        }

        // ---- state for the pages ------------------------------------------------------------

        public Config Config { get { return config; } }
        public SensorReading Latest { get { return latest; } }
        public FanController Fan { get { return fan; } }
        public TempLogStore TempLog { get { return tempLog; } }
        public string FanStatus { get { return fan.Status; } }
        public bool KeyAvailable { get { return keyHook != null && keyHook.IsInstalled; } }
        public bool Cleaning { get { return cleaning; } }
        public string LastCleanText { get { return lastCleanText; } }
        public Icon LargeIcon { get { return largeIcon; } }
        public string StabilityDetail { get { return stabilityDetail; } }
        public PerfMode? CurrentMode { get { return lastMode; } }

        public string ModeLabel
        {
            get
            {
                if (!lastMode.HasValue) return InstrumentMath.Dash;
                if (lastMode.Value == PerfMode.Silencioso) return "Silencioso";
                if (lastMode.Value == PerfMode.Desempenho) return "Desempenho";
                return "Dia a dia";
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
            }
            SensorReading r = fw.ReadSensors();
            fan.Tick(r);
            latest = r;
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
            string tip = Display.TrayTooltip(r.CpuTempC);
            if (tip != lastTip)
            {
                tray.Text = tip;
                lastTip = tip;
            }
            form.NotifyFirmwareTick();
            watchdog.Beat(Environment.TickCount, UserDrivesFans);
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
            bool visible = form.Visible && form.WindowState != FormWindowState.Minimized;
            int want = PollPolicy.FirmwareIntervalMs(fan.Mode, form.FanMaxActive, visible, fan.FirmwareAutoConfirmed);
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
            bool ok;
            try { ok = fw.SetMode(m); }
            catch (Exception) { ok = false; }
            lastMode = ok ? m : SafeGetMode();
            return ok;
        }

        // Fan page "Máximo" (a user action), outside the controller.
        public bool SetFanMax()
        {
            if (fansStopping) return false;
            try { return fw.SetFanMax(); }
            catch (Exception) { return false; }
        }

        // Fan page leaving "Máximo": back to Auto, and the controller is told the truth.
        public bool SetFanAuto()
        {
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
                // Same reader and classification as the Saúde page: blue screens = entries
                // with a bugcheck code; unexpected shutdowns are shown only in Saúde.
                System.Collections.Generic.List<CrashEntry> entries = null;
                bool ok = false;
                try { ok = CrashHistory.TryRead(CrashWindowDays, out entries); }
                catch (Exception) { ok = false; }
                string text = Stability.VerdictDetail(ok, Stability.BlueScreenTimesUtc(entries), DateTime.UtcNow, CrashWindowDays);
                PostToUi(delegate
                {
                    crashQueryBusy = false;
                    stabilityDetail = text;
                    if (!exiting && !form.IsDisposed) form.UpdateStrip();
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
            if (result.AllFailed) Program.LogLine("Limpar RAM: nenhum comando aceito (" + result.Steps + ")");
            RaiseCleaningChanged();
            if (cleanFromTray) tray.ShowBalloonTip(4000, Display.AppName, lastCleanText, ToolTipIcon.None);
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
