using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // Turns the per-second samples of one mode into scores and averages.
    public static class PerfScoring
    {
        // A phase counts as complete when its last sample is within this of the planned length.
        private const double Slack = 1.5;

        public static int? MeanRpm(int? a, int? b)
        {
            if (a.HasValue && b.HasValue) return (int)Math.Round((a.Value + b.Value) / 2.0, MidpointRounding.AwayFromZero);
            if (a.HasValue) return a;
            return b;
        }

        // Work units reached t seconds after the load started: linear between samples,
        // anchored at (0, 0).
        public static double UnitsAt(List<PerfSample> s, double t)
        {
            double prevT = 0, prevU = 0;
            foreach (PerfSample p in s)
            {
                if (t <= p.T)
                {
                    double span = p.T - prevT;
                    if (span <= 0) return p.Units;
                    return prevU + (p.Units - prevU) * ((t - prevT) / span);
                }
                prevT = p.T;
                prevU = p.Units;
            }
            return prevU;
        }

        // Units per second between two moments of the load; null when the window is empty.
        public static double? Rate(List<PerfSample> s, double from, double to)
        {
            if (s == null || s.Count == 0 || to - from <= 0) return null;
            return (UnitsAt(s, to) - UnitsAt(s, from)) / (to - from);
        }

        public static List<PerfSample> Of(PerfModeRun run, PerfPhase phase)
        {
            List<PerfSample> l = new List<PerfSample>();
            foreach (PerfSample p in run.Samples)
            {
                if (p.Phase == phase) l.Add(p);
            }
            return l;
        }

        private static double LastT(List<PerfSample> s)
        {
            return s.Count == 0 ? 0 : s[s.Count - 1].T;
        }

        public static PerfStats Summarize(PerfModeRun run)
        {
            PerfStats st = new PerfStats();
            List<PerfSample> idle = Of(run, PerfPhase.Idle);
            List<PerfSample> multi = Of(run, PerfPhase.Multi);
            List<PerfSample> single = Of(run, PerfPhase.Single);

            double multiEnd = Math.Min(PerfPlan.MultiSec, LastT(multi));
            st.MultiComplete = multi.Count > 0 && LastT(multi) >= PerfPlan.MultiSec - Slack;
            if (multiEnd >= 5)
            {
                st.MultiSustained = Rate(multi, Math.Max(0, multiEnd - PerfPlan.SustainedSec), multiEnd);
            }
            if (LastT(multi) >= PerfPlan.BurstSec - Slack)
            {
                st.Burst = Rate(multi, 0, Math.Min(PerfPlan.BurstSec, LastT(multi)));
            }

            double singleEnd = Math.Min(PerfPlan.SingleSec, LastT(single));
            st.SingleComplete = single.Count > 0 && LastT(single) >= PerfPlan.SingleSec - Slack;
            if (singleEnd >= 5) st.Single = Rate(single, 0, singleEnd);

            st.AvgCpuTemp = Mean(multi, delegate(PerfSample p) { return p.CpuTempC; });
            double lateFrom = LastT(multi) - PerfPlan.LateWindowSec;
            st.LateCpuTemp = Mean(multi, delegate(PerfSample p) { return p.T > lateFrom ? p.CpuTempC : null; });
            st.IdleCpuLoad = Mean(idle, delegate(PerfSample p) { return p.CpuLoadPct; });
            st.AvgGpuTemp = Mean(multi, delegate(PerfSample p) { return p.GpuTempC; });
            st.PeakCpuTemp = Peak(multi, single, delegate(PerfSample p) { return p.CpuTempC; });
            st.PeakGpuTemp = Peak(multi, single, delegate(PerfSample p) { return p.GpuTempC; });
            st.AvgCpuRpm = ToInt(MeanInt(multi, delegate(PerfSample p) { return p.CpuRpm; }));
            st.AvgGpuRpm = ToInt(MeanInt(multi, delegate(PerfSample p) { return p.GpuRpm; }));
            st.IdleCpuRpm = ToInt(MeanInt(idle, delegate(PerfSample p) { return p.CpuRpm; }));
            st.IdleGpuRpm = ToInt(MeanInt(idle, delegate(PerfSample p) { return p.GpuRpm; }));
            st.IdleCpuTemp = Mean(idle, delegate(PerfSample p) { return p.CpuTempC; });
            st.AvgClockMulti = Mean(multi, delegate(PerfSample p) { return p.ClockMHz; });
            st.AvgClockSingle = Mean(single, delegate(PerfSample p) { return p.ClockMHz; });
            st.AvgDischargeMw = Mean(multi, delegate(PerfSample p) { return p.DischargeMw; });
            return st;
        }

        private static double? Mean(List<PerfSample> s, Func<PerfSample, double?> pick)
        {
            double sum = 0;
            int n = 0;
            foreach (PerfSample p in s)
            {
                double? v = pick(p);
                if (v.HasValue) { sum += v.Value; n++; }
            }
            return n == 0 ? (double?)null : sum / n;
        }

        private static double? MeanInt(List<PerfSample> s, Func<PerfSample, int?> pick)
        {
            return Mean(s, delegate(PerfSample p)
            {
                int? v = pick(p);
                return v.HasValue ? (double?)v.Value : null;
            });
        }

        private static double? Peak(List<PerfSample> a, List<PerfSample> b, Func<PerfSample, double?> pick)
        {
            double? best = null;
            foreach (List<PerfSample> list in new List<PerfSample>[] { a, b })
            {
                foreach (PerfSample p in list)
                {
                    double? v = pick(p);
                    if (v.HasValue && (!best.HasValue || v.Value > best.Value)) best = v;
                }
            }
            return best;
        }

        private static int? ToInt(double? v)
        {
            return v.HasValue ? (int)Math.Round(v.Value, MidpointRounding.AwayFromZero) : (int?)null;
        }
    }
}
