using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Forja.Platform
{
    public class GpuInfo
    {
        public int? UsagePct;
        public double? TempC;
    }

    // Polled by the UI only while the window is visible; owns no threads or timers. GPU readings
    // (nvidia-smi and the GPU Engine counters) moved to TelemetryProbe, which gates them.
    public class SystemStats : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private PerformanceCounter cpu;

        public SystemStats()
        {
            cpu = TryCounter("Processor Information", "% Processor Utility")
                ?? TryCounter("Processor", "% Processor Time");
        }

        private static PerformanceCounter TryCounter(string category, string counter)
        {
            PerformanceCounter pc = null;
            try
            {
                pc = new PerformanceCounter(category, counter, "_Total", true);
                pc.NextValue(); // the first sample is always 0; prime it
                return pc;
            }
            catch (Exception)
            {
                if (pc != null)
                {
                    pc.Dispose();
                }
                return null;
            }
        }

        public float CpuPercent()
        {
            if (cpu == null)
            {
                return 0f;
            }
            try
            {
                float v = cpu.NextValue();
                if (float.IsNaN(v) || v < 0f)
                {
                    return 0f;
                }
                return v > 100f ? 100f : v; // Processor Utility can exceed 100 on turbo
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        public long RamTotalBytes()
        {
            MEMORYSTATUSEX m;
            return Query(out m) ? (long)m.ullTotalPhys : 0L;
        }

        public long RamUsedBytes()
        {
            MEMORYSTATUSEX m;
            return Query(out m) ? (long)(m.ullTotalPhys - m.ullAvailPhys) : 0L;
        }

        private static bool Query(out MEMORYSTATUSEX m)
        {
            m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            return GlobalMemoryStatusEx(ref m);
        }

        internal static long TotalPhysBytes()
        {
            MEMORYSTATUSEX m;
            return Query(out m) && m.ullTotalPhys > 0 ? (long)m.ullTotalPhys : -1L;
        }

        internal static long AvailablePhysBytes()
        {
            MEMORYSTATUSEX m;
            return Query(out m) ? (long)m.ullAvailPhys : -1L;
        }

        // Parses "37, 54" (utilization %, temperature C). Null when nothing usable.
        public static GpuInfo ParseNvidiaSmi(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }
            string[] parts = line.Trim().Split(',');
            if (parts.Length != 2)
            {
                return null;
            }
            GpuInfo g = new GpuInfo();
            int usage;
            if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out usage))
            {
                g.UsagePct = usage;
            }
            double temp;
            if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out temp))
            {
                g.TempC = temp;
            }
            return (g.UsagePct.HasValue || g.TempC.HasValue) ? g : null;
        }

        public void Dispose()
        {
            if (cpu != null)
            {
                cpu.Dispose();
                cpu = null;
            }
        }
    }
}
