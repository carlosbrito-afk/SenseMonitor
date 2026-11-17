using System;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Pure parts of the RAM cleaner. Nothing here trims or purges real memory.
    public static class RamCleanTests
    {
        private const long GiB = 1024L * 1024L * 1024L;

        private static byte[] MemoryListBuffer(int ptrSize, long[] counts)
        {
            // SYSTEM_MEMORY_LIST_INFORMATION: Zero, Free, Modified, ModifiedNoWrite, Bad,
            // PageCountByPriority[8], RepurposedPagesByPriority[8], ModifiedPageCountPageFile
            byte[] b = new byte[ptrSize * 22];
            for (int i = 0; i < counts.Length; i++)
            {
                byte[] v = ptrSize == 8 ? BitConverter.GetBytes(counts[i]) : BitConverter.GetBytes((int)counts[i]);
                Array.Copy(v, 0, b, i * ptrSize, ptrSize);
            }
            return b;
        }

        [Test]
        public static void ParseMemoryLists_x64()
        {
            long[] c = new long[22];
            c[0] = 3; c[1] = 7;                           // zero + free = 10 pages
            c[2] = 20; c[3] = 2;                          // modified + modified-no-write = 22 pages
            c[4] = 999;                                   // bad: ignored
            for (int i = 0; i < 8; i++) c[5 + i] = i + 1; // standby 1..8 = 36 pages
            c[13] = 777; c[21] = 555;                     // repurposed / pagefile counters: ignored
            MemorySnapshot s = new MemorySnapshot();
            Assert.True(RamCleaner.ParseMemoryLists(MemoryListBuffer(8, c), 8, 4096, s), "parsed");
            Assert.Equal(10L * 4096, s.Free);
            Assert.Equal(22L * 4096, s.Modified);
            Assert.Equal(36L * 4096, s.Standby);
        }

        [Test]
        public static void ParseMemoryLists_x86Layout()
        {
            long[] c = new long[22];
            for (int i = 0; i < 8; i++) c[5 + i] = 10;
            MemorySnapshot s = new MemorySnapshot();
            Assert.True(RamCleaner.ParseMemoryLists(MemoryListBuffer(4, c), 4, 4096, s), "parsed");
            Assert.Equal(80L * 4096, s.Standby);
        }

        [Test]
        public static void ParseMemoryLists_ShortBuffer_LeavesUnknown()
        {
            MemorySnapshot s = new MemorySnapshot();
            Assert.True(!RamCleaner.ParseMemoryLists(null, 8, 4096, s), "null");
            Assert.True(!RamCleaner.ParseMemoryLists(new byte[8 * 12], 8, 4096, s), "short");
            Assert.Equal(-1L, s.Standby);
        }

        [Test]
        public static void Snapshot_InUse_LikeTaskManager()
        {
            MemorySnapshot s = new MemorySnapshot();
            s.Total = 32 * GiB;
            s.Available = 18 * GiB;
            s.Modified = GiB;
            Assert.Equal(13 * GiB, s.InUse);  // total - available - modified
            s.Modified = -1;
            Assert.Equal(14 * GiB, s.InUse);  // modified unknown: total - available
            s.Available = -1;
            Assert.Equal(-1L, s.InUse);
        }

        [Test]
        public static void Result_Drops_ClampAndUnknown()
        {
            RamCleanResult r = new RamCleanResult();
            Assert.Equal(0L, r.InUseDrop);
            Assert.Equal(-1L, r.StandbyCleared);
            r.Before = Snap(32, 18, 1, 3);
            r.BeforePurge = Snap(32, 21, 0, 5);
            r.After = Snap(32, 21, 0, 0);
            Assert.Equal(2 * GiB, r.InUseDrop);       // (32-18-1)=13 -> (32-21-0)=11
            Assert.Equal(5 * GiB, r.StandbyCleared);  // purged 5 -> 0
            r.After = Snap(32, 17, 1, 0);             // in use grew meanwhile
            Assert.Equal(0L, r.InUseDrop);
        }

        [Test]
        public static void Result_Steps()
        {
            RamCleanResult r = new RamCleanResult();
            r.Privileged = true;
            r.EmptyStatus = 0;
            r.FlushStatus = 0;
            r.PurgeStatus = unchecked((int)0xC0000061);
            Assert.Equal("priv=ok empty=ok flush=ok purge=0xC0000061", r.Steps);
            r.UsedFallback = true;
            r.EmptyStatus = -1;
            Assert.Equal("priv=ok empty=0xFFFFFFFF(fallback) flush=ok purge=0xC0000061", r.Steps);
        }

        private static MemorySnapshot Snap(int total, int avail, int modified, int standby)
        {
            MemorySnapshot s = new MemorySnapshot();
            s.Total = total * GiB;
            s.Available = avail * GiB;
            s.Modified = modified * GiB;
            s.Standby = standby * GiB;
            s.Free = 0;
            return s;
        }

        [Test]
        public static void CleanReport_BothParts()
        {
            Assert.Equal("2,1 GB a menos em uso · cache em espera limpo: 3,4 GB",
                Display.CleanReport((long)(2.1 * GiB), (long)(3.4 * GiB)));
        }

        [Test]
        public static void CleanReport_OnlyOnePart()
        {
            Assert.Equal("Cache em espera limpo: 2,0 GB", Display.CleanReport(0, 2 * GiB));
            Assert.Equal("Cache em espera limpo: 2,0 GB", Display.CleanReport(20L * 1024 * 1024, 2 * GiB));
            Assert.Equal("0,5 GB a menos em uso", Display.CleanReport(GiB / 2, 0));
            Assert.Equal("0,5 GB a menos em uso", Display.CleanReport(GiB / 2, -1)); // standby unknown
        }

        [Test]
        public static void CleanReport_Nothing()
        {
            Assert.Equal("Nada a liberar agora", Display.CleanReport(0, 0));
            Assert.Equal("Nada a liberar agora", Display.CleanReport(0, -1));
            Assert.Equal("Nada a liberar agora", Display.CleanReport(10L * 1024 * 1024, 10L * 1024 * 1024));
        }
    }
}
