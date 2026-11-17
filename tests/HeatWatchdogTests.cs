using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The independent heat guard, the load threads and the on-disk journal, with real threads
    // and short intervals.
    public static class HeatWatchdogTests
    {
        private static SensorReading Reading(double cpu, double gpu)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            return r;
        }

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(5);
            }
            return cond();
        }

        private static HeatWatchdog Make(Func<SensorReading> read, Action stop, int blindMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            HeatWatchdog w = new HeatWatchdog(read, stop, delegate { return clock.ElapsedMilliseconds; }, 15, blindMs);
            w.Start();
            return w;
        }

        [Test]
        public static void Watchdog_NinetyDegrees_StopsTheLoadOnItsOwn()
        {
            int stops = 0;
            using (HeatWatchdog w = Make(delegate { return Reading(91, 60); }, delegate { Interlocked.Increment(ref stops); }, 5000))
            {
                w.Arm();
                Assert.True(WaitFor(delegate { return w.Tripped; }, 2000), "tripped");
                Assert.True(w.TripReason.Contains("90"), w.TripReason);
                Thread.Sleep(80);
                Assert.Equal(1, stops); // stopLoad called once
            }
        }

        [Test]
        public static void Watchdog_GpuNinety_AlsoTrips()
        {
            using (HeatWatchdog w = Make(delegate { return Reading(60, 95); }, delegate { }, 5000))
            {
                w.Arm();
                Assert.True(WaitFor(delegate { return w.Tripped; }, 2000), "tripped on the gpu");
            }
        }

        [Test]
        public static void Watchdog_NormalReadings_NeverTrip()
        {
            int stops = 0;
            using (HeatWatchdog w = Make(delegate { return Reading(70, 60); }, delegate { Interlocked.Increment(ref stops); }, 400))
            {
                w.Arm();
                Thread.Sleep(700);
                Assert.True(!w.Tripped, "healthy");
                Assert.Equal(0, stops);
            }
        }

        [Test]
        public static void Watchdog_ReadThatHangs_TripsAfterTheBlindTime_WithoutWaitingForIt()
        {
            ManualResetEvent never = new ManualResetEvent(false);
            int stops = 0;
            Stopwatch since = Stopwatch.StartNew();
            using (HeatWatchdog w = Make(delegate { never.WaitOne(); return null; }, delegate { Interlocked.Increment(ref stops); }, 200))
            {
                w.Arm();
                Assert.True(WaitFor(delegate { return w.Tripped; }, 2000), "tripped although the read never returns");
                Assert.True(since.ElapsedMilliseconds < 1500, "within the blind window, not a read timeout: " + since.ElapsedMilliseconds);
                Assert.True(w.TripReason.Contains("sem leitura"), w.TripReason);
                Assert.Equal(1, stops);
            }
            never.Set();
        }

        [Test]
        public static void Watchdog_ReadsThatThrow_CountAsNoReading()
        {
            using (HeatWatchdog w = Make(delegate { throw new InvalidOperationException("wmi"); }, delegate { }, 150))
            {
                w.Arm();
                Assert.True(WaitFor(delegate { return w.Tripped; }, 2000), "tripped");
            }
        }

        [Test]
        public static void Watchdog_ReadsWithoutACpuTemperature_CountAsNoReading()
        {
            using (HeatWatchdog w = Make(delegate { return new SensorReading(); }, delegate { }, 150))
            {
                w.Arm();
                Assert.True(WaitFor(delegate { return w.Tripped; }, 2000), "tripped");
            }
        }

        [Test]
        public static void Watchdog_Disarmed_DoesNothing_AndArmClearsAnEarlierTrip()
        {
            int stops = 0;
            bool hot = true;
            using (HeatWatchdog w = Make(delegate { return Reading(hot ? 95 : 60, 50); }, delegate { Interlocked.Increment(ref stops); }, 5000))
            {
                Thread.Sleep(150);
                Assert.True(!w.Tripped && stops == 0, "disarmed: no trip even at 95");
                w.Arm();
                Assert.True(WaitFor(delegate { return w.Tripped; }, 2000), "armed and hot");
                w.Disarm();
                hot = false;
                w.Arm();
                Assert.True(!w.Tripped, "Arm clears the earlier trip");
                Thread.Sleep(150);
                Assert.True(!w.Tripped, "cool now");
                Assert.Equal(1, stops);
            }
        }

        // ---- load threads ----

        [Test]
        public static void PerfLoad_CountsWork_AndStopsFast()
        {
            PerfLoad load = new PerfLoad();
            load.Start(2);
            Thread.Sleep(300);
            long u = load.Units;
            Assert.True(u > 0, "units counted: " + u);
            Stopwatch w = Stopwatch.StartNew();
            load.Stop();
            Assert.True(w.ElapsedMilliseconds < 500, "stopped in " + w.ElapsedMilliseconds + " ms");
            Assert.True(!load.LastStopTimedOut, "all workers ended");
            long after = load.Units;
            Thread.Sleep(100);
            Assert.Equal(after, load.Units); // nothing counts after Stop
            load.Stop(); // idempotent
        }

        [Test]
        public static void PerfLoad_RequestStop_DoesNotBlock_AndStopStillCleansUp()
        {
            PerfLoad load = new PerfLoad();
            load.Start(2);
            Thread.Sleep(100);
            Stopwatch w = Stopwatch.StartNew();
            load.RequestStop();
            Assert.True(w.ElapsedMilliseconds < 20, "non-blocking: " + w.ElapsedMilliseconds);
            Thread.Sleep(100);
            long frozen = load.Units;
            Thread.Sleep(100);
            Assert.Equal(frozen, load.Units); // workers saw the flag
            load.Stop();
        }

        [Test]
        public static void PerfLoad_RestartAfterStop_StartsFromZeroWithFreshThreads()
        {
            PerfLoad load = new PerfLoad();
            load.Start(1);
            Thread.Sleep(100);
            load.Stop();
            load.Start(1);
            long right = load.Units;
            Thread.Sleep(100);
            Assert.True(load.Units > right || load.Units > 0, "counting again");
            Assert.True(right < 100000, "reset on Start");
            load.Stop();
        }

        // ---- journal file ----

        [Test]
        public static void JournalFile_AppendsFlushedLines_AndReadsThemBack()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sm-jr-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "teste-desempenho.journal");
            try
            {
                PerfJournalFile j = new PerfJournalFile(path);
                j.Write("INICIO", "ordem");
                j.Write("FASE", "Silencioso | repouso");
                // readable by someone else right now (flushed to disk, no lock left)
                bool exists, readable;
                List<string> lines = PerfJournalFile.ReadAll(path, out exists, out readable);
                Assert.True(exists && readable, "readable");
                Assert.Equal(2, lines.Count);
                Assert.True(lines[1].EndsWith("|FASE|Silencioso / repouso"), lines[1]);
                Assert.True(PerfJournalFile.PreviousCrashNote(path).Contains("Silencioso / repouso"), "no FIM line: a crash");
                j.Write("FIM", "completo");
                Assert.Null(PerfJournalFile.PreviousCrashNote(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public static void AutoProbes_TempFolder_ExercisesTheRealStoreAndLeavesNothingBehind()
        {
            bool wrote, readBack;
            string error;
            string before = Path.Combine(Path.GetTempPath(), "sm-autoteste-");
            AutoProbes.TempFolderRoundTrip(out wrote, out readBack, out error);
            Assert.True(wrote && readBack, "wrote=" + wrote + " read=" + readBack + " err=" + error);
            Assert.Equal(0, Directory.GetDirectories(Path.GetTempPath(), "sm-autoteste-*").Length);
        }

        [Test]
        public static void PerfProbe_ReadPower_NeverThrows_AndPercentIsSane()
        {
            PerfPower p = PerfProbe.ReadPower();
            Assert.True(!p.Percent.HasValue || (p.Percent.Value >= 0 && p.Percent.Value <= 100), "percent " + p.Percent);
        }

        [Test]
        public static void JournalFile_MissingFile_IsNotACrash()
        {
            bool exists, readable;
            string path = Path.Combine(Path.GetTempPath(), "sm-nao-existe-" + Guid.NewGuid().ToString("N") + ".journal");
            PerfJournalFile.ReadAll(path, out exists, out readable);
            Assert.True(!exists && readable, "absent but fine");
            Assert.Null(PerfJournalFile.PreviousCrashNote(path));
        }

        [Test]
        public static void JournalFile_TrimKeepsTheRecentLines()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sm-jr-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "j.journal");
            try
            {
                PerfJournalFile j = new PerfJournalFile(path);
                for (int i = 0; i < 450; i++) j.Write("AMOSTRA", "linha " + i);
                PerfJournalFile.TrimToRecent(path);
                bool exists, readable;
                List<string> lines = PerfJournalFile.ReadAll(path, out exists, out readable);
                Assert.Equal(400, lines.Count);
                Assert.True(lines[399].EndsWith("linha 449"), lines[399]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
