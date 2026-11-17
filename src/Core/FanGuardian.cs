using System;

namespace Forja.Core
{
    public enum SentinelStep { Wait, WriteAuto, Exit }

    public enum SentinelReason { None, AppGone, AppStalled }

    // Decision logic of the sentinel, the tiny separate process that hands the fans back to the
    // firmware when no app can do it (killed, crashed, frozen). It looks at three facts: is ANY
    // SenseMonitor instance alive (the single-instance mutex: the app, or one of the tools that write
    // to the fans), is the fan marker set (the fans may be off Auto), how old is the heartbeat.
    // It acts only when the marker is set:
    //  - no instance alive + marker: write Auto, again every 5 s until it is confirmed (never gives up);
    //  - no instance alive, no marker: leave, once the start-up grace has passed (the app that started
    //    the sentinel may not have taken the mutex a moment ago);
    //  - an instance alive + marker + heartbeat known and older than 30 s: it froze with the fans fixed,
    //    write Auto (same retry rule);
    //  - an instance alive with a fresh or unknown heartbeat: never writes, whatever the marker says.
    // Pure and single-threaded; times are milliseconds from any monotonic clock.
    public sealed class SentinelBrain
    {
        public const int StaleHeartbeatMs = 30000;
        public const int RetryMs = 5000;
        public const int StartupGraceMs = 10000;

        private readonly int startMs;
        private readonly int graceMs;
        private int attempts;
        private bool retryPending;
        private int nextTry;
        private SentinelReason reason;

        public SentinelBrain(int startMs) : this(startMs, StartupGraceMs)
        {
        }

        public SentinelBrain(int startMs, int graceMs)
        {
            this.startMs = startMs;
            this.graceMs = graceMs;
        }

        // Why the last WriteAuto was asked for.
        public SentinelReason Reason { get { return reason; } }

        // WriteAuto attempts since the last time things were quiet.
        public int Attempts { get { return attempts; } }

        // heartbeatAgeMs: null when no heartbeat is known (never published, or the sentinel could not
        // open the shared section): that is "unknown", never "frozen".
        public SentinelStep Next(bool appAlive, bool markerSet, long? heartbeatAgeMs, int nowMs)
        {
            reason = SentinelReason.None;
            if (!appAlive)
            {
                if (!markerSet)
                {
                    attempts = 0;
                    retryPending = false;
                    return unchecked(nowMs - startMs) >= graceMs ? SentinelStep.Exit : SentinelStep.Wait;
                }
                if (retryPending && unchecked(nowMs - nextTry) < 0) return SentinelStep.Wait;
                reason = SentinelReason.AppGone;
                return SentinelStep.WriteAuto;
            }

            if (!markerSet || !heartbeatAgeMs.HasValue || heartbeatAgeMs.Value < StaleHeartbeatMs)
            {
                attempts = 0;
                retryPending = false;
                return SentinelStep.Wait;
            }
            if (retryPending && unchecked(nowMs - nextTry) < 0) return SentinelStep.Wait;
            reason = SentinelReason.AppStalled;
            return SentinelStep.WriteAuto;
        }

        // The outcome of the WriteAuto that Next asked for. A failure (or a marker that stays) is
        // retried after RetryMs.
        public void Report(bool ok, int nowMs)
        {
            attempts++;
            retryPending = true;
            nextTry = unchecked(nowMs + RetryMs);
        }
    }

    // What the sentinel loop needs from the world; the real one (Platform) uses the mutex, the marker
    // file, the shared heartbeat and its own firmware, the tests use fakes.
    public interface ISentinelHost
    {
        bool AnyAppAlive();
        bool MarkerSet { get; }
        long? HeartbeatAgeMs();
        // Own firmware connection, with its own time limit. Confirmed = Auto written and read back (the
        // guarded write cleared the marker); AcceptedUnconfirmed = the firmware accepted it but the
        // read-back did not confirm (the marker stays); Failed = not written (or timed out).
        AutoResult TryWriteAuto();
        // Drops the fan marker (the "accepted but never confirmed" outcome ends the sentinel's job).
        void ClearMarker();
        void BumpIntervention();
        int NowMs();
        void Sleep(int ms);
        void Log(string text);
    }

