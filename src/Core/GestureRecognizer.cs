using System;
using System.Collections.Generic;
using System.Text;

namespace Forja.Core
{
    public enum NitroGesture
    {
        None = 0,
        Tap1 = 1,
        Tap2 = 2,
        Tap3 = 3,
        Long = 4
    }

    // Turns the raw events of ONE key (the NitroSense key) into gestures. Pure: no clock, no thread,
    // no Windows. The caller passes event times (ms, wrap-safe) and polls with the current time while
    // Busy so decisions that only time can make (a lone tap is final once no second tap follows) get
    // taken. Every method is safe to call from any thread.
    //
    // Why a recognizer and not a debounce: the OEM key does not send one clean down/up per press. It
    // sends a make/break BURST (down, up, down, up...) within about 100 ms. So:
    //   - events closer than BurstMs belong to ONE press (a "cluster");
    //   - clusters closer than MultiTapMs (end of one to start of the next) are taps of ONE gesture:
    //     1 tap, 2 taps, 3 taps; a 4th tap, or a key that keeps emitting pairs, is "held" and does
    //     nothing (never a surprise action);
    //   - long press exists ONLY when LongPressEnabled: a down that is not followed by an up (or that
    //     auto-repeats) for LongPressMs. The key's keyup is NOT trustworthy (bursts, lost events), so
    //     it is off by default (see docs in the settings card). Held without long press = nothing.
    // PRIVACY: the only state is a handful of timestamps of the target key. Nothing about any other key.
    public sealed class GestureRecognizer
    {
        // Events closer than this are the same press.
        public const int BurstMs = 120;
        // A tap that starts within this of the end of the previous one continues the gesture.
        public const int MultiTapMs = 350;
        public const int LongPressMs = 900;
        // A key that looks held (last event was a down) but is silent this long lost its keyup.
        public const int StuckMs = 1500;
        public const int MaxTaps = 3;
        private const int MaxTrace = 8;

        private readonly object gate = new object();

        // false: a held key does nothing (the default; the key's keyup is not reliable).
        private bool longPressEnabled;
        private bool doubleEnabled = true;
        private bool tripleEnabled = true;

        private bool open;          // a press cluster is in progress
        private bool lastDown;      // the last event of the cluster was a down (key may still be held)
        private bool longFired;     // this hold already produced Long
        private bool heldSwallow;   // this hold is over LongPressMs but Long is off: ends with nothing
        private int clusterStart;
        private int lastEvent;
        private int taps;           // completed clusters of the current gesture
        private bool overflow;      // more than MaxTaps clusters: ends with nothing
        private int lastClusterEnd;
        private int chainStart;
        private readonly List<int[]> spans = new List<int[]>();
        private string lastTrace = "";

        // Long press: down held for LongPressMs. Leave off unless the key's keyup was verified.
        public bool LongPressEnabled
        {
            get { lock (gate) { return longPressEnabled; } }
            set { lock (gate) { longPressEnabled = value; } }
        }

        // When no action is mapped to a double (or triple) tap there is no reason to wait for one:
        // the shorter gesture then fires as soon as its press ends.
        public bool DoubleTapEnabled
        {
            get { lock (gate) { return doubleEnabled; } }
            set { lock (gate) { doubleEnabled = value; } }
        }

        public bool TripleTapEnabled
        {
            get { lock (gate) { return tripleEnabled; } }
            set { lock (gate) { tripleEnabled = value; } }
        }

        // True while a press or a tap sequence is undecided: the caller keeps polling.
        public bool Busy
        {
            get { lock (gate) { return open || taps > 0; } }
        }

        // One line about the last decided gesture ("2 toques: 0-90, 310-390 ms"): timing only, for
        // tuning against the real key. Never mentions another key.
        public string LastTrace
        {
            get { lock (gate) { return lastTrace; } }
        }

