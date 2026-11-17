using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    public sealed class InstalledProcess
    {
        public int Pid;
        public string CommandLine;
        public AppProcessKind Kind;
    }

    // The running instances of the INSTALLED exe, told apart by command line (Win32_Process): the app and
    // the tools are "App", the fan guard is "Sentinel" (same image, --sentinela). When WMI cannot answer,
    // every process whose image is the installed exe is listed as "App" (the cautious reading).
    public static class ProcessScan
    {
        public static List<InstalledProcess> Installed()
        {
            List<InstalledProcess> list = new List<InstalledProcess>();
            string installed = InstallPaths.InstalledExe;
            try
            {
                ObjectQuery q = new ObjectQuery("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '" + InstallPaths.ExeName + "'");
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), q))
                {
                    s.Options.Timeout = TimeSpan.FromSeconds(10);
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            string path = o["ExecutablePath"] as string;
                            if (!UpdatePolicy.IsInstalledCopy(path, installed)) continue;
                            InstalledProcess p = new InstalledProcess();
                            p.Pid = Convert.ToInt32(o["ProcessId"]);
                            p.CommandLine = o["CommandLine"] as string;
                            p.Kind = ProcessKinds.Classify(p.CommandLine);
                            list.Add(p);
                        }
                    }
                }
                return list;
            }
            catch (Exception)
            {
                return Fallback(installed);
            }
        }

        private static List<InstalledProcess> Fallback(string installed)
        {
            List<InstalledProcess> list = new List<InstalledProcess>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception) { return list; }
            foreach (Process p in all)
            {
                try
                {
                    if (!p.ProcessName.StartsWith("SenseMonitor", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!UpdatePolicy.IsInstalledCopy(p.MainModule.FileName, installed)) continue;
                    InstalledProcess ip = new InstalledProcess();
                    ip.Pid = p.Id;
                    ip.Kind = AppProcessKind.App;
                    list.Add(ip);
                }
                catch (Exception)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
            return list;
        }

        // Instances of the app or its tools (never the sentinel, never the excluded pid).
        public static List<int> AppIds(int excludePid)
        {
            List<int> ids = new List<int>();
            foreach (InstalledProcess p in Installed())
            {
                if (p.Pid != excludePid && p.Kind == AppProcessKind.App) ids.Add(p.Pid);
            }
            return ids;
        }

        // The tray app specifically (ProcessKinds.IsTray): not a CLI tool, not the sentinel.
        public static List<int> TrayIds(int excludePid)
        {
            List<int> ids = new List<int>();
            foreach (InstalledProcess p in Installed())
            {
                if (p.Pid == excludePid || p.Kind != AppProcessKind.App) continue;
                // An unreadable command line (WMI fallback) counts as a possible tray: the cautious reading is
                // "do not start another", which the single-instance mutex would turn away anyway.
                if (p.CommandLine == null || ProcessKinds.IsTray(p.CommandLine)) ids.Add(p.Pid);
            }
            return ids;
        }

        public static int SentinelCount()
        {
            int n = 0;
            foreach (InstalledProcess p in Installed()) if (p.Kind == AppProcessKind.Sentinel) n++;
            return n;
        }
    }

    // The helper holds this for as long as it runs: an exclusive lock file in the admin-only data folder
    // (nobody without write access there can squat it) and, as the original design asked, a named mutex
    // created with an administrators-only security descriptor. Only the file lock decides: a named
    // mutex in Global\ can be created first by any process, which would otherwise be a way to stop updates
    // for good; that case is logged and ignored.
    public sealed class HelperLock : IDisposable
    {
        public const string MutexName = @"Global\SenseMonitor.UpdateHelper";
        private FileStream file;
        private Mutex mutex;
        private bool mutexOwned;
        public string Note;

        private HelperLock()
        {
        }

        public static HelperLock TryAcquire()
        {
            string reason;
            string dir = UpdateStore.SecureDataDir(true, out reason);
            if (dir == null) return null;
            return TryAcquire(dir, MutexName);
        }

        // dir = an already verified folder (tests pass a temp one and their own mutex name).
        internal static HelperLock TryAcquire(string dir, string mutexName)
        {
            HelperLock l = new HelperLock();
            try
            {
                l.file = new FileStream(Path.Combine(dir, UpdateStore.LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (Exception)
            {
                return null; // held by another helper (or unusable)
            }
            try
            {
                MutexSecurity sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                l.mutex = new Mutex(true, mutexName, out created, sec);
                if (created) l.mutexOwned = true;
                else
                {
                    try { l.mutexOwned = l.mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { l.mutexOwned = true; }
                    if (!l.mutexOwned) l.Note = "o mutex " + mutexName + " já existia e está ocupado (criado por outro processo); o bloqueio de arquivo é o que vale";
                }
            }
            catch (Exception ex)
            {
                l.Note = "o mutex " + mutexName + " não pôde ser criado (" + ex.GetType().Name + "); o bloqueio de arquivo é o que vale";
            }
            return l;
        }

        // Is a helper running right now? (Opening the lock file exclusively fails while it is held.)
        public static bool IsHeld()
        {
            string reason;
            string dir = UpdateStore.SecureDataDir(false, out reason);
            if (dir == null) return false;
            return IsHeld(dir);
        }

        internal static bool IsHeld(string dir)
        {
            string p = Path.Combine(dir, UpdateStore.LockName);
            if (!File.Exists(p)) return false;
            try
            {
                using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1)) { }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool WaitNotHeld(int timeoutMs)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (IsHeld())
            {
                if (w.ElapsedMilliseconds >= timeoutMs) return false;
                Thread.Sleep(500);
            }
            return true;
        }

        public void Dispose()
        {
            if (mutex != null)
            {
                if (mutexOwned)
                {
                    try { mutex.ReleaseMutex(); }
                    catch (Exception) { }
                }
                mutex.Close();
                mutex = null;
            }
            if (file != null)
            {
                string p = file.Name;
                file.Dispose();
                file = null;
                try { File.Delete(p); }
                catch (Exception) { }
            }
        }
    }

    // The on-demand task that runs the update helper (SenseMonitor.aplicador.exe --aplicar-atualizacao)
    // outside the old app's job object, elevated, whatever the autostart setting is. Registered with the
    // other tasks at install; the app re-registers it before an apply when it is missing or stale.
    public static class UpdateTask
    {
        public const string TaskName = "SenseMonitor-Atualizar";
        public const string Switch = "--aplicar-atualizacao";

        // No trigger (run only on demand), Parallel (a still-"running" earlier instance, which owns the
        // app it started, must never make a later /Run a no-op), no time limit (a limit that killed the job
        // would kill the app started inside it), same battery-safe settings and elevation as the other tasks.
        public static string BuildXml(string exePath, string userId)
        {
            string user = SecurityElement.Escape(userId);
            string cmd = SecurityElement.Escape("\"" + exePath + "\"");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>");
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

        public static string BuildCreateArgs(string xmlPath)
        {
            return "/Create /TN " + TaskName + " /XML \"" + xmlPath + "\" /F";
        }

        // Does the task (schtasks /Query /XML output) run exactly this exe with --aplicar-atualizacao?
        public static bool IsCurrent(string taskXml, string exePath)
        {
            if (!Autostart.TaskPointsTo(taskXml, exePath)) return false;
            Match m = Regex.Match(taskXml, @"<Arguments>\s*(.*?)\s*</Arguments>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success && string.Equals(m.Groups[1].Value.Trim(), Switch, StringComparison.OrdinalIgnoreCase);
        }

        private static string Schtasks { get { return InstallPaths.System32("schtasks.exe"); } }

        // null = registered (or refreshed). The task points at the fixed helper path inside the staging
        // folder; the file itself is put there only at apply time.
        public static string Register()
        {
            string dir = InstallPaths.SecureTempDir();
            if (dir == null) return Autostart.NotInstalledText;
            string tmp = null;
            try
            {
                tmp = Autostart.XmlPathIn(dir);
                File.WriteAllText(tmp, BuildXml(UpdateStore.HelperExe, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
                int code;
                bool ran = ToolRunner.Run(Schtasks, BuildCreateArgs(tmp), 15000, out code);
                return ran && code == 0 ? null : "Não foi possível registrar a tarefa de atualização.";
            }
            catch (Exception)
            {
                return "Não foi possível registrar a tarefa de atualização.";
            }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
            }
        }

        // Registers it when it is missing or does not point at the helper path. null = ready to run.
        public static string EnsureCurrent()
        {
            string xml;
            int code;
            if (ToolRunner.RunCapture(Schtasks, "/Query /TN " + TaskName + " /XML", 15000, out code, out xml) && code == 0 && IsCurrent(xml, UpdateStore.HelperExe)) return null;
            return Register();
        }

        public static bool Run()
        {
            int code;
            return ToolRunner.Run(Schtasks, "/Run /TN " + TaskName, 15000, out code) && code == 0;
        }
    }
}
