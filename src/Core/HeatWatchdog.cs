using System;
using System.Globalization;
using System.Threading;

namespace Forja.Core
{
    // Independent heat guard of the performance test: it stops the load on its own, whatever
    // the test thread is doing.
    public interface IPerfGuard
    {
        // Start watching (a load is about to run). Clears an earlier trip.
        void Arm();
        // Stop watching (no load runs).
        void Disarm();
        bool Tripped { get; }
        string TripReason { get; }
    }

    // Watchdog thread with its own sensor reads. Every read runs on a dedicated reader thread
    // that the watchdog never waits for: a read that hangs (a stuck WMI call) simply stops the
    // "last good reading" clock, and when that clock is older than BlindMs the load is stopped.
    // A reading >= 90 °C (CPU or GPU) stops it at once. Stopping = calling stopLoad, which must
    // not block (set a flag).
    public sealed class HeatWatchdog : IPerfGuard, IDisposable
    {
        public const int DefaultPollMs = 500;
        public const int DefaultBlindMs = 3000;

        private readonly Func<SensorReading> read;
        private readonly Action stopLoad;
        private readonly Func<long> nowMs;
        private readonly int pollMs;
        private readonly int blindMs;
        private readonly object gate = new object();
        private readonly AutoResetEvent request = new AutoResetEvent(false);
        private Thread reader;
        private Thread monitor;
        private volatile bool disposed;
        private volatile bool readerBusy;
        private bool armed;
        private bool tripped;
        private string reason;
        private long lastGoodMs;

        // read: called only on the reader thread (create your own firmware object inside it).
        public HeatWatchdog(Func<SensorReading> read, Action stopLoad, Func<long> nowMs, int pollMs, int blindMs)
        {
            if (read == null) throw new ArgumentNullException("read");
            if (stopLoad == null) throw new ArgumentNullException("stopLoad");
            if (nowMs == null) throw new ArgumentNullException("nowMs");
            this.read = read;
            this.stopLoad = stopLoad;
            this.nowMs = nowMs;
            this.pollMs = pollMs < 10 ? 10 : pollMs;
            this.blindMs = blindMs;
        }

        public void Start()
        {
            if (reader != null) return;
            reader = new Thread(ReaderLoop);
            reader.IsBackground = true;
            reader.Name = "SenseMonitor.HeatReader";
            reader.SetApartmentState(ApartmentState.MTA);
            reader.Start();
            monitor = new Thread(MonitorLoop);
            monitor.IsBackground = true;
            monitor.Name = "SenseMonitor.HeatWatchdog";
            monitor.Start();
        }

        public void Arm()
        {
            lock (gate)
            {
                armed = true;
                tripped = false;
                reason = null;
                lastGoodMs = nowMs();
            }
            try { request.Set(); }
            catch (ObjectDisposedException) { }
        }

        public void Disarm()
        {
            lock (gate) { armed = false; }
        }

        public bool Tripped
        {
            get { lock (gate) { return tripped; } }
        }

        public string TripReason
        {
            get { lock (gate) { return reason; } }
        }

        private void Trip(string why)
        {
            lock (gate)
            {
                if (!armed || tripped) return;
                tripped = true;
                reason = why;
            }
            try { stopLoad(); }
            catch (Exception) { }
        }

        private void ReaderLoop()
        {
            while (!disposed)
            {
                try { request.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                if (disposed) return;
                readerBusy = true;
                try
                {
                    SensorReading r = read();
                    Evaluate(r);
                }
                catch (Exception)
                {
                    // a failed read counts as no reading
                }
                finally
                {
                    readerBusy = false;
                }
            }
        }

        private void Evaluate(SensorReading r)
        {
            if (r == null || !r.CpuTempC.HasValue) return;
            double cpu = r.CpuTempC.Value;
            if (double.IsNaN(cpu) || double.IsInfinity(cpu) || cpu <= 0) return;
            lock (gate) { lastGoodMs = nowMs(); }
            double hot = cpu;
            if (r.GpuTempC.HasValue && !double.IsNaN(r.GpuTempC.Value) && r.GpuTempC.Value > hot) hot = r.GpuTempC.Value;
            if (hot >= PerfPlan.AbortTempC)
            {
                Trip("vigia: temperatura chegou a " + ((int)hot).ToString(CultureInfo.InvariantCulture) + " °C (limite 90 °C)");
            }
        }

        private void MonitorLoop()
        {
            while (!disposed)
            {
                Thread.Sleep(pollMs);
                bool isArmed;
                long since;
                lock (gate)
                {
                    isArmed = armed && !tripped;
                    since = nowMs() - lastGoodMs;
                }
                if (!isArmed) continue;
                if (!readerBusy)
                {
                    try { request.Set(); }
                    catch (ObjectDisposedException) { return; }
                }
                if (since >= blindMs)
                {
                    Trip("vigia: sem leitura de temperatura por " + (blindMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s");
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { request.Set(); }
            catch (ObjectDisposedException) { }
        }
    }
}
