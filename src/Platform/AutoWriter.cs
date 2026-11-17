using System;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // One "fans to Auto" attempt on a throw-away MTA thread with its own firmware object and a time
    // limit (ManagementObject.InvokeMethod has none). The firmware is created INSIDE the attempt, a
    // new one every time, so a WMI class that was missing or a provider that restarted is found again
    // by the next attempt instead of being cached as unavailable. A thread that outlives its limit is
    // abandoned (it is a background thread); at most MaxHung of them are tolerated at once, after
    // which attempts are refused until one of them comes back.
    public sealed class AutoWriter
    {
        public const int MaxHung = 3;
        // At the cap an attempt is still made once a minute (a fresh connection may work while the old
        // calls stay stuck), up to an absolute limit of stuck threads.
        public const int SlowRetryMs = 60000;
        public const int AbsoluteMaxHung = 8;

        private readonly Func<GuardedFirmware> create;
        private readonly int timeoutMs;
        private readonly Func<int> clock;
        private int hung;
        private int lastSlowTry;
        private bool slowTried;

        public AutoWriter(Func<GuardedFirmware> create, int timeoutMs) : this(create, timeoutMs, null)
        {
        }

        public AutoWriter(Func<GuardedFirmware> create, int timeoutMs, Func<int> clock)
        {
            if (create == null) throw new ArgumentNullException("create");
            this.create = create;
            this.timeoutMs = timeoutMs;
            this.clock = clock ?? delegate { return Environment.TickCount; };
        }

        public int Hung { get { return Thread.VolatileRead(ref hung); } }

        // true = Auto written and accepted by the firmware (confirmed or not: the guard's marker says which).
        public bool Try()
        {
            if (Hung >= MaxHung)
            {
                int now = clock();
                if (Hung >= AbsoluteMaxHung) return false;
                if (!slowTried)
                {
                    // just reached the cap: the first extra attempt comes a minute from now
                    slowTried = true;
                    lastSlowTry = now;
                    return false;
                }
                if (unchecked(now - lastSlowTry) < SlowRetryMs) return false;
                lastSlowTry = now;
            }
            else
            {
                slowTried = false;
            }
            bool ok = false;
            int state = 0; // 0 running, 1 finished, 2 abandoned
            Thread t = new Thread(delegate()
            {
                try
                {
                    GuardedFirmware fw = create();
                    ok = fw != null && fw.SetFanAutoSafety();
                }
                catch (Exception)
                {
                    ok = false;
                }
                finally
                {
                    // if the caller already gave up on this thread it is no longer "hung"
                    if (Interlocked.Exchange(ref state, 1) == 2) Interlocked.Decrement(ref hung);
                }
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.SentinelAuto";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            if (t.Join(timeoutMs)) return ok;
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0) Interlocked.Increment(ref hung);
            else return ok; // it finished right at the limit
            return false;
        }
    }
}
