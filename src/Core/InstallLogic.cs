using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Forja.Core
{
    public enum StartupAction
    {
        // Normal start (tray app) or one of the other existing command-line tools.
        RunApp,
        Install,
        Uninstall,
        // The helper: SenseMonitor.novo.exe --aplicar-atualizacao <pid>
        ApplyUpdate,
        // Double-clicked from somewhere that is not the install, with nothing installed: offer it.
        OfferInstall,
        // Same double-click, but SenseMonitor IS installed: hand off to the installed copy, unless the
        // user chooses to update/reinstall with this file.
        OfferHandOff,
        // A switch of this family with a malformed value: do nothing and say so.
        BadArguments
    }

    // Where the installer window starts. The only thing the elevated relaunch passes along: a step token, never
    // a path, a result or any state a user-level process could have written.
    public enum InstallerStage
    {
        Welcome,
        // Elevated after the "Instalar" button of the welcome step: continue with "Reconhecendo seu notebook".
        Detect
    }

    public sealed class StartupRoute
    {
        public StartupAction Action;
        public bool KeepData;
        public InstallerStage Stage;
        // --instalar only: the SHA-256 of the exe as the first stage held it (lowercase hex), or null. The elevated copy
        // compares it with the file it opens itself, so a file swapped between the two is refused.
        public string ExpectedSha256;

        public StartupRoute(StartupAction action)
        {
            Action = action;
        }
    }

    public static class StartupRouter
    {
        public const string InstallSwitch = "--instalar";
        public const string UninstallSwitch = "--desinstalar";
        public const string KeepDataSwitch = "--manter-dados";
        public const string ApplySwitch = "--aplicar-atualizacao";
        // Added by the elevated relaunch of a "No, just run it" answer: never offers the install again.
        public const string ExecuteHereSwitch = "--executar-aqui";
        // Added by the elevated relaunch (so a relaunch that did not elevate cannot loop).
        public const string ElevatedSwitch = "--elevado";
        // Step token of the installer window (only valid with --instalar): the elevated relaunch resumes there.
        public const string StagePrefix = "--passo=";
        public const string StageDetectToken = "reconhecer";
        public const string StageDetectSwitch = StagePrefix + StageDetectToken;
        public const string ShaPrefix = "--sha=";
        // SenseMonitor.exe --sair: signals the running app's exit event and ends (no window, no elevation question; the
        // event's own access list decides who may). Exactly this one argument, handled before everything else in Main.
        public const string ExitSwitch = "--sair";

        // args = Environment.GetCommandLineArgs(): the exe and exactly "--sair".
        public static bool IsExitRequest(string[] args)
        {
            return args != null && args.Length == 2 && Is(args[1], ExitSwitch);
        }

        // "--sha=<64 hex>" for a hash, or null when it is not one.
        public static string ShaSwitch(string sha256Hex)
        {
            string h = NormalizeSha(sha256Hex);
            return h == null ? null : ShaPrefix + h;
        }

        private static string NormalizeSha(string s)
        {
            if (s == null || s.Length != 64) return null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            return s.ToLowerInvariant();
        }

        // args = Environment.GetCommandLineArgs() (args[0] is the exe). Only the switches of the
        // install/update family are decided here; every other switch stays with the old routing.
        public static StartupRoute Decide(string[] args, string runningExe, string installedExe, bool installedExists)
        {
            if (args == null || args.Length <= 1)
            {
                bool elsewhere = !UpdatePolicy.IsInstalledCopy(runningExe, installedExe);
                if (!elsewhere) return new StartupRoute(StartupAction.RunApp);
                return new StartupRoute(installedExists ? StartupAction.OfferHandOff : StartupAction.OfferInstall);
            }
            bool install = false, uninstall = false, keep = false, apply = false, stray = false;
            bool stageGiven = false, badStage = false, shaGiven = false, badSha = false;
            string sha = null;
            InstallerStage stage = InstallerStage.Welcome;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a != null && a.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // exactly one known token, nothing else is believed
                    if (stageGiven) badStage = true;
                    stageGiven = true;
                    if (string.Equals(a.Substring(StagePrefix.Length), StageDetectToken, StringComparison.OrdinalIgnoreCase)) stage = InstallerStage.Detect;
                    else badStage = true;
                }
                else if (a != null && a.StartsWith(ShaPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (shaGiven) badSha = true;
                    shaGiven = true;
                    sha = NormalizeSha(a.Substring(ShaPrefix.Length));
                    if (sha == null) badSha = true;
                }
                else if (Is(a, InstallSwitch)) install = true;
                else if (Is(a, UninstallSwitch)) uninstall = true;
                else if (Is(a, KeepDataSwitch)) keep = true;
                else if (Is(a, ExecuteHereSwitch)) { /* known, changes nothing: only keeps the install offer away */ }
                else if (Is(a, ApplySwitch))
                {
                    apply = true;
                    // The helper reads everything from the admin-only instructions file: no value follows.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) stray = true;
                }
            }
            int families = (install ? 1 : 0) + (uninstall ? 1 : 0) + (apply ? 1 : 0);
            if (keep && !uninstall) return new StartupRoute(StartupAction.BadArguments);
            if (stray) return new StartupRoute(StartupAction.BadArguments);
            if (badStage || (stageGiven && !install)) return new StartupRoute(StartupAction.BadArguments);
            if (badSha || (shaGiven && !install)) return new StartupRoute(StartupAction.BadArguments);
            if (families == 0) return new StartupRoute(StartupAction.RunApp);
            if (families > 1) return new StartupRoute(StartupAction.BadArguments);
            if (apply) return new StartupRoute(StartupAction.ApplyUpdate);
            if (install)
            {
                StartupRoute ir = new StartupRoute(StartupAction.Install);
                ir.Stage = stage;
                ir.ExpectedSha256 = sha;
                return ir;
            }
            StartupRoute u = new StartupRoute(StartupAction.Uninstall);
            u.KeepData = keep;
            return u;
        }

        private static bool Is(string a, string name)
        {
            return string.Equals(a, name, StringComparison.OrdinalIgnoreCase);
        }
    }

    public enum FirstStageAction
    {
        // Run what the command line says, in this process.
        Proceed,
        // Not elevated: start an elevated copy with the same arguments and leave.
        Elevate,
        // Not elevated and nothing installed: open the installer window (its welcome step runs here, without
        // administrator rights). "Instalar" = elevate with --instalar --passo=reconhecer; "Abrir sem instalar" =
        // elevate with --executar-aqui.
        ShowInstaller,
        // Files that Windows or the CLR would load next to the exe: refuse to go on (see FirstStage).
        RefuseCompanions,
        // Not elevated, SenseMonitor already installed, this exe is somewhere else: ask "open the installed
        // one" (nothing downloaded is elevated) or "update/reinstall with this file" (which is checked).
        HandOffOrReinstall
    }

    // The part of start-up that decides BEFORE elevation is requested (the exe's manifest is
    // asInvoker; the app elevates itself, after this check). A downloaded SenseMonitor.exe is run from a
    // folder the user (and any user-level process) can write to: a SenseMonitor.exe.config, a DLL, a
    // manifest or a .local folder placed next to it would be picked up by the elevated process.
    public static class FirstStage
    {
        private static readonly string[] Extensions = { ".config", ".dll", ".manifest", ".local" };

        private static HashSet<string> cultureNames;

        // A folder named like a culture ("pt-BR", "pt", "en") is where the CLR looks for satellite assemblies
        // (<exe folder>\<culture>\*.resources.dll) when it does not find them elsewhere.
        private static bool IsCultureName(string n)
        {
            if (cultureNames == null)
            {
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (System.Globalization.CultureInfo c in System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures))
                    {
                        if (c.Name.Length > 0) set.Add(c.Name);
                    }
                }
                catch (Exception)
                {
                }
                cultureNames = set;
            }
            return cultureNames.Contains(n);
        }

        // Names (files and folders) in the exe's folder that must not be there.
        public static List<string> FindCompanions(IEnumerable<string> entryNames)
        {
            List<string> found = new List<string>();
            if (entryNames == null) return found;
            foreach (string n in entryNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                bool hit = false;
                for (int i = 0; i < Extensions.Length && !hit; i++)
                {
                    if (n.EndsWith(Extensions[i], StringComparison.OrdinalIgnoreCase)) hit = true;
                }
                if (!hit && IsCultureName(n)) hit = true;
                if (hit) found.Add(n);
            }
            return found;
        }

        // The fan sentinel's launcher re-spawns the running image elevated. From the installed copy that is
        // fine. From anywhere else it must obey the same rule as every other elevation, with ONE allowance for
        // development runs (bin\): a SenseMonitor.exe.config beside the exe whose bytes equal the config embedded
        // in the exe itself is not a companion (it is what the build puts there, and is equal to what the
        // installer would write). entryNames: the names in the exe's folder; ownConfigName: the file name of
        // "<exe>.config".
        public static bool RespawnAllowed(bool runningFromInstalledCopy, IEnumerable<string> entryNames, string ownConfigName, bool ownConfigMatchesEmbedded)
        {
            if (runningFromInstalledCopy) return true;
            List<string> companions = FindCompanions(entryNames);
            if (ownConfigMatchesEmbedded && ownConfigName != null)
            {
                companions.RemoveAll(delegate(string c) { return string.Equals(c, ownConfigName, StringComparison.OrdinalIgnoreCase); });
            }
            return !MustRefuseElevation(false, companions);
        }

        // THE rule, one function: an exe that is not the installed copy (so it sits in a folder anybody can
        // write to) must not be elevated, or be copied into Program Files, while a *.config, *.dll,
        // *.manifest or *.local is next to it. Every elevation request goes through here.
        public static bool MustRefuseElevation(bool runningFromInstalledCopy, IList<string> companions)
        {
            return !runningFromInstalledCopy && companions != null && companions.Count > 0;
        }

        // route: from StartupRouter. runningFromInstalledCopy: the exe is the one in Program Files (its
        // own .config and friends are expected there). companions: FindCompanions of the exe's folder
        // (needed whenever the exe is not the installed copy).
        public static FirstStageAction Decide(StartupRoute route, bool elevated, bool runningFromInstalledCopy, IList<string> companions)
        {
            if (route.Action == StartupAction.BadArguments || route.Action == StartupAction.ApplyUpdate) return FirstStageAction.Proceed;
            bool refuse = MustRefuseElevation(runningFromInstalledCopy, companions);
            bool installing = route.Action == StartupAction.Install || route.Action == StartupAction.OfferInstall;
            if (elevated)
            {
                // Already elevated: nothing more to ask, but a file planted after the UAC question must not
                // be copied into Program Files.
                return installing && refuse ? FirstStageAction.RefuseCompanions : FirstStageAction.Proceed;
            }
            // The hand-off asks nothing of the downloaded exe: nothing of it is elevated unless the user
            // then picks "update/reinstall with this file", and that path is checked again when it elevates.
            if (route.Action == StartupAction.OfferHandOff) return FirstStageAction.HandOffOrReinstall;
            if (refuse) return FirstStageAction.RefuseCompanions;
            return route.Action == StartupAction.OfferInstall ? FirstStageAction.ShowInstaller : FirstStageAction.Elevate;
        }
    }

    public enum InstallStep
    {
        CloseRunningApp,
        PrepareFolder,
        CopyExe,
        CopyConfig,
        RegisterTasks,
        CreateShortcuts,
        WriteUninstallEntry,
        StartApp
    }

    public enum UninstallStep
    {
        CloseRunningApp,
        FansToAuto,
        ReenableServices,
        DeleteTasks,
        RemoveUninstallEntry,
        RemoveShortcuts,
        RemoveData,
        RemoveUpdateData,
        RemoveInstallDir
    }

    public static class InstallPlanner
    {
        // Close first (never force), then the files, then the tasks that point at them, then the
        // Apps & features entry, and only then start. Running from the installed copy itself
        // (a repair) skips the copy.
        public static List<InstallStep> ForInstall(bool appRunning, bool runningFromInstalledCopy)
        {
            List<InstallStep> s = new List<InstallStep>();
            if (appRunning) s.Add(InstallStep.CloseRunningApp);
            if (!runningFromInstalledCopy)
            {
                s.Add(InstallStep.PrepareFolder);
                s.Add(InstallStep.CopyExe);
                s.Add(InstallStep.CopyConfig);
            }
            s.Add(InstallStep.RegisterTasks);
            s.Add(InstallStep.CreateShortcuts);
            s.Add(InstallStep.WriteUninstallEntry);
            s.Add(InstallStep.StartApp);
            return s;
        }

        // The installer window: the same steps without the start (the app opens from the last screen, after the
        // guided tests and the saved result).
        public static List<InstallStep> ForWizardInstall(bool appRunning)
        {
            List<InstallStep> s = ForInstall(appRunning, false);
            s.Remove(InstallStep.StartApp);
            return s;
        }

        // A failure of a fatal step undoes the install; a failure of any other step is only a warning. Fatal: the
        // ones without which what is installed would not work or could not be removed again. The config file is
        // rewritten at every start of the installed copy and the shortcuts are a convenience.
        public static bool IsFatal(InstallStep step)
        {
            switch (step)
            {
                case InstallStep.CloseRunningApp:
                case InstallStep.PrepareFolder:
                case InstallStep.CopyExe:
                case InstallStep.RegisterTasks:
                case InstallStep.WriteUninstallEntry:
                    return true;
                default:
                    return false;
            }
        }

        // The app is closed gracefully FIRST and the fans are confirmed on Auto before anything is
        // deleted; the tasks go before the files (nothing may start a half-removed app); the
        // install folder is last because the running uninstaller deletes itself with it.
        public static List<UninstallStep> ForUninstall(bool keepData)
        {
            List<UninstallStep> s = new List<UninstallStep>();
            s.Add(UninstallStep.CloseRunningApp);
            s.Add(UninstallStep.FansToAuto);
            s.Add(UninstallStep.ReenableServices);
            s.Add(UninstallStep.DeleteTasks);
            s.Add(UninstallStep.RemoveUninstallEntry);
            s.Add(UninstallStep.RemoveShortcuts);
            if (!keepData) s.Add(UninstallStep.RemoveData);
            s.Add(UninstallStep.RemoveUpdateData);
            s.Add(UninstallStep.RemoveInstallDir);
            return s;
        }

        // The installed version is newer than the exe being installed: ask before going back.
        public static bool NeedsDowngradeConfirmation(UpdateVersion installed, UpdateVersion thisExe)
        {
            return installed != null && thisExe != null && installed.IsNewerThan(thisExe);
        }
    }

    public sealed class RegistryValue
    {
        public string Name;
        public object Value;   // string or int

        public RegistryValue(string name, object value)
        {
            Name = name;
            Value = value;
        }
    }

    // The "Apps & features" entry: HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SenseMonitor.
    public static class UninstallEntry
    {
        public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SenseMonitor";
        public const string Publisher = "carlosbrito-afk";

        public static List<RegistryValue> Build(string installDir, string exePath, string version, int estimatedSizeKb)
        {
            List<RegistryValue> v = new List<RegistryValue>();
            v.Add(new RegistryValue("DisplayName", "SenseMonitor"));
            v.Add(new RegistryValue("DisplayVersion", version));
            v.Add(new RegistryValue("Publisher", Publisher));
            v.Add(new RegistryValue("InstallLocation", installDir));
            v.Add(new RegistryValue("DisplayIcon", exePath));
            v.Add(new RegistryValue("UninstallString", "\"" + exePath + "\" " + StartupRouter.UninstallSwitch));
            v.Add(new RegistryValue("URLInfoAbout", "https://github.com/" + UpdateEndpoints.Owner + "/" + UpdateEndpoints.Repo));
            v.Add(new RegistryValue("NoModify", 1));
            v.Add(new RegistryValue("NoRepair", 1));
            v.Add(new RegistryValue("EstimatedSize", estimatedSizeKb < 1 ? 1 : estimatedSizeKb));
            return v;
        }
    }

    public static class PathSafety
    {
        // Characters that would change the meaning of the one cmd.exe line used to delete the
        // install folder after the uninstaller exits.
        private static readonly char[] ShellMeta = { '"', '&', '%', '^', '|', '<', '>', '(', ')', '!', ';', '\r', '\n' };

        private static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\'); }
            catch (Exception) { return null; }
        }

        private static bool SameDir(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            return x != null && y != null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        // Exactly <ProgramFiles>\SenseMonitor and nothing else (never the parent, never a sibling).
        public static bool IsInstallDir(string dir, string programFiles)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(programFiles)) return false;
            if (dir.IndexOfAny(ShellMeta) >= 0) return false;
            string pf = Norm(programFiles);
            if (pf == null || pf.Length < 6) return false;
            return SameDir(dir, Path.Combine(pf, "SenseMonitor"));
        }

        // Exactly <AppData>\SenseMonitor.
        public static bool IsDataDir(string dir, string appData)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(appData)) return false;
            string ad = Norm(appData);
            if (ad == null || ad.Length < 6) return false;
            return SameDir(dir, Path.Combine(ad, "SenseMonitor"));
        }

        // Arguments for System32\cmd.exe: wait a few seconds (the uninstaller is still exiting),
        // then remove the folder. Refuses anything that is not the exact install folder.
        public static string DelayedRemoveArgs(string installDir, string programFiles, string systemDir)
        {
            return DelayedRemoveArgs(installDir, programFiles, systemDir, null);
        }

        // Same, and when taskToDelete is the one known on-demand task that hosts the uninstaller, it is deleted as the very
        // last thing (the uninstaller cannot delete the task it is running in). Any other name is refused.
        public static string DelayedRemoveArgs(string installDir, string programFiles, string systemDir, string taskToDelete)
        {
            if (!IsInstallDir(installDir, programFiles)) throw new ArgumentException("pasta de instalação inesperada");
            // ping by its System32 path (the elevated cmd must not resolve it through the user's PATH).
            // The path is used unquoted, so it must be plain: no spaces, no shell characters.
            string sys = string.IsNullOrEmpty(systemDir) ? null : Norm(systemDir);
            if (sys == null || sys.Length < 8 || sys.IndexOfAny(ShellMeta) >= 0 || sys.IndexOf(' ') >= 0) throw new ArgumentException("pasta do sistema inesperada");
            string line = "/d /c " + sys + "\\ping.exe -n 4 127.0.0.1 >nul & rmdir /s /q \"" + Norm(installDir) + "\"";
            if (taskToDelete != null)
            {
                if (taskToDelete != "SenseMonitor-Desinstalar") throw new ArgumentException("tarefa inesperada");
                line += " & " + sys + "\\schtasks.exe /Delete /TN " + taskToDelete + " /F >nul";
            }
            return line;
        }
    }
}
