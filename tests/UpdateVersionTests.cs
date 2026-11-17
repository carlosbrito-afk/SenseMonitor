using Forja.Core;

namespace Forja.Tests
{
    public static class UpdateVersionTests
    {
        private static UpdateVersion P(string s)
        {
            UpdateVersion v;
            Assert.True(UpdateVersion.TryParse(s, out v), "should parse: " + s);
            return v;
        }

        [Test]
        public static void Parse_ThreeNumbers()
        {
            UpdateVersion v = P("0.2.10");
            Assert.Equal(0, v.Major);
            Assert.Equal(2, v.Minor);
            Assert.Equal(10, v.Patch);
            Assert.Equal("0.2.10", v.ToString());
        }

        [Test]
        public static void Parse_RejectsAnythingElse()
        {
            string[] bad = { null, "", " ", "1", "1.2", "1.2.3.4", "v1.2.3", "1.2.3-beta", "1.2.3+x", " 1.2.3", "1.2.3 ", "1.2.3\n",
                "01.2.3", "1.02.3", "1.2.03", "-1.2.3", "1.-2.3", "1..3", "a.b.c", "1.2.99999", "99999.1.1", "١.2.3", "1,2,3" };
            foreach (string s in bad)
            {
                UpdateVersion v;
                Assert.True(!UpdateVersion.TryParse(s, out v), "should reject: " + (s ?? "null"));
                Assert.Null(v);
            }
        }

        [Test]
        public static void Compare_IsNumericNotTextual()
        {
            Assert.True(P("0.10.0").CompareTo(P("0.9.0")) > 0, "0.10.0 > 0.9.0");
            Assert.True(P("1.0.0").CompareTo(P("0.99.99")) > 0, "major wins");
            Assert.True(P("0.2.1").CompareTo(P("0.2.0")) > 0, "patch");
            Assert.True(P("0.2.0").CompareTo(P("0.2.0")) == 0, "equal");
            Assert.True(P("0.2.0").CompareTo(P("0.2.1")) < 0, "less");
        }

        [Test]
        public static void IsNewerThan_IsStrict()
        {
            Assert.True(P("0.2.1").IsNewerThan(P("0.2.0")), "newer");
            Assert.True(!P("0.2.0").IsNewerThan(P("0.2.0")), "same is not newer");
            Assert.True(!P("0.1.9").IsNewerThan(P("0.2.0")), "older is not newer");
        }
    }
}
