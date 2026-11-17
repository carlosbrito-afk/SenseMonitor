using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Forja.Core;
using Microsoft.Win32;

namespace Forja.Platform
{
    // The running copies of the installed app (told apart by command line, never by image path alone)
    // and the orderly way to stop them: the app's named exit event first (ExitSignal), then taskkill WITHOUT /F, which
    // posts WM_CLOSE; either way the app exits through its normal path and hands the fans back to the firmware. A copy
    // that does not close is never forced.
    // The fan sentinel is not "the app": it is never asked to close (it leaves on its own a few seconds
    // after the last app exits with the fans on Auto); callers that delete files wait for it separately.
    public static class AppProcesses
    {
        public static List<int> InstalledProcessIds(int excludePid)
        {
            return ProcessScan.AppIds(excludePid);
        }

        public static void RequestClose(int excludePid)
        {
            foreach (int id in ProcessScan.AppIds(excludePid))
            {
                int code;
                ToolRunner.Run(InstallPaths.System32("taskkill.exe"), "/PID " + id, 15000, out code);
            }
        }

        public static bool WaitGone(int excludePid, int timeoutMs, bool includeSentinels)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (true)
            {
                bool gone = ProcessScan.AppIds(excludePid).Count == 0 && (!includeSentinels || ProcessScan.SentinelCount() == 0);
                if (gone) return true;
                if (w.ElapsedMilliseconds >= timeoutMs) return false;
                Thread.Sleep(500);
            }
        }

