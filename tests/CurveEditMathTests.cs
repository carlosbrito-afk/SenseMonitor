using Forja.Core;

namespace Forja.Tests
{
    public static class CurveEditMathTests
    {
        private static PlotRect R()
        {
            // 70 degrees wide -> 700 px (10 px/degC); 70 pct tall -> 350 px (5 px/pct)
            return new PlotRect(50, 20, 700, 350);
        }

        private static FanCurve Curve(params int[] tp)
        {
            FanCurve c = new FanCurve();
            for (int i = 0; i + 1 < tp.Length; i += 2) c.Points.Add(new CurvePoint(tp[i], tp[i + 1]));
            return c;
        }

        [Test]
        public static void TempToX_Endpoints()
        {
            Assert.Equal(50.0, CurveEditMath.TempToX(30, R()));
            Assert.Equal(750.0, CurveEditMath.TempToX(100, R()));
            Assert.Equal(400.0, CurveEditMath.TempToX(65, R()));
        }

        [Test]
        public static void PctToY_HundredIsTop()
        {
            Assert.Equal(20.0, CurveEditMath.PctToY(100, R()));
            Assert.Equal(370.0, CurveEditMath.PctToY(30, R()));
            Assert.Equal(195.0, CurveEditMath.PctToY(65, R()));
        }

        [Test]
        public static void XYToValues_RoundAndClamp()
        {
            Assert.Equal(65, CurveEditMath.XToTemp(402.0, R()));
            Assert.Equal(30, CurveEditMath.XToTemp(-500.0, R()));
            Assert.Equal(100, CurveEditMath.XToTemp(5000.0, R()));
            Assert.Equal(65, CurveEditMath.YToPct(196.0, R()));
            Assert.Equal(100, CurveEditMath.YToPct(-40.0, R()));
            Assert.Equal(30, CurveEditMath.YToPct(900.0, R()));
        }

        [Test]
        public static void HitTest_PicksNearestInsideRadius()
        {
            FanCurve c = Curve(50, 30, 52, 30, 85, 100);
            // (50,30) -> x=250,y=370 ; (52,30) -> x=270,y=370
            Assert.Equal(0, CurveEditMath.HitTest(c, 252, 372, R(), 12));
            Assert.Equal(1, CurveEditMath.HitTest(c, 264, 370, R(), 12));
            Assert.Equal(-1, CurveEditMath.HitTest(c, 400, 200, R(), 12));
        }

        [Test]
        public static void Add_InsertsSortedAndNormalized()
        {
            FanCurve c = CurveEditMath.Add(FanCurve.Default(), 70, 60);
            Assert.Equal(5, c.Points.Count);
            Assert.Equal(70, c.Points[2].TempC);
            Assert.Equal(60, c.Points[2].Pct);
            Assert.Equal(75, c.Points[3].TempC);
            Assert.Equal(85, c.Points[4].TempC);
        }

        [Test]
        public static void Add_ClampsAndReplacesSameTemp()
        {
            FanCurve c = CurveEditMath.Add(FanCurve.Default(), 65, 999);
            Assert.Equal(4, c.Points.Count);
            Assert.Equal(100, c.Points[1].Pct);
            c = CurveEditMath.Add(c, 5, 5);
            Assert.Equal(30, c.Points[0].TempC);
            Assert.Equal(30, c.Points[0].Pct);
        }

        [Test]
        public static void Remove_NeverBelowTwoPoints()
        {
            FanCurve c = CurveEditMath.Remove(FanCurve.Default(), 1);
            Assert.Equal(3, c.Points.Count);
            c = CurveEditMath.Remove(c, 0);
            Assert.Equal(2, c.Points.Count);
            FanCurve same = CurveEditMath.Remove(c, 0);
            Assert.Equal(2, same.Points.Count);
            Assert.Equal(c.Points[0].TempC, same.Points[0].TempC);
        }

        [Test]
        public static void Remove_BadIndex_Unchanged()
        {
            Assert.Equal(4, CurveEditMath.Remove(FanCurve.Default(), -1).Points.Count);
            Assert.Equal(4, CurveEditMath.Remove(FanCurve.Default(), 9).Points.Count);
        }

        [Test]
        public static void Move_ClampsToGridAndKeepsOrder()
        {
            FanCurve c = CurveEditMath.Move(FanCurve.Default(), 1, 200, 5);
            // neighbours are 50 and 75: temp stays strictly between them
            Assert.Equal(74, c.Points[1].TempC);
            Assert.Equal(30, c.Points[1].Pct);
            Assert.Equal(4, c.Points.Count);
            c = CurveEditMath.Move(FanCurve.Default(), 1, 0, 500);
            Assert.Equal(51, c.Points[1].TempC);
            Assert.Equal(100, c.Points[1].Pct);
        }

        [Test]
        public static void Move_EndPoints_UseGridLimits()
        {
            FanCurve c = CurveEditMath.Move(FanCurve.Default(), 0, 0, 40);
            Assert.Equal(30, c.Points[0].TempC);
            Assert.Equal(40, c.Points[0].Pct);
            c = CurveEditMath.Move(FanCurve.Default(), 3, 500, 60);
            Assert.Equal(100, c.Points[3].TempC);
        }

        [Test]
        public static void Move_DoesNotMutateInput()
        {
            FanCurve src = FanCurve.Default();
            CurveEditMath.Move(src, 1, 70, 90);
            Assert.Equal(65, src.Points[1].TempC);
            Assert.Equal(50, src.Points[1].Pct);
        }
    }
}
