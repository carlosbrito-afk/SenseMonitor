using System;

namespace Forja.Core
{
    // What the environment test itself changed in the firmware, so that cancel, close, exit and crash undo
    // exactly that and nothing else. Flags are set BEFORE the first write of a step. Nothing touched = no write.
    public sealed class EnvTouch
    {
        private readonly object gate = new object();
        private bool fans;
        private FanLayout layout;
        private bool mode;
        private PerfMode? modeBefore;

        // The fan test starts: the fans may leave Automático; layout = the fans it addresses.
        public void TouchFans(FanLayout l)
        {
            lock (gate)
            {
                fans = true;
                layout = l ?? FanLayout.Both;
            }
        }

        // The mode test starts: before = the profile read just before it (null = could not be read).
        public void TouchMode(PerfMode? before)
        {
            lock (gate)
            {
                if (!mode) modeBefore = before; // the first read is the state the user had
                mode = true;
            }
        }

        // Setter kept for tests/diagnostics: a mode known from the detection that was never touched.
        public PerfMode? ModeBefore
        {
            get { lock (gate) { return modeBefore; } }
            set { lock (gate) { modeBefore = value; } }
        }

        // The fan test ended with Automático confirmed (or never wrote): nothing to hand back.
        public void ClearFans()
        {
            lock (gate)
            {
                fans = false;
                layout = null;
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                fans = false;
                layout = null;
                mode = false;
            }
        }

        public RestorePlan Plan()
        {
            lock (gate)
            {
                RestorePlan p = new RestorePlan();
                p.Fans = fans;
                p.Layout = layout ?? FanLayout.Both;
                p.Mode = mode;
                // Back to what was read before the test; Dia a dia only when that could not be read.
                p.Target = modeBefore ?? PerfMode.DiaADia;
                return p;
            }
        }
    }

    public sealed class RestorePlan
    {
        public bool Fans;
        public FanLayout Layout;
        public bool Mode;
        public PerfMode Target;

        public bool IsEmpty { get { return !Fans && !Mode; } }
    }

    public sealed class RestoreResult
    {
        public bool Wrote;
        public bool FansOk = true;
        public bool ModeOk = true;
    }

    // Applies a plan through the firmware it is given (the guarded one in the app). Fans first, then the mode;
    // each is tried twice. An empty plan never touches the firmware.
    public static class RestoreExecutor
    {
        public static RestoreResult Run(RestorePlan plan, IFirmware fw)
        {
            RestoreResult r = new RestoreResult();
            if (plan == null || plan.IsEmpty || fw == null) return r;
            if (plan.Fans)
            {
                r.Wrote = true;
                r.FansOk = Try(delegate { return fw.SetFanAuto(); }) || Try(delegate { return fw.SetFanAuto(); });
            }
            if (plan.Mode)
            {
                r.Wrote = true;
                PerfMode t = plan.Target;
                r.ModeOk = Try(delegate { return fw.SetMode(t); }) || Try(delegate { return fw.SetMode(t); });
            }
            return r;
        }

        private static bool Try(Func<bool> f)
        {
            try { return f(); }
            catch (Exception) { return false; }
        }
    }

    // A file (or any store) that says "a mode test was in progress, the profile before it was X, on this machine".
    public interface IModeMarker
    {
        bool Exists { get; }
        bool Set(PerfMode before);
        bool Set(PerfMode before, string fingerprint);
        // null = absent or unreadable.
        PerfMode? Read();
        // null = none recorded.
        string ReadFingerprint();
        bool Clear();
    }

    public sealed class ModeRecoveryResult
    {
        public bool Needed;
        public bool Restored;
    }

    // What the recovery of an interrupted mode test may trust.
    public sealed class ModeRecoveryContext
    {
        // MachineFingerprint of this machine; the marker must carry the same one.
        public string Fingerprint;
        // The profile allows this mode on this notebook (announced and not dangerous). null = nothing is allowed.
        public Func<PerfMode, bool> Allowed;
        // Diagnostic read-only mode: nothing is written, so the marker is discarded instead of kept forever.
        public bool ReadOnly;
    }

    // Next start after an interrupted mode test (tray exit, crash, power loss): put the profile back to the one read
    // before the test, then drop the marker. Without a marker nothing is read or written. The marker is trusted only if
    // it carries THIS machine's fingerprint; Turbo is never replayed and a mode the profile does not announce is not
    // replayed either (the balanced profile is restored instead, so Eco/Turbo never stay set); anything else
    // (no balanced profile announced, read-only mode, unreadable) is discarded without a write (a standard user, or a roaming profile,
    // must not be able to make the elevated app write a profile).
    public static class ModeRecovery
    {
        public static ModeRecoveryResult Run(IFirmware fw, IModeMarker marker, IJournal journal, ModeRecoveryContext ctx)
        {
            ModeRecoveryResult r = new ModeRecoveryResult();
            if (marker == null || !marker.Exists) return r;
            if (journal == null) journal = NullJournal.Instance;
            if (ctx == null) ctx = new ModeRecoveryContext();
            r.Needed = true;
            PerfMode? target = marker.Read();
            string fp = marker.ReadFingerprint();
            if (!target.HasValue)
            {
                journal.Write(JournalKind.Recovery, "marcador de teste de modos ilegível: descartado sem escrever");
                marker.Clear();
                return r;
            }
            if (string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(ctx.Fingerprint) || !MachineFingerprint.Same(fp, ctx.Fingerprint))
            {
                journal.Write(JournalKind.Recovery, "marcador de teste de modos sem a identificação desta máquina: descartado sem escrever");
                marker.Clear();
                return r;
            }
            if (target.Value == PerfMode.Turbo) target = PerfMode.DiaADia; // never replay Turbo
            if (ctx.Allowed != null && !ctx.Allowed(target.Value) && target.Value != PerfMode.DiaADia && ctx.Allowed(PerfMode.DiaADia))
            {
                // The mode the user had is not announced (first run, no profile yet): Eco/Turbo must not stay set, so the
                // balanced profile goes back instead.
                journal.Write(JournalKind.Recovery, "marcador de teste de modos com um modo que o perfil não libera: Dia a dia no lugar");
                target = PerfMode.DiaADia;
            }
            if (ctx.Allowed == null || !ctx.Allowed(target.Value))
            {
                journal.Write(JournalKind.Recovery, "marcador de teste de modos sem nenhum modo liberado pelo perfil: descartado sem escrever");
                marker.Clear();
                return r;
            }
            if (ctx.ReadOnly)
            {
                journal.Write(JournalKind.Recovery, "modo somente leitura: marcador de teste de modos descartado sem escrever");
                marker.Clear();
                return r;
            }
            if (fw == null || !fw.Available)
            {
                journal.Write(JournalKind.Recovery, "teste de modos interrompido antes: firmware indisponível, marcador mantido");
                return r;
            }
            PerfMode? now = null;
            try { now = fw.GetMode(); }
            catch (Exception) { now = null; }
            bool ok;
            if (now.HasValue && now.Value == target.Value)
            {
                ok = true;
            }
            else
            {
                try { ok = fw.SetMode(target.Value); }
                catch (Exception) { ok = false; }
            }
            r.Restored = ok;
            journal.Write(JournalKind.Recovery, "teste de modos interrompido antes: modo " + ModeCatalog.Name(target.Value) + (ok ? " restaurado" : " NÃO restaurado (marcador mantido)"));
            if (ok) marker.Clear();
            return r;
        }
    }
}
