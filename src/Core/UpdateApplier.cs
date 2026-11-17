using System;

namespace Forja.Core
{
    // Everything the apply step touches in the outside world, so the swap / confirm / roll back
    // sequence can be tested without files, processes or a scheduler.
    public interface IUpdateApplyEnv
    {
        // True when the process is gone (or never existed) within the time.
        bool WaitForProcessExit(int pid, int timeoutMs);
        // True when no instance of the installed exe is running except fan sentinels (and this helper,
        // which is a different file): the app, a tool, a second copy.
        bool WaitNoOtherInstances(int timeoutMs);
        bool Exists(string path);
        void Delete(string path);
        // Plain rename on the same volume; throws when it cannot (used to repair a failed Replace).
        void Move(string from, string to);
        // ONE atomic step (File.Replace): source takes the name of target; the previous target is kept
        // as backup. Throws, leaving everything as it was, when it cannot.
        void Replace(string source, string target, string backup);
        // The "started ok" record the new app writes after it ran healthy (its own version).
        void ClearOkFlag();
        bool OkFlagMatches(string version);
        // Starts the installed exe directly (the helper is elevated; no scheduled task involved).
        bool StartInstalledApp();
        // An instance of the installed exe other than a sentinel (the tray or a tool).
        bool IsInstalledAppRunning();
        // Specifically the tray app (not a CLI tool, not the sentinel): the thing that must be running when
        // the helper ends. A tool that happens to be running does not make a closed tray acceptable.
        bool IsTrayRunning();
        // Do not try to apply again before now + span (so the app is not closed and reopened at every idle window).
        void DeferApply(TimeSpan span, string reason);
        // Graceful close only (WM_CLOSE, never a kill: the app hands the fans back on exit).
        void RequestCloseInstalledApp();
        bool WaitInstalledAppExit(int timeoutMs);
        // permanent = a release that ran and failed; otherwise a 24 h pause (the failure may not be its fault).
        void BlockVersion(string version, string reason, bool permanent);
        void Log(string line);
        void Sleep(int ms);
        long NowMs();
    }

    public sealed class ApplyPaths
    {
        public string Current;   // %ProgramFiles%\SenseMonitor\SenseMonitor.exe
        public string Anterior;  // ...\SenseMonitor.anterior.exe (the version being replaced)
        public string Novo;      // ...\atualizacao\SenseMonitor.novo.exe (the staged, verified one)
        public string Falhou;    // ...\atualizacao\SenseMonitor.falhou.exe (a release that was rolled back)
    }

    public sealed class ApplyTimings
    {
        public int WaitOldExitMs = 90 * 1000;
        public int WaitOthersMs = 60 * 1000;
        public int StartGraceMs = 30 * 1000;   // the new app must be seen running within this
        public int OkWaitMs = 3 * 60 * 1000;   // and confirm healthy within this
        public int PollMs = 2000;
        public int CloseWaitMs = 30 * 1000;
        public TimeSpan DeferShort = TimeSpan.FromHours(1);  // another instance / the old app is still there
        public TimeSpan DeferLong = TimeSpan.FromHours(6);   // the swap itself failed
    }

    public enum ApplyResult
    {
        // New version running and confirmed.
        Applied,
        // The old process did not exit in time: nothing was touched, the staged file stays.
        OldStillRunning,
        // Another instance of the app is running: nothing was touched.
        OtherInstanceRunning,
        // Nothing usable to swap or the swap failed (atomically: nothing changed); the old version stays.
        NotApplied,
        // The new version did not prove healthy; the old one is back and the new one is blocked.
        RolledBack,
        // The new version failed AND the old one could not be restored automatically.
        RollbackFailed
    }

    // The helper's whole job (SenseMonitor.aplicador.exe, a copy of the CURRENT, known-good exe):
    // old process gone -> no other instance -> atomic swap (current -> anterior, novo -> current) ->
    // start the new exe -> wait for its "started ok" record; on any failure put anterior back.
    // Whatever happens, the app is left running (the old one, the new one, or the rolled-back one):
    // the helper never ends with the tray app closed.
    public sealed class UpdateApplier
    {
        private readonly ApplyPaths paths;
        private readonly ApplyTimings t;
        private bool swapped;
        private bool appStarted;   // this run already started the app that should be running at the end

        public UpdateApplier(ApplyPaths paths, ApplyTimings timings)
        {
            this.paths = paths;
            t = timings ?? new ApplyTimings();
        }

        public ApplyResult Run(IUpdateApplyEnv env, int oldPid, string newVersion)
        {
            ApplyResult r;
            try
            {
                r = RunCore(env, oldPid, newVersion);
            }
            catch (Exception ex)
            {
                env.Log("falha inesperada no aplicador (" + ex.GetType().Name + ")");
                try { r = swapped ? RollBack(env, newVersion, "falha inesperada depois da troca", true) : ApplyResult.NotApplied; }
                catch (Exception) { r = ApplyResult.RollbackFailed; }
            }
            if (r != ApplyResult.Applied && !appStarted) EnsureAppRunning(env);
            return r;
        }

        // The tray app must be running when the helper ends, unless an instance already is.
        private void EnsureAppRunning(IUpdateApplyEnv env)
        {
            try
            {
                // Only a running TRAY counts. Starting a second tray while one runs is harmless (the new process
                // is turned away by the single-instance mutex), so when in doubt the start is attempted.
                if (env.IsTrayRunning()) return;
                if (env.StartInstalledApp()) env.Log("o app foi iniciado de novo (a atualização não foi aplicada ou foi revertida)");
                else env.Log("ATENÇÃO: não foi possível reiniciar o app; ele abre no próximo logon ou pelo menu Iniciar");
            }
            catch (Exception ex)
            {
                env.Log("ATENÇÃO: falha ao reiniciar o app (" + ex.GetType().Name + ")");
            }
        }

