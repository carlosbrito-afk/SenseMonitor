using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class FanCurveTests
    {
        private static void AssertPoint(CurvePoint p, int temp, int pct)
        {
            Assert.Equal(temp, p.TempC);
            Assert.Equal(pct, p.Pct);
        }

        [Test]
        public static void Default_HasSpecPoints()
        {
            FanCurve c = FanCurve.Default();
            Assert.Equal(4, c.Points.Count);
            AssertPoint(c.Points[0], 50, 30);
            AssertPoint(c.Points[1], 65, 50);
            AssertPoint(c.Points[2], 75, 70);
            AssertPoint(c.Points[3], 85, 100);
        }

        [Test]
        public static void Evaluate_Interpolates()
        {
            Assert.Equal(60, FanCurve.Default().Evaluate(70.0));
        }

        [Test]
        public static void Evaluate_BelowAndAbove()
        {
            FanCurve c = FanCurve.Default();
            Assert.Equal(30, c.Evaluate(20.0));
            Assert.Equal(30, c.Evaluate(50.0));
            Assert.Equal(100, c.Evaluate(85.0));
            Assert.Equal(100, c.Evaluate(110.0));
        }

        [Test]
        public static void Normalize_SortsClampsAndDedupes()
        {
            FanCurve input = new FanCurve();
            input.Points = new List<CurvePoint>
            {
                new CurvePoint(90, 10), new CurvePoint(40, 50),
                new CurvePoint(40, 60), new CurvePoint(120, 100)
            };
            FanCurve n = input.Normalize();
            Assert.Equal(3, n.Points.Count);
            AssertPoint(n.Points[0], 40, 60);
            AssertPoint(n.Points[1], 90, 30);
            AssertPoint(n.Points[2], 100, 100);
            Assert.Equal(4, input.Points.Count);
            AssertPoint(input.Points[0], 90, 10);
        }

        [Test]
        public static void Normalize_TooFewPoints_ReturnsDefault()
        {
            FanCurve input = new FanCurve();
            input.Points = new List<CurvePoint> { new CurvePoint(50, 50), new CurvePoint(50, 70) };
            FanCurve n = input.Normalize();
            Assert.Equal(4, n.Points.Count);
            AssertPoint(n.Points[0], 50, 30);
        }
    }
}
