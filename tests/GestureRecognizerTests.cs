using System;
using Forja.Core;

namespace Forja.Tests
{
    // Real-shaped sequences of the NitroSense key: a press is a make/break burst within ~100 ms.
    public static class GestureRecognizerTests
    {
        // down, up, down, up inside one burst starting at t (about 90 ms long).
        private static NitroGesture Burst(GestureRecognizer r, int t)
        {
            NitroGesture g = r.Feed(true, t);
            Merge(ref g, r.Feed(false, t + 30));
            Merge(ref g, r.Feed(true, t + 60));
            Merge(ref g, r.Feed(false, t + 90));
            return g;
        }

        private static void Merge(ref NitroGesture total, NitroGesture g)
        {
            if (g != NitroGesture.None) total = g;
        }

        // Polls every 20 ms from..to and returns the one gesture decided (or None).
        private static NitroGesture Run(GestureRecognizer r, int from, int to)
        {
            NitroGesture seen = NitroGesture.None;
            int count = 0;
            for (int t = from; t <= to; t += 20)
            {
                NitroGesture g = r.Poll(t);
                if (g != NitroGesture.None)
                {
                    seen = g;
                    count++;
                }
            }
            Assert.True(count <= 1, "at most one gesture, got " + count);
            return seen;
        }

        [Test]
        public static void SingleBurst_IsOneTap_OnlyAfterTheWindow()
        {
            GestureRecognizer r = new GestureRecognizer();
            Assert.Equal(NitroGesture.None, Burst(r, 1000));
            Assert.True(r.Busy, "undecided");
            Assert.Equal(NitroGesture.None, r.Poll(1400)); // 1090 + 350 not over yet
            Assert.Equal(NitroGesture.Tap1, r.Poll(1500));
            Assert.True(!r.Busy, "decided");
            Assert.Equal(NitroGesture.None, r.Poll(1600));
        }

        [Test]
        public static void CleanDownUp_IsOneTap()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.Feed(true, 5000);
            r.Feed(false, 5100);
            Assert.Equal(NitroGesture.Tap1, Run(r, 5100, 5700));
        }

