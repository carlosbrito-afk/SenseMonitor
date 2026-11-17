using System;
using System.Collections.Generic;
using System.IO;

namespace Forja.Core
{
    public enum InstallMode
    {
        // Nothing installed: the window with the welcome, the environment test and the install.
        Wizard,
        // Another copy is installed and this file replaces it (the "Não: atualizar/reinstalar" answer of the hand-off).
        Replace,
        // Run from the installed copy itself: tasks and shortcuts are recreated, nothing is copied.
        Repair
    }

    public static class InstallerFlow
    {
        // installExists: SenseMonitor.exe is in Program Files. sameCopy: this process IS that exe.
        public static InstallMode Mode(bool installExists, bool sameCopy)
        {
            if (sameCopy) return InstallMode.Repair;
            return installExists ? InstallMode.Replace : InstallMode.Wizard;
        }
    }

    // What an install can undo, in the order the plan lists them (the reverse of how it was done).
    public enum RollbackStep
    {
        RemoveUninstallEntry,
        // The two shortcuts (and the Start Menu folder when it ends up empty).
        RemoveShortcuts,
        DeleteTasks,
        RemoveConfigFile,
        // The exe this install copied (a fresh install).
        RemoveInstalledExe,
        // The previous exe comes back over the new one (a reinstall).
        RestorePreviousExe,
        // The install folder, only when THIS run created it.
        RemoveInstallFolder,
        // The app that was open before a reinstall is opened again.
        RestartPreviousApp
    }

    // Which of the done steps are undone when a later one fails or the user cancels. A fresh install is removed
    // completely. A reinstall puts the old exe back and leaves the tasks, the entry and the shortcuts alone (they
    // existed before and point to the same paths). The undo actions are idempotent, so the step that FAILED is
    // undone too (it may have been half done), except the exe swap: CopyExe cleans up after itself when it fails,
    // and an old SenseMonitor.anterior.exe from an EARLIER update must never be moved over a working exe.
    public static class InstallRollback
    {
        public static List<RollbackStep> Plan(IList<InstallStep> completed, InstallStep? failed, bool hadInstall, bool folderCreatedByUs, bool appWasRunning)
        {
            List<RollbackStep> p = new List<RollbackStep>();
            Func<InstallStep, bool> attempted = delegate(InstallStep s) { return completed.Contains(s) || (failed.HasValue && failed.Value == s); };
            if (!hadInstall)
            {
                if (attempted(InstallStep.WriteUninstallEntry)) p.Add(RollbackStep.RemoveUninstallEntry);
                if (attempted(InstallStep.CreateShortcuts)) p.Add(RollbackStep.RemoveShortcuts);
                if (attempted(InstallStep.RegisterTasks)) p.Add(RollbackStep.DeleteTasks);
                if (attempted(InstallStep.CopyConfig)) p.Add(RollbackStep.RemoveConfigFile);
            }
            if (completed.Contains(InstallStep.CopyExe)) p.Add(hadInstall ? RollbackStep.RestorePreviousExe : RollbackStep.RemoveInstalledExe);
            if (!hadInstall && folderCreatedByUs && completed.Contains(InstallStep.PrepareFolder)) p.Add(RollbackStep.RemoveInstallFolder);
            if (hadInstall && appWasRunning && completed.Contains(InstallStep.CloseRunningApp)) p.Add(RollbackStep.RestartPreviousApp);
            return p;
        }
    }

    public sealed class ShortcutSpec
    {
        public string FileName;
        public string Target;
        public string Arguments;
        public string Description;
    }

    // The Start Menu entries (all users): <Programs>\SenseMonitor\SenseMonitor.lnk and "Desinstalar SenseMonitor.lnk".
    public static class StartMenuShortcuts
    {
        public const string FolderName = "SenseMonitor";
        public const string OpenFile = "SenseMonitor.lnk";
        public const string UninstallFile = "Desinstalar SenseMonitor.lnk";
        private static readonly char[] Meta = { '"', '&', '%', '^', '|', '<', '>', '!', ';', '\r', '\n' };

