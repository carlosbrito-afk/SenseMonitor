namespace Forja.Core
{
    // Paces the 1 Hz history: Due(now) says how many samples to add so the history keeps one
    // sample per second whichever tick calls it (1 s timer while the Monitor page is open, the
    // 2 s or 12 s firmware tick while the window is hidden). Phase is kept across calls; a long pause
    // (suspend, a stalled UI) adds at most MaxBurst samples and re-syncs. Pure (now = TickCount).
    public sealed class SamplePacer
    {
        public const int PeriodMs = 1000;
        // Covers the 12 s firmware tick used while the window is hidden and fans are Automático.
        public const int MaxBurst = 15;

        private bool started;
        private int last;

        public int Due(int nowTick)
        {
            if (!started)
            {
                started = true;
                last = nowTick;
                return 1;
            }
            int elapsed = unchecked(nowTick - last);
            if (elapsed < 0)
            {
                last = nowTick;
                return 0;
            }
            int n = elapsed / PeriodMs;
            if (n <= 0) return 0;
            if (n > MaxBurst)
            {
                last = nowTick;
                return MaxBurst;
            }
            last = unchecked(last + n * PeriodMs);
            return n;
        }
    }
}
