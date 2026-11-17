using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Forja.Core
{
    [DataContract]
    public class CurvePoint
    {
        [DataMember] public int TempC;
        [DataMember] public int Pct;

        public CurvePoint()
        {
        }

        public CurvePoint(int tempC, int pct)
        {
            TempC = tempC;
            Pct = pct;
        }
    }

    [DataContract]
    public class FanCurve
    {
        private const int MinTemp = 30;
        private const int MaxTemp = 100;
        private const int MinPct = 30;
        private const int MaxPct = 100;

        [DataMember] public List<CurvePoint> Points = new List<CurvePoint>();

        public static FanCurve Default()
        {
            FanCurve c = new FanCurve();
            c.Points.Add(new CurvePoint(50, 30));
            c.Points.Add(new CurvePoint(65, 50));
            c.Points.Add(new CurvePoint(75, 70));
            c.Points.Add(new CurvePoint(85, 100));
            return c;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // Returns a new, valid curve; this instance is not modified.
        public FanCurve Normalize()
        {
            SortedDictionary<int, int> byTemp = new SortedDictionary<int, int>();
            if (Points != null)
            {
                foreach (CurvePoint p in Points)
                {
                    if (p == null) continue;
                    // later duplicates overwrite earlier ones (keep last)
                    byTemp[Clamp(p.TempC, MinTemp, MaxTemp)] = Clamp(p.Pct, MinPct, MaxPct);
                }
            }
            if (byTemp.Count < 2) return Default();

            FanCurve result = new FanCurve();
            foreach (KeyValuePair<int, int> kv in byTemp)
            {
                result.Points.Add(new CurvePoint(kv.Key, kv.Value));
            }
            return result;
        }

        // Assumes Points sorted by temperature (call Normalize first).
        // NaN is treated as "below the first point" and yields the first point's pct.
        public int Evaluate(double tempC)
        {
            if (Points == null || Points.Count == 0) return MinPct;
            CurvePoint first = Points[0];
            CurvePoint last = Points[Points.Count - 1];
            if (double.IsNaN(tempC) || tempC <= first.TempC) return first.Pct;
            if (tempC >= last.TempC) return last.Pct;

            for (int i = 1; i < Points.Count; i++)
            {
                CurvePoint a = Points[i - 1];
                CurvePoint b = Points[i];
                if (tempC <= b.TempC)
                {
                    double span = b.TempC - a.TempC;
                    if (span <= 0) return b.Pct;
                    double t = (tempC - a.TempC) / span;
                    return (int)Math.Round(a.Pct + t * (b.Pct - a.Pct), MidpointRounding.AwayFromZero);
                }
            }
            return last.Pct;
        }
    }
}