    public enum AutoResult { Failed, Confirmed, AcceptedUnconfirmed }

    public static class SentinelLoop
    {
        // Accepted-but-unconfirmed Auto writes in a row after which the sentinel stops insisting.
        public const int MaxUnconfirmed = 3;
        public const int IdleMs = 5000;   // nothing to do: a slow look is enough (heartbeat limit is 30 s)
        public const int BusyMs = 1000;   // marker set or no app: look every second

        // Runs until the app is gone and the fans are safe. Never throws.
        public static void Run(ISentinelHost h)
        {
            Run(h, SentinelBrain.StartupGraceMs);
        }

        public static void Run(ISentinelHost h, int graceMs)
        {
            SentinelBrain brain = new SentinelBrain(h.NowMs(), graceMs);
            int unconfirmed = 0;
            while (true)
            {
                try
                {
                    bool alive = h.AnyAppAlive();
                    bool marker = h.MarkerSet;
                    long? age = h.HeartbeatAgeMs();
                    SentinelStep step = brain.Next(alive, marker, age, h.NowMs());
                    if (step == SentinelStep.Exit) break;
                    if (step == SentinelStep.Wait)
                    {
                        h.Sleep(marker || !alive ? BusyMs : IdleMs);
                        continue;
                    }
                    SentinelReason why = brain.Reason;
                    // One line for the first tries, then one a minute (a failing firmware must not flood the journal).
                    if (brain.Attempts < 2 || brain.Attempts % 12 == 0)
                    {
                        h.Log(why == SentinelReason.AppGone
                            ? "nenhum app aberto e a ventoinha fora do automático: voltando ao automático (tentativa " + (brain.Attempts + 1) + ")"
                            : "o app parou de responder (sem sinal há " + ((age ?? 0) / 1000) + " s) com a ventoinha fora do automático: voltando ao automático (tentativa " + (brain.Attempts + 1) + ")");
                    }
                    AutoResult result = h.TryWriteAuto();
                    bool accepted = result != AutoResult.Failed;
                    if (accepted && why == SentinelReason.AppStalled) h.BumpIntervention();
                    brain.Report(accepted, h.NowMs());
                    if (result == AutoResult.Confirmed)
                    {
                        unconfirmed = 0;
                        if (brain.Attempts > 1) h.Log("automático confirmado após " + brain.Attempts + " tentativas");
                    }
                    else if (result == AutoResult.AcceptedUnconfirmed)
                    {
                        // The firmware took the Auto command but the read-back does not say Auto. The app's own
                        // rule is that an accepted Auto counts, and the read-back decoding is not proven on this
                        // hardware; writing to the EC every few seconds forever (and blocking the deploy) would
                        // be worse. After a few accepted writes: say so once, drop the marker, let the loop end.
                        // The next start of the app re-checks and writes Auto again (its session is still open).
                        unconfirmed++;
                        if (unconfirmed >= MaxUnconfirmed)
                        {
                            h.Log("o firmware aceitou o automático " + unconfirmed + " vezes mas a leitura de volta não o confirma; "
                                + "considerado em automático (a leitura de volta pode não ser confiável neste hardware); marcador apagado");
                            h.ClearMarker();
                            unconfirmed = 0;
                        }
                    }
                    else
                    {
                        unconfirmed = 0;
                    }
                    h.Sleep(BusyMs);
                }
                catch (Exception)
                {
                    // never let the sentinel die: look again in a second
                    try { h.Sleep(BusyMs); }
                    catch (Exception) { return; }
                }
            }
            bool left = false;
            try { left = h.MarkerSet; }
            catch (Exception) { }
            try
            {
                h.Log(left
                    ? "guardião encerrado: automático não confirmado (marcador mantido)"
                    : "guardião encerrado: nenhum app aberto e a ventoinha está em automático");
            }
            catch (Exception) { }
        }
    }

