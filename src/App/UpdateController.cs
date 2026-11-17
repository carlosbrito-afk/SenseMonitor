using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Drives the signed silent updater from the tray app: a 30 s UI timer decides what is due
    // (the decisions themselves are Core: UpdateSchedule, UpdatePolicy, UpdateGate), the network and
    // verification run on a pool thread, and the apply happens only when the app is idle.
    //
    //   2 min after start, then daily  -> check + download + verify + stage (UpdateService)
    //   staged + idle                  -> re-verify, start the helper, exit through the normal path
    //   60 s of healthy running        -> write the "started ok" flag (the helper waits for it)
    internal sealed class UpdateController : IDisposable
    {
        private const int TickMs = 30 * 1000;
        private const int HealthyAfterMs = 60 * 1000;
        private const int CleanExitMinMs = 10 * 1000;

        private readonly TrayApp app;
        private readonly Config config;
        private readonly SynchronizationContext ui;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch uptime = Stopwatch.StartNew();
        private readonly UpdateVersion current;
        private readonly bool installedCopy;
        private readonly string disabledReason;

        private int busy;               // 0/1: a check or an apply is in flight
        private bool startupCheckDone;
        private bool okWritten;
        private bool exitingForUpdate;
        private bool disposed;
        private object healthProbe;     // SensorReading reference seen at the first tick
        private long hiddenSinceMs = -1;
        private long nextLookMs;
        private StagedUpdate staged;
        private bool stagedLooked;
        private string status;
        private string lastGateLog;
        private volatile string ownHash;   // SHA-256 of the installed exe as it was when this process started

        public UpdateController(TrayApp app, Config config)
        {
            this.app = app;
            this.config = config;
            ui = SynchronizationContext.Current;
            UpdateVersion.TryParse(AppVersion.Text, out current);
            installedCopy = UpdatePolicy.IsInstalledCopy(Application.ExecutablePath, InstallPaths.InstalledExe);
            if (!installedCopy) disabledReason = "este não é o app instalado em Arquivos de Programas";
            else if (!UpdateKey.Configured) disabledReason = "a chave de assinatura das atualizações ainda não foi configurada neste build";
            else disabledReason = null;
            if (disabledReason != null)
            {
                status = "Atualizações indisponíveis: " + disabledReason + ".";
                if (installedCopy) UpdateLog.Append("atualização desativada: " + disabledReason);
            }
            else status = "Ainda não verificado.";
            if (installedCopy) StartBackgroundChecks();
            timer.Interval = TickMs;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        // At every start of the installed copy, whatever the update settings say: the .config next to the exe
        // is rewritten from the embedded copy when it differs (it is read before Main, so a planted or stale
        // one must not survive), and the SHA-256 of this very exe is taken once: the apply helper is a copy
        // of it and must match.
        private void StartBackgroundChecks()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (InstallSystem.EnsureConfig(InstallTool.EmbeddedConfig())) UpdateLog.Append("arquivo de configuração do app refeito a partir do embutido (vale a partir da próxima abertura)");
                    ownHash = UpdateHelper.HashOf(InstallPaths.InstalledExe);
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                }
            });
        }

        // Shown on Configurações.
        public string Status { get { return config.AutoUpdate || disabledReason != null ? status : "Atualizações automáticas desligadas."; } }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Dispose();
            // A clean exit after a few seconds counts as "started fine" (the user may close the app
            // before the 60 s mark); the crash path never does, and neither does our own update exit.
            if (installedCopy && !okWritten && !exitingForUpdate && !Program.IsCrashing && uptime.ElapsedMilliseconds >= CleanExitMinMs)
            {
                UpdateStore.WriteOkFlag(AppVersion.Text);
            }
        }

        private void Tick()
        {
            if (disposed) return;
            try
            {
                long up = uptime.ElapsedMilliseconds;
                TrackHidden(up);
                if (healthProbe == null) healthProbe = app.Latest;
                if (installedCopy && !okWritten && up >= HealthyAfterMs) MarkHealthy();
                if (disabledReason != null || !config.AutoUpdate) return;
                if (Interlocked.CompareExchange(ref busy, 0, 0) != 0) return;

                if (!stagedLooked)
                {
                    stagedLooked = true;
                    LookForStaged();
                    return;
                }
                if (staged != null)
                {
                    TryApply(up);
                    return;
                }
                if (up >= UpdateSchedule.StartupDelayMs && up >= nextLookMs) MaybeCheck(up);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        private void TrackHidden(long up)
        {
            bool visible = app.WindowVisible;
            if (visible) hiddenSinceMs = -1;
            else if (hiddenSinceMs < 0) hiddenSinceMs = up;
        }

        // The ticks of the firmware loop replaced app.Latest at least once since the first tick, the UI
        // thread is turning (this timer fired) and no exit path started: healthy enough to say so.
        private void MarkHealthy()
        {
            if (app.IsExiting) return;
            if (ReferenceEquals(healthProbe, app.Latest)) return; // no firmware tick since the first look
            okWritten = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool wrote = UpdateStore.WriteOkFlag(AppVersion.Text);
                string marker = UpdateStore.ReadUpdatedMarker();
                if (marker == AppVersion.Text)
                {
                    UpdateLog.Append("versão " + AppVersion.Text + " rodando e confirmada");
                    Post(delegate
                    {
                        if (config.UpdateBalloon) app.ShowTrayBalloon("SenseMonitor atualizado para " + AppVersion.Text);
                    });
                    UpdateStore.ClearUpdatedMarker();
                }
                else if (!wrote) UpdateLog.Append("não foi possível gravar a confirmação de funcionamento");
            });
        }

        // ---- what is waiting from an earlier run ----------------------------------------------

        private void LookForStaged()
        {
            Interlocked.Exchange(ref busy, 1);
            ThreadPool.QueueUserWorkItem(delegate
            {
                StagedUpdate found = null;
                try
                {
                    string reason;
                    string dir = UpdateStore.SecureStageDir(true, out reason);
                    if (dir == null)
                    {
                        Post(delegate { SetStatus("Atualizações indisponíveis: " + reason + "."); });
                        UpdateLog.Append("atualização desativada: " + reason);
                        return;
                    }
                    string data = UpdateStore.SecureDataDir(true, out reason);
                    if (data == null)
                    {
                        Post(delegate { SetStatus("Atualizações indisponíveis: " + reason + "."); });
                        UpdateLog.Append("atualização desativada: " + reason);
                        return;
                    }
                    UpdateStore.CleanLeftovers(dir);
                    found = UpdateService.VerifyStaged(dir, current, UpdateKey.PublicKeyXml, UpdateService.ProductVersionOfFile,
                        UpdateStore.PathOf(UpdateStore.StagedName), out reason);
                    if (found != null && UpdateStore.LoadState(data).IsBlocked(found.Version, DateTime.UtcNow)) found = null;
                    if (found != null) UpdateLog.Append("atualização " + found.Version + " guardada de antes e conferida; aguardando momento ocioso");
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                }
                finally
                {
                    Post(delegate
                    {
                        staged = found;
                        if (found != null) SetStatus("Versão " + found.Version + " pronta; será instalada quando o app estiver ocioso.");
                        Interlocked.Exchange(ref busy, 0);
                    });
                }
            });
        }

        // ---- check -----------------------------------------------------------------------------

        // Settings "Procurar agora": a check right away, ignoring the once-a-day schedule. Returns a
        // message to show when nothing was started (null = a check is running; the status line follows it).
        public string CheckNow()
        {
            if (!installedCopy || disabledReason != null) return Status;
            if (Interlocked.CompareExchange(ref busy, 0, 0) != 0) return "Uma verificação já está em andamento…";
            MaybeCheck(uptime.ElapsedMilliseconds, true);
            return null;
        }

        private void MaybeCheck(long up)
        {
            MaybeCheck(up, false);
        }

        private void MaybeCheck(long up, bool force)
        {
            string reason;
            string dir = UpdateStore.SecureStageDir(true, out reason);
            if (dir == null)
            {
                SetStatus("Atualizações indisponíveis: " + reason + ".");
                return;
            }
            string dataDir = UpdateStore.SecureDataDir(true, out reason);
            if (dataDir == null)
            {
                SetStatus("Atualizações indisponíveis: " + reason + ".");
                return;
            }
            UpdateState state = UpdateStore.LoadState(dataDir);
            bool startup = !startupCheckDone;
            if (!force && !UpdateSchedule.IsDue(DateTime.UtcNow, state.LastCheckUtc, state.LastAttemptUtc, startup))
            {
                // Not due (checked recently): the startup check has nothing to do.
                startupCheckDone = true;
                nextLookMs = up + 10 * 60 * 1000; // nothing due: look at the clock again in 10 min
                if (state.LastCheckUtc.HasValue && status == "Ainda não verificado.") SetStatus(CheckedText(state.LastCheckUtc.Value, "na versão mais recente (ou aguardando a próxima verificação)"));
                return;
            }
            startupCheckDone = true;
            Interlocked.Exchange(ref busy, 1);
            SetStatus("Verificando atualizações…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                CheckResult r = null;
                try
                {
                    UpdateService svc = new UpdateService(new HttpUpdateNet(), dir, dataDir, current, UpdateKey.PublicKeyXml,
                        delegate { return DateTime.UtcNow; }, UpdateService.ProductVersionOfFile, Environment.OSVersion.Version, UpdateLog.Append);
                    r = svc.CheckAndStage();
                }
                catch (Exception ex)
                {
                    UpdateLog.Append("verificação falhou: " + ex.GetType().Name);
                }
                finally
                {
                    CheckResult res = r;
                    Post(delegate { CheckDone(res, dir); });
                }
            });
        }

        private void CheckDone(CheckResult r, string dir)
        {
            Interlocked.Exchange(ref busy, 0);
            if (r == null) { SetStatus("Não foi possível verificar atualizações agora."); return; }
            DateTime now = DateTime.Now;
            switch (r.Outcome)
            {
                case CheckOutcome.Staged:
                    string reason;
                    staged = UpdateService.VerifyStaged(dir, current, UpdateKey.PublicKeyXml, UpdateService.ProductVersionOfFile,
                        UpdateStore.PathOf(UpdateStore.StagedName), out reason);
                    SetStatus(staged != null ? "Versão " + staged.Version + " pronta; será instalada quando o app estiver ocioso." : "Atualização descartada na conferência final.");
                    break;
                case CheckOutcome.UpToDate:
                case CheckOutcome.NoRelease:
                    SetStatus(CheckedText(now, "na versão mais recente"));
                    break;
                case CheckOutcome.Blocked:
                case CheckOutcome.Rejected:
                    SetStatus(CheckedText(now, "uma versão nova foi recusada (veja atualizacao.log)"));
                    break;
                default:
                    SetStatus("Não foi possível verificar atualizações agora; tentará de novo mais tarde.");
                    break;
            }
        }

        private static string CheckedText(DateTime local, string tail)
        {
            return "Verificado em " + local.ToString("dd/MM HH:mm") + ": " + tail + ".";
        }

        // ---- apply -----------------------------------------------------------------------------

        private void TryApply(long up)
        {
            IdleFacts f = app.CollectIdleFacts(hiddenSinceMs < 0 ? 0 : up - hiddenSinceMs);
            GateResult g = UpdateGate.Check(f);
            if (g == GateResult.Ok && ProcessScan.AppIds(Process.GetCurrentProcess().Id).Count > 0) g = GateResult.ToolRunning; // another app/tool instance (the fan sentinel does not count)
            if (g != GateResult.Ok)
            {
                string why = UpdateGate.Describe(g);
                SetStatus("Versão " + staged.Version + " pronta; aguardando (" + why + ").");
                if (lastGateLog != why)
                {
                    lastGateLog = why;
                    UpdateLog.Append("atualização " + staged.Version + " adiada: " + why);
                }
                return;
            }

            // The helper could not apply a moment ago (swap failed, another instance): wait out the backoff so the
            // app is not closed and reopened at every idle window.
            string dataReason;
            string dataDir = UpdateStore.SecureDataDir(false, out dataReason);
            if (dataDir != null && UpdateStore.LoadState(dataDir).ApplyDeferred(DateTime.UtcNow))
            {
                SetStatus("Versão " + staged.Version + " pronta; nova tentativa de instalar daqui a pouco.");
                return;
            }

            // Right before the helper runs: everything about the staged file is verified again.
            string reason;
            string dir = UpdateStore.SecureStageDir(false, out reason);
            StagedUpdate su = null;
            if (dir != null)
            {
                su = UpdateService.VerifyStaged(dir, current, UpdateKey.PublicKeyXml, UpdateService.ProductVersionOfFile,
                    UpdateStore.PathOf(UpdateStore.StagedName), out reason);
            }
            if (su == null)
            {
                UpdateLog.Append("atualização descartada antes de aplicar: " + reason);
                staged = null;
                SetStatus("Atualização descartada na conferência final.");
                return;
            }
            Interlocked.Exchange(ref busy, 1);
            string err;
            UpdateLog.Append("aplicando " + su.Version + ": iniciando o aplicador e encerrando o app");
            exitingForUpdate = true;
            if (!UpdateHelper.Launch(su, Process.GetCurrentProcess().Id, ownHash, out err))
            {
                exitingForUpdate = false;
                UpdateLog.Append("aplicação não iniciada: " + err);
                // Do not loop: forget it for this run (it is looked at again at the next start).
                staged = null;
                Interlocked.Exchange(ref busy, 0);
                SetStatus("Não foi possível aplicar a atualização agora.");
                return;
            }
            app.ExitApp();
        }

        private void SetStatus(string s)
        {
            status = s;
        }

        private void Post(Action a)
        {
            if (ui == null || disposed) return;
            ui.Post(delegate
            {
                if (disposed) return;
                try { a(); }
                catch (Exception ex) { Program.LogError(ex); }
            }, null);
        }
    }
}
