using System;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // SenseMonitor.exe --teste-ambiente: the environment test on its own (support), run while the tray app is
    // closed (single-instance mutex, like --validar-ventoinha). The tray app opens the same wizard itself on the
    // first run and from Configurações. The installer will host the same steps (EnvWizardSteps.Standard) after its
    // own, with a config-less EnvServices (null config) and its own "finished" handler.
    internal static class EnvWizardTool
    {
        private const string Caption = "SenseMonitor — teste de ambiente";

        // The services of the wizard in progress (tray or standalone), so exit and crash paths can stop its tests.
        internal static volatile IEnvServices Active;

        // Any thread: ask a running fan or mode test to stop (each ends in Automático / Dia a dia by itself).
        public static void CancelCurrent()
        {
            IEnvServices s = Active;
            if (s == null) return;
            try { s.CancelFanTest(); }
            catch (Exception) { }
            try { s.CancelModeTest(); }
            catch (Exception) { }
        }

        // Crash / exit: undo only what the running wizard touched (nothing touched = no write).
        public static void RestoreCurrent()
        {
            IEnvServices s = Active;
            if (s == null) return;
            try { s.RestoreTouched(); }
            catch (Exception) { }
        }

        public static int Run()
        {
            if (!SingleInstance.TryAcquire())
            {
                MessageBox.Show("Feche o SenseMonitor (Sair, no menu do ícone perto do relógio) e rode o teste de novo.",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            try
            {
                // The tests write to the firmware: honour diagnostic read-only mode (the wizard then skips them).
                Safety.ReadOnly = Safety.ReadOnlyRequestedFromDisk();
                string path = Config.DefaultPath;
                Config cfg = Config.Load(path);
                try
                {
                    DetectionRaw id = EnvProbe.ReadIdentity();
                    Safety.Fingerprint = MachineFingerprint.Make(id.Model, id.Bios);
                }
                catch (Exception) { }
                EnvServices svc = new EnvServices(cfg, path, Safety.ReadOnly, null, null);
                Active = svc;
                using (EnvWizardForm f = new EnvWizardForm(svc, EnvWizardSteps.Standard(), svc.SaveOutcome, null))
                {
                    Application.Run(f);
                    return f.DialogResult == DialogResult.OK ? 0 : 1;
                }
            }
            finally
            {
                Active = null;
                SingleInstance.Release();
            }
        }
    }
}