        // down = key went down (WM_KEYDOWN / SYSKEYDOWN), false = key went up. Returns a gesture that
        // the passage of time decided before this event (a previous sequence that ended).
        public NitroGesture Feed(bool down, int time)
        {
            lock (gate)
            {
                NitroGesture g = Process(time);
                if (!open)
                {
                    if (!down) return g; // a keyup with no press in progress (after Reset, sleep): nothing
                    open = true;
                    lastDown = true;
                    longFired = false;
                    heldSwallow = false;
                    clusterStart = time;
                    lastEvent = time;
                    if (taps == 0)
                    {
                        chainStart = time;
                        spans.Clear();
                    }
                    return g;
                }
                lastEvent = time;
                lastDown = down;
                return g;
            }
        }

        // Time moved on: returns a gesture that is now decided, else None.
        public NitroGesture Poll(int now)
        {
            lock (gate)
            {
                return Process(now);
            }
        }

        // Forget everything (resume from sleep, session unlock: a keyup may never have arrived).
        public void Reset()
        {
            lock (gate)
            {
                open = false;
                lastDown = false;
                longFired = false;
                heldSwallow = false;
                taps = 0;
                overflow = false;
                spans.Clear();
            }
        }

        private static int Since(int now, int then)
        {
            int d = unchecked(now - then);
            return d < 0 ? 0 : d;
        }

        private NitroGesture Process(int now)
        {
            NitroGesture g = NitroGesture.None;
            if (open)
            {
                int quiet = Since(now, lastEvent);
                bool heldNow = lastDown && quiet < StuckMs;
                if (heldNow && !longFired && !heldSwallow && Since(now, clusterStart) >= LongPressMs)
                {
                    if (longPressEnabled)
                    {
                        longFired = true;
                        Trace("long", clusterStart, now);
                        ClearChain(); // taps before the hold are part of it: the gesture is the long press
                        return NitroGesture.Long;
                    }
                    heldSwallow = true;
                }
                bool close = lastDown ? quiet >= StuckMs : quiet > BurstMs;
                if (close) g = CloseCluster();
            }
            if (g == NitroGesture.None && !open && taps > 0)
            {
                if (Since(now, lastClusterEnd) > WaitMs()) g = Resolve();
            }
            return g;
        }

        // How long after the last tap before the sequence is final. -1: right away.
        private int WaitMs()
        {
            if (overflow) return MultiTapMs;
            if (taps == 1 && !doubleEnabled && !tripleEnabled) return -1;
            if (taps == 2 && !tripleEnabled) return -1;
            return MultiTapMs;
        }

        private NitroGesture CloseCluster()
        {
            open = false;
            bool alreadyLong = longFired;
            bool swallow = heldSwallow;
            bool longish = Since(lastEvent, clusterStart) >= LongPressMs;
            longFired = false;
            heldSwallow = false;
            if (alreadyLong)
            {
                ClearChain();
                return NitroGesture.None;
            }
            if (longish || swallow)
            {
                bool fire = longPressEnabled && longish && !swallow;
                ClearChain();
                if (fire)
                {
                    Trace("long", clusterStart, lastEvent);
                    return NitroGesture.Long;
                }
                return NitroGesture.None;
            }
            taps++;
            lastClusterEnd = lastEvent;
            if (spans.Count < MaxTrace) spans.Add(new int[] { Since(clusterStart, chainStart), Since(lastEvent, chainStart) });
            if (taps > MaxTaps) overflow = true;
            return NitroGesture.None;
        }

        private NitroGesture Resolve()
        {
            int n = taps;
            bool bad = overflow;
            string t = Describe(bad ? "toques demais (ignorado)" : n + (n == 1 ? " toque" : " toques"));
            ClearChain();
            lastTrace = t;
            if (bad) return NitroGesture.None;
            return n == 1 ? NitroGesture.Tap1 : (n == 2 ? NitroGesture.Tap2 : NitroGesture.Tap3);
        }

        private string Describe(string head)
        {
            StringBuilder sb = new StringBuilder(head);
            sb.Append(": ");
            for (int i = 0; i < spans.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(spans[i][0]).Append('-').Append(spans[i][1]);
            }
            sb.Append(" ms");
            return sb.ToString();
        }

        private void Trace(string head, int start, int end)
        {
            lastTrace = head + ": " + Since(end, start) + " ms segurada";
        }

        private void ClearChain()
        {
            taps = 0;
            overflow = false;
            spans.Clear();
        }
    }
}