    public sealed class RecoveryResult
    {
        public bool Needed;
        public bool Wrote;
        public bool AutoConfirmed;
        // The recovery was postponed (an earlier attempt failed and its back-off has not passed).
        public bool Skipped;
        // The first failure of a series: the shell tells the user once (never at every start).
        public bool Alert;
        public string Summary = "";
    }

    // What start-up recovery may know about this machine (from the environment profile).
    public sealed class RecoveryContext
    {
        // The fan control of THIS notebook is confirmed (a fresh profile says so).
        public bool FanControlConfirmed;
        // The fans the confirmed profile addresses (null = both).
        public FanLayout ProfileLayout;
        // MachineFingerprint of this machine (null = unknown).
        public string Fingerprint;
        // UTC ticks (tests).
        public Func<long> UtcTicks;

        public long Now()
        {
            return UtcTicks != null ? UtcTicks() : DateTime.UtcNow.Ticks;
        }

        // The pre-profile behaviour: the fan control counts as confirmed, both fans, any marker accepted.
        public static RecoveryContext Legacy()
        {
            RecoveryContext c = new RecoveryContext();
            c.FanControlConfirmed = true;
            return c;
        }
    }

    // At app start, before anything else touches the firmware: if the previous run did not end cleanly (session
    // flag still there) or left the fan marker set, make sure the fans are Auto - but only where that is safe:
    //  (a) a fan marker exists (this app wrote Custom/Máximo): Auto is written with the fan mask RECORDED in the
    //      marker (never the both-fan mask on a one-fan notebook); a marker from another machine, or one that does not
    //      say which fans were written on a model whose fan control is not confirmed, is discarded without a write;
    //  (b) no marker, but the fan control of this notebook is confirmed: the read-back decides (only the existing fans).
    // An untested or stale model with no marker is never written to. Auto is written unless the read-back is
    // VERIFIED Auto/Auto, and a marker overrides that (it says a Custom write was started). Read-only mode does not
    // change this: it is a safety hand-back. A failing write backs off across starts (2 s, 5 s, 30 s, 5 min, kept in the
    // marker) and alerts once.
    public static class StartupRecovery
    {
        public static RecoveryResult Run(GuardedFirmware fw, IFlag marker, IFlag session, IJournal journal)
        {
            return Run(fw, marker, session, journal, RecoveryContext.Legacy());
        }

