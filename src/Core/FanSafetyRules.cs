using System;

namespace Forja.Core
{
    // Did the fans follow a Custom write? The reference is the settled RPM at Máximo measured by
    // the hardware validation (Config.FanMaxRpmCpu/Gpu); the expected RPM for a percentage is
    // pct% of it, with a generous band because the real curve is not linear.
    public static class RpmGuard
    {
        public const double BandLow = 0.75;
        public const double BandHigh = 1.25;
        public const double MinChange = 0.10;

        public static bool InBand(int? rpm, int pct, int maxRpm)
        {
            if (!rpm.HasValue || maxRpm <= 0) return false;
            double expected = pct * maxRpm / 100.0;
            return rpm.Value >= expected * BandLow && rpm.Value <= expected * BandHigh;
        }

        // True when the fan is in the target band, or moved more than 10 % from the RPM it had
        // at the write, in the direction of the band. A missing reading is not a response.
        public static bool Responded(int? refRpm, int? nowRpm, int pct, int maxRpm)
        {
            if (!nowRpm.HasValue || maxRpm <= 0) return false;
            if (InBand(nowRpm, pct, maxRpm)) return true;
            if (!refRpm.HasValue) return false;
            double expected = pct * maxRpm / 100.0;
            int dir = Math.Sign(expected - refRpm.Value);
            int moved = nowRpm.Value - refRpm.Value;
            return dir != 0 && Math.Sign(moved) == dir && Math.Abs(moved) > MinChange * Math.Max(refRpm.Value, 1);
        }
    }

    // Firmware polling period: 12 s only when nothing but the firmware drives the fans (Automático,
    // Auto confirmed, no Máximo) and the window is hidden; 2 s otherwise.
    public static class PollPolicy
    {
        public const int FastMs = 2000;
        public const int SlowMs = 12000;

        public static int FirmwareIntervalMs(FanUserMode mode, bool maxActive, bool windowVisible, bool autoConfirmed)
        {
            if (mode == FanUserMode.Auto && !maxActive && !windowVisible && autoConfirmed) return SlowMs;
            return FastMs;
        }
    }

    // Startup: the performance mode goes back to "Dia a dia" (spec), confirmed by read-back.
    public static class StartupMode
    {
        // Returns the mode the firmware is in afterwards (null if unreadable). Never throws;
        // a failure is reported through log (one line, no personal data).
        public static PerfMode? Apply(IFirmware fw, Action<string> log)
        {
            bool ok = false;
            try { ok = fw != null && fw.SetMode(PerfMode.DiaADia); }
            catch (Exception) { ok = false; }
            if (ok) return PerfMode.DiaADia;
            PerfMode? now = null;
            try { now = fw == null ? null : fw.GetMode(); }
            catch (Exception) { now = null; }
            if (log != null)
            {
                try
                {
                    log("Modo: não foi possível confirmar Dia a dia ao iniciar (lido: "
                        + (now.HasValue ? now.Value.ToString() : "desconhecido") + ").");
                }
                catch (Exception) { }
            }
            return now;
        }
    }

    // Decision logic of the fan watchdog thread. The UI thread calls Beat after every firmware
    // tick; the watchdog thread calls Check about once a second. If no tick completed for 10 s
    // while the user drives the fans (Manual/Curva/Máximo), Check returns true once per stall:
    // the thread then writes Auto through the isolated path and reports the result. Thread-safe.
    public sealed class FanWatchdog
    {
        public const int TimeoutMs = 10000;
        public const int RetryMs = 5000;

        private readonly object sync = new object();
        private bool started;
        private int lastBeat;
        private bool risky;
        private bool paused;
        private bool fired;
        private bool retryPending;
        private int nextTry;
        private bool pendingNotify;

        public void Beat(int nowMs, bool userDrivesFans)
        {
            lock (sync)
            {
                started = true;
                lastBeat = nowMs;
                risky = userDrivesFans;
                paused = false;
                fired = false;
                retryPending = false;
            }
        }

        // Suspend: the stopped ticks are not a stall. The next Beat re-arms it.
        public void Pause()
        {
            lock (sync) { paused = true; }
        }

        public bool Check(int nowMs)
        {
            lock (sync)
            {
                if (!started || paused || fired || !risky) return false;
                if (unchecked(nowMs - lastBeat) < TimeoutMs) return false;
                if (retryPending && unchecked(nowMs - nextTry) < 0) return false;
                fired = true;
                return true;
            }
        }

        // ok: the isolated Auto write was confirmed (the UI must tell the controller); a failure
        // is retried after 5 s while the stall lasts.
        public void ReportResult(bool ok, int nowMs)
        {
            lock (sync)
            {
                if (ok)
                {
                    pendingNotify = true;
                    return;
                }
                fired = false;
                retryPending = true;
                nextTry = unchecked(nowMs + RetryMs);
            }
        }

        public bool TakePendingNotify()
        {
            lock (sync)
            {
                bool p = pendingNotify;
                pendingNotify = false;
                return p;
            }
        }
    }
}
