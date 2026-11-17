using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    internal sealed class StatsSnapshot
    {
        public bool CpuReady;
        public float Cpu;
        public long RamUsed;
        public long RamTotal;
        public bool GpuReady;
        // Legacy shape for the gauges (usage, temperature); derived from Frame.Gpu.
        public GpuInfo Gpu;
        // Everything the telemetry layer reads (CPU clock/limit/cores, GPU, power plan).
        public TelemetryFrame Frame;
    }

    // Owns SystemStats and TelemetryProbe on ONE background thread (their constructors and
    // nvidia-smi block). Samples about once per second while resumed; when paused the thread
    // sleeps and the performance counters are released. Results are posted to the UI thread.
    internal sealed class StatsPoller
    {
        private const int PeriodMs = 1000;

        private readonly SynchronizationContext ui;
        private readonly Action<StatsSnapshot> onSample;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread thread;
        private volatile bool active;
        private volatile bool stopping;
        // Firmware GPU temperature for the nvidia-smi gate: a boxed double, or null = no reading
        // yet (0 or less = the GPU sleeps and nvidia-smi is not started).
        private volatile object firmwareGpuTemp;

        public void SetFirmwareGpuTemp(double? t)
        {
            firmwareGpuTemp = t.HasValue ? (object)t.Value : null;
        }

        // None while only pages that need no GPU data (Modos) are active: the GPU counters and
        // nvidia-smi are not read at all then. Counters: the driver-free counters only (the widget);
        // Full: counters plus nvidia-smi when its gate allows (the Monitor page).
        private volatile int gpuWant = (int)GpuWant.Full;

        public void SetGpuWant(GpuWant want)
        {
            gpuWant = (int)want;
        }

        public StatsPoller(Action<StatsSnapshot> onSample)
        {
            ui = SynchronizationContext.Current;
            this.onSample = onSample;
        }

        public void Resume()
        {
            if (stopping) return;
            active = true;
            if (thread == null || !thread.IsAlive) // also restarts a thread that died
            {
                thread = new Thread(Run);
                thread.IsBackground = true;
                thread.Name = "SenseMonitor.Stats";
                thread.Priority = ThreadPriority.BelowNormal;
                thread.Start();
            }
            wake.Set();
        }

        public void Pause()
        {
            active = false;
            if (thread != null) wake.Set();
        }

        // Take the next sample now (e.g. right after a RAM cleaning).
        public void Kick()
        {
            if (active && thread != null) wake.Set();
        }

        public void Stop()
        {
            stopping = true;
            active = false;
            Thread t = thread;
            if (t == null) return;
            wake.Set();
            t.Join(3500); // the GPU read may be waiting on nvidia-smi for up to 2 s
        }

        private void Run()
        {
            SystemStats stats = null;
            TelemetryProbe probe = null;
            GpuSample gpu = null;
            bool gpuSleeping = false;
            bool gpuIdle = false;
            bool gpuReady = false;
            try
            {
                while (!stopping)
                {
                    try
                    {
                        if (!active)
                        {
                            if (stats != null)
                            {
                                stats.Dispose();
                                stats = null;
                                if (probe != null) { probe.Dispose(); probe = null; }
                                PerformanceCounter.CloseSharedResources();
                            }
                            wake.WaitOne();
                            continue;
                        }
                        int t0 = Environment.TickCount;
                        StatsSnapshot s = new StatsSnapshot();
                        if (stats == null)
                        {
                            stats = new SystemStats(); // primes the CPU counter; first value is not meaningful
                            probe = new TelemetryProbe();
                            probe.Log = delegate(string msg) { Program.LogLine("Telemetria: " + msg); };
                            gpu = null;
                            gpuSleeping = false;
                            gpuIdle = false;
                            gpuReady = false;
                        }
                        else
                        {
                            s.CpuReady = true;
                            s.Cpu = stats.CpuPercent();
                        }
                        // "Em uso" like Task Manager (total - available - modified) when the
                        // memory lists can be read; otherwise total - available.
                        MemorySnapshot m = RamCleaner.Snapshot();
                        s.RamUsed = m.InUse >= 0 ? m.InUse : stats.RamUsedBytes();
                        s.RamTotal = m.Total > 0 ? m.Total : stats.RamTotalBytes();
                        // CPU telemetry now; the GPU (nvidia-smi, up to 2 s) is posted one period late
                        // so it never delays CPU/RAM.
                        TelemetryFrame frame = new TelemetryFrame();
                        frame.Cpu = probe.ReadCpu();
                        frame.Power = probe.ReadPowerPlan();
                        frame.Gpu = gpu;
                        frame.GpuSleeping = gpuSleeping;
                        frame.GpuIdle = gpuIdle;
                        s.Frame = frame;
                        s.GpuReady = gpuReady;
                        s.Gpu = ToGpuInfo(gpu);
                        if (active && !stopping && ui != null) ui.Post(Deliver, s);
                        object fwTemp = firmwareGpuTemp;
                        TelemetryFrame gf = new TelemetryFrame();
                        GpuWant gw = (GpuWant)gpuWant;
                        if (GpuWants.ReadsGpu(gw)) probe.ReadGpu(gf, fwTemp == null ? (double?)null : (double?)(double)fwTemp, gw);
                        gpu = gf.Gpu;
                        gpuSleeping = gf.GpuSleeping;
                        gpuIdle = gf.GpuIdle;
                        gpuReady = true;
                        int wait = PeriodMs - unchecked(Environment.TickCount - t0);
                        wake.WaitOne(wait < 200 ? 200 : wait);
                    }
                    catch (Exception ex)
                    {
                        // Never let the thread die: log, start over with fresh counters.
                        Program.LogError(ex);
                        if (stats != null)
                        {
                            try { stats.Dispose(); }
                            catch (Exception) { }
                            stats = null;
                        }
                        if (probe != null)
                        {
                            try { probe.Dispose(); }
                            catch (Exception) { }
                            probe = null;
                        }
                        wake.WaitOne(2000);
                    }
                }
            }
            finally
            {
                if (stats != null) stats.Dispose();
                if (probe != null) probe.Dispose();
            }
        }

        private static GpuInfo ToGpuInfo(GpuSample g)
        {
            if (g == null) return null;
            GpuInfo i = new GpuInfo();
            if (g.UsagePct.HasValue) i.UsagePct = (int)Math.Round(g.UsagePct.Value, MidpointRounding.AwayFromZero);
            i.TempC = g.TempC;
            return (i.UsagePct.HasValue || i.TempC.HasValue) ? i : null;
        }

        private void Deliver(object state)
        {
            if (active && !stopping) onSample((StatsSnapshot)state);
        }
    }
}
