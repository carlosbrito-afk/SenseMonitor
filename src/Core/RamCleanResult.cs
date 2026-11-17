using System;
using System.Globalization;

namespace Forja.Core
{
    // Physical memory split the way Task Manager shows it. -1 = could not be read.
    public class MemorySnapshot
    {
        public long Total = -1;
        public long Available = -1;   // standby + free + zero
        public long Standby = -1;
        public long Modified = -1;
        public long Free = -1;        // free + zero

        // Task Manager "Em uso": total - available - modified (modified unknown: total - available).
        public long InUse
        {
            get
            {
                if (Total <= 0 || Available < 0) return -1;
                long v = Total - Available - (Modified > 0 ? Modified : 0);
                return v < 0 ? -1 : v;
            }
        }
    }

    // One RAMMap-style cleaning: snapshots and the NTSTATUS of each step (0 = success).
    public class RamCleanResult
    {
        public MemorySnapshot Before = new MemorySnapshot();
        public MemorySnapshot BeforePurge = new MemorySnapshot();
        public MemorySnapshot After = new MemorySnapshot();
        public bool Privileged;
        public int EmptyStatus = -1;
        public bool UsedFallback;
        public int FlushStatus = -1;
        public int PurgeStatus = -1;

        // How much Task Manager's "Em uso" dropped (0 if unknown or it grew).
        public long InUseDrop
        {
            get
            {
                long a = Before.InUse;
                long b = After.InUse;
                if (a < 0 || b < 0) return 0;
                return Math.Max(0L, a - b);
            }
        }

        // Standby cache emptied by the purge; -1 if unknown.
        public long StandbyCleared
        {
            get
            {
                if (BeforePurge.Standby < 0 || After.Standby < 0) return -1;
                return Math.Max(0L, BeforePurge.Standby - After.Standby);
            }
        }

        // Every system command was refused (e.g. missing privilege): nothing was cleaned.
        public bool AllFailed
        {
            get { return EmptyStatus != 0 && FlushStatus != 0 && PurgeStatus != 0; }
        }

        public string Steps
        {
            get
            {
                return "priv=" + (Privileged ? "ok" : "no")
                    + " empty=" + Status(EmptyStatus) + (UsedFallback ? "(fallback)" : "")
                    + " flush=" + Status(FlushStatus)
                    + " purge=" + Status(PurgeStatus);
            }
        }

        private static string Status(int s)
        {
            return s == 0 ? "ok" : "0x" + unchecked((uint)s).ToString("X8", CultureInfo.InvariantCulture);
        }
    }
}
