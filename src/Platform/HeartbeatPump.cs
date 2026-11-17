using System;
using System.Threading;

namespace Forja.Platform
{
    // For the tools that write to the fans from their own process (--validar-ventoinha,
    // --teste-desempenho): publishes the heartbeat every 2 s from a timer thread so the sentinel,
    // which counts any running SenseMonitor process as "an app", never takes the tool's long wait
    // for a frozen app. The pump proves the process is alive, not that its worker is not stuck;
    // the tools have their own time limits and Auto-on-exit.
    public sealed class HeartbeatPump : IDisposable
    {
        private readonly Heartbeat heartbeat;
        private readonly Timer timer;

        public HeartbeatPump()
        {
            heartbeat = Heartbeat.Create();
            if (heartbeat == null) return;
            heartbeat.Beat();
            timer = new Timer(delegate { heartbeat.Beat(); }, null, 2000, 2000);
        }

        public void Dispose()
        {
            if (timer != null) timer.Dispose();
            if (heartbeat != null) heartbeat.Dispose();
        }
    }
}
