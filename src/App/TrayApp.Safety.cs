using System;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Wave 1 safety wiring: the guarded firmware (journal, read-only mode, fan marker), start-up
    // recovery, the heartbeat for the fan sentinel and the journal of state changes. Kept out of
    // TrayApp.cs so the main file only has the few lines that call into it.
    public sealed partial class TrayApp
    {
        private bool readOnly;
        private bool readOnlyFromSwitch;
        private Heartbeat heartbeat;
        private int lastInterventions;
        private string lastStateLine;
        private bool exitJournaled;

        // Read-only mode (Config.DiagnosticReadOnly or --somente-leitura): decided once at start.
        public bool ReadOnly { get { return readOnly; } }

        public bool ReadOnlyFromSwitch { get { return readOnlyFromSwitch; } }

        // Why Manual/Curva/Máximo are not offered right now.
        public string FanLockedNote
        {
            get
            {
                if (readOnly) return SafetyText.ReadOnlyReason;
                if (config.EnvStale) return EnvCheck.StaleNote;
                if (config.Env == null) return EnvCheck.NeverNote;
                if (config.Env.State(Cap.FanControl) == CapState.Unavailable) return FirstSentence(config.Env.Reason(Cap.FanControl)) ?? FanValidation.LockedNote;
                return FanValidation.LockedNote;
            }
        }

        // What the recovery of an interrupted mode test may trust: this machine's fingerprint and the modes the fresh
        // profile announces (Dia a dia only, when there is no profile yet).
        internal static ModeRecoveryContext ModeRecoveryContextFor(Config cfg, bool readOnly)
        {
            ModeRecoveryContext c = new ModeRecoveryContext();
            c.Fingerprint = Safety.Fingerprint;
            c.ReadOnly = readOnly;
            c.Allowed = delegate(PerfMode m)
            {
                if (cfg == null || cfg.EnvStale || cfg.Env == null) return m == PerfMode.DiaADia;
                CapState s = cfg.Env.State(ModeCatalog.CapOf(m));
                return s == CapState.Untested || s == CapState.Confirmed;
            };
            return c;
        }

        // The isolated Auto writer, but only when there is something to hand back: the controller made the fans leave
        // Auto (or this notebook's fan control is confirmed), or the marker says an earlier run did. A notebook whose fan
        // control was never confirmed never gets an Auto write from the exit, suspend or crash paths.
        private bool IsolatedAuto()
        {
            if (!fan.MayNeedHandBack && !Safety.Marker.IsSet) return true;
            return FanSafety.SetAutoIsolated(IsolatedTimeoutMs);
        }

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf('.');
            return i > 0 ? s.Substring(0, i + 1) : s;
        }

        // First thing the constructor does with the firmware: decide read-only mode, journal the
        // start, put the fans back to Auto if the previous run did not end cleanly, then mark this
        // run as open. Returns the firmware every other part of the app writes through.
        private GuardedFirmware InitSafety(Config cfg)
        {
            string[] args = Environment.GetCommandLineArgs();
            readOnlyFromSwitch = Safety.ReadOnlyRequested(args, null);
            readOnly = readOnlyFromSwitch || cfg.DiagnosticReadOnly;
            Safety.ReadOnly = readOnly;
            Safety.EnsureSentinel = Sentinel.EnsureRunning;
            IJournal journal = Safety.Journal;
            journal.Write(JournalKind.Start, "app iniciado; somente leitura: " + (readOnly ? "sim" : "não")
                + "; controle manual liberado: " + (cfg.ManualFanAllowed ? "sim" : "não"));
            if (cfg.LoadNote != null) journal.Write(JournalKind.Config, cfg.LoadNote);

            GuardedFirmware guarded = Safety.Wrap(new WmiFirmware());
            // Before anything else touches the firmware.
            try
            {
                // Auto is written only where the fan control of THIS notebook is confirmed, or a marker says the app left
                // the fans off Auto (then with the fans the marker records); never to an untested or stale model.
                RecoveryContext rc = new RecoveryContext();
                rc.FanControlConfirmed = !cfg.EnvStale && cfg.Env != null && cfg.Env.Allows(Cap.FanControl);
                rc.ProfileLayout = cfg.EnvFanLayout;
                rc.Fingerprint = Safety.Fingerprint;
                RecoveryResult rr = StartupRecovery.Run(guarded, Safety.Marker, Safety.Session, journal, rc);
                if (rr.Alert) recoveryAlertPending = true;
            }
            catch (Exception ex) { Program.LogError(ex); }
            // A mode test of the environment wizard that was cut short (exit, crash, power loss): the profile read
            // before it comes back. Without the marker file nothing is read or written.
            try { ModeRecovery.Run(guarded, Safety.ModeMarker, journal, ModeRecoveryContextFor(cfg, readOnly)); }
            catch (Exception ex) { Program.LogError(ex); }
            Safety.Session.Set();

            heartbeat = Heartbeat.Create();
            if (heartbeat != null)
            {
                lastInterventions = heartbeat.Interventions;
                heartbeat.Beat(); // a section kept by the sentinel from the previous app still holds an old time
            }
            // The sentinel is needed only when this run may write Custom/Máximo; EnsureSentinel also
            // starts it just before the first such write if it is not up yet.
            if (!readOnly && cfg.ManualFanAllowed && !Sentinel.IsRunning())
            {
                // Off the UI thread: schtasks and the wait for the sentinel can take a few seconds.
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { Sentinel.EnsureRunning(); });
            }
            return guarded;
        }

        // The start-up performance mode: read first, written only when it is not Dia a dia.
        // Read-only mode writes nothing.
        private PerfMode? ApplyStartupMode()
        {
            if (!readOnly && !config.ModeAllowed(PerfMode.DiaADia))
            {
                // The environment test has not confirmed the profile of this notebook (never run, another BIOS,
                // postponed): the mode is read, never written.
                PerfMode? m = SafeGetMode();
                Safety.Journal.Write(JournalKind.Action, "teste de ambiente pendente: modo não alterado ao iniciar (lido: "
                    + (m.HasValue ? GuardedFirmware.ModeName(m.Value) : "desconhecido") + ")");
                return m;
            }
            if (readOnly)
            {
                PerfMode? m = SafeGetMode();
                Safety.Journal.Write(JournalKind.Action, "somente leitura: modo não alterado ao iniciar (lido: "
                    + (m.HasValue ? GuardedFirmware.ModeName(m.Value) : "desconhecido") + ")");
                return m;
            }
            return StartupMode.Apply(fw, Program.LogLine, Safety.Journal);
        }

        // Every firmware tick (UI thread): heartbeat for the sentinel, a sentinel intervention is
        // taken as a watchdog hand-back, and a changed mode / fan state gets one journal line.
        private void SafetyTick()
        {
            if (heartbeat != null)
            {
                heartbeat.Beat();
                int iv = heartbeat.Interventions;
                if (iv != lastInterventions)
                {
                    lastInterventions = iv;
                    fan.NotifyWatchdogAuto();
                    Safety.Journal.Write(JournalKind.State, "o guardião devolveu a ventoinha ao automático (o app ficou sem responder)");
                    Program.LogLine("Ventoinhas: o guardião devolveu o automático porque o app ficou sem responder.");
                }
            }
            string state = "modo=" + ModeLabel + "; ventoinha=" + fan.Status
                + "; automático confirmado=" + (fan.FirmwareAutoConfirmed ? "sim" : "não");
            if (state != lastStateLine)
            {
                lastStateLine = state;
                Safety.Journal.Write(JournalKind.State, state);
            }
        }

        // End of a run: one line, and the open-session flag goes only when the fans are known to be
        // Auto (otherwise the next start recovers them). Idempotent.
        private void SafetyExit(string how)
        {
            if (exitJournaled) return;
            exitJournaled = true;
            Safety.Journal.Write(JournalKind.Exit, how + (fansHandedBack
                ? "; ventoinha em automático confirmado"
                : "; ventoinha NÃO confirmada em automático"));
            if (fansHandedBack) Safety.Session.Clear();
            if (heartbeat != null)
            {
                heartbeat.Dispose();
                heartbeat = null;
            }
        }
    }
}
