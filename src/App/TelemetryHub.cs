using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // The telemetry layer for the whole app, UI thread only (except the poller it owns).
    //
    // Cost model: the StatsPoller thread (counters, nvidia-smi) runs only while at least one page
    // that shows telemetry is active (Monitor, Modos): Acquire/Release. With the window hidden no
    // thread runs; the 2 s / 12 s firmware tick that the tray already runs adds fan, temperature
    // and total-load samples (GetSystemTimes) to the history of a mode switch, nothing more.
    //
    // For a future UI: read CurrentView() (TelemetryView) and BuildModesView() (ModesView); both
    // are plain data. StatsArrived / Updated tell when to refresh.
    internal sealed class TelemetryHub
    {
        private const int StatsFreshMs = 4000; // longer than the period plus a slow nvidia-smi
        private const int BoardCheckMs = 10000;

        private readonly TrayApp app;
        private readonly StatsPoller poller;
        private readonly SynchronizationContext ui;
        // owner -> how much GPU data it wants (Monitor: Full, the widget: Counters, Modos: None)
        private readonly Dictionary<object, GpuWant> owners = new Dictionary<object, GpuWant>();
        private readonly ThrottleDetector throttle = new ThrottleDetector();
        private readonly ModeSwitchTracker tracker = new ModeSwitchTracker();
        private readonly SystemLoadReader hiddenLoad = new SystemLoadReader();
        private TelemetryFrame lastFrame;
        private int lastFrameAt;
        private PerfScoreboard board;
        private DateTime boardStamp = DateTime.MinValue;
        private int boardCheckedAt;
        private bool boardChecked;

        // The raw snapshot (UI thread), for the Monitor gauges and RAM.
        public event Action<StatsSnapshot> StatsArrived;
        // After every snapshot, once the view is rebuilt.
        public event Action Updated;

        public TelemetryHub(TrayApp app)
        {
            this.app = app;
            ui = SynchronizationContext.Current;
            poller = new StatsPoller(OnStats); // captures the UI SynchronizationContext
        }

        // ---- sampling on/off ----

        // wantGpu: the page shows GPU data (the GPU counters and nvidia-smi are read only for those).
        public void Acquire(object owner, bool wantGpu)
        {
            Acquire(owner, wantGpu ? GpuWant.Full : GpuWant.None);
        }

        // The widget asks for GpuWant.Counters: usage from the Windows counters, never an nvidia-smi process.
        public void Acquire(object owner, GpuWant want)
        {
            if (owner == null) return;
            bool first = owners.Count == 0;
            owners[owner] = want;
            UpdateWantGpu();
            if (first)
            {
                hiddenLoad.Reset(); // the first hidden reading after this would average the whole visible time
                poller.Resume();
            }
        }

        public void Release(object owner)
        {
            if (owner == null || !owners.Remove(owner)) return;
            UpdateWantGpu();
            if (owners.Count == 0)
            {
                // Deferred to after the current handler: switching straight from one page to another
                // releases then acquires in one call, and must not tear the counters down and up again.
                if (ui != null) ui.Post(delegate { PauseIfIdle(); }, null);
                else PauseIfIdle();
            }
        }

        private void PauseIfIdle()
        {
            if (owners.Count != 0) return;
            poller.Pause();
            lastFrame = null;     // stale numbers are not shown on the next open
            throttle.Reset();     // the detector is not fed while hidden: its state would be stale
            hiddenLoad.Reset();
        }

        private void UpdateWantGpu()
        {
            poller.SetGpuWant(GpuWants.Combine(owners.Values));
        }

        // Take the next sample now (e.g. right after a RAM cleaning).
        public void Kick()
        {
            if (owners.Count > 0) poller.Kick();
        }

        public void Shutdown()
        {
            owners.Clear();
            poller.Stop();
        }

        // ---- feeds ----

        private void OnStats(StatsSnapshot s)
        {
            if (s == null) return;
            try
            {
                Ingest(s);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            Action<StatsSnapshot> a = StatsArrived;
            if (a != null) a(s);
            Action u = Updated;
            if (u != null) u();
        }

        private void Ingest(StatsSnapshot s)
        {
            lastFrame = s.Frame;
            lastFrameAt = Environment.TickCount;
            SensorReading r = app.Latest ?? new SensorReading();
            CpuSample cpu = s.Frame == null ? null : s.Frame.Cpu;
            if (cpu != null) throttle.Feed(cpu, InstrumentMath.Sanitize(r.CpuTempC));
            tracker.Add(EffectSampleBuilder.Make(Environment.TickCount, r, cpu, null, throttle.State));
            tracker.Evaluate(Environment.TickCount); // freezes the verdict at the 60 s mark, UI or not
        }

        // Every firmware tick (UI thread). Tells the poller what the firmware thinks of the GPU
        // (the nvidia-smi gate) and, when no page samples, keeps the history of a mode switch:
        // fans, temperatures and the total load from GetSystemTimes; no clock, no throttle state.
        // Never throws: it runs inside the safety-critical firmware tick.
        public void OnFirmwareTick(SensorReading r)
        {
            if (r == null) return;
            try
            {
                poller.SetFirmwareGpuTemp(r.GpuTempC);
                if (owners.Count == 0)
                {
                    tracker.Add(EffectSampleBuilder.Make(Environment.TickCount, r, null, hiddenLoad.ReadPct(), ThrottleState.Unknown));
                    tracker.Evaluate(Environment.TickCount);
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // A mode switch that the firmware confirmed (TrayApp.SetMode). from = null when the mode
        // before the write could not be read: it is not invented.
        public void OnModeSwitched(PerfMode? from, PerfMode to)
        {
            try
            {
                tracker.MarkSwitch(from, to, Environment.TickCount);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // ---- the view models ----

        // True for 75 s after a mode switch: TrayApp keeps the firmware tick at 2 s (PollPolicy).
        public bool ProofActive
        {
            get { return tracker.NeedsFastTick(Environment.TickCount); }
        }

        public ModeSwitchEffect Effect
        {
            get { return tracker.Evaluate(Environment.TickCount); }
        }

        // Fresh when the poller delivered within 4 s (otherwise the CPU/GPU parts are empty).
        public TelemetryView CurrentView()
        {
            TelemetryFrame f = lastFrame;
            if (f != null && unchecked(Environment.TickCount - lastFrameAt) > StatsFreshMs) f = null;
            return TelemetryViewBuilder.Build(f, throttle.State, throttle.Evidence, Effect);
        }

        // Per-mode benchmark lines (the last --teste-desempenho) and the proof of the last switch.
        public ModesView BuildModesView()
        {
            return ModesView.Build(Board(), Effect);
        }

        // Re-read only when the file changed, checked at most every 10 s; a failed read (the file
        // being written) is never cached as "sem medição".
        private PerfScoreboard Board()
        {
            int now = Environment.TickCount;
            if (board != null && boardChecked && unchecked(now - boardCheckedAt) < BoardCheckMs) return board;
            boardChecked = true;
            boardCheckedAt = now;
            try
            {
                string path = PerfScoreboard.DefaultPath;
                DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (board == null || stamp != boardStamp)
                {
                    PerfScoreboard b;
                    if (PerfScoreboard.TryFromFile(path, null, out b))
                    {
                        board = b;
                        boardStamp = stamp;
                    }
                    else
                    {
                        boardChecked = false; // look again next time
                        if (board == null) return PerfScoreboard.Empty();
                    }
                }
            }
            catch (Exception)
            {
                boardChecked = false;
                if (board == null) return PerfScoreboard.Empty();
            }
            return board;
        }
    }
}
