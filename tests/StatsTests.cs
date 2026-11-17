using System.Threading;
using Forja.Platform;

namespace Forja.Tests
{
    public static class StatsTests
    {
        [Test]
        public static void ParseNvidiaSmi_Valid()
        {
            GpuInfo g = SystemStats.ParseNvidiaSmi("37, 54");
            Assert.True(g != null, "expected a GpuInfo");
            Assert.Equal(37, g.UsagePct.Value);
            Assert.True(g.TempC.HasValue && System.Math.Abs(g.TempC.Value - 54.0) < 0.001, "temp should be 54.0");
        }

        [Test]
        public static void ParseNvidiaSmi_Garbage_Nulls()
        {
            Assert.Null(SystemStats.ParseNvidiaSmi(null));
            Assert.Null(SystemStats.ParseNvidiaSmi(""));
            Assert.Null(SystemStats.ParseNvidiaSmi("garbage"));
            Assert.Null(SystemStats.ParseNvidiaSmi("a, b"));
            Assert.Null(SystemStats.ParseNvidiaSmi("37"));
            Assert.Null(SystemStats.ParseNvidiaSmi("[N/A], [N/A]"));
        }

        [Test]
        public static void Ram_TotalAboutInstalled()
        {
            using (SystemStats s = new SystemStats())
            {
                double gib = s.RamTotalBytes() / (1024.0 * 1024.0 * 1024.0);
                Assert.True(gib >= 30.0 && gib <= 34.0, "total GiB out of range: " + gib);
                long used = s.RamUsedBytes();
                Assert.True(used > 0 && used <= s.RamTotalBytes(), "used out of range: " + used);
            }
        }

        [Test]
        public static void Cpu_InRange()
        {
            using (SystemStats s = new SystemStats())
            {
                Thread.Sleep(300);
                float c = s.CpuPercent();
                Assert.True(c >= 0f && c <= 100f, "cpu out of range: " + c);
            }
        }
    }
}
