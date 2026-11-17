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
        public GpuInfo Gpu;
    }

    // Owns SystemStats on ONE background thread (its constructor and Gpu() block).
    // Samples about once per second while resumed; when paused the thread sleeps and the
    // performance counter is released. Results are posted to the UI thread.
    internal sealed class StatsPoller
    {
        private const int PeriodMs = 1000;

        private readonly SynchronizationContext ui;
        private readonly Action<StatsSnapshot> onSample;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread thread;
        private volatile bool active;
        private volatile bool stopping;

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
            t.Join(2500); // Gpu() may be waiting on nvidia-smi for up to 1.5 s
        }

        private void Run()
        {
            SystemStats stats = null;
            GpuInfo gpu = null;
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
                            gpu = null;
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
                        // GPU (nvidia-smi, up to 1.5 s) is posted one period late so it never
                        // delays CPU/RAM.
                        s.GpuReady = gpuReady;
                        s.Gpu = gpu;
                        if (active && !stopping && ui != null) ui.Post(Deliver, s);
                        gpu = stats.Gpu();
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
                        wake.WaitOne(2000);
                    }
                }
            }
            finally
            {
                if (stats != null) stats.Dispose();
            }
        }

        private void Deliver(object state)
        {
            if (active && !stopping) onSample((StatsSnapshot)state);
        }
    }
}
