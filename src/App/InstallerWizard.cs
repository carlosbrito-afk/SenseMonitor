using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // The unified installer: ONE window for the welcome, the environment test (the same steps as the standalone
    // wizard), the install itself and the final screen.
    //
    //   Bem-vindo -> Notebook -> Programas -> Instalando -> Tecla -> Ventoinhas -> Modos -> Pronto
    //
    // Two processes take part. The first stage (not elevated) shows only the welcome step; "Instalar" asks Windows for
    // elevation through FirstStageRunner (the planted-file check runs there) and the elevated copy opens the same window
    // at the second step. The only thing that travels between them is the step token on the command line.
    //
    // Cancel before "Instalando" finishes: nothing is installed (an unfinished install is undone, services the test
    // disabled are put back). Cancel after it: the app stays installed, the tests that were not completed stay locked
    // and the app asks for them at its first open. Re-running the exe when SenseMonitor is installed does not come here
    // (the hand-off dialog of FirstStageRunner decides).
    internal static class InstallerWizard
    {
        internal const string Caption = "Instalar o SenseMonitor";
        internal const string HeaderTitle = "INSTALAÇÃO DO SENSEMONITOR";

        // The window's steps, in order. Detect and Conflicts come from the environment test; the install goes in after them.
        internal static List<IWizardStep> Steps(InstallContext ctx)
        {
            List<IWizardStep> l = new List<IWizardStep>();
            l.Add(new WelcomeStep(ctx));
            l.Add(new DetectStep());
            l.Add(new ConflictsStep());
            l.Add(new InstallingStep(ctx));
            l.Add(new KeyStep());
            l.Add(new FanStep());
            l.Add(new ModeStep());
            l.Add(new InstallerSummaryStep(ctx));
            return l;
        }

        // The window (also used by the screenshot harness). startKey: the step to open at (null = the first).
        internal static EnvWizardForm CreateForm(IEnvServices svc, InstallContext ctx, Func<EnvOutcome, bool> onFinish, string startKey)
        {
            EnvWizardForm f = new EnvWizardForm(svc, Steps(ctx), onFinish, Caption);
            f.HeaderTitle = HeaderTitle;
            f.CancelNote = "Cancelando: desfazendo o que foi feito…";
            f.StartKey = startKey;
            f.DesignWidth = 860;
            f.ShowAllBeforeDetection = true;
            return f;
        }

        // ---- first stage (not elevated) ----------------------------------------------------------------------

        public static void RunFirstStage()
        {
            InstallContext ctx = new InstallContext();
            ctx.Elevated = false;
            // The exe is held while this window is open and its hash goes to the elevated copy, which compares it with the file
            // it opens itself: a file swapped in between is refused there. (If it cannot be held, no hash is sent; the elevated
            // copy still holds its own.)
            string holdError;
            ctx.Source = SourceHold.Open(Application.ExecutablePath, out holdError);
            string shaSwitch = ctx.Source == null ? null : StartupRouter.ShaSwitch(ctx.Source.Sha256);
            ctx.ElevateToInstall = delegate
            {
                return FirstStageRunner.ElevateForWizard(StartupRouter.InstallSwitch + " " + StartupRouter.StageDetectSwitch + (shaSwitch == null ? "" : " " + shaSwitch));
            };
            ctx.ElevateToRunOnly = delegate { return FirstStageRunner.ElevateForWizard(StartupRouter.ExecuteHereSwitch); };
            ctx.Blocker = AppOpenText;
            Environment.ExitCode = 1; // closing the window without choosing is a "no"
            try
            {
                using (EnvWizardForm f = CreateForm(new NullEnvServices(), ctx, null, null))
                {
                    Application.Run(f);
                }
            }
            finally
            {
                if (ctx.Source != null) ctx.Source.Dispose();
            }
        }

        // Another SenseMonitor (a copy opened "without installing", a development build) holds the single-instance mutex.
        private static string AppOpenText()
        {
            try
            {
                if (Sentinel.IsAppAlive())
                    return "Há um SenseMonitor aberto. Feche-o (Sair, no menu do ícone perto do relógio) e clique de novo.";
            }
            catch (Exception)
            {
            }
            return null;
        }

        // ---- elevated -----------------------------------------------------------------------------------------

        // Runs the window in the elevated copy. Returns true when the user chose "Abrir sem instalar" and this process
        // should go on as the tray app; otherwise Environment.ExitCode is set and the process ends.
        public static bool RunElevated(InstallerStage stage, string expectedSha256)
        {
            // FIRST, before any window or wait: hold the exe being installed (nobody can write, rename or delete it while the
            // handle is open) and check it is the file the first stage saw. The copy into Program Files is taken from this handle.
            string holdError;
            SourceHold hold = SourceHold.Open(Application.ExecutablePath, out holdError);
            string verifyError = hold == null ? holdError : hold.Verify(expectedSha256);
            if (verifyError != null)
            {
                if (hold != null) hold.Dispose();
                MessageBox.Show("Não foi possível instalar com segurança: " + verifyError + ".", Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Environment.ExitCode = 2;
                return false;
            }
            try
            {
                return RunElevatedHeld(stage, hold);
            }
            finally
            {
                hold.Dispose();
            }
        }

        private static bool RunElevatedHeld(InstallerStage stage, SourceHold hold)
        {
            if (!SingleInstance.TryAcquire())
            {
                MessageBox.Show("Há um SenseMonitor aberto. Feche-o (Sair, no menu do ícone perto do relógio) e abra o instalador de novo.",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Environment.ExitCode = 2;
                return false;
            }
            InstallContext ctx = new InstallContext();
            ctx.Elevated = true;
            ctx.Source = hold;
            ctx.CreateOps = delegate { return new RealInstallOps(hold, Process.GetCurrentProcess().Id, AppVersion.Text, InstallTool.EmbeddedConfig()); };
            ctx.AppIsRunning = delegate { return ProcessScan.AppIds(Process.GetCurrentProcess().Id).Count > 0; };
            InstallerServices svc = null;
            bool openNow = false;
            bool ok = false;
            try
            {
                // The tests write to the firmware: honour diagnostic read-only mode (the wizard then skips them).
                Safety.ReadOnly = Safety.ReadOnlyRequestedFromDisk();
                try
                {
                    DetectionRaw id = EnvProbe.ReadIdentity();
                    Safety.Fingerprint = MachineFingerprint.Make(id.Model, id.Bios);
                }
                catch (Exception) { }
                svc = new InstallerServices(new EnvServices(null, null, Safety.ReadOnly, null, null), ctx);
                EnvWizardTool.Active = svc;
                InstallerServices s = svc;
                using (EnvWizardForm f = CreateForm(svc, ctx, delegate(EnvOutcome o) { return SaveOutcome(s, o); }, stage == InstallerStage.Detect ? "detect" : null))
                {
                    Application.Run(f);
                    ok = f.DialogResult == DialogResult.OK;
                }
                openNow = ok && ctx.OpenRequested;
            }
            finally
            {
                EnvWizardTool.Active = null;
                SingleInstance.Release();
            }

            if (ctx.RunWithoutInstall && !ctx.Installed) return true;
            Environment.ExitCode = ok ? 0 : (ctx.Installed ? 3 : 1);
            if (openNow)
            {
                string err = InstallSystem.StartApp();
                UpdateLog.Append("instalador: abrir o SenseMonitor: " + (err ?? "ok"));
                if (err != null)
                    MessageBox.Show("O SenseMonitor ficou instalado, mas não consegui abri-lo agora (" + err + "). Abra pelo Menu Iniciar.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (!ok && ctx.Installed)
            {
                AfterCancelledInstalled();
            }
            else if (!ok)
            {
                CleanLeftovers();
            }
            return false;
        }

        // The window was closed after the install finished but before the last screen: the app is there, the tests that
        // were not completed stay locked, and nothing of this run was saved (the app runs its own wizard at the first open).
        private static void AfterCancelledInstalled()
        {
            DialogResult r = MessageBox.Show(
                "O SenseMonitor ficou instalado e abre sozinho quando você entrar no Windows.\n\n"
                + "Você fechou o teste antes do fim: o controle das ventoinhas e dos modos continua bloqueado, e o SenseMonitor chama o teste de ambiente na primeira vez que abrir.\n\n"
                + "Abrir o SenseMonitor agora?",
                Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            if (r == DialogResult.Yes) InstallSystem.StartApp();
        }

        // Nothing was installed: the empty admin-only folders the detection created in ProgramData go too (only when empty).
        private static void CleanLeftovers()
        {
            try { InstallSystem.RemoveEmptyStateFolders(); }
            catch (Exception) { }
        }

        // The finished handler: the result goes into the config of the INSTALLED app (EnvOutcomeSaver). An existing
        // config that cannot be read is moved aside by Config.Load, never overwritten.
        private static bool SaveOutcome(InstallerServices svc, EnvOutcome o)
        {
            if (o == null || o.Profile == null) return false;
            string path = Config.DefaultPath;
            bool ok = false;
            try
            {
                Config cfg = Config.Load(path);
                ok = EnvOutcomeSaver.Save(cfg, path, o);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            svc.Journal("perfil salvo pelo instalador: " + (ok ? "sim" : "não") + "; modos liberados=" + o.Profile.ConfirmedModes().Count.ToString(CultureInfo.InvariantCulture)
                + "; controle de ventoinha=" + (o.Profile.Allows(Cap.FanControl) ? "liberado" : "bloqueado"));
            if (ok)
            {
                svc.MarkRunFinished();
                FanLayoutStore.Set(o.Profile.Layout ?? FanLayout.Both);
            }
            return ok;
        }
    }

    // The first stage has no machine to talk to: the welcome step is the only one that is entered.
    internal sealed class NullEnvServices : IEnvServices
    {
        public bool ReadOnly { get { return false; } }
        public string AppVersion { get { return null; } }
        public DetectionRaw Detect() { return new DetectionRaw(); }
        public List<ConflictItem> ScanConflicts() { return new List<ConflictItem>(); }
        public DisableOutcome DisableConflicts() { return new DisableOutcome(); }
        public void AcquireFirmware() { }
        public void ReleaseFirmware() { }
        public FanRun RunFanTest(FanLayout layout, Action<FanProgress> progress) { return new FanRun(); }
        public void CancelFanTest() { }
        public ModeTestOutcome RunModeTest(List<PerfMode> candidates, Func<PerfMode, bool> confirm, Action<ModeTestProgress> progress) { return new ModeTestOutcome(); }
        public void CancelModeTest() { }
        public IDisposable ListenKey(KeyCandidate[] candidates, Action<KeyCandidate> onPress) { return null; }
        public void RestoreTouched() { }
        public List<ServiceUndo> ServiceUndoRecords { get { return new List<ServiceUndo>(); } }
        public void Journal(string line) { }
    }

    // The services of the installer's environment test: everything goes to the config-less EnvServices; the one
    // addition is on cancel. Acer services the test disabled BEFORE the install finished are put back (the user asked
    // to cancel the whole thing; after an install the undo records stay for the app's first start and Settings).
    internal sealed class InstallerServices : IEnvServices
    {
        private readonly EnvServices inner;
        private readonly InstallContext ctx;

        public InstallerServices(EnvServices inner, InstallContext ctx)
        {
            this.inner = inner;
            this.ctx = ctx;
        }

        public bool ReadOnly { get { return inner.ReadOnly; } }
        public string AppVersion { get { return inner.AppVersion; } }
        public DetectionRaw Detect() { return inner.Detect(); }
        public List<ConflictItem> ScanConflicts() { return inner.ScanConflicts(); }
        public DisableOutcome DisableConflicts() { return inner.DisableConflicts(); }
        public void AcquireFirmware() { inner.AcquireFirmware(); }
        public void ReleaseFirmware() { inner.ReleaseFirmware(); }
        public FanRun RunFanTest(FanLayout layout, Action<FanProgress> progress) { return inner.RunFanTest(layout, progress); }
        public void CancelFanTest() { inner.CancelFanTest(); }
        public ModeTestOutcome RunModeTest(List<PerfMode> candidates, Func<PerfMode, bool> confirm, Action<ModeTestProgress> progress) { return inner.RunModeTest(candidates, confirm, progress); }
        public void CancelModeTest() { inner.CancelModeTest(); }
        public IDisposable ListenKey(KeyCandidate[] candidates, Action<KeyCandidate> onPress) { return inner.ListenKey(candidates, onPress); }
        public List<ServiceUndo> ServiceUndoRecords { get { return inner.ServiceUndoRecords; } }
        public void Journal(string line) { inner.Journal(line); }
        public void MarkRunFinished() { inner.MarkRunFinished(); }

        public void RestoreTouched()
        {
            inner.RestoreTouched();
            if (!ctx.Installed) PutServicesBack();
        }

        private void PutServicesBack()
        {
            try
            {
                List<ServiceUndo> records = inner.ServiceUndoRecords;
                if (records == null || records.Count == 0) return;
                Config tmp = new Config();
                tmp.ServiceUndo = new List<ServiceUndo>(records);
                UndoStore store = new UndoStore();
                UndoOutcome r = ConflictServices.UndoAll(new WmiServiceControl(), tmp,
                    delegate { return tmp.ServiceUndo.Count == 0 ? store.Clear() : store.Save(tmp.ServiceUndo); }, Safety.Journal);
                inner.Journal("instalação cancelada: " + r.Restored.ToString(CultureInfo.InvariantCulture) + " serviço(s) da Acer restaurado(s)"
                    + (r.Failed > 0 ? "; " + r.Failed.ToString(CultureInfo.InvariantCulture) + " não deu certo (o registro fica guardado e é tentado de novo na próxima instalação; ou reative em services.msc)" : ""));
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }
    }
}
