using System;
using System.Runtime.InteropServices;
using Forja.Core;

namespace Forja.Platform
{
    // Total CPU busy percentage since the previous call, from GetSystemTimes (kernel32: no counter,
    // no thread, a few microseconds). Used on the firmware tick while the window is hidden, so a
    // mode switch can still be judged against a KNOWN load. Not thread-safe.
    public sealed class SystemLoadReader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        private bool has;
        private long idle0, kernel0, user0;

        private static long Ticks(FILETIME f)
        {
            return ((long)f.High << 32) | f.Low;
        }

        // Forget the previous reading (the next call only primes): after a long gap an average over
        // the whole gap would be a lie about "now".
        public void Reset()
        {
            has = false;
        }

        // Null on the first call after construction/Reset or when the call fails.
        public double? ReadPct()
        {
            try
            {
                FILETIME i, k, u;
                if (!GetSystemTimes(out i, out k, out u)) return null;
                long idle1 = Ticks(i), kernel1 = Ticks(k), user1 = Ticks(u);
                double? r = null;
                if (has) r = CpuMath.BusyPct(idle0, kernel0, user0, idle1, kernel1, user1);
                idle0 = idle1;
                kernel0 = kernel1;
                user0 = user1;
                has = true;
                return r;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
