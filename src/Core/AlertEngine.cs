using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // One firmware reading, as the alert engine sees it.
    public sealed class AlertSample
    {
        public long NowMs;          // monotonic clock (Environment.TickCount64-like), milliseconds
        public DateTime Local;
        public double? CpuC;
        public double? GpuC;
        public bool ThermalProtection;
        public string FanStatus;    // the fan controller's status text (for the protection alert text)
    }

    // Decides WHAT to tell the user and THROUGH WHICH channel. No clock, no UI, no I/O: time comes in
    // with each call, so the whole policy is testable.
    //
    // Two channels:
    //  - the tray icon state (IconState / IconText): follows the conditions (heat, protection,
    //    sensor lost) while they last, and one-off events (restart, fan) until acknowledged or expired.
    //    It always changes, even while a game is full screen.
    //  The same conditions drive the window's top strip (NeedsAttention / AttentionPage): the
    //  conditions are tracked whatever the user chose to be told about; the toggles only decide
    //  balloons and the tray icon.
    //  - balloons (Output.Balloons): at most one per kind per BalloonGapMinutes (one a minute for
    //    Critical). Not shown while a game is full screen, nor in the user's quiet hours or Windows
    //    focus assist (Critical alerts do show in quiet hours, never over a full-screen game). What
    //    was held back is delivered as one balloon when it is over, each item with its time.
    public sealed class AlertEngine
    {
        public const int MaxSampleGapMs = 45000;     // a longer pause between samples breaks "sustained"
        public const double HysteresisC = 3.0;       // a heat alert clears only this far under the limit
        public const int CriticalGapMs = 60000;
        public const int SensorLostMs = 60000;
        public const int BadReadingHoldMs = 10000;   // a missing / garbage reading keeps the last heat state this long
        public const int ReadingGraceMs = 5000;      // "Lendo…" shows this long from the start, then no word until known
        public const int SensorLostStripMs = 10000;  // the strip stops saying "Estável" this soon after the CPU reading goes
        public const int StickyMs = 15 * 60 * 1000;  // one-off events keep the icon this long (crash: until acknowledged)
        public const long DeferKeepMs = 12L * 60 * 60 * 1000;

        private sealed class Heat
        {
            public AlertKind Kind;
            public long? AboveSince;
            public long? BadSince;
            public long LastSample;
            public bool HasLast;
            public bool Active;
            public long Seq;
            public string Title;
        }

        private sealed class Sticky
        {
            public AlertSeverity Severity;
            public string Title;
            public string Page;
            public long Until;   // 0 = until acknowledged
            public long Seq;
        }

        private AlertSettings settings;
        private int cpuLimit;
        private int gpuLimit;
        private readonly Heat cpu = new Heat();
        private readonly Heat gpu = new Heat();
        private const string ThermalPage = "Ventoinha";
        private bool thermalActive;
        private long thermalSeq;
        private string thermalTitle;
        private long? cpuNullSince;
        private bool sensorLostActive;
        private bool sensorStripActive;
        private int unseenRestarts;
        private long unseenSeq;
        private bool haveCpuReading;
        private bool restartLogKnown;
        private long? beganMs;
        private long sensorSeq;
        private long seq;
        private readonly Dictionary<AlertKind, long> lastBalloon = new Dictionary<AlertKind, long>();
        private readonly Dictionary<AlertKind, Sticky> sticky = new Dictionary<AlertKind, Sticky>();
        private readonly Dictionary<AlertKind, Alert> deferred = new Dictionary<AlertKind, Alert>();
        private readonly Dictionary<AlertKind, long> deferredAt = new Dictionary<AlertKind, long>();

        public AlertEngine(AlertSettings settings, int cpuLimitC, int gpuLimitC)
        {
            Configure(settings, cpuLimitC, gpuLimitC);
        }

        // Settings or limits changed (the user edited them, or the CPU model became known).
        public void Configure(AlertSettings newSettings, int cpuLimitC, int gpuLimitC)
        {
            settings = (newSettings ?? new AlertSettings()).Copy().Normalize();
            cpuLimit = cpuLimitC;
            gpuLimit = gpuLimitC;
            cpu.Kind = AlertKind.CpuHot;
            gpu.Kind = AlertKind.GpuHot;
        }

        public AlertSettings Settings { get { return settings; } }

        private static void ResetHeat(Heat h)
        {
            h.AboveSince = null;
            h.BadSince = null;
            h.Active = false;
            h.HasLast = false;
        }

        // ---- continuous conditions ---------------------------------------------------------------

        public AlertOutput Tick(AlertSample s, AlertContext ctx)
        {
            AlertOutput output = new AlertOutput();
            if (ctx == null) ctx = AlertContext.Clear();
            double? c = Plausible(s.CpuC);
            double? g = Plausible(s.GpuC);
            if (!beganMs.HasValue) beganMs = s.NowMs;
            if (c.HasValue) haveCpuReading = true;

            if (UpdateHeat(cpu, c, cpuLimit, s.NowMs))
            {
                cpu.Seq = ++seq;
                Alert a = AlertText.Hot(AlertKind.CpuHot, c.Value, cpuLimit, settings.SustainSeconds, s.Local);
                cpu.Title = a.Title;
                Deliver(a, s.NowMs, ctx, output);
            }
            if (UpdateHeat(gpu, g, gpuLimit, s.NowMs))
            {
                gpu.Seq = ++seq;
                Alert a = AlertText.Hot(AlertKind.GpuHot, g.Value, gpuLimit, settings.SustainSeconds, s.Local);
                gpu.Title = a.Title;
                Deliver(a, s.NowMs, ctx, output);
            }

            if (s.ThermalProtection && !thermalActive)
            {
                thermalActive = true;
                thermalSeq = ++seq;
                Alert a = AlertText.Protection(s.FanStatus, s.Local);
                thermalTitle = a.Title;
                Deliver(a, s.NowMs, ctx, output);
            }
            else if (!s.ThermalProtection) thermalActive = false;

            if (c.HasValue)
            {
                cpuNullSince = null;
                sensorLostActive = false;
                sensorStripActive = false;
            }
            else
            {
                if (!cpuNullSince.HasValue) cpuNullSince = s.NowMs;
                long lost = s.NowMs - cpuNullSince.Value;
                if (!sensorStripActive && lost >= SensorLostStripMs)
                {
                    sensorStripActive = true;
                    sensorSeq = ++seq;
                }
                if (!sensorLostActive && lost >= SensorLostMs)
                {
                    sensorLostActive = true;
                    Deliver(AlertText.SensorLost(SensorLostMs / 1000, s.Local), s.NowMs, ctx, output);
                }
            }

            Flush(s.NowMs, s.Local, ctx, output);
            return output;
        }

        // Firmware garbage (255, 0) is not a temperature: it counts as "no reading", never as heat.
        public const double MaxPlausibleC = 125.0;

        private static double? Plausible(double? t)
        {
            double? v = InstrumentMath.Sanitize(t);
            return v.HasValue && v.Value <= MaxPlausibleC ? v : null;
        }

        // true when the heat condition just became active.
        private bool UpdateHeat(Heat h, double? temp, int limit, long now)
        {
            if (!temp.HasValue)
            {
                // A missing or garbage reading is not "cool": hold the last state for a few seconds
                // (EC readings drop out exactly when it is hot); after that the state is unknown and
                // the strip falls back to "sensor sem leitura" (CPU) through the sensor condition.
                if (!h.BadSince.HasValue) h.BadSince = now;
                if (now - h.BadSince.Value > BadReadingHoldMs)
                {
                    h.AboveSince = null;
                    h.Active = false;
                    h.HasLast = false;
                }
                return false;
            }
            h.BadSince = null;
            if (h.HasLast && now - h.LastSample > MaxSampleGapMs) h.AboveSince = null; // slept, or the app stalled
            h.LastSample = now;
            h.HasLast = true;
            double t = temp.Value;
            if (t >= limit)
            {
                if (!h.AboveSince.HasValue) h.AboveSince = now;
                if (!h.Active && now - h.AboveSince.Value >= settings.SustainSeconds * 1000L)
                {
                    h.Active = true;
                    return true;
                }
                return false;
            }
            // Between limit - 3 and the limit nothing changes (no new alert, none cleared); only a valid
            // reading clearly under the band restarts the count and clears the condition.
            if (t < limit - HysteresisC)
            {
                h.AboveSince = null;
                h.Active = false;
            }
            return false;
        }

        // ---- one-off events -----------------------------------------------------------------------

        // A crash, a fan event: raised by the caller when it sees one.
        public AlertOutput Raise(Alert a, long nowMs, DateTime local, AlertContext ctx)
        {
            AlertOutput output = new AlertOutput();
            if (ctx == null) ctx = AlertContext.Clear();
            if (a == null) return output;
            if (a.Kind != AlertKind.UnexpectedRestart)
            {
                // (a restart is not sticky: the strip follows the set of restarts not yet seen)
                Sticky st = new Sticky();
                st.Severity = a.Severity;
                st.Title = a.Title;
                st.Page = a.Page;
                st.Until = nowMs + StickyMs;
                st.Seq = ++seq;
                sticky[a.Kind] = st;
            }
            Deliver(a, nowMs, ctx, output);
            Flush(nowMs, local, ctx, output);
            return output;
        }

        // How many unexpected restarts of the last 72 h the user has not looked at yet (the caller keeps the
        // list, persisted by incident id). > 0 keeps the strip on "Requer sua atenção", pointing to Saúde.
        public void SetUnseenRestarts(int count)
        {
            if (count < 0) count = 0;
            if (count > 0 && unseenRestarts == 0) unseenSeq = ++seq;
            unseenRestarts = count;
        }

        public int UnseenRestarts { get { return unseenRestarts; } }

        // Whether the restart log could be read at the last attempt. An unreadable log adds no attention
        // but also never lets the strip say "Estável".
        public void SetRestartLogKnown(bool known, long nowMs)
        {
            if (!beganMs.HasValue) beganMs = nowMs;
            restartLogKnown = known;
        }

        // The strip's state: Attention when something is actionable; Stable only when a valid CPU
        // reading has been seen AND the restart log is readable; otherwise Reading (unknown).
        public StripState State(long nowMs)
        {
            if (NeedsAttention(nowMs)) return StripState.Attention;
            if (!haveCpuReading || !restartLogKnown) return StripState.Reading;
            return StripState.Stable;
        }

        // true while the neutral "Lendo…" word may still show (the first ReadingGraceMs).
        public bool ReadingGrace(long nowMs)
        {
            return !beganMs.HasValue || nowMs - beganMs.Value < ReadingGraceMs;
        }

        // The user looked (opened the window or clicked the balloon): one-off events stop colouring the icon.
        public void Acknowledge()
        {
            sticky.Clear();
        }

        // The user opened a page: one-off events that point to it are seen.
        public void Acknowledge(string page)
        {
            List<AlertKind> gone = new List<AlertKind>();
            foreach (KeyValuePair<AlertKind, Sticky> p in sticky)
            {
                if (p.Value.Page == page) gone.Add(p.Key);
            }
            foreach (AlertKind k in gone) sticky.Remove(k);
        }

        // ---- delivery -----------------------------------------------------------------------------

        private void Deliver(Alert a, long now, AlertContext ctx, AlertOutput output)
        {
            if (!settings.Allows(a.Kind)) return;
            int gap = settings.BalloonGapMinutes * 60 * 1000;
            if (a.Severity == AlertSeverity.Critical && gap > CriticalGapMs) gap = CriticalGapMs;
            long last;
            if (lastBalloon.TryGetValue(a.Kind, out last) && now - last < gap) return; // rate limit
            if (Held(a.Severity, a.Local, ctx))
            {
                Alert later = Clone(a);
                later.Text = AlertText.WithTime(a.Text, a.Local);
                deferred[a.Kind] = later;
                deferredAt[a.Kind] = now;
                return;
            }
            lastBalloon[a.Kind] = now;
            output.Balloons.Add(a);
        }

        // Full screen holds everything (a balloon there is hidden and can disturb the game); quiet
        // hours and focus assist hold everything except Critical.
        private bool Held(AlertSeverity sev, DateTime local, AlertContext ctx)
        {
            if (ctx.Fullscreen) return true;
            if (sev == AlertSeverity.Critical) return false;
            if (ctx.OsQuiet) return true;
            return QuietHours.IsQuiet(local, settings.QuietHoursEnabled, settings.QuietStartHour, settings.QuietEndHour);
        }

        private void Flush(long now, DateTime local, AlertContext ctx, AlertOutput output)
        {
            if (deferred.Count == 0) return;
            if (ctx.Fullscreen) return;   // nothing is shown over a full-screen game, not even critical
            bool quiet = ctx.OsQuiet || QuietHours.IsQuiet(local, settings.QuietHoursEnabled, settings.QuietStartHour, settings.QuietEndHour);
            List<Alert> due = new List<Alert>();
            List<AlertKind> taken = new List<AlertKind>();
            foreach (KeyValuePair<AlertKind, Alert> p in deferred)
            {
                bool expired = now - deferredAt[p.Key] > DeferKeepMs;
                // quiet hours and focus assist hold only the non-critical ones (the same rule as Held)
                if (quiet && !expired && p.Value.Severity != AlertSeverity.Critical) continue;
                taken.Add(p.Key);
                if (!expired) due.Add(p.Value);
            }
            foreach (AlertKind k in taken)
            {
                deferred.Remove(k);
                deferredAt.Remove(k);
            }
            if (due.Count == 0) return;
            due.Sort(delegate(Alert x, Alert y)
            {
                int c = y.Severity.CompareTo(x.Severity);
                return c != 0 ? c : x.Local.CompareTo(y.Local);
            });
            foreach (Alert a in due) lastBalloon[a.Kind] = now;
            if (due.Count == 1)
            {
                output.Balloons.Add(due[0]);
                return;
            }
            Alert summary = new Alert();
            summary.Kind = due[0].Kind;
            summary.Severity = due[0].Severity;
            summary.Title = "Avisos que ficaram em espera";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Alert a in due)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(a.Title).Append(" (").Append(a.Local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append(')');
            }
            summary.Text = AlertText.Cut(sb.ToString(), AlertText.MaxText);
            summary.Local = local;
            summary.Page = "Monitor";
            foreach (Alert a in due)
            {
                if (a.Kind == AlertKind.UnexpectedRestart) summary.Page = "Saude";
            }
            summary.Summary = true;
            output.Balloons.Add(summary);
        }

        private static Alert Clone(Alert a)
        {
            Alert c = new Alert();
            c.Kind = a.Kind;
            c.Severity = a.Severity;
            c.Title = a.Title;
            c.Text = a.Text;
            c.Local = a.Local;
            c.Page = a.Page;
            c.Summary = a.Summary;
            return c;
        }

        // ---- icon channel and strip -----------------------------------------------------------------

        private struct Reason
        {
            public AlertSeverity Severity;
            public string Title;
            public string Page;
            public long Seq;
        }

        // The most important thing that needs the user right now. forIcon: only what the user chose
        // to be told about (the strip ignores the toggles: a hot PC is not "Estável" because balloons are off).
        private bool TopReason(long now, bool forIcon, out Reason top)
        {
            top = new Reason();
            bool any = false;
            if (!forIcon || settings.Enabled)
            {
                if (cpu.Active && (!forIcon || settings.Allows(AlertKind.CpuHot)))
                    any = Consider(ref top, any, AlertSeverity.Warning, cpu.Title, "Monitor", cpu.Seq);
                if (gpu.Active && (!forIcon || settings.Allows(AlertKind.GpuHot)))
                    any = Consider(ref top, any, AlertSeverity.Warning, gpu.Title, "Monitor", gpu.Seq);
                if (thermalActive && (!forIcon || settings.Allows(AlertKind.ThermalProtection)))
                    any = Consider(ref top, any, AlertSeverity.Critical, thermalTitle, ThermalPage, thermalSeq);
                if (sensorStripActive && (!forIcon || (settings.Allows(AlertKind.SensorLost) && sensorLostActive)))
                    any = Consider(ref top, any, AlertSeverity.Warning, "Sem leitura de temperatura", "Monitor", sensorSeq);
                if (unseenRestarts > 0 && (!forIcon || settings.Allows(AlertKind.UnexpectedRestart)))
                    any = Consider(ref top, any, AlertSeverity.Warning, "Reinício inesperado ainda não visto", "Saude", unseenSeq);
                foreach (KeyValuePair<AlertKind, Sticky> p in sticky)
                {
                    Sticky st = p.Value;
                    if (st.Until != 0 && now >= st.Until) continue;
                    if (forIcon && !settings.Allows(p.Key)) continue;
                    any = Consider(ref top, any, st.Severity, st.Title, st.Page, st.Seq);
                }
            }
            return any;
        }

        private static bool Consider(ref Reason top, bool any, AlertSeverity sev, string title, string page, long seq)
        {
            if (!any || sev > top.Severity || (sev == top.Severity && seq > top.Seq))
            {
                top.Severity = sev;
                top.Title = title;
                top.Page = page;
                top.Seq = seq;
            }
            return true;
        }

        public TrayAlertState IconState(long nowMs)
        {
            Reason r;
            if (!TopReason(nowMs, true, out r)) return TrayAlertState.Normal;
            return r.Severity == AlertSeverity.Critical ? TrayAlertState.Critical : TrayAlertState.Warning;
        }

        // Tooltip for the tray icon while it is not Normal; null when it is.
        public string IconText(long nowMs)
        {
            Reason r;
            if (!TopReason(nowMs, true, out r)) return null;
            return AlertText.Cut("SenseMonitor — " + r.Title, AlertText.MaxTitle);
        }

        // The top strip: "Requer sua atenção" while something actionable exists.
        public bool NeedsAttention(long nowMs)
        {
            Reason r;
            return TopReason(nowMs, false, out r);
        }

        // Critical (thermal protection engaged): the strip's LED goes red instead of amber.
        public bool AttentionIsCritical(long nowMs)
        {
            Reason r;
            return TopReason(nowMs, false, out r) && r.Severity == AlertSeverity.Critical;
        }

        // Where clicking the strip goes: the page that explains the top reason; null when stable.
        public string AttentionPage(long nowMs)
        {
            Reason r;
            return TopReason(nowMs, false, out r) ? r.Page : null;
        }

        // What the strip's tooltip says; null when stable.
        public string AttentionReason(long nowMs)
        {
            Reason r;
            return TopReason(nowMs, false, out r) ? r.Title : null;
        }
    }
}
