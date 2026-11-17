using System;
using System.Threading;
using System.Xml;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class NitroKeyGestureTests
    {
        private const int Down = 0x100;
        private const int Up = 0x101;

        // The gesture logic itself is in GestureRecognizerTests; this is the key filter.
        private static NitroGesture Tap(NitroKey k, int t)
        {
            NitroGesture g = k.OnKeyGesture(Down, 0xFF, 0x75, 0x1, t);
            g = Pick(g, k.OnKeyGesture(Up, 0xFF, 0x75, 0x81, t + 30));
            g = Pick(g, k.OnKeyGesture(Down, 0xFF, 0x75, 0x1, t + 60));
            g = Pick(g, k.OnKeyGesture(Up, 0xFF, 0x75, 0x81, t + 90));
            return g;
        }

        private static NitroGesture Pick(NitroGesture a, NitroGesture b)
        {
            return b != NitroGesture.None ? b : a;
        }

        [Test]
        public static void NitroKey_BurstPress_IsOneTapAfterTheWindow()
        {
            NitroKey k = new NitroKey();
            Assert.Equal(NitroGesture.None, Tap(k, 1000));
            Assert.True(k.Busy, "waiting for a possible second tap");
            Assert.Equal(NitroGesture.Tap1, k.Poll(1600));
            Assert.True(!k.Busy, "decided");
        }

        [Test]
        public static void NitroKey_TwoBurstsClose_IsADoubleTap()
        {
            NitroKey k = new NitroKey();
            Tap(k, 10000);
            Tap(k, 10300);
            Assert.Equal(NitroGesture.Tap2, k.Poll(11000));
        }

        [Test]
        public static void OtherKeys_Ignored_AndLeaveNoTrace()
        {
            NitroKey k = new NitroKey();
            Assert.Equal(NitroGesture.None, k.OnKeyGesture(Down, 0x41, 0x1E, 0, 1000));
            Assert.Equal(NitroGesture.None, k.OnKeyGesture(Up, 0x41, 0x1E, 0x80, 1050));
            Assert.True(!k.Busy, "typing starts nothing");
            Tap(k, 1100);
            // typing between target events must not extend or split the press
            Assert.Equal(NitroGesture.None, k.OnKeyGesture(Down, 0x41, 0x1E, 0, 1150));
            Assert.Equal(NitroGesture.Tap1, k.Poll(1700));
        }

        [Test]
        public static void SameScanDifferentVk_Ignored()
        {
            NitroKey k = new NitroKey();
            k.OnKeyGesture(Down, 0x41, 0x75, 0, 1000);
            k.OnKeyGesture(Down, 0xFF, 0x76, 0, 1000);
            Assert.True(!k.Busy, "neither is the target key");
            Assert.Equal(NitroGesture.None, k.Poll(2000));
        }

        [Test]
        public static void SysKeyMessages_AlsoCount_OtherMessagesDoNot()
        {
            NitroKey k = new NitroKey();
            k.OnKeyGesture(0x104, 0xFF, 0x75, 0x1, 1000);
            k.OnKeyGesture(0x105, 0xFF, 0x75, 0x81, 1050);
            Assert.Equal(NitroGesture.Tap1, k.Poll(1700));
            k.OnKeyGesture(0x0102, 0xFF, 0x75, 0x1, 3000); // WM_CHAR is not a key event
            Assert.True(!k.Busy, "ignored");
        }

        [Test]
        public static void Reset_ForgetsAHeldKey()
        {
            NitroKey k = new NitroKey();
            k.OnKeyGesture(Down, 0xFF, 0x75, 0x1, 1000);
            Assert.True(k.Busy, "pressed");
            k.Reset();
            Assert.True(!k.Busy, "reset");
            Tap(k, 1200);
            Assert.Equal(NitroGesture.Tap1, k.Poll(1800));
        }

        [Test]
        public static void ExposesTheRecognizerOptions()
        {
            NitroKey k = new NitroKey();
            Assert.True(!k.Recognizer.LongPressEnabled, "long press is off by default (the key's keyup is unreliable)");
            k.Recognizer.DoubleTapEnabled = false;
            k.Recognizer.TripleTapEnabled = false;
            Tap(k, 1000);
            Assert.Equal(NitroGesture.Tap1, k.Poll(1300));
        }

        [Test]
        public static void SecondCandidateCode_FeedsTheSameGestures()
        {
            NitroKey k = new NitroKey(KeyCandidates.All);
            k.OnKeyGesture(Down, 0x41, 0xF5, 0x1, 1000); // scan 0xF5, any virtual key
            k.OnKeyGesture(Up, 0x41, 0xF5, 0x81, 1040);
            Assert.Equal(NitroGesture.Tap1, k.Poll(1600));
            NitroKey ours = new NitroKey();
            ours.OnKeyGesture(Down, 0x41, 0xF5, 0x1, 1000);
            Assert.True(!ours.Busy, "a code that is not a candidate is ignored");
            NitroKey none = new NitroKey(new KeyCandidate[0]);
            none.OnKeyGesture(Down, 0xFF, 0x75, 0x1, 1000);
            Assert.True(!none.Busy, "an empty candidate list listens for nothing");
        }

        [Test]
        public static void GestureAndPressApis_AreIndependent_ThePressDebounceIsUntouched()
        {
            // the wizard's key test counts presses with OnKey (700 ms debounce): gestures never change that
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "press counted");
            Assert.True(!k.Busy, "OnKey does not feed the gesture recognizer");
            Tap(k, 5000);
            Assert.True(k.Busy, "the gesture API does");
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 9000), "press counted again after the gap");
        }

        [Test]
        public static void KeyHook_ForGestures_InstallsAndDisposesCleanly()
        {
            SynchronizationContext old = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try
            {
                KeyHook h = KeyHook.ForGestures(new NitroKey(), delegate(NitroGesture g) { });
                Assert.True(h.IsInstalled, "installed");
                h.Reset();
                h.Dispose();
                Assert.True(!h.IsInstalled, "uninstalled");
                Assert.Throws<ArgumentNullException>(delegate { KeyHook.ForGestures(new NitroKey(), null); });
            }
            finally { SynchronizationContext.SetSynchronizationContext(old); }
        }
    }
}
