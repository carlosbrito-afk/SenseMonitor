using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Command-line side of install / uninstall / update-apply. The decisions (which command, which
    // steps in which order, which paths are acceptable) live in Core and are unit-tested; this class
    // only asks the user and runs the steps.
    //   SenseMonitor.exe --instalar [--passo=reconhecer]   nothing installed: the installer window (InstallerWizard);
    //                                                      installed: replace/repair with this file (no window)
    //   SenseMonitor.exe --desinstalar [--manter-dados]    the uninstall window, then the steps
    //   SenseMonitor.novo.exe --aplicar-atualizacao        (the update helper; no window, journal only)
    internal static class InstallTool
    {
        private const string Caption = "SenseMonitor";
        private const int CloseWaitMs = 30 * 1000;

        // True = handled (Environment.ExitCode is set, the caller returns); false = go on and run
        // the tray app normally (the user chose "Abrir sem instalar").
        public static bool Handle(StartupRoute route)
        {
            switch (route.Action)
            {
                case StartupAction.ApplyUpdate:
                    // Hidden helper: a crash must end it quietly (no dialog nobody would see).
                    Program.Unattended = true;
                    Environment.ExitCode = UpdateHelper.Run(Application.ExecutablePath, UpdateKey.PublicKeyXml);
                    return true;
                case StartupAction.Install:
                    Prepare();
                    return RunInstall(route.Stage, route.ExpectedSha256, false);
                case StartupAction.Uninstall:
                    Prepare();
                    Environment.ExitCode = Uninstall(route.KeepData);
                    return true;
                case StartupAction.OfferInstall:
                    // Reached already elevated (started from an elevated prompt); the normal double-click opens the
                    // installer window in the non-elevated first stage (FirstStageRunner).
                    Prepare();
                    return RunInstall(InstallerStage.Welcome, null, true);
                case StartupAction.OfferHandOff:
                    // Reached already elevated (an administrator prompt); the normal double-click asks this in the
                    // non-elevated first stage. Nothing downloaded is elevated unless the user picks "reinstall".
                    Prepare();
                    DialogResult hr = FirstStageRunner.AskHandOff();
                    if (hr == DialogResult.Yes) FirstStageRunner.HandOffToInstalled();
                    else if (hr == DialogResult.No)
                    {
                        if (FirstStageRunner.ReinstallAllowed(Application.ExecutablePath, false)) Environment.ExitCode = Install(true);
                    }
                    return true;
                default:
                    MessageBox.Show("Argumentos inválidos.\n\nUso: --instalar, --desinstalar [--manter-dados].", Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Environment.ExitCode = 2;
                    return true;
            }
        }

        // Nothing installed: the window. Another copy installed (or this IS the installed copy): the quick path.
        private static bool RunInstall(InstallerStage stage, string expectedSha256, bool offered)
        {
            string source = Application.ExecutablePath;
            bool same = UpdatePolicy.IsInstalledCopy(source, InstallPaths.InstalledExe);
            InstallMode mode = InstallerFlow.Mode(InstallSystem.InstallExists, same);
            if (mode != InstallMode.Wizard)
            {
                Environment.ExitCode = Install(offered);
                return true;
            }
            if (!InstallSystem.IsElevated())
            {
                // The first stage already relaunches elevated; this is the last guard.
                FirstStageRunner.ElevateSelf(StartupRouter.InstallSwitch + (stage == InstallerStage.Detect ? " " + StartupRouter.StageDetectSwitch : "") + (expectedSha256 == null ? "" : " " + StartupRouter.ShaSwitch(expectedSha256)));
                return true;
            }
            // true = "Abrir sem instalar": this process goes on as the tray app.
            return !InstallerWizard.RunElevated(stage, expectedSha256);
        }

        private static void Prepare()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }

        private static void Info(string text)
        {
            MessageBox.Show(text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Error(string text)
        {
            MessageBox.Show(text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // The first stage (FirstStageRunner) already relaunches elevated; this is the last guard if a
        // step is reached without elevation: ask Windows for an elevated copy and let that one do the work.
        private static bool EnsureElevated(string switches)
        {
            if (InstallSystem.IsElevated()) return true;
            // Not a second place that elevates: the one function that does (with its companion check) is used.
            FirstStageRunner.ElevateSelf(switches);
            return false;
        }

        // ---- install without the window (replace / repair) ---------------------------------------------------

        // The same engine as the window (steps, shortcuts, undo when a step fails), with question boxes: used when another
        // copy is already installed ("update/reinstall with this file") or when the installed copy repairs itself.
        private static int Install(bool offered)
        {
            if (!EnsureElevated(StartupRouter.InstallSwitch)) return offered ? 1 : 0;
            string source = Application.ExecutablePath;
            string installed = InstallPaths.InstalledExe;
            bool sameCopy = UpdatePolicy.IsInstalledCopy(source, installed);
            int myPid = Process.GetCurrentProcess().Id;
            // Held FIRST, before any question: the downgrade box below can wait for a click, and the file it copies must be
            // the one that was opened (see SourceHold). A repair from the installed copy copies nothing.
            SourceHold hold = null;
            if (!sameCopy)
            {
                string holdError;
                hold = SourceHold.Open(source, out holdError);
                if (hold == null)
                {
                    Error("Não foi possível instalar com segurança: " + holdError + ".");
                    return 1;
                }
            }
            try
            {
                return InstallHeld(source, hold, sameCopy, myPid);
            }
            finally
            {
                if (hold != null) hold.Dispose();
            }
        }

        private static int InstallHeld(string source, SourceHold hold, bool sameCopy, int myPid)
        {
            bool running = ProcessScan.AppIds(myPid).Count > 0;
            bool hadInstall = InstallSystem.InstallExists;

            if (!sameCopy && hadInstall)
            {
                UpdateVersion mine;
                UpdateVersion.TryParse(AppVersion.Text, out mine);
                UpdateVersion there = UpdateHelper.InstalledVersion();
                if (InstallPlanner.NeedsDowngradeConfirmation(there, mine))
                {
                    DialogResult d = MessageBox.Show("A versão instalada (" + there + ") é mais nova que este arquivo (" + AppVersion.Text + ").\n\nInstalar a versão antiga mesmo assim?",
                        Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (d != DialogResult.Yes) return 1;
                }
            }

            List<InstallStep> steps = InstallPlanner.ForInstall(running, sameCopy);
            UpdateLog.Append("instalação " + AppVersion.Text + ": " + string.Join(", ", steps.ConvertAll<string>(delegate(InstallStep s) { return s.ToString(); }).ToArray()));
            RealInstallOps ops = new RealInstallOps(hold, myPid, AppVersion.Text, EmbeddedConfig());
            InstallOutcome o = InstallEngine.Run(ops, steps, hadInstall, running, null, null);
            if (!o.Ok)
            {
                UpdateLog.Append("instalação, passo " + o.FailedStep + ": " + o.Error);
                string text = o.Error + (o.RollbackClean ? "\n\nO que já tinha sido feito foi desfeito." : "\n\nNão consegui desfazer tudo:\n- " + string.Join("\n- ", o.RollbackProblems.ToArray()));
                Error(text);
                return 1;
            }
            foreach (string w in o.Warnings) UpdateLog.Append("instalação, aviso: " + w);

            string msg = "SenseMonitor " + AppVersion.Text + " instalado.\n\nEle já está na bandeja do relógio e abre sozinho quando você entrar no Windows. Para remover: Configurações do Windows > Apps, ou o atalho Desinstalar SenseMonitor no Menu Iniciar.";
            if (o.Warnings.Count > 0) msg += "\n\nAtenção:\n- " + string.Join("\n- ", o.Warnings.ToArray());
            Info(msg);
            return o.Warnings.Count == 0 ? 0 : 1;
        }

        internal static byte[] EmbeddedConfig()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SenseMonitor.app.config"))
                {
                    if (s == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- uninstall -------------------------------------------------------------------------

        private static int Uninstall(bool keepDataFlag)
        {
            if (!EnsureElevated(StartupRouter.UninstallSwitch + (keepDataFlag ? " " + StartupRouter.KeepDataSwitch : ""))) return 0;
            // Started from Settings (through its own task): the app already asked, and left the answer in the admin-only folder.
            UninstallInstructions pre = ConsumeUninstallInstructions();
            UninstallChoice choice;
            if (pre != null && !keepDataFlag) choice = pre.KeepData ? UninstallChoice.Keep : UninstallChoice.Erase;
            else
            {
                UninstallSummary summary = UninstallSummary.Build(InstallPaths.InstallDir, InstallSystem.AcerServicesPendingRestore());
                // The window lists what goes and asks "Manter suas configurações?"; --manter-dados answers it beforehand.
                choice = UninstallForm.Ask(summary, keepDataFlag);
            }
            if (choice == UninstallChoice.Cancel) return 1;
            bool keepData = choice == UninstallChoice.Keep;

            int myPid = Process.GetCurrentProcess().Id;
            List<string> warnings = new List<string>();
            List<UninstallStep> steps = InstallPlanner.ForUninstall(keepData);
            UpdateLog.Append("desinstalação: " + string.Join(", ", steps.ConvertAll<string>(delegate(UninstallStep s) { return s.ToString(); }).ToArray()));
            foreach (UninstallStep step in steps)
            {
                string err = null;
                switch (step)
                {
                    case UninstallStep.CloseRunningApp:
                        // An update in progress owns the install folder for a moment: wait for it first.
                        if (!HelperLock.WaitNotHeld(CloseWaitMs))
                        {
                            UpdateLog.Append("desinstalação interrompida: uma atualização está em andamento");
                            Error("Uma atualização do SenseMonitor está em andamento. Espere um minuto e tente de novo. Nada foi removido.");
                            return 1;
                        }
                        // The fan sentinel is waited for too (it leaves a few seconds after the app, with the
                        // fans on Auto): its exe is in the folder that is about to be deleted.
                        if (!AppProcesses.CloseGracefully(myPid, CloseWaitMs, true))
                        {
                            // Nothing has been touched yet, and a running app is never forced.
                            UpdateLog.Append("desinstalação interrompida: o app não fechou em 30 s");
                            Error("O SenseMonitor não fechou em 30 segundos. Feche-o pelo ícone da bandeja (Sair) e tente de novo. Nada foi removido.");
                            return 1;
                        }
                        break;
                    case UninstallStep.FansToAuto:
                        UpdateLog.Append("ventoinhas em automático: " + InstallSystem.FansToAuto());
                        break;
                    case UninstallStep.ReenableServices:
                        err = InstallSystem.ReenableAcerServices();
                        break;
                    case UninstallStep.DeleteTasks:
                        err = InstallTasks.DeleteAllForUninstaller(); // the Desinstalar task (this process may be running in it) goes with the folder
                        break;
                    case UninstallStep.RemoveUninstallEntry:
                        err = InstallSystem.RemoveUninstallEntry();
                        break;
                    case UninstallStep.RemoveShortcuts:
                        err = StartMenu.Remove();
                        break;
                    case UninstallStep.RemoveData:
                        err = InstallSystem.RemoveData();
                        break;
                    case UninstallStep.RemoveUpdateData:
                        err = InstallSystem.RemoveUpdateData(); // also switches the update journal off
                        break;
                    case UninstallStep.RemoveInstallDir:
                        // Say goodbye first: the cmd that finishes the job starts counting here.
                        Info("SenseMonitor removido." + (keepData ? "\n\nSuas configurações foram mantidas." : "")
                            + (warnings.Count > 0 ? "\n\nAtenção:\n- " + string.Join("\n- ", warnings.ToArray()) : ""));
                        err = InstallSystem.RemoveInstallDir();
                        break;
                }
                if (err == null) continue;
                UpdateLog.Append("desinstalação, passo " + step + ": " + err);
                warnings.Add(err);
            }
            return warnings.Count == 0 ? 0 : 1;
        }

        // The answer the app left for the uninstaller, read ONCE (the file is removed whatever it holds). null = none, or not valid.
        private static UninstallInstructions ConsumeUninstallInstructions()
        {
            try
            {
                string name = UninstallInstructionsFile.FileName;
                byte[] data = UpdateStore.ReadBytes(name, 2048);
                if (!File.Exists(UpdateStore.DataPathOf(name))) return null;
                UpdateStore.Remove(name);
                UninstallInstructions i;
                string err;
                if (UninstallInstructionsFile.TryParse(data, DateTime.UtcNow, out i, out err)) return i;
                UpdateLog.Append("desinstalação: instruções ignoradas (" + err + ")");
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool InstructionsPickedUp(int waitMs)
        {
            string path = UpdateStore.DataPathOf(UninstallInstructionsFile.FileName);
            for (int waited = 0; waited < waitMs; waited += 200)
            {
                if (!File.Exists(path)) return true;
                System.Threading.Thread.Sleep(200);
            }
            return !File.Exists(path);
        }

        // Settings > Desinstalar. The question ("Manter suas configurações?") is asked HERE, in the app; the answer goes to
        // the uninstaller in an admin-only file (never on a command line). The uninstaller must not be a child of the app: the
        // app runs in the job of its own task and the uninstaller closes it first thing. So it is started through ITS OWN
        // on-demand task (outside that job, like the update helper); if that does not work, with CREATE_BREAKAWAY_FROM_JOB; if
        // the job does not allow that either, nothing is started and the text says to use Apps & features. Returns null when
        // the uninstaller picked its instructions up (or the user cancelled), else the text to show.
        internal static string UninstallFromApp()
        {
            const string fallback = "Não foi possível abrir a desinstalação a partir daqui. Use Configurações do Windows > Aplicativos > SenseMonitor > Desinstalar.";
            try
            {
                string exe = InstallPaths.InstalledExe;
                if (!File.Exists(exe) || !UpdatePolicy.IsInstalledCopy(Application.ExecutablePath, exe)) return fallback;
                UninstallSummary summary = UninstallSummary.Build(InstallPaths.InstallDir, InstallSystem.AcerServicesPendingRestore());
                UninstallChoice choice = UninstallForm.Ask(summary, false);
                if (choice == UninstallChoice.Cancel) return null;
                byte[] data = UninstallInstructionsFile.Build(choice == UninstallChoice.Keep, DateTime.UtcNow, ApplyInstructionsFile.NewNonce());
                if (!UpdateStore.WriteBytes(UninstallInstructionsFile.FileName, data)) return fallback;
                bool started = false;
                if (UninstallTask.EnsureCurrent() == null && UninstallTask.Run()) started = InstructionsPickedUp(8000);
                if (!started)
                {
                    int error;
                    if (JobBreakaway.Start(exe, StartupRouter.UninstallSwitch, out error)) started = InstructionsPickedUp(8000);
                    else UpdateLog.Append("desinstalação a partir do app: a tarefa não serviu e o job não deixa sair (erro " + error + ")");
                }
                if (!started)
                {
                    UpdateStore.Remove(UninstallInstructionsFile.FileName);
                    return fallback;
                }
                return null;
            }
            catch (Exception)
            {
                try { UpdateStore.Remove(UninstallInstructionsFile.FileName); }
                catch (Exception) { }
                return fallback;
            }
        }
    }
}
