using System;

namespace Forja.Core
{
    // For a writer that keeps retrying while the firmware fails (the sentinel during a WMI outage):
    // a new firmware object per attempt means the guard's own "same failure" summary never builds
    // up, so every attempt would add two journal lines for as long as the outage lasts and push
    // the history out of the two small files. This wrapper is long-lived and shared by all attempts:
    //  - lines that are not firmware writes pass through untouched;
    //  - write lines that report a success pass through (and are preceded by the count of omitted ones);
    //  - write lines of a failing series: the first FreeLines pass, then one line per window
    //    (60 s), the rest are counted and summarized by the next line that passes.
    // "Failing" = the line says it failed or says the marker was kept (accepted but not confirmed).
    public sealed class ThrottledJournal : IJournal
    {
        public const int FreeLines = 4;          // two attempts = a "before" and an "after" each
        public const int WindowMs = 60000;

        private readonly IJournal inner;
        private readonly Func<int> clock;
        private readonly object sync = new object();
        private int freeUsed;
        private int lastEmitted;
        private bool emittedAny;
        private bool openBefore;
        private int suppressed;

        public ThrottledJournal(IJournal inner, Func<int> clock)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
            this.clock = clock ?? delegate { return Environment.TickCount; };
        }

        public void Write(string kind, string text)
        {
            bool isWrite = kind == JournalKind.WriteBefore || kind == JournalKind.WriteAfter;
            if (!isWrite)
            {
                inner.Write(kind, text);
                return;
            }
            lock (sync)
            {
                bool failing = kind == JournalKind.WriteBefore ? true : IsFailing(text);
                if (kind == JournalKind.WriteAfter && !failing)
                {
                    Flush();
                    freeUsed = 0;
                    emittedAny = false;
                    openBefore = false;
                    inner.Write(kind, text);
                    return;
                }
                int now = clock();
                // a "before" that passed lets its own "after" pass, so a pair is never cut in half
                bool pairOpen = kind == JournalKind.WriteAfter && openBefore;
                if (freeUsed < FreeLines || !emittedAny || pairOpen || unchecked(now - lastEmitted) >= WindowMs)
                {
                    Flush();
                    if (freeUsed < FreeLines) freeUsed++;
                    lastEmitted = now;
                    emittedAny = true;
                    openBefore = kind == JournalKind.WriteBefore;
                    inner.Write(kind, text);
                    return;
                }
                suppressed++;
            }
        }

        private void Flush()
        {
            if (suppressed > 0)
            {
                inner.Write(JournalKind.WriteAfter, "(" + suppressed + " linhas de escrita repetidas omitidas)");
                suppressed = 0;
            }
        }

        private static bool IsFailing(string text)
        {
            if (text == null) return true;
            return text.Contains(": falhou") || text.Contains("marcador mantido") || text.Contains("marcador não pôde");
        }
    }
}
