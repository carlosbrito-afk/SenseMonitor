using System;
using System.Management;
using Forja.Core;

namespace Forja.Platform
{
    // Win32_Service through WMI (System.Management is already referenced; no new assembly). Needs
    // administrator, like the rest of the app. Only the five Acer service names of ConflictServices
    // are ever passed in; the names are looked up by exact match and quoted for WQL.
    public sealed class WmiServiceControl : IServiceControl
    {
        private const int WaitStopMs = 8000;

        public ServiceInfo Query(string name)
        {
            try
            {
                using (ManagementObject mo = Find(name))
                {
                    if (mo == null) return null;
                    ServiceInfo i = new ServiceInfo();
                    i.Name = Convert.ToString(mo["Name"]);
                    i.DisplayName = Convert.ToString(mo["DisplayName"]);
                    i.State = Convert.ToString(mo["State"]);
                    i.StartMode = Convert.ToString(mo["StartMode"]);
                    i.Delayed = ReadDelayed(mo);
                    i.PathName = ExecutablePath(Convert.ToString(mo["PathName"]));
                    FillFileInfo(i);
                    return i;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool Stop(string name)
        {
            try
            {
                using (ManagementObject mo = Find(name))
                {
                    if (mo == null) return false;
                    if (!string.Equals(Convert.ToString(mo["State"]), "Running", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Convert.ToString(mo["State"]), "Start Pending", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    // 0 = success, 5 = already stopped
                    uint rc = Convert.ToUInt32(mo.InvokeMethod("StopService", null));
                    if (rc != 0 && rc != 5) return false;
                }
                // The stop is asynchronous: wait until it reports Stopped.
                for (int waited = 0; waited < WaitStopMs; waited += 250)
                {
                    ServiceInfo i = Query(name);
                    if (i == null || string.Equals(i.State, "Stopped", StringComparison.OrdinalIgnoreCase)) return true;
                    System.Threading.Thread.Sleep(250);
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Start(string name)
        {
            try
            {
                using (ManagementObject mo = Find(name))
                {
                    if (mo == null) return false;
                    // 0 = success, 10 = already running
                    uint rc = Convert.ToUInt32(mo.InvokeMethod("StartService", null));
                    return rc == 0 || rc == 10;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetStartMode(string name, string mode)
        {
            if (mode != "Automatic" && mode != "AutomaticDelayed" && mode != "Manual" && mode != "Disabled") return false;
            if (mode == "AutomaticDelayed") return SetDelayedAuto(name);
            try
            {
                using (ManagementObject mo = Find(name))
                {
                    if (mo == null) return false;
                    ManagementBaseObject p = mo.GetMethodParameters("ChangeStartMode");
                    p["StartMode"] = mode;
                    ManagementBaseObject o = mo.InvokeMethod("ChangeStartMode", p, null);
                    return o != null && Convert.ToUInt32(o["ReturnValue"]) == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Win32_Service.DelayedAutoStart (Windows 8 and later); missing = not delayed.
        private static bool ReadDelayed(ManagementObject mo)
        {
            try
            {
                object v = mo["DelayedAutoStart"];
                return v != null && Convert.ToBoolean(v);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // "Automatic (delayed)" is set through sc.exe (WMI ChangeStartMode has no such value). The names come from a
        // fixed list; anything but letters, digits and underscores is refused before it reaches a command line.
        private static bool SetDelayedAuto(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    System.IO.Path.Combine(Environment.SystemDirectory, "sc.exe"), "config " + name + " start= delayed-auto");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(10000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // PathName is the command line: "C:\x y\a.exe" args, or C:\x\a.exe args.
        private static string ExecutablePath(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return null;
            string s = commandLine.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                return end > 1 ? s.Substring(1, end - 1) : null;
            }
            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? s.Substring(0, exe + 4) : s;
        }

        private static void FillFileInfo(ServiceInfo i)
        {
            try
            {
                if (string.IsNullOrEmpty(i.PathName) || !System.IO.File.Exists(i.PathName)) return;
                System.Diagnostics.FileVersionInfo v = System.Diagnostics.FileVersionInfo.GetVersionInfo(i.PathName);
                i.Company = v.CompanyName;
                i.Product = v.ProductName;
            }
            catch (Exception)
            {
            }
        }

        // Exact name only; anything with a quote or a backslash is refused (the names come from a fixed list).
        private static ManagementObject Find(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\'') >= 0 || name.IndexOf('\\') >= 0) return null;
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name = '" + name + "'"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    return mo;
                }
            }
            return null;
        }
    }
}
