using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // SenseMonitor.exe --autoteste [--auto] [--com-limpeza-ram]: checks within 90 seconds that
    // every function works on this machine and changes nothing. Each check reports OK / FALHOU /
    // PULADO with a one-line reason. It reads sensors, the mode and the fan state, reads config.json
    // (never writes it: a missing or corrupt file is reported, not recreated), measures memory
    // (read-only), installs the key hook for 1 s, reads the autostart task, starts a real second
    // copy of the app to see the single-instance guard turn it away, reads the battery report,
    // disks, crash log, startup list, cleanup sizes, repair tools, System Restore state and the
    // journal of the performance test. The RAM cleaner (which empties the working sets of the
    // whole system) runs ONLY with --com-limpeza-ram, never implied by --auto. It never deletes,
    // toggles or creates anything and never runs dism/sfc. Output: autoteste.txt and autoteste.json
    // next to config.json, one line in erro.log, a final window. With --auto no dialog is shown.
    // Exit code: 0 only when nothing FALHOU; 1 when something failed or it was cut short; 2 another
    // instance is open; 3 declined.
    internal static class AutoTestTool
    {
        private const string Caption = "SenseMonitor — autoteste";
        public const string TextFile = "autoteste.txt";
        public const string JsonFile = "autoteste.json";
        public const string NotRunFile = "autoteste.nao-executado.txt";

        private static volatile bool cancelled;

        public static void CancelCurrent()
        {
            cancelled = true;
        }

        public static int Run(bool unattended, bool withRamClean)
        {
            if (!SingleInstance.TryAcquire())
            {
                if (unattended)
                {
                    Program.LogLine("Autoteste não executado: o SenseMonitor está aberto (outra instância).");
                    TryWriteNotRun("Autoteste NÃO executado em " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC: o SenseMonitor estava aberto (outra instância).");
                }
                else
                {
                    MessageBox.Show("Feche o SenseMonitor (Sair, no menu do ícone perto do relógio) e rode o teste de novo.",
                        Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return ToolExit.OtherInstance;
            }
            try
            {
                if (!unattended)
                {
                    DialogResult go = MessageBox.Show(
                        "Autoteste: confere em até 90 segundos se cada função funciona neste computador.\n\n"
                        + "• Não muda nada: não apaga, não liga nem desliga nada, não cria nada e não grava na configuração.\n"
                        + "• Liga o gancho do teclado por 1 segundo (nenhuma tecla é registrada) e abre uma segunda cópia do app por um instante, para ver se ela é barrada.\n"
                        + (withRamClean
                            ? "• ATENÇÃO: você pediu a limpeza de RAM (--com-limpeza-ram): ela esvazia a memória de todos os programas do sistema.\n"
                            : "• Não limpa a memória RAM (isso só roda com --com-limpeza-ram).\n")
                        + "• Não roda o reparo do Windows (DISM/SFC) nem cria ponto de restauração.\n\n"
                        + "Começar?", Caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                    if (go != DialogResult.OK) return ToolExit.Declined;
                }

                cancelled = false;
                AutoTestReport report = null;
                using (ToolProgressForm form = new ToolProgressForm(Caption, "Preparando…", CancelCurrent, unattended))
                {
                    Thread worker = new Thread(delegate()
                    {
                        report = Execute(form, withRamClean);
                        try { form.BeginInvoke((MethodInvoker)form.Done); }
                        catch (Exception) { }
                    });
                    worker.IsBackground = true;
                    worker.Name = "SenseMonitor.AutoTest";
                    worker.SetApartmentState(ApartmentState.MTA);
                    form.Shown += delegate { worker.Start(); };
                    Application.Run(form);
                    if (!worker.Join(5000))
                    {
                        CancelCurrent();
                        worker.Join(3000);
                    }
                }
                if (report == null)
                {
                    Program.LogLine("Autoteste: a rotina terminou sem resultado.");
                    return ToolExit.Failed;
                }
                int code = ToolExit.ForAuto(report);
                string text = Save(report, code);
                if (!unattended) ToolResultDialog.Show(Caption, text);
                return code;
            }
            finally
            {
                SingleInstance.Release();
            }
        }

        private sealed class Step
        {
            public string Name;
            public int TimeoutSec;
            public Func<CancellationToken, AutoCheck> Do;

            public Step(string name, int timeoutSec, Func<CancellationToken, AutoCheck> run)
            {
                Name = name;
                TimeoutSec = timeoutSec;
                Do = run;
            }
        }

        private static AutoTestReport Execute(ToolProgressForm form, bool withRamClean)
        {
            AutoTestReport rep = new AutoTestReport();
            rep.StartedUtc = DateTime.UtcNow;
            Stopwatch clock = Stopwatch.StartNew();
            KeepAwake.Begin();
            try
            {
                List<Step> steps = Steps(withRamClean);
                for (int i = 0; i < steps.Count; i++)
                {
                    Step s = steps[i];
                    if (cancelled)
                    {
                        rep.Aborted = true;
                        rep.Reason = "teste cancelado";
                        break;
                    }
                    // The 90 s ceiling of the whole test: a step gets what is left, and when
                    // nothing is left it is skipped (and said so).
                    int limit = AutoBudget.StepLimitSec(s.TimeoutSec, clock.Elapsed.TotalSeconds);
                    if (limit <= 0)
                    {
                        rep.Checks.Add(AutoChecks.NoTime(s.Name));
                        continue;
                    }
                    form.SetStatus("Verificando: " + s.Name + " (" + (i + 1) + " de " + steps.Count + ")",
                        Math.Max(0, (int)Math.Round(AutoBudget.TotalSec - clock.Elapsed.TotalSeconds)));
                    rep.Checks.Add(RunStep(s, limit));
                }
            }
            catch (Exception ex)
            {
                rep.Aborted = true;
                rep.Reason = "erro " + ex.GetType().Name;
            }
            finally
            {
                KeepAwake.End();
            }
            rep.ElapsedSec = clock.Elapsed.TotalSeconds;
            return rep;
        }

        // Each check on its own MTA thread so a hung WMI/OS call costs only its own time limit;
        // the token is cancelled when the limit passes (the steps that accept it stop working).
        private static AutoCheck RunStep(Step s, int limitSec)
        {
            AutoCheck result = null;
            string error = null;
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                CancellationToken token = cts.Token;
                Thread t = new Thread(delegate()
                {
                    try { result = s.Do(token); }
                    catch (Exception ex) { error = ex.GetType().Name; }
                });
                t.IsBackground = true;
                t.Name = "SenseMonitor.AutoTest.Step";
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                if (!t.Join(limitSec * 1000))
                {
                    cts.Cancel();
                    return new AutoCheck(s.Name, AutoStatus.Falhou, "não respondeu em " + limitSec + " s");
                }
            }
            if (error != null) return new AutoCheck(s.Name, AutoStatus.Falhou, "erro inesperado (" + error + ")");
            if (result == null) return new AutoCheck(s.Name, AutoStatus.Falhou, "sem resultado");
            return result;
        }

        // Reads only; wrapped anyway so that every firmware object of the app comes from Safety.Wrap.
        private static AutoCheck Firmware(string name, Func<GuardedFirmware, AutoCheck> use)
        {
            GuardedFirmware fw = Safety.Wrap(new WmiFirmware());
            if (!fw.Available) return new AutoCheck(name, AutoStatus.Falhou, "o firmware não respondeu (o app precisa estar como administrador)");
            return use(fw);
        }

        private static List<Step> Steps(bool withRamClean)
        {
            List<Step> l = new List<Step>();
            l.Add(new Step("Sensores", 15, delegate(CancellationToken ct)
            {
                return Firmware("Sensores", delegate(GuardedFirmware fw) { return AutoChecks.Sensors(fw.ReadSensors()); });
            }));
            l.Add(new Step("Leitura do modo", 15, delegate(CancellationToken ct)
            {
                return Firmware("Leitura do modo", delegate(GuardedFirmware fw) { return AutoChecks.ModeRead(fw.GetMode()); });
            }));
            l.Add(new Step("Estado das ventoinhas", 15, delegate(CancellationToken ct)
            {
                return Firmware("Estado das ventoinhas", delegate(GuardedFirmware fw)
                {
                    FanMode? cpu, gpu;
                    bool read = fw.GetFanBehavior(out cpu, out gpu);
                    return AutoChecks.FanState(read, cpu, gpu);
                });
            }));
            l.Add(new Step("Validação das ventoinhas", 8, delegate(CancellationToken ct)
            {
                // Config.TryLoad never creates, truncates or rewrites config.json.
                Config c;
                ConfigRead st = Config.TryLoad(Config.DefaultPath, out c);
                return AutoChecks.FanValidation(st, c);
            }));
            l.Add(new Step("Leitura de memória", 8, delegate(CancellationToken ct)
            {
                return AutoChecks.MemoryRead(RamCleaner.Snapshot());
            }));
            l.Add(new Step("Limpeza de RAM", 20, delegate(CancellationToken ct)
            {
                // Off unless --com-limpeza-ram was typed (--auto never turns it on).
                // Also skipped in diagnostic read-only mode (RAM cleaning is blocked there).
                if (!withRamClean || Safety.ReadOnlyRequestedFromDisk()) return AutoChecks.RamSkipped();
                return AutoChecks.Ram(RamCleaner.CleanDetailed(), Cleaner.IsElevated());
            }));
            l.Add(new Step("Tecla de hardware (hook)", 10, delegate(CancellationToken ct)
            {
                bool installed, removed;
                AutoProbes.HookRoundTrip(out installed, out removed);
                return AutoChecks.Hook(installed, removed);
            }));
            l.Add(new Step("Início automático", 18, delegate(CancellationToken ct)
            {
                string exe = InstallPaths.InstalledExe;
                if (!File.Exists(exe)) return AutoChecks.Autostart(false, exe, -1, null);
                string xml;
                int code;
                Autostart.QueryTaskXml(out xml, out code);
                return AutoChecks.Autostart(true, exe, code, xml);
            }));
            l.Add(new Step("Instância única", 14, delegate(CancellationToken ct)
            {
                bool held;
                bool? blocked;
                AutoProbes.MutexState(out held, out blocked);
                return AutoChecks.Mutex(held, blocked);
            }));
            l.Add(new Step("Relatório da bateria", 20, delegate(CancellationToken ct)
            {
                bool has = true;
                try { has = (SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0; }
                catch (Exception) { }
                return AutoChecks.Battery(has, has ? BatteryHealth.Read() : null);
            }));
            l.Add(new Step("Saúde dos discos", 20, delegate(CancellationToken ct)
            {
                return AutoChecks.Disks(DiskHealth.Read());
            }));
            l.Add(new Step("Histórico de quedas", 25, delegate(CancellationToken ct)
            {
                List<CrashEntry> entries;
                bool read = CrashHistory.TryRead(90, out entries);
                return AutoChecks.Crashes(read, entries == null ? 0 : entries.Count, 90);
            }));
            l.Add(new Step("Registro de temperatura", 10, delegate(CancellationToken ct)
            {
                bool wrote, readBack, own;
                string error;
                AutoProbes.TempLogRoundTrip(out wrote, out readBack, out error, out own);
                return AutoChecks.TempLog(wrote, readBack, error, own);
            }));
            l.Add(new Step("Itens de inicialização", 15, delegate(CancellationToken ct)
            {
                try
                {
                    return AutoChecks.Startup(StartupManager.CreateDefault(null).List().Count, null);
                }
                catch (Exception ex)
                {
                    return AutoChecks.Startup(null, ex.GetType().Name);
                }
            }));
            l.Add(new Step("Análise de limpeza", 30, delegate(CancellationToken ct)
            {
                // Analyze only measures; it stops when the step's token is cancelled (time limit).
                return AutoChecks.Cleanup(Cleaner.CreateDefault(null).Analyze(null, ct));
            }));
            l.Add(new Step("Ferramentas de reparo", 5, delegate(CancellationToken ct)
            {
                return AutoChecks.Repair(AutoProbes.SystemToolExists("Dism.exe"), AutoProbes.SystemToolExists("sfc.exe"));
            }));
            l.Add(new Step("Ponto de restauração", 20, delegate(CancellationToken ct)
            {
                return AutoChecks.Restore(AutoProbes.RestoreDisabledByPolicy(), AutoProbes.RestorePointCount());
            }));
            l.Add(new Step("Diário do teste de desempenho", 5, delegate(CancellationToken ct)
            {
                bool exists, readable;
                System.Collections.Generic.List<string> lines = PerfJournalFile.ReadAll(PerfJournalFile.DefaultPath(), out exists, out readable);
                return AutoChecks.PerfJournalState(exists, readable, readable ? PerfJournal.CrashNote(lines) : null);
            }));
            return l;
        }

        private static string Dir()
        {
            return Path.GetDirectoryName(Config.DefaultPath);
        }

        // A run that did not happen goes to its own file: the last real result stays untouched.
        private static void TryWriteNotRun(string text)
        {
            try
            {
                Directory.CreateDirectory(Dir());
                File.WriteAllText(Path.Combine(Dir(), NotRunFile), text, Encoding.UTF8);
            }
            catch (Exception)
            {
            }
        }

        private static string Save(AutoTestReport rep, int code)
        {
            string text = rep.ToText();
            string note = "";
            try
            {
                Directory.CreateDirectory(Dir());
                File.WriteAllText(Path.Combine(Dir(), TextFile), text, Encoding.UTF8);
                File.WriteAllText(Path.Combine(Dir(), JsonFile), rep.ToJson(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                note = "\nNão foi possível salvar os arquivos (" + ex.GetType().Name + ").";
            }
            List<string> failed = new List<string>();
            foreach (AutoCheck c in rep.Checks) { if (c.Status == AutoStatus.Falhou) failed.Add(c.Name); }
            Program.LogLine("Autoteste: " + rep.Summary() + (rep.Aborted ? " (interrompido: " + rep.Reason + ")" : "")
                + (failed.Count > 0 ? "; falharam: " + string.Join(", ", failed.ToArray()) : "") + "; código de saída " + code);
            return text + "\nSalvo em %APPDATA%\\SenseMonitor\\" + TextFile + " e " + JsonFile + note;
        }
    }
}