        // false = a copy is still running after the wait (and was not forced).
        public static bool CloseGracefully(int excludePid, int waitMs, bool alsoWaitForSentinel)
        {
            // The exit event first, the window close only if the app is still there after the grace (see GracefulClose).
            return GracefulClose.Run(new RealCloseEnv(excludePid, alsoWaitForSentinel), waitMs);
        }
    }

    // The scheduled tasks that belong to the install.
    public static class InstallTasks
    {
        // Names to delete on uninstall (all three exist after an install: the autostart task, the fan
        // sentinel's on-demand task and the update helper's on-demand task).
        public static readonly string[] TaskNames =
        {
            Autostart.TaskName,
            Autostart.SentinelTaskName,
            UpdateTask.TaskName,
            UninstallTask.TaskName
        };

        // What the UNINSTALLER deletes in-process: everything but the task it may be running in (Settings > Desinstalar
        // starts it through that task; the detached cmd that removes the folder deletes the task last).
        public static readonly string[] TaskNamesForUninstaller =
        {
            Autostart.TaskName,
            Autostart.SentinelTaskName,
            UpdateTask.TaskName
        };

        // Registrars run in order; each returns null on success or a pt-BR reason. They require the
        // installed exe to exist already. Autostart.Enable registers the autostart task AND the sentinel's.
        public static readonly Func<string>[] Registrars =
        {
            Autostart.Enable,
            UpdateTask.Register,
            UninstallTask.Register
        };

        public static string RegisterAll()
        {
            string firstError = null;
            for (int i = 0; i < Registrars.Length; i++)
            {
                string e = Registrars[i]();
                if (e != null && firstError == null) firstError = e;
            }
            return firstError;
        }

        // Deletes every task of the install (the install's own undo); a task that is not there is not an error.
        public static string DeleteAll()
        {
            return Delete(TaskNames);
        }

        // The uninstaller: every task but the one that may be hosting it.
        public static string DeleteAllForUninstaller()
        {
            return Delete(TaskNamesForUninstaller);
        }

        private static string Delete(string[] names)
        {
            string firstError = null;
            for (int i = 0; i < names.Length; i++)
            {
                int code;
                string exe = InstallPaths.System32("schtasks.exe");
                bool ran = ToolRunner.Run(exe, "/Query /TN " + names[i], 15000, out code);
                if (ran && code != 0) continue; // not found
                ran = ToolRunner.Run(exe, "/Delete /TN " + names[i] + " /F", 15000, out code);
                if ((!ran || code != 0) && firstError == null) firstError = "não foi possível remover a tarefa agendada " + names[i];
            }
            return firstError;
        }
    }

    // One function per install/uninstall step (each returns null on success or a pt-BR reason);
    // the order and the user questions belong to the caller (InstallPlanner + InstallTool).
    public static class InstallSystem
    {
        private const string InstallingTmp = "SenseMonitor.instalando.tmp";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string newName, int flags);
        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        public static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool InstallExists
        {
            get { return File.Exists(InstallPaths.InstalledExe); }
        }

        // ---- install ---------------------------------------------------------------------------

        // The install folder, created when missing; refused when it is a link or its ACL lets
        // non-administrators write (a swapped exe there would run elevated at every logon).
        public static string PrepareInstallDir()
        {
            try
            {
                string dir = InstallPaths.InstallDir;
                if (!PathSafety.IsInstallDir(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) return "pasta de instalação inesperada";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "a pasta de instalação é um link; recusado";
                string acl = UpdateStore.AclProblem(dir);
                if (acl != null) return "pasta de instalação insegura (" + acl + ")";
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível preparar a pasta de instalação (" + ex.GetType().Name + ")";
            }
        }

        // Copies the held exe next to its final name, verifies the copy byte for byte (SHA-256), then swaps it in; a previous
        // copy is kept as SenseMonitor.anterior.exe until the next update. The bytes come from the HELD handle (SourceHold),
        // never from the source path again: the file there may no longer be the one that was opened.
        public static string CopyExe(SourceHold source)
        {
            return CopyExe(source, InstallPaths.InstalledExe, Path.Combine(InstallPaths.InstallDir, InstallingTmp), UpdateStore.PreviousExe);
        }

        internal static string CopyExe(SourceHold source, string target, string tmp, string previous)
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                string written;
                using (FileStream o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    written = source.CopyTo(o);
                }
                // what was written to disk must be what was read from the handle, and what the handle held at open
                string onDisk;
                using (FileStream s = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read)) onDisk = UpdateManifestFile.Sha256Hex(s);
                if (!string.Equals(written, onDisk, StringComparison.OrdinalIgnoreCase) || !string.Equals(written, source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    return "a cópia do executável não confere";
                }
                bool hadOld = File.Exists(target);
                if (hadOld)
                {
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(target, previous);
                }
                try
                {
                    File.Move(tmp, target);
                }
                catch (Exception)
                {
                    if (hadOld) File.Move(previous, target);
                    throw;
                }
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
                return "não foi possível copiar o executável (" + ex.GetType().Name + ")";
            }
        }

        // SenseMonitor.exe.config is ALWAYS written from the copy embedded in the exe. A file beside the
        // source (a downloaded exe sits in a folder anybody can write to) is never read or copied.
        public static string WriteConfig(byte[] embedded)
        {
            try
            {
                if (embedded == null) return null; // nothing to write; the exe still runs
                string target = InstallPaths.InstalledExe + ".config";
                string tmp = target + ".tmp";
                File.WriteAllBytes(tmp, embedded);
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível gravar o arquivo de configuração (" + ex.GetType().Name + ")";
            }
        }

        // Keeps SenseMonitor.exe.config equal to the copy embedded in the running exe: an update swaps
        // only the exe, so a changed app.config reaches installed copies this way (read at the next
        // start). Returns true when the file was rewritten.
        public static bool EnsureConfig(byte[] embedded)
        {
            try
            {
                if (embedded == null || embedded.Length == 0 || embedded.Length > 16 * 1024) return false;
                string target = InstallPaths.InstalledExe + ".config";
                if (File.Exists(target))
                {
                    byte[] have = File.ReadAllBytes(target);
                    if (have.Length == embedded.Length && UpdateManifestFile.Sha256Hex(have) == UpdateManifestFile.Sha256Hex(embedded)) return false;
                }
                string tmp = target + ".tmp";
                File.WriteAllBytes(tmp, embedded);
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string WriteUninstallEntry(string version)
        {
            try
            {
                long bytes = new FileInfo(InstallPaths.InstalledExe).Length;
                int kb = (int)Math.Min(int.MaxValue, bytes / 1024 + 1);
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = hklm.CreateSubKey(UninstallEntry.KeyPath))
                {
                    foreach (RegistryValue v in UninstallEntry.Build(InstallPaths.InstallDir, InstallPaths.InstalledExe, version, kb))
                    {
                        if (v.Value is int) k.SetValue(v.Name, (int)v.Value, RegistryValueKind.DWord);
                        else k.SetValue(v.Name, (string)v.Value, RegistryValueKind.String);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível registrar em Apps e recursos (" + ex.GetType().Name + ")";
            }
        }

        public static string StartApp()
        {
            int code;
            bool ran = ToolRunner.Run(InstallPaths.System32("schtasks.exe"), "/Run /TN " + Autostart.TaskName, 15000, out code);
            return ran && code == 0 ? null : "não foi possível iniciar o app pela tarefa agendada";
        }

        // ---- undoing an install (the installer's rollback; each returns null on success or a pt-BR reason) ----

        // Exactly SenseMonitor.exe.config (and a left-over .tmp of it).
        public static string RemoveConfigFile()
        {
            return RemoveConfigFile(InstallPaths.InstalledExe);
        }

        internal static string RemoveConfigFile(string exe)
        {
            try
            {
                string cfg = exe + ".config";
                DeleteRegularFile(cfg);
                DeleteRegularFile(cfg + ".tmp");
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível remover o arquivo de configuração (" + ex.GetType().Name + ")";
            }
        }

        // The exe an install copied, and the temporary file it copies through. Nothing else in the folder.
        public static string RemoveCopiedExe()
        {
            return RemoveCopiedExe(InstallPaths.InstalledExe, Path.Combine(InstallPaths.InstallDir, InstallingTmp));
        }

        internal static string RemoveCopiedExe(string exe, string tmp)
        {
            try
            {
                DeleteRegularFile(tmp);
                DeleteRegularFile(exe);
                return File.Exists(exe) ? "o programa copiado continua na pasta" : null;
            }
            catch (Exception ex)
            {
                return "não foi possível remover o programa copiado (" + ex.GetType().Name + ")";
            }
        }

        // A reinstall that failed: the exe that was installed before (kept as SenseMonitor.anterior.exe) goes back.
        public static string RestorePreviousExe()
        {
            return RestorePreviousExe(InstallPaths.InstalledExe, UpdateStore.PreviousExe);
        }

        internal static string RestorePreviousExe(string exe, string previous)
        {
            try
            {
                if (!File.Exists(previous)) return "não há versão anterior guardada para devolver";
                if (File.Exists(exe)) File.Replace(previous, exe, null);
                else File.Move(previous, exe);
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível devolver a versão anterior (" + ex.GetType().Name + ")";
            }
        }

        // The folder an install created: everything in it was put there by this install (it is under Program Files, so
        // only administrators write there; links are removed as links, never followed). Refuses anything but the exact
        // install folder.
        public static string RemoveFreshInstallFolder()
        {
            return RemoveFreshInstallFolder(InstallPaths.InstallDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        }

        internal static string RemoveFreshInstallFolder(string dir, string programFiles)
        {
            try
            {
                if (!PathSafety.IsInstallDir(dir, programFiles)) return "pasta de instalação inesperada; deixada como está";
                if (!Directory.Exists(dir)) return null;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "a pasta de instalação é um link; deixada como está";
                DeleteAllExcept(dir, null);
                Directory.Delete(dir, false);
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível remover a pasta de instalação (" + ex.GetType().Name + ")";
            }
        }

        // An installer that was cancelled before it installed anything: the admin-only folders the detection made in
        // %ProgramData%\SenseMonitor (state, update data, logs) go, but ONLY the ones that are empty (a fan marker or an
        // undo record that is still needed keeps its folder). Nothing is deleted, listed or opened inside them.
        public static void RemoveEmptyStateFolders()
        {
            AdminFolder.ReleaseAll(); // the verified folders are held open without delete sharing until now
            TryRemoveIfEmpty(StateFolder.DefaultPath);
            TryRemoveIfEmpty(UpdateStore.LogDir);
            TryRemoveIfEmpty(UpdateStore.DataDir);
            TryRemoveIfEmpty(UpdateStore.DataRoot);
        }

        // A file only: a folder or a link in its place is left alone (a link is not followed).
        private static void DeleteRegularFile(string path)
        {
            FileAttributes a;
            try { a = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((a & FileAttributes.Directory) != 0) return;
            File.Delete(path);
        }

        // ---- uninstall -------------------------------------------------------------------------

        // Belt and braces after the app's own orderly exit: write Automático once more from an isolated writer, but only
        // where the shared rule allows it (AutoHandBackRule: a fan marker, on its recorded fans, or a profile that confirms the
        // fan control of this notebook, on its fans). An untested or Unavailable notebook gets no fan write at all.
        // The text for the uninstall log.
        public static string FansToAuto()
        {
            bool skipped;
            string reason;
            bool ok = FanSafety.SetAutoIsolated(5000, out skipped, out reason);
            if (skipped) return "nada a devolver (controle de ventoinha não confirmado neste notebook e sem marcador): nenhuma escrita";
            return ok ? "confirmado" : (reason ?? "não confirmado");
        }

        // The Acer services the environment test disabled (on the user's click) go back to what they were, before the undo
        // records (config.json, the state folder) are deleted: after uninstall nobody could undo it from Settings. A service
        // that cannot be restored is reported in the final message.
        public static string ReenableAcerServices()
        {
            List<ServiceUndo> state;
            try { state = new UndoStore().Load(); }
            catch (Exception) { state = new List<ServiceUndo>(); }
            return ReenableAcerServices(Config.DefaultPath, new WmiServiceControl(), state, Safety.Journal);
        }

        // The testable core. configPath: where config.json lives; stateRecords: the undo records of the state folder.
        // config.json is saved back ONLY when it was read: an unreadable one (corrupt, locked) is never overwritten with
        // defaults (the user's settings and any records only it held would be lost), and a missing one is not created. The
        // services are restored from the records in memory either way, and a warning says the file was left alone.
        internal static string ReenableAcerServices(string configPath, IServiceControl services, List<ServiceUndo> stateRecords, IJournal journal)
        {
            try
            {
                Config cfg;
                ConfigRead read = Config.TryLoad(configPath, out cfg);
                bool readable = read == ConfigRead.Ok && cfg != null;
                if (!readable) cfg = new Config();
                try { EnvApply.MergeServiceUndo(cfg, stateRecords); }
                catch (Exception) { }
                string unreadable = read == ConfigRead.Corrupt
                    ? "o arquivo de configurações não pôde ser lido e foi mantido como está (os registros de serviços que só ele tinha não foram restaurados)"
                    : null;
                if (cfg.ServiceUndo == null || cfg.ServiceUndo.Count == 0) return unreadable;
                UndoOutcome r = ConflictServices.UndoAll(services, cfg,
                    delegate
                    {
                        if (!readable) return true; // nothing to save: the records live in memory only
                        try { cfg.Save(configPath); return true; }
                        catch (Exception) { return false; }
                    }, journal ?? Safety.Journal);
                string failed = r.Failed == 0 ? null : "serviços da Acer que continuam desativados (reative em services.msc): " + string.Join(" ", r.Messages.ToArray());
                if (failed == null) return unreadable;
                return unreadable == null ? failed : failed + "; " + unreadable;
            }
            catch (Exception ex)
            {
                return "não foi possível reativar os serviços da Acer (" + ex.GetType().Name + ")";
            }
        }

        // Are there Acer services the environment test disabled that the uninstaller will put back (records in config.json
        // or in the state folder)? Read-only; any failure answers false.
        public static bool AcerServicesPendingRestore()
        {
            try
            {
                Config cfg;
                if (Config.TryLoad(Config.DefaultPath, out cfg) == ConfigRead.Ok && cfg != null && cfg.ServiceUndo != null && cfg.ServiceUndo.Count > 0) return true;
                return new UndoStore().Load().Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string RemoveUninstallEntry()
        {
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    hklm.DeleteSubKeyTree(UninstallEntry.KeyPath, false);
                }
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível remover a entrada de Apps e recursos (" + ex.GetType().Name + ")";
            }
        }

        // The names the app itself writes into its data folders. Uninstall deletes ONLY these, one by one,
        // never recursively: an elevated recursive delete over a folder the user can write to can be
        // steered through a junction swapped in mid-walk. Anything else stays (and keeps the folder).
        public static readonly string[] KnownDataFiles =
        {
            "config.json", "config.json.bak", "config.json.tmp", "erro.log", "atualizacao.log",
            "historico.json", "teste-desempenho.journal", "diario.log", "diario.log.1",
            "ventoinha-custom.flag", "sessao-aberta.flag"
        };

        public static string RemoveData()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "SenseMonitor");
                if (!PathSafety.IsDataDir(dir, appData)) return "pasta de dados inesperada";
                string problems = RemoveKnownFiles(dir, KnownDataFiles, null);
                // temperature history: %LOCALAPPDATA%\SenseMonitor\temperaturas (files named by date only)
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string localRoot = Path.Combine(local, "SenseMonitor");
                string temps = Path.Combine(localRoot, "temperaturas");
                string r2 = RemoveKnownFiles(temps, null, delegate(string name) { DateTime d; return TempLog.TryParseFileDay(name, out d); });
                if (r2 != null && problems == null) problems = r2;
                TryRemoveIfEmpty(temps);
                TryRemoveIfEmpty(localRoot);
                TryRemoveIfEmpty(dir);
                return problems;
            }
            catch (Exception ex)
            {
                return "não foi possível remover os dados (" + ex.GetType().Name + ")";
            }
        }

        // Deletes the listed names (or the names the predicate accepts) directly inside dir: no recursion,
        // nothing that is a folder, and nothing that is a reparse point (checked per file). Returns a note
        // when a link was skipped or a delete failed.
        internal static string RemoveKnownFiles(string dir, string[] names, Func<string, bool> accept)
        {
            if (!Directory.Exists(dir)) return null;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "a pasta " + Path.GetFileName(dir) + " é um link; deixada como está";
            string note = null;
            List<string> targets = new List<string>();
            if (names != null) targets.AddRange(names);
            else
            {
                string[] all;
                try { all = Directory.GetFiles(dir); }
                catch (Exception) { return "não foi possível listar " + Path.GetFileName(dir); }
                foreach (string f in all)
                {
                    string n = Path.GetFileName(f);
                    if (accept != null && accept(n)) targets.Add(n);
                }
            }
            foreach (string n in targets)
            {
                string f = Path.Combine(dir, n);
                try
                {
                    FileAttributes a;
                    try { a = File.GetAttributes(f); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) != 0)
                    {
                        note = "um item em " + Path.GetFileName(dir) + " é um link ou pasta e foi deixado: " + n;
                        continue;
                    }
                    File.Delete(f);
                }
                catch (Exception)
                {
                    note = "não foi possível apagar " + n;
                }
            }
            return note;
        }

        // Removes a folder only when it is empty (Directory.Delete without recursion refuses otherwise).
        internal static void TryRemoveIfEmpty(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
                Directory.Delete(dir, false);
            }
            catch (Exception)
            {
            }
        }

        // %ProgramData%\SenseMonitor\estado: the files the elevated app trusts (fan marker, session flag, mode-test
        // marker, service undo records). Only after the app and its sentinel were closed (the uninstall plan closes them
        // first) and after AdminFolder let go of the handle it holds on the folder. Verified by handle before anything
        // is deleted; a folder that does not verify is left alone. Only regular files are deleted, links and folders are left.
        internal static string RemoveStateFolder(string state)
        {
            try
            {
                AdminFolder.ReleaseAll(); // the verified folders are held open without delete sharing until now
                if (!Directory.Exists(state)) return null;
                string parent = Path.GetDirectoryName(state);
                string parentProblem = string.IsNullOrEmpty(parent) ? null : AdminFolder.Problem(parent);
                if (parentProblem != null) return "a pasta de estado não foi removida (a pasta acima não passou na verificação: " + parentProblem + ")";
                string problem = AdminFolder.Problem(state);
                if (problem != null) return "a pasta de estado não foi removida (" + problem + ")";
                string note = null;
                foreach (string f in Directory.GetFiles(state))
                {
                    try
                    {
                        if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) { note = "um link em estado foi deixado"; continue; }
                        File.Delete(f);
                    }
                    catch (Exception)
                    {
                        note = "não foi possível apagar um arquivo de estado";
                    }
                }
                TryRemoveIfEmpty(state);
                return note;
            }
            catch (Exception ex)
            {
                return "não foi possível remover a pasta de estado (" + ex.GetType().Name + ")";
            }
        }

        // %ProgramData%\SenseMonitor: the updater's admin-only data and the app's state folder. Verified (owner, ACL, no
        // links) before anything is deleted; if it does not verify it is left alone.
        public static string RemoveUpdateData()
        {
            try
            {
                string root = UpdateStore.DataRoot;
                if (!Directory.Exists(root)) return null;
                string stateErr = RemoveStateFolder(StateFolder.DefaultPath);
                string reason;
                if (UpdateStore.SecureDataDir(false, out reason) == null)
                {
                    if (!Directory.Exists(UpdateStore.DataDir))
                    {
                        AdminFolder.ReleaseAll(); // the check above holds the root again
                        TryRemoveIfEmpty(root);
                        return stateErr;
                    }
                    return "a pasta de dados da atualização não foi removida (" + reason + ")" + (stateErr != null ? "; " + stateErr : "");
                }
                UpdateLog.Disable();
                AdminFolder.ReleaseAll(); // the verified folders are held open without delete sharing until now
                string logs = UpdateStore.LogDir;
                if (Directory.Exists(logs) && (File.GetAttributes(logs) & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (string f in Directory.GetFiles(logs))
                    {
                        try { File.Delete(f); }
                        catch (Exception) { }
                    }
                    TryRemoveIfEmpty(logs);
                }
                foreach (string f in Directory.GetFiles(UpdateStore.DataDir))
                {
                    try { File.Delete(f); }
                    catch (Exception) { }
                }
                TryRemoveIfEmpty(UpdateStore.DataDir);
                TryRemoveIfEmpty(root);
                return stateErr;
            }
            catch (Exception ex)
            {
                return "não foi possível remover os dados da atualização (" + ex.GetType().Name + ")";
            }
        }

        // The running uninstaller lives in the folder it removes: everything else goes now, then a
        // detached cmd.exe waits for this process to end and removes the rest. If cmd cannot be
        // started, the leftovers are scheduled for deletion at the next restart.
        public static string RemoveInstallDir()
        {
            string dir = InstallPaths.InstallDir;
            try
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                // The task that may have started this uninstaller (Settings > Desinstalar) cannot be deleted from inside itself:
                // the detached cmd deletes it as its last word, after the folder is gone.
                string args = PathSafety.DelayedRemoveArgs(dir, programFiles, Environment.SystemDirectory, UninstallTask.TaskName);
                if (!Directory.Exists(dir)) return null;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "a pasta de instalação é um link; recusado";

                string self = null;
                try { self = Process.GetCurrentProcess().MainModule.FileName; }
                catch (Exception) { }
                DeleteAllExcept(dir, self);

                ProcessStartInfo psi = new ProcessStartInfo(InstallPaths.System32("cmd.exe"), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Environment.SystemDirectory;
                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) return null;
                    }
                }
                catch (Exception)
                {
                    // fall through to the restart fallback
                }
                if (self != null) MoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                return "os últimos arquivos serão apagados na próxima reinicialização";
            }
            catch (Exception ex)
            {
                return "não foi possível remover a pasta de instalação (" + ex.GetType().Name + ")";
            }
        }

        // Best effort: files and sub-folders go, the exe that is running (and the folder) stay.
        private static void DeleteAllExcept(string dir, string keepFile)
        {
            foreach (string f in SafeFiles(dir))
            {
                if (keepFile != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(keepFile), StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch (Exception) { }
            }
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            foreach (string d in subs)
            {
                try
                {
                    // A link inside the folder is removed as a link, never followed.
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
                    else Directory.Delete(d, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); }
            catch (Exception) { return new string[0]; }
        }
    }
}
