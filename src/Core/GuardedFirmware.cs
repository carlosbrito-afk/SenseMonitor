using System;
using System.Threading;

namespace Forja.Core
{
    // What WmiFirmware offers beyond IFirmware (the "Máximo" option, the read-back of the fan
    // behavior). Every method catches internally and never throws.
    public interface IFirmwareExt : IFirmware
    {
        bool SetFanMax();
        bool? IsFanMax();
        bool GetFanBehavior(out FanMode? cpu, out FanMode? gpu);
    }

    // The read-only questions of the environment test: what the firmware announces about itself. Never writes.
    // Every method catches internally and returns null when the call fails.
    public interface IFirmwareProbe
    {
        // GetGamingSysInfo(0): raw answer (sensor bitmap in bits 24-39).
        ulong? ReadSensorBitmapRaw();
        // GetGamingMiscSetting(0x0A): raw answer (profile bitmap in byte 1).
        ulong? ReadProfileBitmapRaw();
        // GetGamingMiscSetting(0x0B): the current profile code, whatever it is.
        byte? ReadProfileCode();
    }

    // A firmware whose commands address a given set of fans (WmiFirmware). Changeable after the environment test.
    public interface IFirmwareLayout
    {
        FanLayout Layout { get; set; }
    }

    // A tiny persistent boolean (a file that exists or not). Set/Clear return false when the
    // state could not be changed; IsSet reads the real state every time.
    public interface IFlag
    {
        bool IsSet { get; }
        bool Set();
        bool Clear();
    }

    // Every firmware WRITE of the app goes through here:
    //  - journal: a line BEFORE and a line AFTER each write (with the read-back result), so a
    //    process killed right after the write still leaves the evidence;
    //  - read-only mode: no write reaches the firmware (modes, speeds, Máximo); the call is journaled
    //    as blocked. Auto is reported as done: nothing was changed, so there is nothing to hand back;
    //  - fan marker (ventoinha-custom.flag): set BEFORE the first Custom/Máximo write and cleared
    //    only after Auto was written and read back, so the sentinel and the next start know the
    //    fans may be off Auto. If the marker cannot be written the Custom write is refused.
    // The decorated firmware is used from one thread, like the WmiFirmware it wraps.
    public sealed class GuardedFirmware : IFirmwareExt, IFirmwareProbe
    {
        // Identical failed writes inside this window are summarized instead of repeated.
        public const int RepeatWindowMs = 30000;

        private readonly IFirmware inner;
        private readonly IFirmwareExt ext;
        private readonly IJournal journal;
        private readonly IFlag marker;
        private readonly bool readOnly;
        private readonly Func<bool> beforeCustom;
        private readonly Func<int> clock;

        // Custom/Máximo writes in flight and a counter that moves at the start and the end of each, shared
        // by every GuardedFirmware of the process (the UI thread's and the isolated emergency writer's):
        // an Auto write only clears the fan marker when no Custom write overlapped it, so a stuck
        // Custom write that lands after an Auto can never leave the fans off Auto without the marker.
        private static int customInFlight;
        private static int customSeq;

        private string lastFailText;
        private int lastFailAt;
        private int suppressed;

        // beforeCustom: runs after the marker is set and before a Custom/Máximo write (starts the
        // sentinel); returning false refuses the write. May be null. clock: milliseconds (tests).
        public GuardedFirmware(IFirmware inner, IJournal journal, IFlag marker, bool readOnly,
            Func<bool> beforeCustom, Func<int> clock)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
            this.ext = inner as IFirmwareExt;
            this.journal = journal ?? NullJournal.Instance;
            this.marker = marker;
            this.readOnly = readOnly;
            this.beforeCustom = beforeCustom;
            this.clock = clock ?? delegate { return Environment.TickCount; };
        }

        public bool ReadOnly { get { return readOnly; } }

        // MachineFingerprint written into the fan marker (so a marker that roams to another notebook is never replayed).
        public string Fingerprint;

        // The fans the marker must cover whatever a single write addresses (the wizard's manual test writes one fan at a time but may
        // leave others off Automático): the notebook's own fans. null = the layout of the firmware at the time of the write.
        public FanLayout HandBackLayout;

        // Raises the fan marker recording which fans the next write addresses (the layout of the firmware) and which machine.
        // The marker is a union: an existing one is widened, never narrowed, while any fan may be off Automático.
        private bool RaiseMarker()
        {
            IFanMarker fm = marker as IFanMarker;
            if (fm == null) return marker.Set();
            IFirmwareLayout l = inner as IFirmwareLayout;
            FanLayout now = l == null ? FanLayout.Both : (l.Layout ?? FanLayout.Both);
            string text = FanMarkerData.Union(FanMarkerData.LayoutText(now), HandBackLayout == null ? null : FanMarkerData.LayoutText(HandBackLayout));
            FanMarkerData u = new FanMarkerData();
            u.Layout = text;
            return fm.Set(u.GetLayout() ?? now, Fingerprint);
        }