        public static List<ShortcutSpec> Specs(string installedExe)
        {
            List<ShortcutSpec> l = new List<ShortcutSpec>();
            ShortcutSpec open = new ShortcutSpec();
            open.FileName = OpenFile;
            open.Target = installedExe;
            open.Arguments = "";
            open.Description = "Monitor de temperatura, ventoinhas e modos do notebook";
            l.Add(open);
            ShortcutSpec un = new ShortcutSpec();
            un.FileName = UninstallFile;
            un.Target = installedExe;
            un.Arguments = StartupRouter.UninstallSwitch;
            un.Description = "Remove o SenseMonitor deste computador";
            l.Add(un);
            return l;
        }

        public static string FolderPath(string commonPrograms)
        {
            return Path.Combine(commonPrograms, FolderName);
        }

        private static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\'); }
            catch (Exception) { return null; }
        }

        // Exactly <Programs>\SenseMonitor: never the parent, a sibling or a path with dot-dot or shell characters.
        public static bool IsOurFolder(string dir, string commonPrograms)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(commonPrograms)) return false;
            if (dir.IndexOfAny(Meta) >= 0 || dir.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            string root = Norm(commonPrograms);
            if (root == null || root.Length < 6) return false;
            string want = Norm(Path.Combine(root, FolderName));
            string have = Norm(dir);
            return want != null && have != null && string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }

        // Only these two names are ever deleted from that folder.
        public static bool IsOurFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return string.Equals(name, OpenFile, StringComparison.OrdinalIgnoreCase) || string.Equals(name, UninstallFile, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The text of the uninstall window: what goes away, what is asked, what the uninstaller does on the way.
    public sealed class UninstallSummary
    {
        public const string QuestionText = "Manter suas configurações?";

        private readonly string installDir;
        private readonly bool servicesPending;

        private UninstallSummary(string installDir, bool servicesPending)
        {
            this.installDir = installDir;
            this.servicesPending = servicesPending;
        }

        // servicesPending: the environment test disabled Acer services that the uninstaller puts back.
        public static UninstallSummary Build(string installDir, bool servicesPending)
        {
            return new UninstallSummary(installDir, servicesPending);
        }

        public const string ExplanationText = "Sim guarda o config.json, o diário e o histórico de temperaturas, para quando você instalar de novo. Não apaga tudo.";

        public string Question { get { return QuestionText + " " + ExplanationText; } }

        public List<string> RemovedLines()
        {
            List<string> l = new List<string>();
            l.Add("O programa: " + installDir);
            l.Add("As tarefas agendadas: início com o Windows, guardião das ventoinhas e atualizador");
            l.Add("Os atalhos do Menu Iniciar (SenseMonitor e Desinstalar SenseMonitor)");
            l.Add("A entrada em Apps e recursos");
            l.Add("Os dados internos do programa em ProgramData (marcadores de segurança e diário de atualização)");
            return l;
        }

        public string DataLine(bool keep)
        {
            return keep
                ? "Suas configurações e históricos serão mantidos."
                : "Suas configurações e históricos serão apagados (config.json, diário e histórico de temperaturas).";
        }

        // What the uninstaller also does, before it removes anything.
        public List<string> Notes()
        {
            List<string> l = new List<string>();
            l.Add("As ventoinhas voltam ao automático antes de qualquer remoção.");
            if (servicesPending) l.Add("Os serviços da Acer que o teste de ambiente desativou voltam a ser como eram.");
            return l;
        }
    }

    // pt-BR lines of the installer, in one place.
    public static class InstallerText
    {
        public static string Line(InstallStep step)
        {
            switch (step)
            {
                case InstallStep.CloseRunningApp: return "Fechando o SenseMonitor que está aberto";
                case InstallStep.PrepareFolder: return "Preparando a pasta em Arquivos de Programas";
                case InstallStep.CopyExe: return "Copiando o programa e conferindo a cópia";
                case InstallStep.CopyConfig: return "Gravando o arquivo de configuração do programa";
                case InstallStep.RegisterTasks: return "Criando o início automático, o guardião e o atualizador";
                case InstallStep.CreateShortcuts: return "Criando os atalhos do Menu Iniciar";
                case InstallStep.WriteUninstallEntry: return "Registrando em Apps e recursos do Windows";
                case InstallStep.StartApp: return "Abrindo o SenseMonitor";
                default: return "Instalando";
            }
        }

        public static string Line(RollbackStep step)
        {
            switch (step)
            {
                case RollbackStep.RemoveUninstallEntry: return "Removendo a entrada de Apps e recursos";
                case RollbackStep.RemoveShortcuts: return "Removendo os atalhos do Menu Iniciar";
                case RollbackStep.DeleteTasks: return "Removendo as tarefas agendadas";
                case RollbackStep.RemoveConfigFile: return "Removendo o arquivo de configuração";
                case RollbackStep.RemoveInstalledExe: return "Removendo o programa copiado";
                case RollbackStep.RestorePreviousExe: return "Devolvendo a versão que estava instalada";
                case RollbackStep.RemoveInstallFolder: return "Removendo a pasta de instalação";
                case RollbackStep.RestartPreviousApp: return "Abrindo de novo o SenseMonitor que estava aberto";
                default: return "Desfazendo";
            }
        }
    }

    public enum InstallStepState
    {
        Running,
        Done,
        // A non-fatal step failed: the install goes on.
        Warning,
        Failed
    }

    public sealed class InstallProgress
    {
        public InstallStep Step;
        public InstallStepState State;
        // The reason, for Warning and Failed.
        public string Message;
    }

    public sealed class InstallOutcome
    {
        public bool Ok;
        public bool Cancelled;
        public InstallStep? FailedStep;
        public string Error;
        public List<string> Warnings = new List<string>();
        // The undo ran with nothing left behind.
        public bool RollbackClean = true;
        public List<string> RollbackProblems = new List<string>();
        public List<string> RollbackDone = new List<string>();
    }

    // What the install does to the machine, one call per step; every call returns null on success or a pt-BR reason.
    // The real implementation is in Platform (InstallSystem, InstallTasks, the Start Menu); tests use a fake.
    public interface IInstallOps
    {
        string CloseRunningApp();
        string PrepareFolder();
        string CopyExe();
        string CopyConfig();
        string RegisterTasks();
        string CreateShortcuts();
        string WriteUninstallEntry();
        string StartApp();
        // After PrepareFolder: did this run create the folder (nothing was there before)?
        bool FolderCreatedByUs { get; }
        string Undo(RollbackStep step);
    }

    // Runs the steps in order on the calling thread, reports each one, stops at the first fatal failure or a cancel
    // request (asked before each step; once the last step is done a cancel is too late) and undoes what was done.
    public static class InstallEngine
    {
        public static InstallOutcome Run(IInstallOps ops, IList<InstallStep> steps, bool hadInstall, bool appWasRunning, Func<bool> cancelled, Action<InstallProgress> report)
        {
            InstallOutcome o = new InstallOutcome();
            List<InstallStep> done = new List<InstallStep>();
            InstallStep? failed = null;
            bool cancel = false;
            foreach (InstallStep step in steps)
            {
                if (cancelled != null && cancelled()) { cancel = true; break; }
                Report(report, step, InstallStepState.Running, null);
                string err;
                try { err = Do(ops, step); }
                catch (Exception ex) { err = "erro inesperado (" + ex.GetType().Name + ")"; }
                if (err == null)
                {
                    done.Add(step);
                    Report(report, step, InstallStepState.Done, null);
                    continue;
                }
                if (!InstallPlanner.IsFatal(step))
                {
                    done.Add(step);
                    o.Warnings.Add(err);
                    Report(report, step, InstallStepState.Warning, err);
                    continue;
                }
                failed = step;
                o.FailedStep = step;
                o.Error = err;
                Report(report, step, InstallStepState.Failed, err);
                break;
            }

            if (!failed.HasValue && !cancel)
            {
                o.Ok = true;
                return o;
            }
            o.Cancelled = cancel;
            bool created = done.Contains(InstallStep.PrepareFolder) && ops.FolderCreatedByUs;
            foreach (RollbackStep r in InstallRollback.Plan(done, failed, hadInstall, created, appWasRunning))
            {
                string e;
                try { e = ops.Undo(r); }
                catch (Exception ex) { e = "erro inesperado (" + ex.GetType().Name + ")"; }
                if (e == null) o.RollbackDone.Add(InstallerText.Line(r));
                else
                {
                    o.RollbackClean = false;
                    o.RollbackProblems.Add(InstallerText.Line(r) + ": " + e);
                }
            }
            return o;
        }

        private static string Do(IInstallOps ops, InstallStep step)
        {
            switch (step)
            {
                case InstallStep.CloseRunningApp: return ops.CloseRunningApp();
                case InstallStep.PrepareFolder: return ops.PrepareFolder();
                case InstallStep.CopyExe: return ops.CopyExe();
                case InstallStep.CopyConfig: return ops.CopyConfig();
                case InstallStep.RegisterTasks: return ops.RegisterTasks();
                case InstallStep.CreateShortcuts: return ops.CreateShortcuts();
                case InstallStep.WriteUninstallEntry: return ops.WriteUninstallEntry();
                case InstallStep.StartApp: return ops.StartApp();
                default: return "passo desconhecido";
            }
        }

        private static void Report(Action<InstallProgress> report, InstallStep step, InstallStepState state, string message)
        {
            if (report == null) return;
            InstallProgress p = new InstallProgress();
            p.Step = step;
            p.State = state;
            p.Message = message;
            report(p);
        }
    }

    // An elevated process that is not the installed copy runs from a folder anybody can write to: the CLR looks for
    // satellite resource assemblies in <exe folder>\<culture>\ when it resolves resources for the UI culture. The app's own
    // text is all hard-coded pt-BR (no resource manager, no .resx), so the invariant UI culture changes nothing that is shown
    // and leaves no culture folder to probe. Number and date formatting use the format culture, which is not touched.
    public static class CulturePolicy
    {
        public static bool UseInvariantUi(bool elevated, bool runningFromInstalledCopy)
        {
            return elevated && !runningFromInstalledCopy;
        }
    }

    // What the Settings button tells the uninstaller, through a file in the admin-only data folder (never through the
    // command line): the answer to "Manter suas configurações?" the app already asked, when it was written and a nonce.
    [System.Runtime.Serialization.DataContract]
    public sealed class UninstallInstructions
    {
        [System.Runtime.Serialization.DataMember(Name = "manterDados")] public bool KeepData;
        [System.Runtime.Serialization.DataMember(Name = "criadoUtc")] public string CriadoUtc = null;
        [System.Runtime.Serialization.DataMember(Name = "nonce")] public string Nonce = null;
    }

    public static class UninstallInstructionsFile
    {
        public const string FileName = "desinstalar.json";
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
        private const int MaxBytes = 2048;

        public static byte[] Build(bool keepData, DateTime nowUtc, string nonce)
        {
            UninstallInstructions i = new UninstallInstructions();
            i.KeepData = keepData;
            i.CriadoUtc = UpdateState.FormatUtc(nowUtc);
            i.Nonce = nonce;
            using (MemoryStream ms = new MemoryStream())
            {
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(UninstallInstructions)).WriteObject(ms, i);
                return ms.ToArray();
            }
        }

        // Strict: one shape, a fresh timestamp (not older than MaxAge, not from the future) and a 32-hex nonce.
        public static bool TryParse(byte[] json, DateTime nowUtc, out UninstallInstructions result, out string error)
        {
            result = null;
            error = null;
            if (json == null || json.Length == 0 || json.Length > MaxBytes) { error = "instruções vazias ou grandes demais"; return false; }
            UninstallInstructions i;
            try
            {
                using (MemoryStream ms = new MemoryStream(json, false))
                {
                    i = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(UninstallInstructions)).ReadObject(ms) as UninstallInstructions;
                }
            }
            catch (Exception)
            {
                error = "instruções ilegíveis";
                return false;
            }
            if (i == null) { error = "instruções ilegíveis"; return false; }
            DateTime? made = UpdateState.ParseUtc(i.CriadoUtc);
            if (!made.HasValue) { error = "data inválida"; return false; }
            if (made.Value > nowUtc + TimeSpan.FromMinutes(1)) { error = "instruções do futuro"; return false; }
            if (nowUtc - made.Value > MaxAge) { error = "instruções velhas demais"; return false; }
            if (i.Nonce == null || !System.Text.RegularExpressions.Regex.IsMatch(i.Nonce, @"\A[0-9a-f]{32}\z")) { error = "nonce inválido"; return false; }
            result = i;
            return true;
        }
    }
}
