using System;
using System.Diagnostics;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // CPU load for the performance test: N background threads running PerfKernel in batches and
    // counting finished work units in padded per-thread slots (no shared cache line, no locks).
    // Each Start makes a new "generation" with its own stop flag, so a worker that overstayed a
    // Stop can never be revived by the next Start. The flag is checked after every batch
    // (about half a millisecond of work, far under the 50 ms limit).
    public sealed class PerfLoad : IPerfLoad
    {
        private const int Batch = 64;       // units between a counter update / stop check
        private const int Stride = 16;      // longs per slot: 128 bytes apart
        public const int JoinTotalMs = 2000;
        private static ulong sink;           // keeps the JIT from discarding the work

        private sealed class Generation
        {
            public volatile bool Stop;
            public long[] Slots;
            public Thread[] Threads;
        }

        private Generation current;
        private long[] counters = new long[Stride];
        private int timedOut;

        // True when a Stop could not see every worker end (they are background threads and
        // already told to stop; they die with the process).
        public bool LastStopTimedOut
        {
            get { return Thread.VolatileRead(ref timedOut) != 0; }
        }

        public void Start(int count)
        {
            Stop();
            if (count < 1) count = 1;
            Generation g = new Generation();
            g.Slots = new long[count * Stride];
            g.Threads = new Thread[count];
            counters = g.Slots;
            Thread.VolatileWrite(ref timedOut, 0);
            for (int i = 0; i < count; i++)
            {
                int slot = i * Stride;
                ulong seed = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
                Thread t = new Thread(delegate() { Work(g, slot, seed); });
                t.IsBackground = true;
                t.Name = "SenseMonitor.PerfLoad" + i;
                g.Threads[i] = t;
            }
            current = g;
            foreach (Thread t in g.Threads) t.Start();
        }

        private static void Work(Generation g, int slot, ulong state)
        {
            long done = 0;
            long[] slots = g.Slots;
            while (!g.Stop)
            {
                for (int b = 0; b < Batch; b++) state = PerfKernel.Unit(state);
                done += Batch;
                Thread.VolatileWrite(ref slots[slot], done);
            }
            sink ^= state;
        }

        // Non-blocking: only raises the flag (watchdog and crash path use this).
        public void RequestStop()
        {
            Generation g = current;
            if (g != null) g.Stop = true;
        }

        // Idempotent and safe from any thread; waits at most JoinTotalMs in total.
        public void Stop()
        {
            Generation g = Interlocked.Exchange(ref current, null);
            if (g == null) return;
            g.Stop = true;
            Stopwatch w = Stopwatch.StartNew();
            foreach (Thread t in g.Threads)
            {
                int left = (int)Math.Max(1, JoinTotalMs - w.ElapsedMilliseconds);
                try
                {
                    if (!t.Join(left)) Thread.VolatileWrite(ref timedOut, 1);
                }
                catch (Exception) { }
            }
        }

        public long Units
        {
            get
            {
                long[] slots = counters;
                long sum = 0;
                for (int i = 0; i < slots.Length; i += Stride) sum += Thread.VolatileRead(ref slots[i]);
                return sum;
            }
        }
    }
}