        private ApplyResult RunCore(IUpdateApplyEnv env, int oldPid, string newVersion)
        {
            env.Log("aplicando " + newVersion + ": aguardando o app antigo (pid " + oldPid + ") encerrar");
            if (!env.WaitForProcessExit(oldPid, t.WaitOldExitMs))
            {
                env.Log("o app antigo não encerrou em " + (t.WaitOldExitMs / 1000) + " s; nada foi alterado (a atualização fica para depois)");
                env.DeferApply(t.DeferShort, "app antigo não encerrou");
                return ApplyResult.OldStillRunning;
            }
            if (!env.WaitNoOtherInstances(t.WaitOthersMs))
            {
                env.Log("há outra instância do app em execução (não é o guardião); nada foi alterado");
                env.DeferApply(t.DeferShort, "outra instância em execução");
                return ApplyResult.OtherInstanceRunning;
            }
            if (!env.Exists(paths.Novo))
            {
                env.Log("arquivo da atualização ausente; nada foi alterado");
                env.DeferApply(t.DeferLong, "arquivo ausente");
                return ApplyResult.NotApplied;
            }
            if (!env.Exists(paths.Current))
            {
                env.Log("o executável instalado não existe; nada foi alterado");
                env.DeferApply(t.DeferLong, "executável instalado ausente");
                return ApplyResult.NotApplied;
            }

            try
            {
                env.Replace(paths.Novo, paths.Current, paths.Anterior);
            }
            catch (Exception ex)
            {
                env.Log("não foi possível trocar o executável (" + ex.GetType().Name + ")");
                env.DeferApply(t.DeferLong, "troca do executável falhou");
                // ReplaceFile can fail half way (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): the install name is gone and the
                // old exe sits under the backup name. Put it back before anything else.
                if (!env.Exists(paths.Current) && env.Exists(paths.Anterior))
                {
                    try
                    {
                        env.Move(paths.Anterior, paths.Current);
                        env.Log("a versão anterior foi recolocada no lugar (a troca falhou no meio)");
                    }
                    catch (Exception ex2)
                    {
                        env.Log("ERRO GRAVE: a troca falhou no meio e a versão anterior não pôde ser recolocada (" + ex2.GetType().Name + "); copie SenseMonitor.anterior.exe sobre SenseMonitor.exe");
                        return ApplyResult.RollbackFailed;
                    }
                }
                else env.Log("nada foi alterado");
                return ApplyResult.NotApplied;
            }
            swapped = true;
            env.Log("versão trocada; iniciando o app novo");

            env.ClearOkFlag();
            // Failing to START a process is not proof that the release is bad: roll back, pause it 24 h.
            if (!env.StartInstalledApp()) return RollBack(env, newVersion, "o app novo não pôde ser iniciado", false);

            long t0 = env.NowMs();
            bool seenRunning = false;
            while (env.NowMs() - t0 < t.OkWaitMs)
            {
                if (env.OkFlagMatches(newVersion))
                {
                    env.Log("nova versão " + newVersion + " confirmada (rodou estável)");
                    return ApplyResult.Applied;
                }
                if (env.IsInstalledAppRunning()) seenRunning = true;
                else if (seenRunning) return RollBack(env, newVersion, "a nova versão encerrou antes de ficar estável", true);
                else if (env.NowMs() - t0 > t.StartGraceMs) return RollBack(env, newVersion, "a nova versão não chegou a iniciar", true);
                env.Sleep(t.PollMs);
            }
            if (env.OkFlagMatches(newVersion))
            {
                env.Log("nova versão " + newVersion + " confirmada (rodou estável)");
                return ApplyResult.Applied;
            }
            return RollBack(env, newVersion, "a nova versão não confirmou funcionamento em " + (t.OkWaitMs / 1000) + " s", true);
        }

        private ApplyResult RollBack(IUpdateApplyEnv env, string newVersion, string reason, bool permanent)
        {
            env.Log("REVERTENDO: " + reason);
            env.BlockVersion(newVersion, reason, permanent);
            if (env.IsInstalledAppRunning())
            {
                env.RequestCloseInstalledApp();
                if (!env.WaitInstalledAppExit(t.CloseWaitMs))
                {
                    env.Log("a nova versão não fechou por conta própria e não será forçada (as ventoinhas precisam voltar ao automático); reverta à mão: feche o app e copie SenseMonitor.anterior.exe sobre SenseMonitor.exe");
                    return ApplyResult.RollbackFailed;
                }
            }
            if (!env.Exists(paths.Anterior))
            {
                env.Log("ERRO GRAVE: SenseMonitor.anterior.exe não existe; não há versão para restaurar");
                return ApplyResult.RollbackFailed;
            }
            try
            {
                // Atomic as well: anterior takes the install name, the failed release is kept as falhou.
                env.Replace(paths.Anterior, paths.Current, paths.Falhou);
            }
            catch (Exception ex)
            {
                env.Log("ERRO GRAVE: não foi possível restaurar a versão anterior (" + ex.GetType().Name + ")");
                return ApplyResult.RollbackFailed;
            }
            if (env.StartInstalledApp())
            {
                appStarted = true;
                env.Log("versão anterior restaurada e iniciada");
            }
            else env.Log("versão anterior restaurada; inicie pelo menu Iniciar ou no próximo logon");
            return ApplyResult.RolledBack;
        }
    }
}
