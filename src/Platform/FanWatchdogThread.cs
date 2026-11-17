using System;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // Background thread around FanWatchdog: once a second it asks whether the firmware ticks
    // stalled for 10 s while the user drives the fans; if so it writes Auto through the isolated
    // path (FanSafety: own MTA thread, own WmiFirmware) and reports the result. The UI thread
    // tells the controller on its next tick (FanWatchdog.TakePendingNotify). No action while
    // the fans are Automático.
    public sealed class FanWatchdogThread : IDisposable
    {
        private const int CheckEveryMs = 1000;

        private readonly FanWatchdog watchdog;
        private readonly int isolatedTimeoutMs;
        private readonly Action<string> log;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Thread thread;

        public FanWatchdogThread(FanWatchdog watchdog, int isolatedTimeoutMs, Action<string> log)
        {
            if (watchdog == null) throw new ArgumentNullException("watchdog");
            this.watchdog = watchdog;
            this.isolatedTimeoutMs = isolatedTimeoutMs;
            this.log = log;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "SenseMonitor.FanWatchdog";
            thread.Start();
        }

        private void Loop()
        {
            while (!stop.WaitOne(CheckEveryMs))
            {
                try
                {
                    if (!watchdog.Check(Environment.TickCount)) continue;
                    bool ok = FanSafety.SetAutoIsolated(isolatedTimeoutMs);
                    watchdog.ReportResult(ok, Environment.TickCount);
                    Log(ok
                        ? "Vigia das ventoinhas: sem leitura do firmware por 10 s; automático aplicado."
                        : "Vigia das ventoinhas: sem leitura por 10 s e o automático não foi confirmado; tentando de novo.");
                }
                catch (Exception)
                {
                    // never let the watchdog die
                }
            }
        }

        private void Log(string line)
        {
            if (log == null) return;
            try { log(line); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            stop.Set();
            thread.Join(isolatedTimeoutMs + 1000);
        }
    }
}
