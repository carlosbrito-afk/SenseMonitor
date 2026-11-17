using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // SenseMonitor.exe --validar-ventoinha [pct]: the one-time hardware validation, run with the
    // user present while the tray app is closed (single-instance mutex). Fans: Automático ->
    // pct% -> Máximo -> Automático (~30 s, RPM sampled every second); modes: Silencioso ->
    // Dia a dia -> Desempenho -> Dia a dia with read-back. Any failure, 90 °C or an exception
    // puts the fans back in Automático at once. The outcome goes to config.json (unlocks
    // Manual/Curva/Máximo only when it passes), to validacao-ventoinha.txt next to it and to
    // one line in erro.log.
    internal static class FanValidationTool
    {
        private const string Caption = "SenseMonitor — teste das ventoinhas";

        // The routine in progress (worker thread), so exit and crash paths can cancel it.
        private static volatile FanValidationRunner current;

        // Crash/exit: stop before the next firmware write (the routine then writes Auto).
        public static void CancelCurrent()
        {
            FanValidationRunner r = current;
            if (r != null) r.Cancel();
        }

        public static int Run(int pct)
        {
            if (!SingleInstance.TryAcquire())
            {
                MessageBox.Show("Feche o SenseMonitor (Sair, no menu do ícone perto do relógio) e rode o teste de novo.",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            try
            {
                // The capability profile decides whether this tool may write to THIS notebook at all.
                Config gateCfg;
                if (Config.TryLoad(Config.DefaultPath, out gateCfg) != ConfigRead.Ok) gateCfg = new Config();
                DetectionRaw id = null;
                try { id = EnvProbe.ReadIdentity(); }
                catch (Exception) { id = null; }
                Safety.Fingerprint = MachineFingerprint.Make(id == null ? null : id.Model, id == null ? null : id.Bios);
                string refusal = EnvGate.ForFanTool(gateCfg, id == null ? null : id.Model, id == null ? null : id.Bios, EnvSession.NowUtc());
                if (refusal != null)
                {
                    Program.LogLine("Teste das ventoinhas recusado: " + refusal);
                    MessageBox.Show(refusal, Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return ToolExit.Refused;
                }
                FanLayout layout = gateCfg.EnvFanLayout;
                bool modesOk = EnvGate.ModesAnnounced(gateCfg.Env);

                DialogResult go = MessageBox.Show(
                    "Teste rápido das ventoinhas e dos modos (cerca de 30 segundos).\n\n"
                    + "• As ventoinhas vão mudar de velocidade e ficar no máximo por alguns segundos (barulho alto é normal).\n"
                    + "• O modo passa por Silencioso e Desempenho e termina em Dia a dia.\n"
                    + "• Se a temperatura chegar a 90 °C ou algo falhar, as ventoinhas voltam ao automático na hora.\n\n"
                    + "Começar?", Caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                if (go != DialogResult.OK) return 3;

                Outcome outcome = null;
                using (ProgressForm form = new ProgressForm())
                {
                    Thread worker = new Thread(delegate()
                    {
                        Outcome o = Execute(pct, layout, modesOk);
                        outcome = o;
                        try { form.BeginInvoke((MethodInvoker)form.Done); }
                        catch (Exception) { }
                    });
                    worker.IsBackground = true;
                    worker.Name = "SenseMonitor.FanValidation";
                    worker.SetApartmentState(ApartmentState.MTA);
                    form.Shown += delegate { worker.Start(); };
                    Application.Run(form);
                    if (!worker.Join(5000))
                    {
                        // Leaving while the routine still runs (session end, message loop gone):
                        // cancel it, give it a moment to write Auto itself, then the isolated Auto.
                        CancelCurrent();
                        worker.Join(3000);
                        FanSafety.SetAutoIsolated(2500);
                    }
                }
                if (outcome == null)
                {
                    FanSafety.SetAutoIsolated(2500);
                    return 1;
                }
                string text = Save(outcome);
                MessageBox.Show(text, Caption, MessageBoxButtons.OK,
                    outcome.Fans != null && outcome.Fans.Passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                return outcome.Fans != null && outcome.Fans.Passed ? 0 : 1;
            }
            finally
            {
                SingleInstance.Release();
            }
        }

        private sealed class Outcome
        {
            public FanValidationReport Fans;
            public ModeCheckReport Modes;
            public string Error;
        }

        // Worker thread (MTA) with its own WmiFirmware; always ends with the fans in Automático.
        private static Outcome Execute(int pct, FanLayout layout, bool modesOk)
        {
            Outcome o = new Outcome();
            GuardedFirmware fw = null;
            HeartbeatPump pump = null;
            Program.Validating = true;
            try
            {
                // The test writes to the firmware: not in diagnostic read-only mode. Its writes are
                // journaled and tracked by the fan marker like the app's own.
                Safety.ReadOnly = Safety.ReadOnlyRequestedFromDisk();
                if (Safety.ReadOnly)
                {
                    o.Error = "O modo diagnóstico (somente leitura) está ligado: o teste envia comandos ao firmware. Desligue-o em Configurações e tente de novo.";
                    return o;
                }
                // The fan sentinel guards the test too (it is the riskiest thing the app does: Máximo, 50 %).
                Safety.EnsureSentinel = Sentinel.EnsureRunning;
                pump = new HeartbeatPump();
                FanLayoutStore.Set(layout); // only the fans this notebook has
                fw = Safety.Wrap(new WmiFirmware());
                if (!fw.Available)
                {
                    o.Error = "O firmware não respondeu (o app precisa estar como administrador).";
                    return o;
                }
                FanValidationRunner runner = new FanValidationRunner(fw, fw.SetFanMax, Thread.Sleep, pct, layout);
                current = runner;
                o.Fans = runner.RunFans();
                // No mode changes after heat, an unreadable temperature or a cancel.
                if (!o.Fans.SkipModeCheck && modesOk) o.Modes = runner.RunModes();
            }
            catch (Exception ex)
            {
                o.Error = "Erro durante o teste (" + ex.GetType().Name + ").";
            }
            finally
            {
                try { if (fw != null) fw.SetFanAuto(); }
                catch (Exception) { }
                current = null;
                if (pump != null) pump.Dispose();
                Program.Validating = false;
            }
            return o;
        }

        // Writes config.json, validacao-ventoinha.txt and one log line; returns the user message.
        private static string Save(Outcome o)
        {
            DateTime now = DateTime.UtcNow;
            StringBuilder file = new StringBuilder();
            file.AppendLine("Teste das ventoinhas e modos — " + now.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            if (o.Error != null) file.AppendLine(o.Error);
            if (o.Fans != null)
            {
                file.AppendLine(o.Fans.Summary());
                foreach (string line in o.Fans.Raw) file.AppendLine("  " + line);
            }
            if (o.Modes != null)
            {
                file.AppendLine(o.Modes.Passed ? "Modos: APROVADO" : "Modos: REPROVADO");
                foreach (string line in o.Modes.Lines) file.AppendLine("  " + line);
            }

            string saveNote = "";
            try
            {
                string path = Config.DefaultPath;
                Config cfg = Config.Load(path);
                if (o.Fans != null) o.Fans.ApplyTo(cfg, now);
                else
                {
                    cfg.FanControlValidated = false;
                    cfg.FanValidationUtc = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    cfg.FanValidationNotes = o.Error;
                }
                if (o.Modes != null) o.Modes.ApplyTo(cfg, now);
                cfg.Save(path);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "validacao-ventoinha.txt"), file.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                saveNote = "\n\nNão foi possível salvar o resultado (" + ex.GetType().Name + ").";
            }
            Program.LogLine("Teste das ventoinhas: " + (o.Fans != null ? o.Fans.Summary() : o.Error)
                + (o.Modes != null ? " | " + (o.Modes.Passed ? "modos ok" : "modos falharam") : ""));

            StringBuilder msg = new StringBuilder();
            if (o.Fans != null && o.Fans.Passed)
            {
                msg.Append("Aprovado: as ventoinhas seguiram os comandos.\n\nAbra o SenseMonitor de novo: Manual, Curva e Máximo ficam liberados.");
            }
            else
            {
                msg.Append("Não aprovado: ");
                msg.Append(o.Fans != null ? (o.Fans.Reason ?? FirstFailure(o.Fans)) : o.Error);
                msg.Append("\n\nO controle manual continua bloqueado; o app segue no automático (o firmware decide).");
            }
            if (o.Modes != null)
            {
                msg.Append(o.Modes.Passed ? "\n\nModos: Silencioso, Dia a dia e Desempenho confirmados." : "\n\nModos: algum modo não foi confirmado pelo firmware (detalhes no arquivo).");
            }
            msg.Append("\n\nDetalhes em %APPDATA%\\SenseMonitor\\validacao-ventoinha.txt");
            msg.Append(saveNote);
            return msg.ToString();
        }

        private static string FirstFailure(FanValidationReport r)
        {
            if (r.Cpu != null && !r.Cpu.Passed) return "CPU — " + r.Cpu.Reason;
            if (r.Gpu != null && !r.Gpu.Passed) return "GPU — " + r.Gpu.Reason;
            return "motivo desconhecido";
        }

        // Small window while the test runs; it cannot be closed until the routine returns.
        private sealed class ProgressForm : Form
        {
            private readonly Label label = new Label();
            private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            private readonly DateTime started = DateTime.UtcNow;
            private bool done;

            public ProgressForm()
            {
                Text = Caption;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ControlBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                TopMost = true;
                ClientSize = new Size(420, 90);
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleCenter;
                label.Font = new Font("Segoe UI", 10f);
                Controls.Add(label);
                timer.Interval = 500;
                timer.Tick += delegate { UpdateText(); };
                timer.Start();
                UpdateText();
            }

            private void UpdateText()
            {
                int s = (int)(DateTime.UtcNow - started).TotalSeconds;
                label.Text = "Testando as ventoinhas e os modos… " + s + " s\nNão feche nem suspenda o computador.";
            }

            public void Done()
            {
                done = true;
                timer.Stop();
                Close();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
                base.OnFormClosing(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    timer.Dispose();
                    label.Font.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
