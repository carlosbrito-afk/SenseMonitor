using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Forja.Platform
{
    // Logon autostart through a scheduled task with highest privileges (the app needs elevation).
    // The task always runs the installed copy (%ProgramFiles%\SenseMonitor\SenseMonitor.exe),
    // never a user-writable folder; schtasks is called by its full System32 path and the task
    // XML is written to the admin-only install folder, not %TEMP%.
    public static class Autostart
    {
        public const string TaskName = "SenseMonitor";
        public const string NotInstalledText = "Instale o app em Arquivos de Programas (deploy.ps1) para iniciar com o Windows.";

        // Arguments for creating the task from an XML definition file.
        public static string BuildCreateArgs(string xmlPath)
        {
            return "/Create /TN " + TaskName + " /XML \"" + xmlPath + "\" /F";
        }

        // Task definition. A plain /Create /SC ONLOGON would get laptop defaults (start only
        // on AC, stop on battery, 72 h limit), so the settings are spelled out here.
        public static string BuildTaskXml(string exePath, string userId)
        {
            string user = SecurityElement.Escape(userId);
            string cmd = SecurityElement.Escape("\"" + exePath + "\"");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>");
            sb.AppendLine("  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\"><Exec><Command>" + cmd + "</Command></Exec></Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        public static string BuildDeleteArgs()
        {
            return "/Delete /TN " + TaskName + " /F";
        }

        public static string BuildQueryArgs()
        {
            return "/Query /TN " + TaskName + " /XML";
        }

        // Does a task definition (schtasks /Query /XML output) run exactly this exe?
        public static bool TaskPointsTo(string taskXml, string exePath)
        {
            if (string.IsNullOrEmpty(taskXml) || string.IsNullOrEmpty(exePath)) return false;
            Match m = Regex.Match(taskXml, @"<Command>\s*(.*?)\s*</Command>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return false;
            string cmd = m.Groups[1].Value.Replace("&quot;", "\"").Replace("&amp;", "&").Trim().Trim('"').Trim();
            return string.Equals(cmd, exePath, StringComparison.OrdinalIgnoreCase);
        }

        // A unique file name inside the protected folder.
        public static string XmlPathIn(string secureDir)
        {
            return Path.Combine(secureDir, "task-" + Guid.NewGuid().ToString("N") + ".xml");
        }

        // Points the task at the installed copy. Returns null on success, else a pt-BR reason.
        public static string Enable()
        {
            string exe = InstallPaths.InstalledExe;
            if (!File.Exists(exe)) return NotInstalledText;
            string dir = InstallPaths.SecureTempDir();
            if (dir == null) return NotInstalledText;
            string tmp = null;
            try
            {
                tmp = XmlPathIn(dir);
                string xml = BuildTaskXml(exe, WindowsIdentity.GetCurrent().Name);
                File.WriteAllText(tmp, xml, Encoding.Unicode); // UTF-16 with BOM, as Task Scheduler expects
                string output;
                return Run(BuildCreateArgs(tmp), out output) ? null : "Não foi possível alterar a inicialização automática.";
            }
            catch (Exception)
            {
                return "Não foi possível alterar a inicialização automática.";
            }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
            }
        }

        public static bool Disable()
        {
            string output;
            return Run(BuildDeleteArgs(), out output);
        }

        // On only when the task exists AND runs the installed copy (an old task that points at
        // a development folder counts as off, so turning it on again fixes it).
        public static bool IsEnabled()
        {
            string output;
            return Run(BuildQueryArgs(), out output) && TaskPointsTo(output, InstallPaths.InstalledExe);
        }

        // Read-only: the task definition (schtasks /Query /XML); false when the task is missing.
        public static bool QueryTaskXml(out string xml)
        {
            return Run(BuildQueryArgs(), out xml);
        }

        // Same query with the schtasks exit code: 0 found, 1 not found, -1 could not run / timed out.
        public static bool QueryTaskXml(out string xml, out int exitCode)
        {
            return Run(BuildQueryArgs(), out xml, out exitCode);
        }

        private static bool Run(string args, out string output)
        {
            int code;
            return Run(args, out output, out code);
        }

        private static bool Run(string args, out string output, out int exitCode)
        {
            output = null;
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(InstallPaths.System32("schtasks.exe"), args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string err = null;
                    string text = null;
                    System.Threading.Thread t = new System.Threading.Thread(delegate()
                    {
                        try { err = p.StandardError.ReadToEnd(); }
                        catch (Exception) { }
                    });
                    t.IsBackground = true;
                    t.Start();
                    // stdout is read on its own thread too, so the 15 s limit holds if the process hangs.
                    System.Threading.Thread o = new System.Threading.Thread(delegate()
                    {
                        try { text = p.StandardOutput.ReadToEnd(); }
                        catch (Exception) { }
                    });
                    o.IsBackground = true;
                    o.Start();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); }
                        catch (Exception) { }
                        return false;
                    }
                    o.Join(2000);
                    t.Join(2000);
                    output = text;
                    GC.KeepAlive(err);
                    exitCode = p.ExitCode;
                    return p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