        // Which fans the firmware commands address from now on (no-op for a firmware that has no layout).
        public void SetLayout(FanLayout layout)
        {
            IFirmwareLayout l = inner as IFirmwareLayout;
            if (l != null) l.Layout = layout ?? FanLayout.Both;
        }

        // ---- reads (never journaled) -------------------------------------------------------

        public bool Available { get { return inner.Available; } }

        public SensorReading ReadSensors()
        {
            return inner.ReadSensors();
        }

        public PerfMode? GetMode()
        {
            return inner.GetMode();
        }

        public bool GetFanBehavior(out FanMode? cpu, out FanMode? gpu)
        {
            cpu = null;
            gpu = null;
            if (ext == null) return false;
            return ext.GetFanBehavior(out cpu, out gpu);
        }

        public bool? IsFanMax()
        {
            return ext == null ? null : ext.IsFanMax();
        }

        // The probe is read-only: no journal line, no marker, allowed in read-only mode.
        public ulong? ReadSensorBitmapRaw()
        {
            IFirmwareProbe p = inner as IFirmwareProbe;
            return p == null ? null : p.ReadSensorBitmapRaw();
        }

        public ulong? ReadProfileBitmapRaw()
        {
            IFirmwareProbe p = inner as IFirmwareProbe;
            return p == null ? null : p.ReadProfileBitmapRaw();
        }

        public byte? ReadProfileCode()
        {
            IFirmwareProbe p = inner as IFirmwareProbe;
            return p == null ? null : p.ReadProfileCode();
        }

        // ---- writes ------------------------------------------------------------------------

        public bool SetMode(PerfMode m)
        {
            string what = "modo -> " + ModeName(m);
            if (readOnly) return Block(what);
            bool quiet = Begin(what);
            bool ok;
            try { ok = inner.SetMode(m); }
            catch (Exception) { ok = false; }
            string readBack = null;
            if (!ok)
            {
                PerfMode? now = SafeMode();
                readBack = "modo lido: " + (now.HasValue ? ModeName(now.Value) : "desconhecido");
            }
            End(what, ok, readBack, quiet);
            return ok;
        }

        public bool SetFanAuto()
        {
            if (readOnly)
            {
                // A marker left by an earlier run means the fans may still be off Auto: that hand-back is a
                // real write (the one exception, journaled) and is retried by every caller until confirmed.
                if (marker != null && marker.IsSet)
                {
                    return WriteAuto("ventoinha -> automático (marcador de ventoinha manual presente, somente leitura ligado)");
                }
                // Otherwise nothing was ever written by this run, so the firmware is as it was.
                journal.Write(JournalKind.Blocked, "ventoinha -> automático: somente leitura, nada a devolver");
                return true;
            }
            return WriteAuto("ventoinha -> automático");
        }

        // Auto even in read-only mode: the recovery of a fan left off Auto by an earlier run, and
        // the sentinel. The only write read-only mode lets through, and always journaled.
        public bool SetFanAutoSafety()
        {
            return WriteAuto(readOnly ? "ventoinha -> automático (recuperação de segurança, somente leitura ligado)"
                : "ventoinha -> automático (segurança)");
        }

        public bool SetFanPercent(int cpuPct, int gpuPct)
        {
            string what = "ventoinha -> manual CPU " + cpuPct + "% / GPU " + gpuPct + "%";
            if (readOnly) return Block(what);
            return RunCustom(what, delegate { return inner.SetFanPercent(cpuPct, gpuPct); });
        }

        public bool SetFanMax()
        {
            string what = "ventoinha -> máximo";
            if (readOnly) return Block(what);
            if (ext == null) return false;
            return RunCustom(what, delegate { return ext.SetFanMax(); });
        }

        // ---- internals ---------------------------------------------------------------------

        // A Custom/Máximo write: in flight for its whole duration; the marker is set before it and
        // re-asserted after it (an Auto that finished in between must not leave it cleared).
        private bool RunCustom(string what, Func<bool> write)
        {
            Interlocked.Increment(ref customInFlight);
            Interlocked.Increment(ref customSeq);
            try
            {
                if (!PrepareCustom(what)) return false;
                bool quiet = Begin(what);
                bool ok;
                try { ok = write(); }
                catch (Exception) { ok = false; }
                if (marker != null && !marker.IsSet) RaiseMarker(); // keeps the recorded fans and machine, like the first raise
                End(what, ok, null, quiet);
                return ok;
            }
            finally
            {
                Interlocked.Increment(ref customSeq);
                Interlocked.Decrement(ref customInFlight);
            }
        }

