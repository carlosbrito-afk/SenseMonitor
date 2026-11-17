using System;

namespace Forja.Core
{
    // Plot area in control pixels (the grid itself, without axis labels).
    public struct PlotRect
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;

        public PlotRect(double left, double top, double width, double height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
    }

    // Pure geometry and editing rules of the fan curve editor. Every edit returns a
    // new, normalized FanCurve; the input is never modified.
    public static class CurveEditMath
    {
        public const int MinTemp = 30;
        public const int MaxTemp = 100;
        public const int MinPct = 30;
        public const int MaxPct = 100;
        public const int MinPoints = 2;

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static double TempToX(int temp, PlotRect r)
        {
            return r.Left + (double)(temp - MinTemp) / (MaxTemp - MinTemp) * r.Width;
        }

        public static double TempToX(double temp, PlotRect r)
        {
            return r.Left + (temp - MinTemp) / (MaxTemp - MinTemp) * r.Width;
        }

        // 100 % is the top edge.
        public static double PctToY(int pct, PlotRect r)
        {
            return r.Top + (double)(MaxPct - pct) / (MaxPct - MinPct) * r.Height;
        }

        public static int XToTemp(double x, PlotRect r)
        {
            if (r.Width <= 0) return MinTemp;
            double t = MinTemp + (x - r.Left) / r.Width * (MaxTemp - MinTemp);
            return Clamp((int)Math.Round(t, MidpointRounding.AwayFromZero), MinTemp, MaxTemp);
        }

        public static int YToPct(double y, PlotRect r)
        {
            if (r.Height <= 0) return MinPct;
            double p = MaxPct - (y - r.Top) / r.Height * (MaxPct - MinPct);
            return Clamp((int)Math.Round(p, MidpointRounding.AwayFromZero), MinPct, MaxPct);
        }

        // Index of the nearest point whose center is within radius px of (px, py); -1 if none.
        public static int HitTest(FanCurve c, double px, double py, PlotRect r, double radius)
        {
            if (c == null || c.Points == null) return -1;
            int best = -1;
            double bestD = radius * radius;
            for (int i = 0; i < c.Points.Count; i++)
            {
                double dx = TempToX(c.Points[i].TempC, r) - px;
                double dy = PctToY(c.Points[i].Pct, r) - py;
                double d = dx * dx + dy * dy;
                if (d <= bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        private static FanCurve Copy(FanCurve c)
        {
            FanCurve n = new FanCurve();
            if (c != null && c.Points != null)
            {
                foreach (CurvePoint p in c.Points)
                {
                    if (p != null) n.Points.Add(new CurvePoint(p.TempC, p.Pct));
                }
            }
            return n;
        }

        // A point at an existing temperature replaces that point's percentage.
        public static FanCurve Add(FanCurve c, int temp, int pct)
        {
            FanCurve n = Copy(c);
            n.Points.Add(new CurvePoint(Clamp(temp, MinTemp, MaxTemp), Clamp(pct, MinPct, MaxPct)));
            return n.Normalize();
        }

        // Never leaves fewer than MinPoints points; a bad index leaves the curve unchanged.
        public static FanCurve Remove(FanCurve c, int index)
        {
            FanCurve n = Copy(c);
            if (index < 0 || index >= n.Points.Count || n.Points.Count <= MinPoints) return n.Normalize();
            n.Points.RemoveAt(index);
            return n.Normalize();
        }

        // Moves one point; its temperature stays strictly between its neighbours so the
        // index (and the point under the mouse) never changes during a drag.
        public static FanCurve Move(FanCurve c, int index, int temp, int pct)
        {
            FanCurve n = Copy(c).Normalize();
            if (index < 0 || index >= n.Points.Count) return n;
            int lo = index > 0 ? n.Points[index - 1].TempC + 1 : MinTemp;
            int hi = index < n.Points.Count - 1 ? n.Points[index + 1].TempC - 1 : MaxTemp;
            n.Points[index].TempC = Clamp(temp, lo, hi);
            n.Points[index].Pct = Clamp(pct, MinPct, MaxPct);
            return n;
        }
    }
}
