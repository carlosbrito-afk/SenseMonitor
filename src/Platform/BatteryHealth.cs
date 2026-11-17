using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // Battery wear from "powercfg /batteryreport /xml". Works without administrator rights, takes
    // about half a second, and the temp file is always deleted. Read-only for the system.
    public static class BatteryHealth
    {
        public const int TimeoutMs = 15000;

        // Null when there is no battery, powercfg fails or times out. Never throws.
        public static BatteryInfo Read()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sm-battery-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                if (!RunPowercfg(tmp)) return null;
                if (!File.Exists(tmp)) return null;
                string xml = File.ReadAllText(tmp, Encoding.UTF8);
                return BatteryParse.Parse(xml);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
            }
        }

        private static bool RunPowercfg(string outputPath)
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(sys, "powercfg.exe"));
            psi.Arguments = "/batteryreport /xml /output \"" + outputPath + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                // Drain the pipes so a chatty child cannot block on a full buffer.
                p.OutputDataReceived += delegate { };
                p.ErrorDataReceived += delegate { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(TimeoutMs))
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                    return false;
                }
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }
    }
}