        private bool WriteAuto(string what)
        {
            int seq0 = Thread.VolatileRead(ref customSeq);
            bool quiet = Begin(what);
            bool ok;
            try { ok = inner.SetFanAuto(); }
            catch (Exception) { ok = false; }
            string readBack = null;
            bool autoSeen = false;
            bool readable = false;
            if (ok && ext != null)
            {
                FanMode? cpu;
                FanMode? gpu;
                readable = SafeBehavior(out cpu, out gpu);
                if (readable)
                {
                    autoSeen = cpu == FanMode.Auto && gpu == FanMode.Auto;
                    readBack = "lido: CPU " + cpu + " / GPU " + gpu;
                }
                else
                {
                    readBack = "leitura de volta indisponível";
                }
            }
            // The marker goes away only when Auto is accepted and the read-back agrees (or cannot be
            // read: Auto acceptance is then the same proof the rest of the app relies on).
            bool overlapped = Thread.VolatileRead(ref customInFlight) > 0 || Thread.VolatileRead(ref customSeq) != seq0;
            bool clear = ok && !overlapped && (autoSeen || ext == null || !readable);
            string markerNote = null;
            if (clear && marker != null)
            {
                bool wasSet = marker.IsSet;
                if (wasSet && !marker.Clear()) markerNote = "marcador não pôde ser apagado";
            }
            else if (ok && marker != null && marker.IsSet)
            {
                markerNote = overlapped
                    ? "marcador mantido: houve escrita manual ao mesmo tempo"
                    : "marcador mantido: o firmware não confirmou automático";
            }
            End(what, ok, Join(readBack, markerNote), quiet);
            return ok;
        }

        // The marker first, then the sentinel; either failing refuses the write (fail closed).
        private bool PrepareCustom(string what)
        {
            // a fan marker is re-raised on every Custom write: an existing one is widened to cover this write too
            if (marker != null && (marker is IFanMarker || !marker.IsSet) && !RaiseMarker())
            {
                journal.Write(JournalKind.Blocked, what + ": recusado, o marcador de ventoinha manual não pôde ser gravado");
                return false;
            }
            if (beforeCustom != null)
            {
                bool ok;
                try { ok = beforeCustom(); }
                catch (Exception) { ok = false; }
                if (!ok)
                {
                    journal.Write(JournalKind.Blocked, what + ": recusado, o guardião da ventoinha não pôde ser iniciado");
                    return false;
                }
            }
            return true;
        }

        private bool Block(string what)
        {
            journal.Write(JournalKind.Blocked, what + ": somente leitura, nada foi enviado ao firmware");
            return false;
        }

        // Journals the line before the write, unless this exact write has been failing: then it
        // is only counted. Returns true when the "before" line was skipped.
        private bool Begin(string what)
        {
            if (lastFailText == what && unchecked(clock() - lastFailAt) < RepeatWindowMs) return true;
            journal.Write(JournalKind.WriteBefore, what);
            return false;
        }

        private void End(string what, bool ok, string detail, bool quiet)
        {
            if (!ok)
            {
                if (lastFailText == what && unchecked(clock() - lastFailAt) < RepeatWindowMs)
                {
                    suppressed++;
                    return;
                }
                FlushSuppressed();
                lastFailText = what;
                lastFailAt = clock();
                journal.Write(JournalKind.WriteAfter, what + ": falhou" + Suffix(detail));
                return;
            }
            FlushSuppressed();
            lastFailText = null;
            journal.Write(JournalKind.WriteAfter, what + ": ok" + Suffix(detail) + (quiet ? " (antes sem registro: repetição)" : ""));
        }

        private void FlushSuppressed()
        {
            if (suppressed > 0 && lastFailText != null)
            {
                journal.Write(JournalKind.WriteAfter, lastFailText + ": falhou mais " + suppressed + " vezes seguidas");
            }
            suppressed = 0;
        }

        private PerfMode? SafeMode()
        {
            try { return inner.GetMode(); }
            catch (Exception) { return null; }
        }

        private bool SafeBehavior(out FanMode? cpu, out FanMode? gpu)
        {
            cpu = null;
            gpu = null;
            try { return ext.GetFanBehavior(out cpu, out gpu); }
            catch (Exception) { return false; }
        }

        private static string Suffix(string detail)
        {
            return string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")";
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "; " + b;
        }

        public static string ModeName(PerfMode m)
        {
            return ModeCatalog.Name(m);
        }
    }
}
