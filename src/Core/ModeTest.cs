using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // Which profiles the wizard may try, and in which order. Only profiles that are announced (by the
    // firmware, or by the table when the firmware's list is garbage) and not flagged dangerous reach here:
    // CapabilityBuilder leaves exactly those Untested.
    public static class ModeTestPlan
    {
        // Untested modes other than Dia a dia, lowest power first (Turbo last). Dia a dia always closes the test.
        public static List<PerfMode> Candidates(CapabilityProfile pre)
        {
            List<PerfMode> l = new List<PerfMode>();
            if (pre == null || !CanRun(pre)) return l;
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                if (m == PerfMode.DiaADia) continue;
                if (pre.State(ModeCatalog.CapOf(m)) == CapState.Untested) l.Add(m);
            }
            return l;
        }

        // The test needs the balanced profile to come back to: without it nothing is written.
        public static bool CanRun(CapabilityProfile pre)
        {
            return pre != null && pre.State(Cap.ModeBalanced) == CapState.Untested;
        }

        // The steps in the order they run, ending on Dia a dia. No step equals the one before it (a write
        // that changes nothing proves nothing), so the first candidate is moved aside when it is the current mode.
        public static List<PerfMode> Order(PerfMode? current, IList<PerfMode> candidates)
        {
            List<PerfMode> seq = new List<PerfMode>();
            if (candidates != null)
            {
                foreach (PerfMode m in candidates)
                {
                    if (m != PerfMode.DiaADia && !seq.Contains(m)) seq.Add(m);
                }
            }
            if (seq.Count > 0 && current.HasValue && seq[0] == current.Value)
            {
                if (seq.Count == 1)
                {
                    seq.Insert(0, PerfMode.DiaADia);
                }
                else
                {
                    PerfMode first = seq[0];
                    seq.RemoveAt(0);
                    seq.Add(first);
                }
            }
            seq.Add(PerfMode.DiaADia);
            return seq;
        }
    }

    public sealed class ModeTestProgress
    {
        public PerfMode Mode;
        public int Index;
        public int Total;
        public string Text;
    }

    public sealed class ModeTestOutcome
    {
        public readonly List<ModeStepResult> Steps = new List<ModeStepResult>();
        public bool Cancelled;
        // The firmware reads back Dia a dia at the end (the app never leaves the notebook in a mode it did not confirm).
        public bool EndedBalanced;
        public readonly List<string> Lines = new List<string>();
    }

    // Runs the plan: each mode is written, read back after a pause, and judged only if the state really
    // changed. Always ends on Dia a dia, also after a cancel or an error. Every write goes through the
    // firmware it is given (the app passes the guarded one: journal, read-only mode).
    public sealed class ModeTestRunner
    {
        public const int PauseMs = 1500;

        private readonly IFirmware fw;
        private readonly Action<int> sleep;
        private volatile bool cancelled;

        // Optional: told before each step.
        public Action<ModeTestProgress> Progress;
        // Optional: asked before each step; false skips it (the wizard asks for the extra Turbo confirmation here).
        public Func<PerfMode, bool> Confirm;

        public ModeTestRunner(IFirmware fw, Action<int> sleep)
        {
            if (fw == null) throw new ArgumentNullException("fw");
            if (sleep == null) throw new ArgumentNullException("sleep");
            this.fw = fw;
            this.sleep = sleep;
        }

        // Any thread: no further step starts; the runner then restores Dia a dia and returns.
        public void Cancel()
        {
            cancelled = true;
        }

        public ModeTestOutcome Run(IList<PerfMode> candidates)
        {
            ModeTestOutcome o = new ModeTestOutcome();
            PerfMode? prev = SafeGet();
            List<PerfMode> plan = ModeTestPlan.Order(prev, candidates);
            for (int i = 0; i < plan.Count; i++)
            {
                if (cancelled)
                {
                    o.Cancelled = true;
                    break;
                }
                PerfMode m = plan[i];
                Report(m, i, plan.Count);
                if (cancelled)
                {
                    o.Cancelled = true;
                    break;
                }
                if (Confirm != null)
                {
                    bool go;
                    try { go = Confirm(m); }
                    catch (Exception) { go = false; }
                    if (!go)
                    {
                        ModeStepResult sk = new ModeStepResult();
                        sk.Mode = m;
                        sk.Status = StepStatus.Skipped;
                        o.Steps.Add(sk);
                        o.Lines.Add(ModeCatalog.Name(m) + ": pulado");
                        continue;
                    }
                }
                bool ok = false;
                PerfMode? back = null;
                bool threw = false;
                try
                {
                    ok = fw.SetMode(m);
                    sleep(PauseMs);
                    back = fw.GetMode();
                }
                catch (Exception ex)
                {
                    threw = true;
                    o.Lines.Add(ModeCatalog.Name(m) + ": erro " + ex.GetType().Name);
                }
                ModeStepResult r = new ModeStepResult();
                r.Mode = m;
                bool pass = !threw && ok && back.HasValue && back.Value == m;
                r.Status = pass ? StepStatus.Passed : StepStatus.Failed;
                r.Proven = pass && prev.HasValue && prev.Value != m;
                o.Steps.Add(r);
                if (!threw)
                {
                    o.Lines.Add(ModeCatalog.Name(m) + ": escrita " + (ok ? "ok" : "recusada") + ", lido "
                        + (back.HasValue ? ModeCatalog.Name(back.Value) : "-") + (pass ? " — ok" : " — falhou"));
                }
                prev = threw ? SafeGet() : back;
            }

            // Whatever happened, the notebook goes back to Dia a dia and the read-back says whether it did.
            PerfMode? last = SafeGet();
            if (!last.HasValue || last.Value != PerfMode.DiaADia)
            {
                try { fw.SetMode(PerfMode.DiaADia); }
                catch (Exception) { }
                last = SafeGet();
            }
            o.EndedBalanced = last.HasValue && last.Value == PerfMode.DiaADia;
            return o;
        }

        private void Report(PerfMode m, int index, int total)
        {
            Action<ModeTestProgress> cb = Progress;
            if (cb == null) return;
            ModeTestProgress p = new ModeTestProgress();
            p.Mode = m;
            p.Index = index;
            p.Total = total;
            p.Text = m == PerfMode.DiaADia
                ? "Voltando para o Dia a dia (passo " + (index + 1).ToString(CultureInfo.InvariantCulture) + " de " + total.ToString(CultureInfo.InvariantCulture) + ")."
                : "Trocando para " + ModeCatalog.Name(m) + " (passo " + (index + 1).ToString(CultureInfo.InvariantCulture) + " de " + total.ToString(CultureInfo.InvariantCulture) + "). Escute: o ruído das ventoinhas pode mudar.";
            try { cb(p); }
            catch (Exception) { }
        }

        private PerfMode? SafeGet()
        {
            try { return fw.GetMode(); }
            catch (Exception) { return null; }
        }
    }
}
