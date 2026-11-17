using Forja.Core;

namespace Forja.Tests
{
    // The 10 min history needs one sample per second whatever feeds it: the 1 Hz timer while
    // the Monitor page is open, the 2 s firmware tick while the window is hidden.
    public static class SamplePacerTests
    {
        [Test]
        public static void FirstCall_TakesOneSample()
        {
            SamplePacer p = new SamplePacer();
            Assert.Equal(1, p.Due(5000));
            Assert.Equal(0, p.Due(5400));
        }

        [Test]
        public static void TwoSecondTicks_TakeTwoSamplesEach()
        {
            SamplePacer p = new SamplePacer();
            p.Due(0);
            int total = 0;
            for (int t = 2000; t <= 600000; t += 2000) total += p.Due(t);
            Assert.Equal(600, total); // 10 min of 2 s ticks = 600 samples
        }

        [Test]
        public static void JitteryOneSecondTimer_KeepsPhase()
        {
            SamplePacer p = new SamplePacer();
            p.Due(0);
            Assert.Equal(0, p.Due(990));
            Assert.Equal(1, p.Due(1010));
            Assert.Equal(1, p.Due(2005));
            Assert.Equal(1, p.Due(2995 + 10));
        }

        [Test]
        public static void MixedSources_NeverDoubleCount()
        {
            SamplePacer p = new SamplePacer();
            p.Due(0);
            Assert.Equal(1, p.Due(1000));   // timer
            Assert.Equal(0, p.Due(1200));   // firmware tick in the same second
            Assert.Equal(1, p.Due(2001));
        }

        [Test]
        public static void LongPause_IsCappedAndResynced()
        {
            SamplePacer p = new SamplePacer();
            p.Due(0);
            Assert.Equal(SamplePacer.MaxBurst, p.Due(60000)); // e.g. after suspend
            Assert.Equal(0, p.Due(60500));
            Assert.Equal(1, p.Due(61000));
        }

        [Test]
        public static void TickCountWrap_StillCounts()
        {
            SamplePacer p = new SamplePacer();
            p.Due(int.MaxValue - 500);
            Assert.Equal(2, p.Due(unchecked(int.MaxValue + 1500)));
        }
    }
}