        [Test]
        public static void LostKeyUp_IsTreatedAsAHold_NotAGesture()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.Feed(true, 5000);
            // The polling keeps going: the key counts as held, 900 ms in it is a hold (long press is off).
            NitroGesture g = Run(r, 5000, 9000);
            Assert.Equal(NitroGesture.None, g);
            Assert.True(!r.Busy, "stuck press forgotten");
        }

        [Test]
        public static void DoubleTap_TwoBurstsWithinTheWindow()
        {
            GestureRecognizer r = new GestureRecognizer();
            Burst(r, 1000);
            Assert.Equal(NitroGesture.None, Burst(r, 1300)); // 1090 -> 1300: 210 ms
            Assert.Equal(NitroGesture.None, r.Poll(1700));
            Assert.Equal(NitroGesture.Tap2, r.Poll(1800)); // 1390 + 350 = 1740 passed
        }

        [Test]
        public static void TripleTap()
        {
            GestureRecognizer r = new GestureRecognizer();
            Burst(r, 1000);
            Burst(r, 1300);
            Burst(r, 1600);
            Assert.Equal(NitroGesture.None, r.Poll(2000));
            Assert.Equal(NitroGesture.Tap3, r.Poll(2100)); // 1690 + 350
        }

        [Test]
        public static void FourTaps_AreIgnored_ThenTheNextGestureWorks()
        {
            GestureRecognizer r = new GestureRecognizer();
            Burst(r, 1000);
            Burst(r, 1300);
            Burst(r, 1600);
            Burst(r, 1900);
            Assert.Equal(NitroGesture.None, Run(r, 1900, 3000));
            Assert.True(!r.Busy, "forgotten");
            Burst(r, 5000);
            Assert.Equal(NitroGesture.Tap1, Run(r, 5090, 5700));
        }

        [Test]
        public static void SlowSecondTap_IsASeparateSingleTap()
        {
            GestureRecognizer r = new GestureRecognizer();
            Burst(r, 1000);
            // 1090 -> 1500: 410 ms, too slow: the first single was decided by this very event.
            NitroGesture first = r.Feed(true, 1500);
            Assert.Equal(NitroGesture.Tap1, first);
            r.Feed(false, 1530);
            Assert.Equal(NitroGesture.Tap1, Run(r, 1530, 2200));
        }

        [Test]
        public static void QuietEventDecidesAnOldGestureWithoutPolling()
        {
            GestureRecognizer r = new GestureRecognizer();
            Burst(r, 1000);
            NitroGesture g = r.Feed(true, 9000); // nobody polled: the next press decides the old one
            Assert.Equal(NitroGesture.Tap1, g);
        }

        [Test]
        public static void EventsWithin120ms_AreOneCluster_121Splits()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.Feed(true, 1000);
            r.Feed(false, 1120);   // exactly 120: same press
            r.Feed(true, 1240);    // 120 again: same press
            r.Feed(false, 1260);
            Assert.Equal(NitroGesture.Tap1, Run(r, 1260, 1900));

            GestureRecognizer s = new GestureRecognizer();
            s.Feed(true, 1000);
            s.Feed(false, 1030);
            s.Feed(true, 1151);    // 121: a second tap
            s.Feed(false, 1180);
            Assert.Equal(NitroGesture.Tap2, Run(s, 1180, 1800));
        }

        [Test]
        public static void NoDoubleOrTripleMapped_SingleFiresRightAfterThePress()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.DoubleTapEnabled = false;
            r.TripleTapEnabled = false;
            Burst(r, 1000);
            Assert.Equal(NitroGesture.None, r.Poll(1200)); // 1090 + 120 not yet over (press may continue)
            Assert.Equal(NitroGesture.Tap1, r.Poll(1211));
        }

        [Test]
        public static void NoTripleMapped_DoubleFiresRightAfterTheSecondPress()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.TripleTapEnabled = false;
            Burst(r, 1000);
            Burst(r, 1300);
            Assert.Equal(NitroGesture.Tap2, r.Poll(1511)); // 1390 + 121
        }

        [Test]
        public static void HeldKeyWithAutoRepeat_LongOff_DoesNothing()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.Feed(true, 1000);
            for (int t = 1500; t < 3000; t += 33) r.Feed(true, t); // typematic repeat
            r.Feed(false, 3000);
            Assert.Equal(NitroGesture.None, Run(r, 3000, 4500));
            Assert.True(!r.Busy, "forgotten");
            Burst(r, 6000);
            Assert.Equal(NitroGesture.Tap1, Run(r, 6090, 6700));
        }

        [Test]
        public static void ShortHoldWithAutoRepeat_IsAnOrdinaryTap()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.Feed(true, 1000);
            for (int t = 1500; t < 1600; t += 33) r.Feed(true, t);
            r.Feed(false, 1650);
            Assert.Equal(NitroGesture.Tap1, Run(r, 1650, 2300));
        }

        [Test]
        public static void LongPress_WhenEnabled_FiresWhileStillHeld_AtLongPressMs()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.LongPressEnabled = true;
            r.Feed(true, 1000);
            for (int t = 1500; t < 1880; t += 33) r.Feed(true, t);
            Assert.Equal(NitroGesture.None, r.Poll(1899));
            Assert.Equal(NitroGesture.Long, r.Poll(1900));
            Assert.Equal(NitroGesture.None, r.Poll(2000));
            r.Feed(true, 2500);
            r.Feed(false, 3000);
            Assert.Equal(NitroGesture.None, Run(r, 3000, 4500)); // releasing does not fire anything more
            Burst(r, 7000);
            Assert.Equal(NitroGesture.Tap1, Run(r, 7090, 7700));
        }

        [Test]
        public static void LongPress_NoRepeat_JustDownThenUp()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.LongPressEnabled = true;
            r.Feed(true, 1000);
            Assert.Equal(NitroGesture.Long, r.Poll(1950));
            r.Feed(false, 2500);
            Assert.Equal(NitroGesture.None, Run(r, 2500, 3500));
        }

        [Test]
        public static void LongPress_NotPolledInTime_StillFiresOnRelease()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.LongPressEnabled = true;
            r.Feed(true, 1000);
            Assert.Equal(NitroGesture.Long, r.Feed(false, 2200)); // the release itself decides it
            Assert.Equal(NitroGesture.None, Run(r, 2200, 3200));
        }

        [Test]
        public static void TapThenHold_IsTheHoldAlone()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.LongPressEnabled = true;
            Burst(r, 1000);
            r.Feed(true, 1300); // second press is held
            Assert.Equal(NitroGesture.Long, r.Poll(2250));
            r.Feed(false, 2300);
            Assert.Equal(NitroGesture.None, Run(r, 2300, 3500)); // the first tap did not fire
        }

        [Test]
        public static void HeldPairsEvery250ms_ForTwoSeconds_AreIgnored()
        {
            GestureRecognizer r = new GestureRecognizer();
            for (int t = 5000; t < 7000; t += 250)
            {
                r.Feed(true, t);
                r.Feed(false, t + 20);
            }
            Assert.Equal(NitroGesture.None, Run(r, 7000, 8500));
            Assert.True(!r.Busy, "forgotten");
        }

        [Test]
        public static void OrphanKeyUp_IsIgnored_AndResetForgets()
        {
            GestureRecognizer r = new GestureRecognizer();
            Assert.Equal(NitroGesture.None, r.Feed(false, 1000));
            Assert.True(!r.Busy, "nothing started");
            Burst(r, 2000);
            r.Reset();
            Assert.True(!r.Busy, "reset");
            Assert.Equal(NitroGesture.None, Run(r, 2100, 3000));
        }

        [Test]
        public static void TimeWraparound_StillRecognises()
        {
            GestureRecognizer r = new GestureRecognizer();
            int t = int.MaxValue - 150;
            Burst(r, t);
            Burst(r, unchecked(t + 300));
            NitroGesture g = Run(r, unchecked(t + 390), unchecked(t + 1000));
            Assert.Equal(NitroGesture.Tap2, g);
        }

        [Test]
        public static void PollTimeBehindTheLastEvent_IsHarmless()
        {
            GestureRecognizer r = new GestureRecognizer();
            r.Feed(true, 1000);
            Assert.Equal(NitroGesture.None, r.Poll(990)); // the hook time can lead the tick count by a few ms
            r.Feed(false, 1020);
            Assert.Equal(NitroGesture.Tap1, Run(r, 1020, 1700));
        }

        [Test]
        public static void Trace_ListsTheClusterTimings()
        {
            GestureRecognizer r = new GestureRecognizer();
            Burst(r, 1000);
            Burst(r, 1300);
            Run(r, 1390, 2000);
            Assert.Equal("2 toques: 0-90, 300-390 ms", r.LastTrace);
        }
    }
}
