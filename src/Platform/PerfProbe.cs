using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // Per-second readings of the performance test that do not come from the firmware, plus the
    // one-off facts for its preconditions. Everything is read-only and never throws.
    public sealed class PerfProbe : IPerfProbe, IDisposable
    {
        private readonly bool onBattery;
        private PerformanceCounter clock;
        private PerformanceCounter load;
        private double maxMHz;

        public PerfProbe(bool onBattery)
        {
            this.onBattery = onBattery;
            try
            {
                maxMHz = MaxClockMHz();
                if (maxMHz > 0)
                {
                    clock = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    clock.NextValue(); // the first read of a rate counter is always 0
                }
            }
            catch (Exception)
            {
                if (clock != null) { clock.Dispose(); clock = null; }
            }
        }

        // Effective clock = "% Processor Performance" (share of the maximum, can pass 100 with
        // turbo) x Win32_Processor.MaxClockSpeed. null when the counter does not exist.
        public double? ClockMHz()
        {
            try
            {
                if (clock == null) return null;
                float pct = clock.NextValue();
                if (pct <= 0 || float.IsNaN(pct) || float.IsInfinity(pct)) return null;
                return pct / 100.0 * maxMHz;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // root\WMI BatteryStatus.DischargeRate in mW; only asked while on battery.
        public double? DischargeMw()
        {
            if (!onBattery) return null;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\WMI", "SELECT DischargeRate FROM BatteryStatus"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        using (o)
                        {
                            if (o["DischargeRate"] == null) continue;
                            double v = Convert.ToDouble(o["DischargeRate"]);
                            if (v > 0) return v;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // Total CPU use of the machine (percent), 1 s apart; the first call primes the counter.
        public double? CpuLoadPct()
        {
            try
            {
                if (load == null)
                {
                    load = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    load.NextValue();
                    return null;
                }
                float v = load.NextValue();
                if (v < 0 || float.IsNaN(v) || float.IsInfinity(v)) return null;
                return v > 100 ? 100 : v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // AC / battery and charge now.
        public PerfPower Power()
        {
            return ReadPower();
        }

        public static PerfPower ReadPower()
        {
            PerfPower p = new PerfPower();
            try
            {
                System.Windows.Forms.PowerStatus s = System.Windows.Forms.SystemInformation.PowerStatus;
                System.Windows.Forms.PowerLineStatus line = s.PowerLineStatus;
                if (line == System.Windows.Forms.PowerLineStatus.Offline) p.OnBattery = true;
                else if (line == System.Windows.Forms.PowerLineStatus.Online) p.OnBattery = false;
                float pct = s.BatteryLifePercent;
                if (pct >= 0f && pct <= 1f) p.Percent = (int)Math.Round(pct * 100.0);
            }
            catch (Exception)
            {
            }
            return p;
        }

        // Average total CPU use over `seconds` (1 s apart); null when the counter does not exist
        // or the wait was cancelled. Run it before the test starts.
        public static double? MeasureBackgroundCpu(int seconds, Func<bool> cancelled)
        {
            try
            {
                using (PerformanceCounter c = new PerformanceCounter("Processor", "% Processor Time", "_Total", true))
                {
                    c.NextValue();
                    double sum = 0;
                    int n = 0;
                    for (int i = 0; i < seconds; i++)
                    {
                        System.Threading.Thread.Sleep(1000);
                        if (cancelled != null && cancelled()) return null;
                        float v = c.NextValue();
                        if (v >= 0 && !float.IsNaN(v) && !float.IsInfinity(v)) { sum += v; n++; }
                    }
                    return n == 0 ? (double?)null : sum / n;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (clock != null) { clock.Dispose(); clock = null; }
            if (load != null) { load.Dispose(); load = null; }
        }

        private static double MaxClockMHz()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        using (o)
                        {
                            if (o["MaxClockSpeed"] != null) return Convert.ToDouble(o["MaxClockSpeed"]);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return 0;
        }

        // Fills CPU name and core counts.
        public static void ReadCpu(PerfPreconditions pre)
        {
            pre.LogicalCores = Environment.ProcessorCount;
            try
            {
                int physical = 0;
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        using (o)
                        {
                            if (pre.CpuName == null && o["Name"] != null) pre.CpuName = o["Name"].ToString().Trim();
                            if (o["NumberOfCores"] != null) physical += Convert.ToInt32(o["NumberOfCores"]);
                        }
                    }
                }
                pre.PhysicalCores = physical;
            }
            catch (Exception)
            {
            }
        }

        // powercfg /getactivescheme (System32 path, 10 s limit). Leaves the fields null on failure.
        public static void ReadPowerScheme(PerfPreconditions pre)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(InstallPaths.System32("powercfg.exe"), "/getactivescheme");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = RepairRunner.OemEncoding();
                using (Process p = Process.Start(psi))
                {
                    // Read on another thread so the 10 s limit holds even if the process hangs.
                    string output = null;
                    System.Threading.Thread reader = new System.Threading.Thread(delegate()
                    {
                        try { output = p.StandardOutput.ReadToEnd(); }
                        catch (Exception) { }
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    if (!p.WaitForExit(10000))
                    {
                        try { p.Kill(); }
                        catch (Exception) { }
                        return;
                    }
                    reader.Join(2000);
                    string guid, name;
                    if (PerfPowerScheme.Parse(output, out guid, out name))
                    {
                        pre.PowerSchemeGuid = guid;
                        pre.PowerSchemeName = name;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
