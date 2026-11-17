using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The "apply" half. The old, running app (known-good code) does NOT hand the swap to the new release:
    //
    //   app:    copies ITS OWN exe (hash-checked against the hash taken when it started) to
    //           atualizacao\SenseMonitor.aplicador.exe, writes the instructions (pid, version, nonce) into the
    //           admin-only data folder, and runs the on-demand task SenseMonitor-Atualizar (so the helper is
    //           outside the app's job object). It exits only after the helper answers "ready".
    //   helper: (the old code) refuses unless it really is that file, in the admin-only folders, holds the
    //           lock, has fresh instructions and a staged release that verifies again; then answers "ready",
    //           waits for the app to exit and for any other instance (except the fan sentinel) to be gone,
    //           swaps atomically, starts the new exe DIRECTLY (it is elevated; the autostart task is not
    //           involved) and waits for the new app's "started ok" record with its own version. On any
    //           failure it rolls back, and whatever happens it leaves the app running.
    public static class UpdateHelper
    {
        public const int ReadyWaitMs = 20 * 1000;

        // ---- app side ----------------------------------------------------------------------------

        public static bool Launch(StagedUpdate staged, int currentPid, string ownHash, out string error)
        {
            error = null;
            try
            {
                string reason;
                string stage = UpdateStore.SecureStageDir(false, out reason);
                if (stage == null) { error = reason; return false; }
                if (UpdateStore.SecureDataDir(true, out reason) == null) { error = reason; return false; }
                if (!UpdatePolicy.IsInstalledCopy(staged.ExePath, UpdateStore.PathOf(UpdateStore.StagedName))) { error = "caminho inesperado do arquivo guardado"; return false; }
                if (string.IsNullOrEmpty(ownHash)) { error = "ainda sem a impressão digital do executável em uso"; return false; }

                // The helper is a copy of the CURRENT exe, byte for byte what is running now.
                string installed = InstallPaths.InstalledExe;
                if (HashOf(installed) != ownHash) { error = "o executável instalado mudou desde que o app abriu"; return false; }
                string tmp = Path.Combine(stage, "SenseMonitor.aplicador.tmp");
                string helper = UpdateStore.HelperExe;
                SafeDelete(tmp);
                File.Copy(installed, tmp, true);
                if (HashOf(tmp) != ownHash) { SafeDelete(tmp); error = "a cópia do aplicador não confere"; return false; }
                SafeDelete(helper);
                File.Move(tmp, helper);

                string nonce = ApplyInstructionsFile.NewNonce();
                UpdateStore.Remove(UpdateStore.ReadyName);
                if (!UpdateStore.WriteBytes(UpdateStore.InstructionsName, ApplyInstructionsFile.Build(currentPid, staged.Version.ToString(), DateTime.UtcNow, nonce)))
                {
                    error = "não foi possível gravar as instruções do aplicador";
                    return false;
                }
                string taskError = UpdateTask.EnsureCurrent();
                if (taskError != null) { UpdateStore.Remove(UpdateStore.InstructionsName); error = taskError; return false; }
                if (!UpdateTask.Run()) { UpdateStore.Remove(UpdateStore.InstructionsName); error = "a tarefa " + UpdateTask.TaskName + " não iniciou"; return false; }

                // The app leaves only when the helper has checked everything and said so.
                Stopwatch w = Stopwatch.StartNew();
                while (w.ElapsedMilliseconds < ReadyWaitMs)
                {
                    if (UpdateStore.ReadText(UpdateStore.ReadyName) == nonce) return true;
                    Thread.Sleep(250);
                }
                UpdateStore.Remove(UpdateStore.InstructionsName);
                error = "o aplicador não confirmou em " + (ReadyWaitMs / 1000) + " s; o app continua aberto";
                return false;
            }
            catch (Exception ex)
            {
                error = "não foi possível preparar o aplicador (" + ex.GetType().Name + ")";
                return false;
            }
        }

        public static string HashOf(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return UpdateManifestFile.Sha256Hex(fs);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- helper side -------------------------------------------------------------------------

        // Entry point of the helper process. Exit code: 0 applied, 1 not applied / rolled back,
        // 2 refused (nothing touched, the app was never asked to leave), 3 another helper holds the lock.
        public static int Run(string ownExePath, string publicKeyXml)
        {
            Action<string> log = UpdateLog.Append;
            bool ready = false;
            RealApplyEnv env = null;
            try
            {
                if (!InstallSystem.IsElevated()) { log("aplicador recusado: não está elevado"); return 2; }
                string reason;
                string stage = UpdateStore.SecureStageDir(false, out reason);
                if (stage == null) { log("aplicador recusado: " + reason); return 2; }
                if (UpdateStore.SecureDataDir(true, out reason) == null) { log("aplicador recusado: " + reason); return 2; }
                if (!UpdatePolicy.IsInstalledCopy(ownExePath, UpdateStore.HelperExe)) { log("aplicador recusado: este executável não é o aplicador guardado em atualizacao"); return 2; }

                using (HelperLock held = HelperLock.TryAcquire())
                {
                    // Without the lock nothing is done and the app is NOT asked to leave (no "ready").
                    if (held == null) { log("aplicador: outro aplicador está em execução; saindo sem mexer em nada"); return 3; }
                    if (held.Note != null) log("aplicador: " + held.Note);

                    ApplyInstructions instr;
                    string err;
                    if (!ApplyInstructionsFile.TryParse(UpdateStore.ReadBytes(UpdateStore.InstructionsName, 4096), DateTime.UtcNow, out instr, out err))
                    {
                        log("aplicador recusado: instruções inválidas (" + err + ")");
                        return 2;
                    }
                    UpdateStore.Remove(UpdateStore.InstructionsName); // consumed: they work once

                    UpdateVersion installed = InstalledVersion();
                    StagedUpdate su = UpdateService.VerifyStaged(stage, installed, publicKeyXml, UpdateService.ProductVersionOfFile,
                        UpdateStore.PathOf(UpdateStore.StagedName), out reason);
                    if (su == null) { log("aplicador recusado: " + reason); return 2; }
                    if (su.Version.ToString() != instr.Versao) { log("aplicador recusado: a versão guardada (" + su.Version + ") não é a das instruções (" + instr.Versao + ")"); return 2; }
                    UpdateState state = UpdateStore.LoadState(UpdateStore.DataDir);
                    if (state.IsBlocked(su.Version, DateTime.UtcNow)) { log("aplicador recusado: versão " + su.Version + " está bloqueada"); return 2; }

                    ApplyPaths paths = new ApplyPaths();
                    paths.Current = InstallPaths.InstalledExe;
                    paths.Anterior = UpdateStore.PreviousExe;
                    paths.Novo = UpdateStore.PathOf(UpdateStore.StagedName);
                    paths.Falhou = UpdateStore.PathOf(UpdateStore.FailedName);
                    env = new RealApplyEnv(UpdateStore.DataDir, log);

                    // Everything checked: tell the app it may leave.
                    UpdateStore.WriteUpdatedMarker(su.Version.ToString());
                    ready = UpdateStore.WriteText(UpdateStore.ReadyName, instr.Nonce);
                    if (!ready) { log("aplicador: não foi possível avisar o app; nada foi alterado"); UpdateStore.ClearUpdatedMarker(); return 2; }

                    ApplyResult r = new UpdateApplier(paths, null).Run(env, instr.Pid, su.Version.ToString());
                    log("aplicação terminou: " + r);
                    UpdateStore.Remove(UpdateStore.ReadyName);
                    if (r == ApplyResult.Applied)
                    {
                        TryDelete(UpdateStore.PathOf(UpdateStore.ManifestName));
                        TryDelete(UpdateStore.PathOf(UpdateStore.SignatureName));
                        return 0;
                    }
                    UpdateStore.ClearUpdatedMarker();
                    return 1;
                }
            }
            catch (Exception ex)
            {
                log("aplicador falhou: " + ex.GetType().Name + ": " + ex.Message);
                // After "ready" the app may already be gone: never leave it closed.
                if (ready && env != null)
                {
                    try { if (!env.IsTrayRunning()) env.StartInstalledApp(); }
                    catch (Exception) { }
                }
                return 1;
            }
        }

        // The installed exe's version; builds from before versioning (or unreadable) count as 0.0.0.
        public static UpdateVersion InstalledVersion()
        {
            UpdateVersion v;
            if (UpdateVersion.TryParse(UpdateService.ProductVersionOfFile(InstallPaths.InstalledExe), out v)) return v;
            return new UpdateVersion(0, 0, 0);
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception) { }
        }

        private static void SafeDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception) { }
        }

        private sealed class RealApplyEnv : IUpdateApplyEnv
        {
            private readonly string dataDir;
            private readonly Action<string> log;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly int ownPid = Process.GetCurrentProcess().Id;

            public RealApplyEnv(string dataDir, Action<string> log)
            {
                this.dataDir = dataDir;
                this.log = log;
            }

            public bool WaitForProcessExit(int pid, int timeoutMs)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        return p.WaitForExit(timeoutMs);
                    }
                }
                catch (ArgumentException)
                {
                    return true; // no such process: already gone
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public bool WaitNoOtherInstances(int timeoutMs)
            {
                Stopwatch w = Stopwatch.StartNew();
                while (true)
                {
                    if (ProcessScan.AppIds(ownPid).Count == 0) return true;
                    if (w.ElapsedMilliseconds >= timeoutMs) return false;
                    Thread.Sleep(500);
                }
            }

            public bool Exists(string path) { return File.Exists(path); }
            public void Delete(string path) { File.Delete(path); }

            // ReplaceFile: one call; on failure nothing changed. Works with a running image as the target
            // (the fan sentinel may still be running from the installed file).
            public void Replace(string source, string target, string backup)
            {
                File.Replace(source, target, backup, true);
            }

            public void ClearOkFlag() { UpdateStore.ClearOkFlag(); }
            public bool OkFlagMatches(string version) { return UpdateStore.OkFlagMatches(version); }

            // Direct start: the helper is elevated and the new process inherits that. No scheduled task, so
            // a user who switched "Iniciar com o Windows" off still gets a working update.
            public bool StartInstalledApp()
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(InstallPaths.InstalledExe);
                    psi.UseShellExecute = false;
                    psi.WorkingDirectory = InstallPaths.InstallDir;
                    using (Process p = Process.Start(psi))
                    {
                        return p != null;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public bool IsInstalledAppRunning() { return ProcessScan.AppIds(ownPid).Count > 0; }
            public bool IsTrayRunning() { return ProcessScan.TrayIds(ownPid).Count > 0; }

            public void Move(string from, string to) { File.Move(from, to); }

            public void DeferApply(TimeSpan span, string reason)
            {
                try
                {
                    UpdateState s = UpdateStore.LoadState(dataDir);
                    s.DeferApply(DateTime.UtcNow, span);
                    UpdateStore.SaveState(dataDir, s);
                    log("nova tentativa de aplicar só depois de " + (int)span.TotalMinutes + " min (" + reason + ")");
                }
                catch (Exception)
                {
                }
            }

            // The exit event first; no /F ever: after the grace the window close (WM_CLOSE) follows, so the app exits through its
            // normal path and hands the fans back. WaitInstalledAppExit then gives it the rest of the time.
            public void RequestCloseInstalledApp()
            {
                GracefulClose.Run(new RealCloseEnv(ownPid, false), ExitSignal.SignalGraceMs + 2000);
            }

            public bool WaitInstalledAppExit(int timeoutMs)
            {
                Stopwatch w = Stopwatch.StartNew();
                while (true)
                {
                    if (ProcessScan.AppIds(ownPid).Count == 0) return true;
                    if (w.ElapsedMilliseconds >= timeoutMs) return false;
                    Thread.Sleep(500);
                }
            }

            public void BlockVersion(string version, string reason, bool permanent)
            {
                UpdateVersion v;
                if (!UpdateVersion.TryParse(version, out v)) return;
                UpdateState s = UpdateStore.LoadState(dataDir);
                if (permanent) s.BlockPermanently(v, reason);
                else s.BlockForFailure(v, DateTime.UtcNow, reason);
                UpdateStore.SaveState(dataDir, s);
            }

            public void Log(string line) { log(line); }
            public void Sleep(int ms) { Thread.Sleep(ms); }
            public long NowMs() { return clock.ElapsedMilliseconds; }
        }
    }

    // Runs a Windows tool by full path with a time limit; never through a shell.
    public static class ToolRunner
    {
        public static bool Run(string exe, string args, int timeoutMs, out int exitCode)
        {
            string ignored;
            return RunCapture(exe, args, timeoutMs, out exitCode, out ignored);
        }

        // Same, returning standard output (e.g. schtasks /Query /XML).
        public static bool RunCapture(string exe, string args, int timeoutMs, out int exitCode, out string stdout)
        {
            exitCode = -1;
            stdout = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    object gate = new object();
                    // Drain both pipes so a chatty tool cannot block on a full buffer.
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { if (sb.Length < 262144) sb.AppendLine(e.Data); } };
                    p.ErrorDataReceived += delegate { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); }
                        catch (Exception) { }
                        return false;
                    }
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    lock (gate) { stdout = sb.ToString(); }
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
