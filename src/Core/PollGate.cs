using System;
using System.Threading;

namespace Forja.Core
{
    // The on/off switch of a polling timer that must run only while something is undecided (the gesture
    // recognizer between a press and its final answer). Pure: the caller supplies "is there work" and
    // "turn the timer on / off". Safe to call Ensure from one thread (the key hook) while Tick runs on
    // another (the timer's).
    //
    // The invariant: if work shows up at ANY moment, the timer ends up ON. Tick therefore stops the timer
    // BEFORE it releases the "running" flag and looks at the work once more afterwards. (Releasing the flag
    // first lets an Ensure win in between and have its freshly started timer stopped by the late stop,
    // leaving the flag set with the timer off for good.)
    public sealed class PollGate
    {
        private readonly Func<bool> busy;
        private readonly Action<bool> setTimer;
        private int running;

        public PollGate(Func<bool> busy, Action<bool> setTimer)
        {
            if (busy == null) throw new ArgumentNullException("busy");
            if (setTimer == null) throw new ArgumentNullException("setTimer");
            this.busy = busy;
            this.setTimer = setTimer;
        }

        public bool Running { get { return Thread.VolatileRead(ref running) == 1; } }

        // Work may have appeared: start the timer if it is not running.
        public void Ensure()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            setTimer(true);
        }

        // The timer fired: do the poll, and stop the timer when nothing is left to decide.
        public void Tick(Action poll)
        {
            if (poll != null) poll();
            if (busy()) return;
            setTimer(false);
            Interlocked.Exchange(ref running, 0);
            if (busy()) Ensure(); // something arrived while stopping
        }
    }
}
