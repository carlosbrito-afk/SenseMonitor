using System;
using System.IO;
using Forja.Core;

namespace Forja.Platform
{
    // The install steps of the real machine (InstallEngine runs them in the order InstallPlanner gives, and the undo
    // actions of InstallRollback). Must run elevated. Nothing here asks the user anything.
    public sealed class RealInstallOps : IInstallOps
    {
        private readonly SourceHold source;
        private readonly int myPid;
        private readonly string version;
        private readonly byte[] embeddedConfig;
        private bool folderExisted;
        private bool preparedFolder;

        // source: the exe being installed, HELD open since the elevated start (the copy is taken from that handle).
        // myPid: this process (never asked to close). embeddedConfig: the app.config embedded in the exe (always
        // written from there, never read from beside the source).
        public RealInstallOps(SourceHold source, int myPid, string version, byte[] embeddedConfig)
        {
            this.source = source;
            this.myPid = myPid;
            this.version = version;
            this.embeddedConfig = embeddedConfig;
        }

        public bool FolderCreatedByUs { get { return preparedFolder && !folderExisted; } }

        public string CloseRunningApp()
        {
            if (AppProcesses.CloseGracefully(myPid, 30 * 1000, false)) return null;
            return "O SenseMonitor que está aberto não fechou em 30 segundos. Feche-o pelo ícone da bandeja (Sair) e tente de novo. Nada foi alterado.";
        }

        public string PrepareFolder()
        {
            try { folderExisted = Directory.Exists(InstallPaths.InstallDir); }
            catch (Exception) { folderExisted = true; } // cannot tell: never claim to have created it
            string err = InstallSystem.PrepareInstallDir();
            if (err != null)
            {
                // A folder this attempt just made and then refused (a link, an unsafe ACL) is not left behind.
                if (!folderExisted) InstallSystem.TryRemoveIfEmpty(InstallPaths.InstallDir);
                return "Não foi possível instalar: " + err + ".";
            }
            preparedFolder = true;
            return null;
        }

        public string CopyExe()
        {
            if (source == null) return "o arquivo do instalador não está seguro para copiar";
            return InstallSystem.CopyExe(source);
        }

        public string CopyConfig()
        {
            return InstallSystem.WriteConfig(embeddedConfig);
        }

        public string RegisterTasks()
        {
            return InstallTasks.RegisterAll();
        }

        public string CreateShortcuts()
        {
            return StartMenu.Create();
        }

        public string WriteUninstallEntry()
        {
            return InstallSystem.WriteUninstallEntry(version);
        }

        public string StartApp()
        {
            return InstallSystem.StartApp();
        }

        public string Undo(RollbackStep step)
        {
            switch (step)
            {
                case RollbackStep.RemoveUninstallEntry: return InstallSystem.RemoveUninstallEntry();
                case RollbackStep.RemoveShortcuts: return StartMenu.Remove();
                case RollbackStep.DeleteTasks: return InstallTasks.DeleteAll();
                case RollbackStep.RemoveConfigFile: return InstallSystem.RemoveConfigFile();
                case RollbackStep.RemoveInstalledExe: return InstallSystem.RemoveCopiedExe();
                case RollbackStep.RestorePreviousExe: return InstallSystem.RestorePreviousExe();
                case RollbackStep.RemoveInstallFolder: return InstallSystem.RemoveFreshInstallFolder();
                case RollbackStep.RestartPreviousApp: return InstallSystem.StartApp();
                default: return "ação de desfazer desconhecida";
            }
        }
    }
}
