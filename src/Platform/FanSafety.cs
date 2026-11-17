using System;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // Emergency "fans to Auto" that works while the UI (STA) thread is inside a sent message
    // (logoff, suspend), where COM refuses its outgoing calls. Runs SetFanAuto on a dedicated
    // MTA thread with its OWN WmiFirmware instance (never the UI thread's, whose COM objects
    // would marshal back to the blocked STA and deadlock) and waits with a timeout.
    public static class FanSafety
    {
        public static bool SetAutoIsolated(int timeoutMs)
        {
            bool skipped;
            return SetAutoIsolated(timeoutMs, out skipped);
        }

        // Gated by the shared rule (Safety.HandBackNow / AutoHandBackRule): Auto is written only when a fan marker exists (on the
        // fans it recorded) or the saved profile confirms the fan control of this notebook (the fans it has). Otherwise
        // nothing is written, skipped is true and the result is true (there was nothing to hand back).
        public static bool SetAutoIsolated(int timeoutMs, out bool skipped)
        {
            string reason;
            return SetAutoIsolated(timeoutMs, out skipped, out reason);
        }

        // The decision (marker, machine identity, saved profile) is capped at this, and never takes more than a third of the
        // caller's total: a cold WMI identity read must not eat the time the write needs (m20), and the whole hand-back
        // (decision + write) never takes longer than the total the caller gave (exit, crash and watchdog stay within 3 s, m4).
        public const int DecisionBudgetMs = 1000;

        // total = decision + write, exactly. The write keeps at least two thirds.
        internal static void SplitBudget(int totalMs, out int decisionMs, out int writeMs)
        {
            int total = Math.Max(2, totalMs);
            decisionMs = Math.Max(1, Math.Min(DecisionBudgetMs, total / 3));
            writeMs = total - decisionMs;
        }

        // reason: null when Auto was handed back (or there was nothing to hand back); otherwise what failed, in words that say
        // WHICH part failed (the decision timed out, the write timed out, the firmware refused).
        public static bool SetAutoIsolated(int timeoutMs, out bool skipped, out string reason)
        {
            int decisionBudget, writeWindow;
            SplitBudget(timeoutMs, out decisionBudget, out writeWindow);
            bool ok = RunAutoIsolated(
                Safety.HandBackNow,
                delegate(HandBackDecision hb)
                {
                    // Journaled and tracked by the fan marker like every other write (Safety.Wrap).
                    GuardedFirmware fw = Safety.Wrap(new WmiFirmware());
                    fw.SetLayout(hb.Layout);
                    if (!fw.Available) return "sem interface de firmware (o app precisa estar como administrador)";
                    return fw.SetFanAuto() ? null : "o firmware não confirmou o Automático";
                },
                decisionBudget, writeWindow, out skipped, out reason);
            if (skipped) Safety.Journal.Write(JournalKind.Recovery, "ventoinhas: nada a devolver (controle ainda sem confirmação neste notebook e sem marcador)");
            else if (!ok && reason != null) Safety.Journal.Write(JournalKind.Recovery, "ventoinhas: " + reason);
            return ok;
        }

        // The testable core: decide (own budget), then write (timeoutMs, counted from the decision). Both run on one isolated
        // MTA thread. write returns null on success or the reason it failed.
        internal static bool RunAutoIsolated(Func<HandBackDecision> decide, Func<HandBackDecision, string> write, int decisionBudgetMs, int writeTimeoutMs, out bool skipped, out string reason)
        {
            bool ok = false;
            bool skip = false;
            string why = null;
            ManualResetEvent decided = new ManualResetEvent(false);
            Thread t = new Thread(delegate()
            {
                try
                {
                    HandBackDecision hb = decide();
                    decided.Set();
                    if (hb == null || !hb.Write)
                    {
                        skip = true;
                        ok = true;
                        return;
                    }
                    why = write(hb);
                    ok = why == null;
                }
                catch (Exception ex)
                {
                    decided.Set();
                    why = "erro ao devolver ao automático (" + ex.GetType().Name + ")";
                    ok = false;
                }
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.FanAuto";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            if (!decided.WaitOne(Math.Max(0, decisionBudgetMs)))
            {
                skipped = false;
                reason = "a decisão de devolver ao automático não terminou em " + decisionBudgetMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " ms (leitura do marcador ou da identidade do notebook); nada foi confirmado";
                return false;
            }
            // Join is a full fence, so ok is read after the thread's write.
            bool done = t.Join(writeTimeoutMs);
            if (done) decided.Dispose(); // the thread is over: nothing can Set it any more
            skipped = done && skip;
            if (!done)
            {
                reason = "a escrita do Automático não terminou em " + writeTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms; não foi confirmada";
                return false;
            }
            reason = ok ? null : (why ?? "o firmware não confirmou o Automático");
            return ok;
        }

        // Undoes ONLY what the environment test touched, from an isolated MTA thread with its own firmware object
        // (through the guard: journaled, read-only mode respected). The fans are handed back on the layout that was
        // tested, the profile to the one read before the mode test. An empty plan never creates a firmware object.
        public static RestoreResult RestoreIsolated(RestorePlan plan, int timeoutMs)
        {
            RestoreResult result = new RestoreResult();
            if (plan == null || plan.IsEmpty) return result;
            result.FansOk = false;
            result.ModeOk = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    GuardedFirmware fw = Safety.Wrap(new WmiFirmware());
                    if (!fw.Available) return;
                    fw.SetLayout(plan.Layout);
                    RestoreResult r = RestoreExecutor.Run(plan, fw);
                    result.Wrote = r.Wrote;
                    result.FansOk = r.FansOk;
                    result.ModeOk = r.ModeOk;
                }
                catch (Exception)
                {
                }
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.EnvRestore";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(timeoutMs);
            if (!plan.Fans) result.FansOk = true;
            if (!plan.Mode) result.ModeOk = true;
            return result;
        }

        // Same isolation, for the performance test's crash path: mode Dia a dia (read back)
        // and fans Automático. True only when both were confirmed.
        public static bool SetSafeStateIsolated(int timeoutMs)
        {
            bool ok = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    // Through the guard: journaled, tracked by the fan marker and blocked in read-only mode.
                    GuardedFirmware fw = Safety.Wrap(new WmiFirmware());
                    if (!fw.Available) return;
                    // WmiFirmware.SetMode reads the profile back itself; two tries each.
                    bool mode = fw.SetMode(Forja.Core.PerfMode.DiaADia) || fw.SetMode(Forja.Core.PerfMode.DiaADia);
                    bool auto = fw.SetFanAuto() || fw.SetFanAuto();
                    ok = mode && auto;
                }
                catch (Exception)
                {
                    ok = false;
                }
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.SafeState";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            return t.Join(timeoutMs) && ok;
        }
    }
}
