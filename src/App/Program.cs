using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    public static class Program
    {
        private const int ASFW_ANY = -1;
        private const long LogMaxBytes = 256 * 1024;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static TrayApp app;
        private static int crashing;
        // True while the fan validation tool runs (the crash path then writes Auto itself).
        internal static volatile bool Validating;
        // True while the performance test runs: the crash path then restores Dia a dia + Automático.
        internal static volatile bool PerfTesting;
        // --auto: nobody is at the screen, so the crash path shows no dialog and exits.
        internal static volatile bool Unattended;

        // True once the crash path started (the update "started ok" flag is never written then).
        internal static bool IsCrashing { get { return Thread.VolatileRead(ref crashing) != 0; } }

        public static string LogPath
        {
            get
            {
                return Path.Combine(UserData.Roaming, "erro.log");
            }
        }

        // LOAD_LIBRARY_SEARCH_SYSTEM32: from here on LoadLibrary and P/Invoke no longer search the exe's
        // folder or the current directory (a downloaded exe sits in a folder anybody can write to).
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        [STAThread]
        public static void Main()
        {
            try { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); }
            catch (Exception) { } // an OS without the API simply keeps the default search
            // An elevated process that runs from outside the install folder looks for no satellite resource folder
            // (<exe folder>\<culture>\): the invariant UI culture. All the app's text is hard-coded pt-BR, so nothing shown changes.
            try
            {
                if (CulturePolicy.UseInvariantUi(InstallSystem.IsElevated(), UpdatePolicy.IsInstalledCopy(Application.ExecutablePath, InstallPaths.InstalledExe)))
                {
                    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
                    Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
                }
            }
            catch (Exception) { }
            UpdateLog.Fallback = LogLine;

            // The fan sentinel and its launcher are windowless helper modes of this exe (see Sentinel).
            int sentinelExit;
            if (Sentinel.TryDispatch(Environment.GetCommandLineArgs(), out sentinelExit))
            {
                Environment.ExitCode = sentinelExit;
                return;
            }

            // --sair: asks the running app to leave through its named exit event, then ends. No window, no elevation question
            // (the event's own access list decides who may signal: Administrators and SYSTEM). Exit code 0 = signalled, 1 = the
            // event was not there or not reachable (no app running, an older app, or not an administrator).
            if (StartupRouter.IsExitRequest(Environment.GetCommandLineArgs()))
            {
                Environment.ExitCode = ExitEvent.Signal() ? 0 : 1;
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Crash(e.Exception, true); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Crash(e.ExceptionObject as Exception, false); };

            // Command-line tools (elevated like the app itself):
            //   --registrar-inicio        points the autostart task at the installed copy (deploy.ps1)
            //   --validar-ventoinha [pct] hardware validation of fan control and modes, run once
            //                             with the user present; unlocks Manual/Curva/Máximo
            string[] args = Environment.GetCommandLineArgs();
            // Install / uninstall / update-apply family (--instalar, --desinstalar, --aplicar-atualizacao,
            // and the "install it?" offer): decided in Core, run by InstallTool (new files).
            StartupRoute route = StartupRouter.Decide(args, Application.ExecutablePath, InstallPaths.InstalledExe, File.Exists(InstallPaths.InstalledExe));
            // Before anything is elevated (the manifest is asInvoker): install checks, UAC question, relaunch.
            if (FirstStageRunner.Run(args, route)) return;
            if (route.Action != StartupAction.RunApp && InstallTool.Handle(route)) return;
            // Started by --autoteste to prove that a second copy is turned away: exit code 0 =
            // the single-instance mutex was already taken (turned away), 1 = it was free.
            if (ToolArgs.Has(args, AutoProbes.ProbeSwitch))
            {
                bool got = SingleInstance.TryAcquire();
                SingleInstance.Release();
                Environment.ExitCode = got ? 1 : 0;
                return;
            }
            if (HasArg(args, "--registrar-inicio"))
            {
                string error = InstallTasks.RegisterAll(); // autostart + fan sentinel + update helper tasks
                LogLine(error == null ? "Inicialização automática registrada para " + InstallPaths.InstalledExe : "Inicialização automática: " + error);
                Environment.ExitCode = error == null ? 0 : 1;
                return;
            }
            if (HasArg(args, "--validar-ventoinha"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Environment.ExitCode = FanValidationTool.Run(PctArg(args, "--validar-ventoinha", 50));
                return;
            }
            //   --teste-ambiente          the environment test wizard (support): detection, conflicts, key,
            //                             fans and modes of this notebook; saves the profile in config.json
            //   --teste-desempenho [--auto]  proves whether each mode changes performance, heat and
            //                                noise (6-8 min, CPU at 100 %); ends in Dia a dia + Automático
            //   --autoteste [--auto]         checks that every function works here (about 1 min,
            //                                nothing permanent); --auto = no dialog at all, exit 0 =
            //                                ran to the end, nonzero = could not run / stopped
            if (ToolArgs.Has(args, "--teste-ambiente"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Environment.ExitCode = EnvWizardTool.Run();
                return;
            }
            bool perf = ToolArgs.Has(args, "--teste-desempenho");
            bool self = ToolArgs.Has(args, "--autoteste");
            if (perf || self)
            {
                bool unattended = ToolArgs.Unattended(args);
                Unattended = unattended;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Environment.ExitCode = perf ? PerfTestTool.Run(unattended) : AutoTestTool.Run(unattended, ToolArgs.WithRamClean(args));
                return;
            }

            if (!SingleInstance.TryAcquire())
            {
                // Let the first instance take the foreground, then ask it to show itself.
                AllowSetForegroundWindow(ASFW_ANY);
                SingleInstance.SignalFirstInstance();
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string path = Config.DefaultPath;
                bool firstRun = !File.Exists(path); // window opens only on the very first run
                Config config = Config.Load(path);
                app = new TrayApp(config, path, firstRun);
                Application.Run(app);
            }
            finally
            {
                SingleInstance.Release();
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int PctArg(string[] args, string name, int fallback)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                int v;
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    return v;
                }
            }
            return fallback;
        }

        // Records an exception without closing the app: type + message only, with the profile
        // path and user name replaced (erro.log never holds personal data).
        public static void LogError(Exception ex)
        {
            if (ex != null) Append(LogText.DescribeForCurrentUser(ex));
        }

        // One-line note (scrubbed the same way).
        public static void LogLine(string line)
        {
            if (!string.IsNullOrEmpty(line)) Append(LogText.ScrubForCurrentUser(line));
        }

        private static void Append(string text)
        {
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > LogMaxBytes) fi.Delete();
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + text + "\r\n\r\n", Encoding.UTF8);
            }
            catch (Exception)
            {
                // nowhere else to report
            }
        }

        private static void Crash(Exception ex, bool canExit)
        {
            if (Interlocked.Exchange(ref crashing, 1) != 0) return;
            LogError(ex);
            if (app != null) app.EmergencyFansAuto(); // isolated writer off the UI thread
            else if (Validating)
            {
                FanValidationTool.CancelCurrent();
                FanSafety.SetAutoIsolated(2500);
            }
            else if (EnvWizardTool.Active != null)
            {
                // The environment wizard of a standalone run: stop its fan/mode test, then the isolated safe state.
                EnvWizardTool.CancelCurrent();
                EnvWizardTool.RestoreCurrent(); // only what the wizard touched

            }
            else if (PerfTesting)
            {
                // First the load: raise the stop flag of its threads (never blocks, needs no
                // WMI), then ask the routine to stop, then restore from an isolated writer.
                PerfTestTool.StopLoadNow();
                PerfTestTool.CancelCurrent();
                FanSafety.SetSafeStateIsolated(4000);
            }
            if (Unattended)
            {
                // Nobody to click OK: no dialog; leave with a nonzero code (the state was restored above).
                Environment.Exit(1);
            }
            try
            {
                MessageBox.Show("O " + Display.AppName + " encontrou um erro e vai fechar.\n\nDetalhes em:\n" + LogPath,
                    Display.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
            if (canExit && app != null)
            {
                try { app.ExitApp(); }
                catch (Exception) { Environment.Exit(1); }
            }
        }

        // Brings our window to the front even when another app has the foreground (the
        // hardware key arrives while some other program is active).
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint me = GetCurrentThreadId();
            uint other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, other, false);
            }
        }
    }
}
