using System;
using System.Collections.Generic;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The key hook's dispatch path (event -> recognizer -> UI post -> gesture) and the polling timer's
    // on/off switch, without installing a system hook: KeyHook.ForTests takes events through Process.
    public static class HookDispatchTests
    {
        private const int Down = 0x100;
        private const int Up = 0x101;

        // Runs the posted callback right away, on the posting thread (the "UI thread" of a test).
        private sealed class InlineContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                d(state);
            }
        }

        private sealed class Sink
        {
            private readonly List<NitroGesture> got = new List<NitroGesture>();

            public void Add(NitroGesture g)
            {
                lock (got) got.Add(g);
            }

            public int Count
            {
                get { lock (got) return got.Count; }
            }

            public NitroGesture At(int i)
            {
                lock (got) return got[i];
            }
        }

        private static KeyHook Hook(Sink sink)
        {
            return KeyHook.ForTests(new NitroKey(), delegate(NitroGesture g) { sink.Add(g); }, new InlineContext());
        }

        // A real press: down, up, down, up within about 90 ms, stamped with the real tick count (the
        // polling timer reads Environment.TickCount).
        private static void Burst(KeyHook h)
        {
            int t = Environment.TickCount;
            h.Process(Down, 0xFF, 0x75, 0x1, t);
            h.Process(Up, 0xFF, 0x75, 0x81, unchecked(t + 20));
            h.Process(Down, 0xFF, 0x75, 0x1, unchecked(t + 40));
            h.Process(Up, 0xFF, 0x75, 0x81, unchecked(t + 60));
        }

        private static bool WaitFor(Func<bool> done, int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount - end < 0)
            {
                if (done()) return true;
                Thread.Sleep(10);
            }
            return done();
        }

        // ---- PollGate: the timer switch ----

        [Test]
        public static void PollGate_StartsOnWork_StopsWhenIdle()
        {
            bool timer = false;
            bool busy = false;
            PollGate g = new PollGate(delegate { return busy; }, delegate(bool on) { timer = on; });
            busy = true;
            g.Ensure();
            Assert.True(timer && g.Running, "started");
            g.Ensure(); // already running: nothing changes
            Assert.True(timer && g.Running, "still running");
            g.Tick(null);
            Assert.True(timer && g.Running, "still busy: keeps running");
            busy = false;
            g.Tick(null);
            Assert.True(!timer && !g.Running, "idle: stopped");
            busy = true;
            g.Ensure();
            Assert.True(timer && g.Running, "restarts after a stop");
        }

        [Test]
        public static void PollGate_WorkArrivingWhileStopping_NeverLeavesTheTimerOff()
        {
            // The race of the review: the key hook thread sees a new press exactly while the timer thread is
            // stopping. Whatever the interleaving, a running flag must never sit set with the timer off.
            for (int where = 0; where < 2; where++)
            {
                bool timer = false;
                bool busy = false;
                PollGate gate = null;
                int stops = 0;
                int busyCalls = 0;
                int at = where;
                gate = new PollGate(
                    delegate
                    {
                        busyCalls++;
                        // second look at the work, after the stop and the release: a press arrives right here
                        if (at == 1 && busyCalls == 2) { busy = true; gate.Ensure(); }
                        return busy;
                    },
                    delegate(bool on)
                    {
                        timer = on;
                        // first stop: a press arrives in the middle of stopping
                        if (!on) { stops++; if (at == 0 && stops == 1) { busy = true; gate.Ensure(); } }
                    });
                busy = true;
                gate.Ensure();
                busy = false;
                busyCalls = 0;
                gate.Tick(null);
                Assert.True(busy, "the press did arrive (case " + where + ")");
                Assert.True(timer, "timer ON for pending work (case " + where + ")");
                Assert.True(gate.Running, "flag set only with the timer on (case " + where + ")");
            }
        }

        // ---- KeyHook dispatch (no system hook) ----

        [Test]
        public static void Hook_Burst_ReachesTheGestureCallback_ThroughTheRealTimer()
        {
            Sink sink = new Sink();
            using (KeyHook h = Hook(sink))
            {
                Burst(h);
                Assert.True(WaitFor(delegate { return sink.Count == 1; }, 3000), "one gesture within 3 s");
                Assert.Equal(NitroGesture.Tap1, sink.At(0));
                Assert.True(WaitFor(delegate { return !h.PollingRunning; }, 1000), "the timer stops when idle");
                // the timer must come back for the next press
                Burst(h);
                Burst2Wait(h, sink, 2);
                Assert.Equal(NitroGesture.Tap1, sink.At(1));
            }
        }

        private static void Burst2Wait(KeyHook h, Sink sink, int total)
        {
            Assert.True(WaitFor(delegate { return sink.Count == total; }, 3000), "gesture number " + total);
        }

        [Test]
        public static void Hook_TwoQuickBursts_AreADoubleTap()
        {
            Sink sink = new Sink();
            using (KeyHook h = Hook(sink))
            {
                Burst(h);
                Thread.Sleep(200);
                Burst(h);
                Assert.True(WaitFor(delegate { return sink.Count == 1; }, 3000), "one gesture");
                Assert.Equal(NitroGesture.Tap2, sink.At(0));
            }
        }

        [Test]
        public static void Hook_OtherKeys_StartNothing()
        {
            Sink sink = new Sink();
            using (KeyHook h = Hook(sink))
            {
                int t = Environment.TickCount;
                h.Process(Down, 0x41, 0x1E, 0, t);
                h.Process(Up, 0x41, 0x1E, 0x80, t + 30);
                Assert.True(!h.PollingRunning, "no timer for another key");
                Assert.True(!h.Filter.Busy, "nothing tracked");
                Thread.Sleep(100);
                Assert.Equal(0, sink.Count);
            }
        }

        [Test]
        public static void Hook_DisposedWithATickInFlight_DeliversNothingAndDoesNotThrow()
        {
            Sink sink = new Sink();
            KeyHook h = Hook(sink);
            Burst(h);
            h.Dispose();
            Thread.Sleep(600);
            Assert.Equal(0, sink.Count);
            h.Process(Down, 0xFF, 0x75, 0x1, Environment.TickCount); // late event after Dispose: harmless
            h.Dispose();
        }

        [Test]
        public static void Hook_ManyBurstsAtTheWindowEdge_NeverLeaveTheTimerStuck()
        {
            // Presses spaced around the 350 ms edge, from a second thread while the timer thread resolves:
            // afterwards the hook must be idle and still deliver the next press.
            Sink sink = new Sink();
            using (KeyHook h = Hook(sink))
            {
                Random rnd = new Random(7);
                for (int i = 0; i < 8; i++)
                {
                    // two presses whose gap sits on the 350 ms edge (the timer thread decides a single tap or
                    // a double tap right about when the second press arrives), then a quiet pause
                    Burst(h);
                    Thread.Sleep(280 + rnd.Next(0, 110));
                    Burst(h);
                    Thread.Sleep(600);
                }
                Assert.True(WaitFor(delegate { return !h.Filter.Busy && !h.PollingRunning; }, 5000), "idle again");
                int before = sink.Count;
                Assert.True(before >= 1, "gestures came out");
                Burst(h);
                Assert.True(WaitFor(delegate { return sink.Count == before + 1; }, 3000), "the next press still delivers");
            }
        }

        // ---- recognizer under two threads ----

        [Test]
        public static void Recognizer_FeedAndPollFromTwoThreads_StaysConsistent()
        {
            GestureRecognizer r = new GestureRecognizer();
            int clock = 100000;
            int decided = 0;
            int stop = 0;
            Exception failure = null;
            Thread poller = new Thread(delegate()
            {
                try
                {
                    while (Thread.VolatileRead(ref stop) == 0)
                    {
                        NitroGesture g = r.Poll(Thread.VolatileRead(ref clock));
                        if (g != NitroGesture.None) Interlocked.Increment(ref decided);
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            poller.Start();
            try
            {
                for (int i = 0; i < 2000; i++)
                {
                    int t = Thread.VolatileRead(ref clock);
                    NitroGesture a = r.Feed(true, t);
                    NitroGesture b = r.Feed(false, t + 30);
                    if (a != NitroGesture.None) Interlocked.Increment(ref decided);
                    if (b != NitroGesture.None) Interlocked.Increment(ref decided);
                    Thread.VolatileWrite(ref clock, t + 30 + (i % 3 == 0 ? 500 : 200));
                }
            }
            finally
            {
                Thread.VolatileWrite(ref stop, 1);
                poller.Join();
            }
            Assert.Null(failure);
            NitroGesture last = r.Poll(Thread.VolatileRead(ref clock) + 10000);
            if (last != NitroGesture.None) decided++;
            Assert.True(!r.Busy, "everything decided");
            Assert.True(decided > 0 && decided <= 2000, "decided gestures: " + decided);
        }
    }
}
