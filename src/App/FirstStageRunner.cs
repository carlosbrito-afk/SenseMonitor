using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Start-up BEFORE elevation. The exe's manifest is asInvoker, so the checks that must come first can:
    // an exe that is NOT the installed copy sits in a folder the user (and any user-level process) can write
    // to, and a *.config, *.dll, *.manifest or *.local next to it would be loaded by the ELEVATED process.
    //
    // ONE rule, ONE place: Elevate() is the only function that asks Windows for elevation, and it begins with
    // RefuseIfCompanions. So every self-elevation of an exe that is not the installed copy (install, the
    // "install it?" offer, "update/reinstall with this file", every CLI tool switch, a plain run) passes the
    // same check, and none of them can skip it. The decision is Core (FirstStage.MustRefuseElevation).
    //
    // When SenseMonitor is already installed, double-clicking an exe from elsewhere elevates nothing of it: it
    // hands off to the installed copy (shows the running one, or starts it through its task), unless the user
    // picks "update/reinstall with this file".
    // What asking Windows for elevation came to.
    internal enum ElevateResult
    {
        // The UAC question was accepted: the elevated copy runs (this process does not wait for it).
        Started,
        // The user said no to the UAC question (or it could not be shown).
        Declined,
        // Files next to the exe that the elevated process would load: nothing was asked, the message was shown.
        Refused
    }

    internal static class FirstStageRunner
    {
        // true = handled (Environment.ExitCode is set; the caller returns); false = go on in this process.
        public static bool Run(string[] args, StartupRoute route)
        {
            string exe = Application.ExecutablePath;
            bool elevated = InstallSystem.IsElevated();
            bool fromInstalled = UpdatePolicy.IsInstalledCopy(exe, InstallPaths.InstalledExe);
            List<string> companions = null;
            // Elevated install routes copy the exe into Program Files; non-elevated routes elevate it. Either
            // way the folder is looked at here, once, before the decision.
            if (!fromInstalled) companions = Companions(Path.GetDirectoryName(exe));
            FirstStageAction action = FirstStage.Decide(route, elevated, fromInstalled, companions);
            switch (action)
            {
                case FirstStageAction.RefuseCompanions:
                    Refuse(companions);
                    return true;
                case FirstStageAction.ShowInstaller:
                    // The installer window: its welcome step runs here (no administrator rights yet) and its "Instalar"
                    // button comes back to Elevate() below, which checks the folder again.
                    Prepare();
                    InstallerWizard.RunFirstStage();
                    return true;
                case FirstStageAction.HandOffOrReinstall:
                    return HandOffOrReinstall(exe, args, fromInstalled);
                case FirstStageAction.Elevate:
                    if (Array.IndexOf(args, StartupRouter.ElevatedSwitch) >= 0)
                    {
                        // Still not elevated after one relaunch (UAC off for this account?): the tray cannot control
                        // anything without administrator rights, so say so instead of running it.
                        Prepare();
                        MessageBox.Show("O SenseMonitor precisa de permissão de administrador para controlar as ventoinhas e não conseguiu obtê-la.",
                            "SenseMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Environment.ExitCode = 1;
                        return true;
                    }
                    if (fromInstalled && args.Length <= 1)
                    {
                        // A plain open of the installed copy (Start Menu, double click in its folder): show the running app,
                        // or start it through its task, which is already elevated, so no UAC question for every launch.
                        HandOffToInstalled();
                        return true;
                    }
                    Elevate(exe, null, args, fromInstalled);
                    return true;
                default:
                    return false;
            }
        }

        // The installer window's "Instalar" (--instalar --passo=reconhecer) and "Abrir sem instalar" (--executar-aqui): the
        // same single elevation function, so the same companion check. Result says what happened.
        internal static ElevateResult ElevateForWizard(string extraSwitches)
        {
            string exe = Application.ExecutablePath;
            return Elevate(exe, extraSwitches, new string[] { exe }, UpdatePolicy.IsInstalledCopy(exe, InstallPaths.InstalledExe));
        }

        // The elevated side of the same question ("já instalado: abrir o instalado ou reinstalar com este
        // arquivo?"), for an exe started from an already elevated prompt. Returns true when the user chose to
        // reinstall with this file and the folder is clean.
        public static bool ReinstallAllowed(string exe, bool fromInstalled)
        {
            return !RefuseIfCompanions(exe, fromInstalled);
        }

        private static void Prepare()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }

        // For callers that find themselves not elevated after the first stage (a last guard): the same single
        // elevation function, so the same check.
        internal static void ElevateSelf(string switches)
        {
            string exe = Application.ExecutablePath;
            List<string> args = new List<string>();
            args.Add(exe);
            args.AddRange(switches.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            Elevate(exe, null, args.ToArray(), UpdatePolicy.IsInstalledCopy(exe, InstallPaths.InstalledExe));
        }

        // ---- the guard -----------------------------------------------------------------------------

        // true = refused (message shown, exit code set).
        private static bool RefuseIfCompanions(string exe, bool fromInstalled)
        {
            if (fromInstalled) return false;
            List<string> companions = Companions(Path.GetDirectoryName(exe));
            if (!FirstStage.MustRefuseElevation(fromInstalled, companions)) return false;
            Refuse(companions);
            return true;
        }

        private static void Refuse(List<string> companions)
        {
            Prepare();
            MessageBox.Show("Para abrir ou instalar o SenseMonitor com permissão de administrador, use uma pasta vazia.\n\n"
                + "Ao lado do SenseMonitor.exe há arquivos que o Windows carregaria junto com o programa:\n- "
                + string.Join("\n- ", companions.ToArray())
                + "\n\nMova só o SenseMonitor.exe para uma pasta nova (por exemplo C:\\Temp\\SenseMonitor) e abra de lá.",
                "SenseMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.ExitCode = 2;
        }

        // The names (files and folders) next to the exe that would be loaded with it. An unreadable folder
        // counts as a problem: better to refuse than to go on blind.
        private static List<string> Companions(string dir)
        {
            try
            {
                List<string> names = new List<string>();
                foreach (string entry in Directory.GetFileSystemEntries(dir)) names.Add(Path.GetFileName(entry));
                return FirstStage.FindCompanions(names);
            }
            catch (Exception)
            {
                List<string> unreadable = new List<string>();
                unreadable.Add("(não foi possível ler a pasta)");
                return unreadable;
            }
        }

        // ---- elevation: the only place that asks for it ------------------------------------------------

        // Starts an elevated copy (UAC prompt) and leaves. Refuses first, on the same rule for every caller.
        private static ElevateResult Elevate(string exe, string extraSwitch, string[] args, bool fromInstalled)
        {
            if (RefuseIfCompanions(exe, fromInstalled)) return ElevateResult.Refused;
            StringBuilder cl = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], StartupRouter.ExecuteHereSwitch, StringComparison.OrdinalIgnoreCase)) continue;
                if (cl.Length > 0) cl.Append(' ');
                cl.Append(CommandLine.Quote(args[i]));
            }
            if (extraSwitch != null)
            {
                if (cl.Length > 0) cl.Append(' ');
                cl.Append(extraSwitch);
            }
            if (cl.Length > 0) cl.Append(' ');
            cl.Append(StartupRouter.ElevatedSwitch);
            ElevateResult r = Start(exe, cl.ToString());
            if (r == ElevateResult.Declined) Environment.ExitCode = 1; // the UAC question was declined
            return r;
        }

        private static ElevateResult Start(string exe, string commandLine)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, commandLine);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                // Does not wait: a first stage left alive would be a second "SenseMonitor.exe" that the update
                // and uninstall logic would take for a running app.
                using (Process p = Process.Start(psi)) { }
                return ElevateResult.Started;
            }
            catch (Win32Exception)
            {
                return ElevateResult.Declined;
            }
            catch (Exception)
            {
                return ElevateResult.Declined;
            }
        }

        // ---- already installed, started from elsewhere ---------------------------------------------------

        private static bool HandOffOrReinstall(string exe, string[] args, bool fromInstalled)
        {
            Prepare();
            DialogResult r = AskHandOff();
            if (r == DialogResult.Yes) HandOffToInstalled();
            else if (r == DialogResult.No) Elevate(exe, StartupRouter.InstallSwitch, args, fromInstalled); // checked there
            return true;
        }

        internal static DialogResult AskHandOff()
        {
            UpdateVersion there = UpdateHelper.InstalledVersion();
            return MessageBox.Show(
                "O SenseMonitor já está instalado (versão " + there + ").\n\n"
                + "Sim: abrir o que está instalado.\n"
                + "Não: atualizar/reinstalar com este arquivo (versão " + AppVersion.Text + ").\n"
                + "Cancelar: não fazer nada.",
                "SenseMonitor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        }

        // Nothing of the downloaded exe runs elevated. A running app is shown; otherwise the installed copy is
        // started through its scheduled task (already elevated, no UAC); if that task is missing, Windows is asked
        // to elevate the INSTALLED exe (it lives in Program Files, so there is nothing to check).
        internal static void HandOffToInstalled()
        {
            try
            {
                if (Sentinel.IsAppAlive())
                {
                    SingleInstance.SignalFirstInstance();
                    return;
                }
                int code;
                // schtasks answers 0 when the run was only ATTEMPTED (a principal that is not logged on, or a task that is
                // already "running" and ignores a second start). So wait a few seconds for the app to show up, and only then
                // fall back to the UAC question of the installed exe.
                if (ToolRunner.Run(InstallPaths.System32("schtasks.exe"), "/Run /TN " + Autostart.TaskName, 15000, out code) && code == 0)
                {
                    for (int waited = 0; waited < 5000; waited += 250)
                    {
                        if (Sentinel.IsAppAlive()) return;
                        System.Threading.Thread.Sleep(250);
                    }
                    if (Sentinel.IsAppAlive()) return;
                }
                string installed = InstallPaths.InstalledExe;
                if (File.Exists(installed)) Start(installed, StartupRouter.ElevatedSwitch);
            }
            catch (Exception)
            {
            }
        }
    }
}
