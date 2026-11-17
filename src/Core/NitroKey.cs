using System;

namespace Forja.Core
{
    // Recognises the dedicated NitroSense / PredatorSense hardware key. This OEM key emits repeated
    // make/break pairs while held, so a press is debounced by time: a target keydown counts
    // only if no event of THAT target (down OR up) was seen in the last DebounceMs. PRIVACY: the only
    // state is one timestamp per CANDIDATE code (KeyCandidates, from the research). Nothing about any
    // other key is ever stored or logged.
    //
    // Two ways to use one instance:
    //  - OnKey(...) / LastFired: one "press" per physical press (the 700 ms debounce above). The key test of
    //    the environment wizard counts presses with this; KeyHook(filter, Action) uses it.
    //  - OnKeyGesture(...) / Poll(...): tap, double tap, triple tap, long press (GestureRecognizer) for the
    //    tray's key gestures; KeyHook.ForGestures uses it. Both feed from the same candidate codes.
    public class NitroKey
    {
        private const int WM_KEYDOWN = 0x100;
        private const int WM_KEYUP = 0x101;
        private const int WM_SYSKEYDOWN = 0x104;
        private const int WM_SYSKEYUP = 0x105;

        public const int DebounceMs = 700;

        private readonly object gate = new object();
        private readonly KeyCandidate[] candidates;
        private readonly bool[] seen;
        private readonly uint[] lastEventTime;
        private KeyCandidate lastFired;
        private readonly GestureRecognizer recognizer = new GestureRecognizer();

        // The code this app has always listened for.
        public NitroKey() : this(new KeyCandidate[] { KeyCandidates.Ours })
        {
        }

        // Only these codes are ever looked at; an empty or null list listens for nothing.
        public NitroKey(KeyCandidate[] candidates)
        {
            this.candidates = candidates ?? new KeyCandidate[0];
            seen = new bool[this.candidates.Length];
            lastEventTime = new uint[this.candidates.Length];
        }

        // The candidate whose press fired last (null before the first). Read it right after OnKey returned true.
        public KeyCandidate LastFired
        {
            get { lock (gate) { return lastFired; } }
        }

        public GestureRecognizer Recognizer { get { return recognizer; } }

        // A press or tap sequence is undecided: poll until this is false (gesture API).
        public bool Busy { get { return recognizer.Busy; } }

        // Index of the candidate this event belongs to, or -1 (any other key: nothing is looked at).
        private int Match(int msg, int vk, int scan, out bool isDown)
        {
            isDown = false;
            int idx = -1;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Matches(vk, scan))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return -1;
            isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            return isDown || isUp ? idx : -1;
        }

        // Gesture API. Returns a gesture the passage of time decided before this event (usually None:
        // gestures come out of Poll). Safe to call from the hook thread.
        public NitroGesture OnKeyGesture(int msg, int vk, int scan, int flags, int time)
        {
            bool down;
            if (Match(msg, vk, scan, out down) < 0) return NitroGesture.None;
            return recognizer.Feed(down, time);
        }

        public NitroGesture Poll()
        {
            return recognizer.Poll(Environment.TickCount);
        }

        public NitroGesture Poll(int now)
        {
            return recognizer.Poll(now);
        }

        // Uses the local tick count as the time source.
        public bool OnKey(int msg, int vk, int scan, int flags)
        {
            return OnKey(msg, vk, scan, flags, Environment.TickCount);
        }

        // time = KBDLLHOOKSTRUCT.time (ms). Safe to call from the hook thread.
        public bool OnKey(int msg, int vk, int scan, int flags, int time)
        {
            int idx = -1;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Matches(vk, scan))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return false;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!isDown && !isUp) return false;
            uint now = unchecked((uint)time);
            lock (gate)
            {
                bool fire = isDown && (!seen[idx] || unchecked(now - lastEventTime[idx]) >= DebounceMs);
                seen[idx] = true;
                lastEventTime[idx] = now;
                if (fire) lastFired = candidates[idx];
                return fire;
            }
        }

        // Forget the last target event (call after resume from sleep / session unlock).
        public void Reset()
        {
            lock (gate)
            {
                for (int i = 0; i < seen.Length; i++) seen[i] = false;
            }
            recognizer.Reset();
        }
    }

    // Counts presses for the key test: the first candidate that answers is the key; presses of the
    // other candidate afterwards are noise. Pure, no timing (the debounce is NitroKey's).
    public sealed class KeyTestState
    {
        public const int Target = 3;

        private int presses;
        private KeyCandidate chosen;

        public int Presses { get { return presses; } }
        public KeyCandidate Chosen { get { return chosen; } }
        public bool Complete { get { return presses >= Target; } }

        public void Pressed(KeyCandidate c)
        {
            if (c == null || Complete) return;
            if (chosen == null) chosen = c;
            if (!object.ReferenceEquals(chosen, c)) return;
            presses++;
        }

        public void Reset()
        {
            presses = 0;
            chosen = null;
        }
    }
}
