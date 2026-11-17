using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // SenseMonitor.exe --teste-desempenho [--auto]: proves whether each mode really changes
    // performance, heat and noise (about 8-12 minutes). Run with the tray app closed (single-
    // instance mutex), as administrator like the app. Four runs in a balanced order (Dia a dia,
    // Silencioso, Desempenho, Dia a dia), fans forced to Automático; the CPU is loaded for 60 s
    // (all threads) and 20 s (one thread) per run. Safety: a watchdog thread with its own
    // firmware reads stops the load at 90 °C or when the temperature cannot be read for 3 s;
    // every step is journaled to disk first; the machine always ends in Dia a dia with the fans
    // in Automático (also on cancel, exception, crash, logoff and suspend). It refuses to start
    // with background CPU load above 10 %, on battery below 50 %, and in --auto on battery at all.
    // Output: teste-desempenho.txt and .json next to config.json, one line in erro.log, and a
    // final window with the table and the verdict. With --auto nothing is asked or shown beyond
    // the progress window (which closes itself). Exit codes: 0 completed and restored,
    // 1 could not run / aborted / refused, 2 another instance, 3 declined, 4 the final restore
    // could not be confirmed.
    internal static class PerfTestTool
    {
        private const string Caption = "SenseMonitor — teste de desempenho dos modos";
        public const string TextFile = "teste-desempenho.txt";
        public const string JsonFile = "teste-desempenho.json";
        public const string NotRunFile = "teste-desempenho.nao-executado.txt";

        private static volatile PerfTestRunner current;
        private static volatile PerfLoad activeLoad;
        private static volatile bool cancelRequested;

        // Cancel button, exit, crash: stops at the next sample. A click before the runner exists
        // is remembered and applied when it is created.
        public static void CancelCurrent()
        {
            cancelRequested = true;
            PerfTestRunner r = current;
            if (r != null) r.Cancel();
        }

        // Crash path: raise the stop flag of the load threads at once (never blocks), before
        // anything else (no dialog, no firmware write) is attempted.
        public static void StopLoadNow()
        {
            PerfLoad l = activeLoad;
            if (l != null)
            {
                try { l.RequestStop(); }
                catch (Exception) { }
            }
        }

        // Logoff, shutdown, suspend: stop the load, then restore from an isolated writer.
        private static void EmergencyStop()
        {
            CancelCurrent();
            StopLoadNow();
            bool ok = FanSafety.SetSafeStateIsolated(2500);
            Program.LogLine("Teste de desempenho: encerrado por logoff/desligamento/suspensão; restauração " + (ok ? "confirmada" : "NÃO confirmada"));
        }

        public static int Run(bool unattended)
        {
            cancelRequested = false;
            if (!SingleInstance.TryAcquire())
            {
                const string why = "Feche o SenseMonitor (Sair, no menu do ícone perto do relógio) e rode o teste de novo.";
                if (unattended)
                {
                    Program.LogLine("Teste de desempenho não executado: o SenseMonitor está aberto (outra instância).");
                    TryWriteNotRun("Teste de desempenho NÃO executado em " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC: o SenseMonitor estava aberto (outra instância).");
                }
                else
                {
                    MessageBox.Show(why, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return ToolExit.OtherInstance;
            }
            try
            {
                if (!unattended)
                {
                    PerfPower power = PerfProbe.ReadPower();
                    string warn = power.OnBattery == true
                        ? "\n\nATENÇÃO: o notebook está na bateria" + (power.Percent.HasValue ? " (" + power.Percent.Value + " %)" : "")
                            + ". O Windows costuma limitar o desempenho assim; abaixo de " + PerfPlan.BatteryMinPct + " % o teste não começa. Para uma comparação justa, ligue na tomada antes."
                        : "";
                    string crash = PerfJournalFile.PreviousCrashNote(PerfJournalFile.DefaultPath());
                    string crashWarn = crash == null ? "" : "\n\nATENÇÃO: " + crash + ". Veja " + PerfJournal.FileName + " em %APPDATA%\\SenseMonitor.";
                    DialogResult go = MessageBox.Show(
                        "Teste de desempenho dos modos (cerca de 8 a 12 minutos).\n\n"
                        + "• O notebook vai trabalhar a 100 % da CPU por alguns minutos em quatro rodadas: Dia a dia, Silencioso, Desempenho e Dia a dia de novo.\n"
                        + "• Antes de começar, o teste mede por 10 s se há outros programas usando a CPU; se houver, ele não começa.\n"
                        + "• As ventoinhas ficam no automático e podem fazer barulho; o computador esquenta.\n"
                        + "• Se a temperatura chegar a 90 °C, ou ficar ilegível por 3 s, a carga para na hora.\n"
                        + "• No fim o modo volta para Dia a dia e as ventoinhas para o automático.\n"
                        + "• Não use o computador durante o teste, para não atrapalhar as medidas." + warn + crashWarn + "\n\nComeçar?",
                        Caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
                    if (go != DialogResult.OK) return ToolExit.Declined;
                }

                Outcome outcome = null;
                SessionEndingEventHandler onEnd = delegate { EmergencyStop(); };
                PowerModeChangedEventHandler onPower = delegate(object s, PowerModeChangedEventArgs e)
                {
                    if (e.Mode == PowerModes.Suspend) EmergencyStop();
                };
                using (ToolProgressForm form = new ToolProgressForm(Caption, "Preparando o teste…", CancelCurrent, unattended))
                {
                    Thread worker = new Thread(delegate()
                    {
                        Outcome o = Execute(unattended, form);
                        outcome = o;
                        try { form.BeginInvoke((MethodInvoker)form.Done); }
                        catch (Exception) { }
                    });
                    worker.IsBackground = true;
                    worker.Name = "SenseMonitor.PerfTest";
                    worker.SetApartmentState(ApartmentState.MTA);
                    form.Shown += delegate { worker.Start(); };
                    SystemEvents.SessionEnding += onEnd;
                    SystemEvents.PowerModeChanged += onPower;
                    try
                    {
                        Application.Run(form);
                    }
                    finally
                    {
                        SystemEvents.SessionEnding -= onEnd;
                        SystemEvents.PowerModeChanged -= onPower;
                    }
                    if (!worker.Join(3000))
                    {
                        // Leaving while the routine still runs (session end, message loop gone):
                        // stop the load FIRST, then let the routine restore, then the isolated writer.
                        CancelCurrent();
                        StopLoadNow();
                        worker.Join(3000);
                        bool ok = FanSafety.SetSafeStateIsolated(4000);
                        Program.LogLine("Teste de desempenho: saída forçada; restauração " + (ok ? "confirmada" : "NÃO confirmada"));
                    }
                }
                if (outcome == null || (outcome.Report == null && outcome.Error == null))
                {
                    StopLoadNow();
                    bool ok = FanSafety.SetSafeStateIsolated(4000);
                    Program.LogLine("Teste de desempenho: a rotina terminou sem resultado; restauração " + (ok ? "confirmada" : "NÃO confirmada"));
                    return ok ? ToolExit.Failed : ToolExit.RestoreFailed;
                }

                int code = outcome.Report == null ? ToolExit.Failed : ToolExit.ForPerf(outcome.Report);
                string text = Save(outcome, code);
                if (!unattended)
                {
                    if (outcome.Report == null)
                        MessageBox.Show(text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        ToolResultDialog.Show(Caption, text);
                }
                return code;
            }
            finally
            {
                SingleInstance.Release();
            }
        }

        private sealed class Outcome
        {
            public PerfTestReport Report;
            public PerfVerdictResult Verdict;
            public string Error;
        }

        // Worker thread (MTA) with its own WmiFirmware. The runner restores the safe state in its
        // own finally; the finally here only covers an exception before/around it.
        private static Outcome Execute(bool unattended, ToolProgressForm form)
        {
            Outcome o = new Outcome();
            WmiFirmware fw = null;
            PerfLoad load = null;
            PerfProbe probe = null;
            HeatWatchdog watchdog = null;
            PerfTestRunner runner = null;
            bool ran = false;
            Program.PerfTesting = true;
            KeepAwake.Begin();
            try
            {
                string journalPath = PerfJournalFile.DefaultPath();
                string crashNote = PerfJournalFile.PreviousCrashNote(journalPath);
                PerfJournalFile.TrimToRecent(journalPath);

                // 1. conditions: power and background CPU (10 s), then refuse or go on
                form.SetStatus("Verificando a tomada e o uso de CPU de fundo (10 s)…", -1);
                PerfPower power = PerfProbe.ReadPower();
                double? background = PerfProbe.MeasureBackgroundCpu(PerfPlan.BackgroundSec, delegate { return cancelRequested; });
                if (cancelRequested) { o.Error = "Teste cancelado antes de começar."; return o; }
                PreflightResult pf = PerfPlan.Preflight(power.OnBattery, power.Percent, background, unattended);
                if (!pf.Allowed)
                {
                    o.Error = "O teste não começou: " + pf.Refusal + ".";
                    return o;
                }

                fw = new WmiFirmware();
                if (!fw.Available)
                {
                    o.Error = "O firmware não respondeu (o app precisa estar como administrador).";
                    return o;
                }
                PerfPreconditions pre = new PerfPreconditions();
                pre.OnBattery = power.OnBattery;
                pre.BatteryPercent = power.Percent;
                pre.BackgroundCpuPct = background;
                pre.PowerSource = power.OnBattery == true ? "bateria" : (power.OnBattery == false ? "tomada" : "desconhecida");
                pre.AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
                form.SetStatus("Lendo as condições do notebook…", -1);
                PerfProbe.ReadCpu(pre);
                if (cancelRequested) { o.Error = "Teste cancelado antes de começar."; return o; }
                PerfProbe.ReadPowerScheme(pre);
                if (cancelRequested) { o.Error = "Teste cancelado antes de começar."; return o; }

                // 2. the machinery
                probe = new PerfProbe(power.OnBattery == true);
                load = new PerfLoad();
                activeLoad = load;
                Stopwatch clock = Stopwatch.StartNew();
                Func<long> now = delegate { return clock.ElapsedMilliseconds; };
                WmiFirmware guardFw = null; // used by the watchdog's reader thread only
                PerfLoad guardLoad = load;
                watchdog = new HeatWatchdog(
                    delegate
                    {
                        if (guardFw == null) guardFw = new WmiFirmware();
                        return guardFw.ReadSensors();
                    },
                    delegate { guardLoad.RequestStop(); },
                    now, HeatWatchdog.DefaultPollMs, HeatWatchdog.DefaultBlindMs);
                watchdog.Start();
                runner = new PerfTestRunner(fw, load, probe, Thread.Sleep, now, Environment.ProcessorCount);
                runner.Progress = form.SetStatus;
                runner.Guard = watchdog;
                runner.Journal = new PerfJournalFile(journalPath);
                current = runner;
                if (cancelRequested) runner.Cancel(); // a click made while this was being set up
                ran = true;
                o.Report = runner.Run(pre);
                o.Report.PreviousCrashNote = crashNote;
                o.Verdict = PerfVerdict.Build(o.Report);
            }
            catch (Exception ex)
            {
                o.Error = "Erro durante o teste (" + ex.GetType().Name + ").";
            }
            finally
            {
                current = null;
                try { if (load != null) load.Stop(); }
                catch (Exception) { }
                activeLoad = null;
                if (watchdog != null) watchdog.Dispose();
                if (probe != null) probe.Dispose();
                // The runner restores Dia a dia + Automático itself; if it died before
                // finishing, do it here.
                if (ran && o.Report == null && fw != null)
                {
                    try { fw.SetMode(PerfMode.DiaADia); }
                    catch (Exception) { }
                    try { fw.SetFanAuto(); }
                    catch (Exception) { }
                }
                KeepAwake.End();
                Program.PerfTesting = false;
            }
            return o;
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

        // Writes the .txt (summary + raw samples), the .json (no BOM) and one log line; returns
        // the summary text for the final window.
        private static string Save(Outcome o, int exitCode)
        {
            string summary;
            if (o.Report == null)
            {
                summary = "Teste de desempenho NÃO executado em " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC: " + o.Error;
                TryWriteNotRun(summary);
                Program.LogLine("Teste de desempenho: " + o.Error);
                return summary;
            }
            summary = PerfReport.Summary(o.Report, o.Verdict);
            string saveNote = "";
            try
            {
                Directory.CreateDirectory(Dir());
                File.WriteAllText(Path.Combine(Dir(), TextFile), PerfReport.FullText(o.Report, o.Verdict), Encoding.UTF8);
                File.WriteAllText(Path.Combine(Dir(), JsonFile), PerfReport.Json(o.Report, o.Verdict), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                saveNote = "\nNão foi possível salvar os arquivos (" + ex.GetType().Name + ").";
            }
            Program.LogLine("Teste de desempenho: " + (o.Report.Aborted ? "interrompido (" + o.Report.Reason + ")" : "concluído")
                + "; Desempenho " + o.Verdict.Desempenho + ", Silencioso " + o.Verdict.Silencioso + (o.Verdict.Reliable ? "" : " (veredito invalidado)")
                + "; fim em Dia a dia " + (o.Report.FinalModeOk ? "ok" : "NÃO confirmado") + ", ventoinhas " + (o.Report.FinalFansAuto ? "Auto" : "NÃO confirmadas")
                + "; código de saída " + exitCode);
            return summary + "\nDetalhes e amostras por segundo em %APPDATA%\\SenseMonitor\\" + TextFile + " (e " + JsonFile + ")." + saveNote;
        }
    }
}
