using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Forja.Core;

namespace Forja.Platform
{
    // The on-demand task that runs the installed exe with --desinstalar (Settings > Desinstalar). The tray app runs inside the
    // job object of its own task, and the uninstaller closes that app as its first step: started from the app as a child it
    // would be a member of the same job, so tearing the job down could cut the uninstall in half. Run through its own task it
    // is outside that job, like the update helper (UpdateTask). Registered with the other tasks at install; no trigger,
    // elevated like them, hidden, no time limit (a limit that killed a half-done uninstall would be worse than none). It is
    // NOT deleted by the uninstaller process itself (it may be running in it): the detached cmd that removes the install
    // folder deletes it last.
    public static class UninstallTask
    {
        public const string TaskName = "SenseMonitor-Desinstalar";
        public const string Switch = "--desinstalar";

        public static string BuildXml(string exePath, string userId)
        {
            string user = SecurityElement.Escape(userId);
            string cmd = SecurityElement.Escape("\"" + exePath + "\"");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>true</Hidden>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\"><Exec><Command>" + cmd + "</Command><Arguments>" + Switch + "</Arguments></Exec></Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        // Does the task (schtasks /Query /XML output) run exactly this exe with --desinstalar?
        public static bool IsCurrent(string taskXml, string exePath)
        {
            if (!Autostart.TaskPointsTo(taskXml, exePath)) return false;
            Match m = Regex.Match(taskXml, @"<Arguments>\s*(.*?)\s*</Arguments>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success && string.Equals(m.Groups[1].Value.Trim(), Switch, StringComparison.OrdinalIgnoreCase);
        }

        private static string Schtasks { get { return InstallPaths.System32("schtasks.exe"); } }

        // null = registered (or refreshed).
        public static string Register()
        {
            string exe = InstallPaths.InstalledExe;
            if (!File.Exists(exe)) return Autostart.NotInstalledText;
            string dir = InstallPaths.SecureTempDir();
            if (dir == null) return Autostart.NotInstalledText;
            string tmp = null;
            try
            {
                tmp = Autostart.XmlPathIn(dir);
                File.WriteAllText(tmp, BuildXml(exe, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
                int code;
                bool ran = ToolRunner.Run(Schtasks, "/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F", 15000, out code);
                return ran && code == 0 ? null : "Não foi possível registrar a tarefa de desinstalação.";
            }
            catch (Exception)
            {
                return "Não foi possível registrar a tarefa de desinstalação.";
            }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
            }
        }

        // Registers it when it is missing or does not point at the installed exe (installs made before this task existed).
        public static string EnsureCurrent()
        {
            string xml;
            int code;
            if (ToolRunner.RunCapture(Schtasks, "/Query /TN " + TaskName + " /XML", 15000, out code, out xml) && code == 0 && IsCurrent(xml, InstallPaths.InstalledExe)) return null;
            return Register();
        }

        // true = Task Scheduler accepted the run (it says nothing about whether the process started: the caller checks).
        public static bool Run()
        {
            int code;
            return ToolRunner.Run(Schtasks, "/Run /TN " + TaskName, 15000, out code) && code == 0;
        }
    }
}