        public static RecoveryResult Run(GuardedFirmware fw, IFlag marker, IFlag session, IJournal journal, RecoveryContext ctx)
        {
            RecoveryResult r = new RecoveryResult();
            if (journal == null) journal = NullJournal.Instance;
            if (ctx == null) ctx = RecoveryContext.Legacy();
            bool unclean = session != null && session.IsSet;
            bool custom = marker != null && marker.IsSet;
            if (!unclean && !custom) return r;
            r.Needed = true;
            journal.Write(JournalKind.Recovery, "a execução anterior não terminou normalmente (sessão aberta: "
                + (unclean ? "sim" : "não") + "; marcador de ventoinha manual: " + (custom ? "sim" : "não") + ")");

            IFanMarker fm = marker as IFanMarker;
            FanMarkerData md = custom && fm != null ? fm.Read() : null;

            // The shared rule (AutoHandBackRule): a marker's recorded fans, or the confirmed profile's fans, never anything else.
            HandBackDecision hb = AutoHandBackRule.Decide(custom, md, ctx.FanControlConfirmed, ctx.ProfileLayout, ctx.Fingerprint);
            if (hb.Kind == HandBackKind.Foreign)
            {
                // A marker written on another machine (a roaming profile, a copied folder) says nothing about this one.
                r.Summary = "marcador de ventoinha de outra máquina descartado, nada escrito";
                journal.Write(JournalKind.Recovery, r.Summary);
                marker.Clear();
                return r;
            }
            if (hb.Kind == HandBackKind.Unconfirmed)
            {
                r.Summary = "controle de ventoinha deste notebook ainda sem confirmação e sem marcador: nada escrito";
                journal.Write(JournalKind.Recovery, r.Summary);
                return r;
            }
            if (hb.Kind == HandBackKind.NoLayout)
            {
                // A marker that does not say which fans were written, on a model that was never confirmed: nobody
                // can tell which mask is safe, and it must not come back at every start.
                r.Summary = "marcador sem registro de quais ventoinhas foram escritas: descartado, nada escrito";
                journal.Write(JournalKind.Recovery, r.Summary);
                if (custom) marker.Clear();
                return r;
            }
            // The same model with another BIOS is the same notebook after an update: the recorded mask is still the right
            // one (the profile itself is stale by then: EnvStartup compares the full fingerprint and relocks the writes).
            if (hb.BiosDiffers) journal.Write(JournalKind.Recovery, "BIOS diferente da registrada no marcador (mesmo modelo): devolvendo o automático com as ventoinhas registradas");
            FanLayout layout = hb.Layout;

            if (custom && md != null && md.Fails > 0 && ctx.Now() < md.NextUtcTicks)
            {
                r.Skipped = true;
                r.Summary = "recuperação adiada (tentativa anterior falhou; nova tentativa mais tarde)";
                journal.Write(JournalKind.Recovery, r.Summary);
                return r;
            }

            if (fw == null || !fw.Available)
            {
                r.Summary = "firmware indisponível, nada feito";
                journal.Write(JournalKind.Recovery, r.Summary);
                if (marker != null && !marker.IsSet) Raise(marker, layout, ctx.Fingerprint); // keep the retries alive
                return r;
            }
            fw.SetLayout(layout);

            string why = "marcador de ventoinha manual presente";
            if (!custom)
            {
                FanMode? cpu;
                FanMode? gpu;
                bool read = SafeRead(fw, out cpu, out gpu);
                if (read && cpu == FanMode.Auto && gpu == FanMode.Auto)
                {
                    r.AutoConfirmed = true;
                    r.Summary = "sem escrita (a leitura confirma a ventoinha em automático)";
                    journal.Write(JournalKind.Recovery, r.Summary);
                    return r;
                }
                why = read ? "ventoinha lida fora do automático (CPU " + cpu + " / GPU " + gpu + ")"
                    : "estado da ventoinha ilegível, automático não verificado";
            }
            r.Wrote = true;
            r.AutoConfirmed = fw.SetFanAutoSafety();
            r.Summary = "automático " + (r.AutoConfirmed ? "confirmado" : "NÃO confirmado") + " (" + why + "; ventoinhas: " + FanMarkerData.LayoutText(layout) + ")";
            journal.Write(JournalKind.Recovery, r.Summary);
            if (!r.AutoConfirmed && marker != null)
            {
                if (!marker.IsSet) Raise(marker, layout, ctx.Fingerprint);
                if (fm != null)
                {
                    FanMarkerData d = fm.Read() ?? new FanMarkerData();
                    d.Fails++;
                    d.NextUtcTicks = ctx.Now() + FanMarkerData.BackoffTicks(d.Fails);
                    if (!d.Alerted)
                    {
                        d.Alerted = true;
                        r.Alert = true;
                    }
                    fm.Update(d);
                }
                else
                {
                    r.Alert = true;
                }
            }
            return r;
        }

        private static void Raise(IFlag marker, FanLayout layout, string fingerprint)
        {
            IFanMarker fm = marker as IFanMarker;
            if (fm != null) fm.Set(layout, fingerprint);
            else marker.Set();
        }

        private static bool SafeRead(GuardedFirmware fw, out FanMode? cpu, out FanMode? gpu)
        {
            cpu = null;
            gpu = null;
            try { return fw.GetFanBehavior(out cpu, out gpu); }
            catch (Exception) { return false; }
        }
    }
}
