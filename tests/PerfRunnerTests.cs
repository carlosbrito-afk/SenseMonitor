using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The performance routine against a fake machine with a virtual clock.
    public static class PerfRunnerTests
    {
        private sealed class Rig
        {
            public long Now;
            public readonly FakeFirmware Fw = new FakeFirmware();
            public readonly FakeLoad Load;
            public readonly FakeProbe Probe;
            public readonly Dictionary<PerfMode, double> PerThreadRate = new Dictionary<PerfMode, double>();
            public double IdleTemp = 45;
            public double LoadedTemp = 75;
            // seconds a hot CPU takes to fall back to IdleTemp after the load stops
            public double CoolSecs = 10;
            public long StoppedAt = -100000000L;
            public Func<PerfMode?, bool, double?> CpuTempOverride;
            public Action<long> OnSleep;
            public int Threads = 4;

            public Rig()
            {
                PerThreadRate[PerfMode.Silencioso] = 800;
                PerThreadRate[PerfMode.Desempenho] = 1200;
                PerThreadRate[PerfMode.DiaADia] = 1000;
                Fw.ModeValue = PerfMode.DiaADia;
                Load = new FakeLoad(this);
                Probe = new FakeProbe(this);
                Fw.ReadingSource = delegate
                {
                    SensorReading r = new SensorReading();
                    double? t = CpuTempOverride != null ? CpuTempOverride(Fw.ModeValue, Load.Running) : Temp();
                    r.CpuTempC = t;
                    r.GpuTempC = 50;
                    r.CpuFanRpm = Load.Running ? 4000 : 2000;
                    r.GpuFanRpm = Load.Running ? 4200 : 2100;
                    return r;
                };
            }

            private double? Temp()
            {
                if (Load.Running) return LoadedTemp;
                double since = (Now - StoppedAt) / 1000.0;
                double k = Math.Max(0, 1 - since / CoolSecs);
                return IdleTemp + (LoadedTemp - IdleTemp) * k;
            }

            public PerfTestRunner Runner()
            {
                PerfTestRunner r = new PerfTestRunner(Fw, Load, Probe,
                    delegate(int ms) { Now += ms; if (OnSleep != null) OnSleep(Now); },
                    delegate { return Now; }, Threads);
                return r;
            }
        }

        private sealed class FakeLoad : IPerfLoad
        {
            private readonly Rig rig;
            private long startAt;
            private long final;
            public int Threads;
            public bool Running;
            public int StartCalls;

            public FakeLoad(Rig rig) { this.rig = rig; }

            public void Start(int threads)
            {
                StartCalls++;
                Threads = threads;
                startAt = rig.Now;
                Running = true;
            }

            public void Stop()
            {
                if (!Running) return;
                final = Compute();
                Running = false;
                rig.StoppedAt = rig.Now;
            }

            private long Compute()
            {
                PerfMode m = rig.Fw.ModeValue ?? PerfMode.DiaADia;
                return (long)(rig.PerThreadRate[m] * Threads * (rig.Now - startAt) / 1000.0);
            }

            public long Units { get { return Running ? Compute() : final; } }
        }

        private sealed class FakeProbe : IPerfProbe
        {
            private readonly Rig rig;
            public double? CpuLoad = 2;
            public Func<long, PerfPower> PowerAt;

            public FakeProbe(Rig rig) { this.rig = rig; }
            public double? ClockMHz() { return 3200; }
            public double? DischargeMw() { return null; }
            public double? CpuLoadPct() { return CpuLoad; }

            public PerfPower Power()
            {
                if (PowerAt != null) return PowerAt(rig.Now);
                PerfPower p = new PerfPower();
                p.OnBattery = false;
                return p;
            }
        }

        private sealed class FakeGuard : IPerfGuard
        {
            public int Arms;
            public int Disarms;
            public bool Armed;
            public bool TrippedNow;
            public string Reason = "vigia: teste";
            public bool Tripped { get { return Armed && TrippedNow; } }
            public string TripReason { get { return Reason; } }
            public void Arm() { Arms++; Armed = true; TrippedNow = false; }
            public void Disarm() { Disarms++; Armed = false; }
        }

        private sealed class FakeJournal : IPerfJournal
        {
            private readonly Rig rig;
            public readonly List<string> Lines = new List<string>();
            // number of firmware calls already made when the line was written
            public readonly List<int> CallsAtWrite = new List<int>();
            public FakeJournal(Rig rig) { this.rig = rig; }
            public void Write(string kind, string detail)
            {
                Lines.Add(kind + "|" + detail);
                CallsAtWrite.Add(rig.Fw.Calls.Count);
            }
        }

        private static List<string> ModeCalls(Rig rig)
        {
            List<string> l = new List<string>();
            foreach (string c in rig.Fw.Calls) { if (c.StartsWith("mode:")) l.Add(c); }
            return l;
        }

        private static void AssertSafeEnd(Rig rig, PerfTestReport rep)
        {
            Assert.True(!rig.Load.Running, "load stopped");
            Assert.Equal("auto", rig.Fw.Last());
            Assert.Equal(PerfMode.DiaADia, rig.Fw.ModeValue.Value);
            Assert.True(rep.FinalModeOk, "mode read back");
            Assert.True(rep.FinalFansAuto, "fans auto");
        }

        [Test]
        public static void Runner_BalancedOrder_DiaADia_Silencioso_Desempenho_DiaADia_ThenRestores()
        {
            Rig rig = new Rig();
            PerfTestReport rep = rig.Runner().Run(null);
            List<string> calls = ModeCalls(rig);
            Assert.Equal("mode:DiaADia,mode:Silencioso,mode:Desempenho,mode:DiaADia,mode:DiaADia", string.Join(",", calls.ToArray()));
            Assert.True(!rep.Aborted, "ran to the end: " + rep.Reason);
            Assert.Equal(4, rep.Runs.Count);
            Assert.Equal(2, rep.FindAll(PerfMode.DiaADia).Count);
            Assert.Equal(0, rep.Runs[0].Order);
            Assert.Equal(3, rep.Runs[3].Order);
            AssertSafeEnd(rig, rep);
            Assert.Equal(0, ToolExit.ForPerf(rep));
        }

        [Test]
        public static void Runner_ScoresFollowTheModesWorkRate()
        {
            Rig rig = new Rig();
            PerfTestReport rep = rig.Runner().Run(null);
            // threads x per-thread rate; sampled on a 1 s grid so allow a small error
            Assert.True(Math.Abs(rep.Find(PerfMode.Silencioso).Stats.MultiSustained.Value - 3200) < 40, "silencioso " + rep.Find(PerfMode.Silencioso).Stats.MultiSustained);
            foreach (PerfModeRun d in rep.FindAll(PerfMode.DiaADia))
                Assert.True(Math.Abs(d.Stats.MultiSustained.Value - 4000) < 40, "dia a dia");
            Assert.True(Math.Abs(rep.Find(PerfMode.Desempenho).Stats.MultiSustained.Value - 4800) < 40, "desempenho");
            Assert.True(Math.Abs(rep.Runs[0].Stats.Single.Value - 1000) < 20, "single core uses one thread");
            Assert.True(rep.Runs[0].Stats.MultiComplete && rep.Runs[0].Stats.SingleComplete, "full phases");
            PerfVerdictResult v = PerfVerdict.Build(rep);
            Assert.Equal(ModeEffect.Works, v.Desempenho);
            Assert.True(v.DriftPct.HasValue && v.DriftPct.Value < 1, "identical reference runs have no drift");
        }

        [Test]
        public static void Runner_PhaseLengths_Are15_60_20Seconds()
        {
            Rig rig = new Rig();
            PerfTestReport rep = rig.Runner().Run(null);
            PerfModeRun dia = rep.Runs[0];
            Assert.Equal(15, PerfScoring.Of(dia, PerfPhase.Idle).Count);
            Assert.Equal(60, PerfScoring.Of(dia, PerfPhase.Multi).Count);
            Assert.Equal(20, PerfScoring.Of(dia, PerfPhase.Single).Count);
        }

        [Test]
        public static void Runner_FansForcedToAutoAtStartAndAfterEveryModeChange()
        {
            Rig rig = new Rig();
            rig.Runner().Run(null);
            Assert.Equal("auto", rig.Fw.Calls[0]);
            // never any manual fan command
            Assert.Equal(0, rig.Fw.Calls.FindAll(delegate(string c) { return c.StartsWith("pct"); }).Count);
            for (int i = 0; i < rig.Fw.Calls.Count - 1; i++)
            {
                if (rig.Fw.Calls[i].StartsWith("mode:") && i < rig.Fw.Calls.Count - 3)
                    Assert.Equal("auto", rig.Fw.Calls[i + 1]);
            }
        }

        [Test]
        public static void Runner_CooldownIsNeverShortened_ReferenceRunsIncluded()
        {
            Rig rig = new Rig();
            rig.CoolSecs = 400; // never cools within the allowance
            PerfTestReport rep = rig.Runner().Run(null);
            for (int i = 1; i < 4; i++)
            {
                Assert.True(rep.Runs[i].CooldownSec >= PerfPlan.CooldownMaxSec - 1, "run " + i + " waited " + rep.Runs[i].CooldownSec);
                Assert.True(rep.Runs[i].CooldownSec <= PerfPlan.CooldownMaxSec + 1, "never beyond 90 s");
                Assert.True(!rep.Runs[i].CooldownReached, "target not reached");
            }
            Assert.True(rep.TotalSec < 4 * (PerfPlan.CooldownMaxSec + 100) + 20, "total " + rep.TotalSec);
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_CoolsDownWhenTheCpuIsAlreadyAtTheStartingTemperature()
        {
            Rig rig = new Rig();
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.True(rep.Runs[0].CooldownReached, "first run starts at the baseline");
            Assert.True(rep.Runs[0].CooldownSec < 1, "no wait");
            Assert.True(rep.Find(PerfMode.Desempenho).CooldownReached, "falls back inside 90 s");
        }

        [Test]
        public static void Runner_RecordsTheTemperatureAtTheStartOfEveryLoad()
        {
            Rig rig = new Rig();
            PerfTestReport rep = rig.Runner().Run(null);
            foreach (PerfModeRun r in rep.Runs)
            {
                Assert.True(r.StartTempC.HasValue && r.StartTempC.Value <= 49.5, "start temp " + r.StartTempC);
            }
            Assert.True(rep.Runs[1].CooldownStartTempC.Value > rep.Runs[1].StartTempC.Value, "cooldown began hotter than it ended");
        }

        [Test]
        public static void Runner_RecordsBackgroundCpuUseWhileIdle()
        {
            Rig rig = new Rig();
            rig.Probe.CpuLoad = 7;
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.Equal(7.0, rep.Runs[0].Stats.IdleCpuLoad.Value);
            Assert.True(PerfVerdict.Build(rep).Reliable, "a quiet machine is reliable");
            Rig rig2 = new Rig();
            rig2.Probe.CpuLoad = 40;
            Assert.True(!PerfVerdict.Build(rig2.Runner().Run(null)).Reliable, "40 % in the idle phase invalidates the verdict");
        }

        [Test]
        public static void Runner_HeatAbort_StopsTheLoadAtOnce_AndGoesOnToTheNextRun()
        {
            Rig rig = new Rig();
            rig.CpuTempOverride = delegate(PerfMode? m, bool loading)
            {
                if (!loading) return 45;
                return m == PerfMode.Desempenho ? 91 : 75;
            };
            PerfTestReport rep = rig.Runner().Run(null);
            PerfModeRun des = rep.Find(PerfMode.Desempenho);
            Assert.True(des.Aborted, "aborted by heat");
            Assert.True(des.AbortReason.Contains("90"), des.AbortReason);
            Assert.Equal(1, PerfScoring.Of(des, PerfPhase.Multi).Count); // stopped on the first hot sample
            Assert.Equal(0, PerfScoring.Of(des, PerfPhase.Single).Count);
            Assert.True(!rep.Runs[3].Aborted, "next run still ran");
            Assert.True(rep.Runs[3].Stats.MultiComplete, "and completed");
            Assert.True(!rep.Aborted, "the test as a whole ran to the end");
            Assert.Equal(0, ToolExit.ForPerf(rep));
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_UnreadableCpuTemp_StopsTheLoadOnlyAfterThreeInARow()
        {
            Rig rig = new Rig();
            rig.CpuTempOverride = delegate(PerfMode? m, bool loading)
            {
                if (!loading) return 45;
                return m == PerfMode.Silencioso ? (double?)null : 75;
            };
            PerfTestReport rep = rig.Runner().Run(null);
            PerfModeRun sil = rep.Find(PerfMode.Silencioso);
            Assert.True(sil.Aborted, "blind guard stops the load");
            Assert.Equal(3, PerfScoring.Of(sil, PerfPhase.Multi).Count);
            Assert.True(sil.AbortReason.Contains("ilegível"), sil.AbortReason);
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_SingleUnreadableSample_DoesNotKillTheRun()
        {
            Rig rig = new Rig();
            int loadedReads = 0;
            rig.CpuTempOverride = delegate(PerfMode? m, bool loading)
            {
                if (!loading) return 45;
                loadedReads++;
                return loadedReads == 5 ? (double?)null : 75;
            };
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.True(!rep.Runs[0].Aborted, "one missing reading is tolerated");
        }

        [Test]
        public static void Runner_TooHotToStart_SkipsTheLoadOfThatRun()
        {
            Rig rig = new Rig();
            rig.CpuTempOverride = delegate(PerfMode? m, bool loading)
            {
                if (loading) return 75;
                return rig.Load.StartCalls == 0 ? 45 : 85; // after the first load the machine stays hot
            };
            PerfTestReport rep = rig.Runner().Run(null);
            PerfModeRun sil = rep.Find(PerfMode.Silencioso);
            Assert.True(sil != null && sil.Aborted && sil.AbortReason.Contains("quente demais"), sil == null ? "missing" : sil.AbortReason);
            Assert.True(!rep.Aborted, "the test goes on");
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_ModeNotConfirmed_IsRecordedAndSkipped()
        {
            Rig rig = new Rig();
            rig.Fw.IgnoredModes.Add(PerfMode.Desempenho);
            PerfTestReport rep = rig.Runner().Run(null);
            PerfModeRun des = rep.Find(PerfMode.Desempenho);
            Assert.True(!des.Applied, "not applied");
            Assert.True(des.SwitchNote.Contains("falhou"), des.SwitchNote);
            Assert.Equal(0, des.Samples.Count);
            Assert.Equal(6, rig.Load.StartCalls); // three runs x (multi + single)
            Assert.True(!rep.Aborted, "the test goes on");
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_Cancel_StopsAndStillEndsInDiaADiaWithFansAuto()
        {
            Rig rig = new Rig();
            PerfTestRunner runner = rig.Runner();
            rig.OnSleep = delegate(long now) { if (now > 40000) runner.Cancel(); };
            PerfTestReport rep = runner.Run(null);
            Assert.True(rep.Aborted && rep.Cancelled, "cancelled");
            Assert.Equal(1, ToolExit.ForPerf(rep));
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_Exception_StillEndsInDiaADiaWithFansAuto()
        {
            Rig rig = new Rig();
            int reads = 0;
            Func<SensorReading> normal = rig.Fw.ReadingSource;
            rig.Fw.ReadingSource = delegate
            {
                if (++reads > 30) throw new InvalidOperationException("boom");
                return normal();
            };
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.True(rep.Aborted && !rep.Cancelled, "aborted by the error");
            Assert.True(rep.Reason.Contains("InvalidOperationException"), rep.Reason);
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_FirmwareThrowingOnModes_NeverEscapes()
        {
            Rig rig = new Rig();
            rig.Fw.ThrowOnMode = true;
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.True(rep.Aborted, "aborted");
            Assert.True(!rep.FinalModeOk, "reported honestly as not confirmed");
            Assert.Equal(ToolExit.RestoreFailed, ToolExit.ForPerf(rep));
            Assert.True(!rig.Load.Running, "load stopped");
            Assert.Equal("auto", rig.Fw.Last());
        }

        [Test]
        public static void Runner_UnreadableBaseline_AbortsBeforeChangingAnyMode()
        {
            Rig rig = new Rig();
            rig.CpuTempOverride = delegate { return null; };
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.True(rep.Aborted, "aborted");
            Assert.True(rep.Reason.Contains("ilegível"), rep.Reason);
            Assert.Equal(1, ModeCalls(rig).Count); // only the final restore
            Assert.Equal(0, rig.Load.StartCalls);
        }

        [Test]
        public static void Runner_FanAutoRefused_Aborts()
        {
            Rig rig = new Rig();
            rig.Fw.SetAutoResult = false;
            PerfTestReport rep = rig.Runner().Run(null);
            Assert.True(rep.Aborted, "aborted");
            Assert.Equal(0, rig.Load.StartCalls);
            Assert.True(!rep.FinalFansAuto, "honest about the failed Auto");
            Assert.Equal(ToolExit.RestoreFailed, ToolExit.ForPerf(rep));
        }

        [Test]
        public static void Runner_ReportsProgressWithRemainingTime()
        {
            Rig rig = new Rig();
            PerfTestRunner runner = rig.Runner();
            List<int> remaining = new List<int>();
            List<string> texts = new List<string>();
            runner.Progress = delegate(string text, int sec) { texts.Add(text); remaining.Add(sec); };
            runner.Run(null);
            Assert.True(remaining.Count > 100, "progress on every sample");
            Assert.True(texts.Exists(delegate(string t) { return t.Contains("carga em todos os núcleos"); }), "mentions the multi-core load");
            Assert.True(remaining[remaining.Count - 1] <= 20, "near zero at the end: " + remaining[remaining.Count - 1]);
            Assert.True(remaining[10] > remaining[remaining.Count - 1], "counts down");
        }

        // ---- independent heat guard ----

        [Test]
        public static void Runner_GuardTrip_StopsTheLoadAndAbortsTheRun()
        {
            Rig rig = new Rig();
            FakeGuard guard = new FakeGuard();
            PerfTestRunner runner = rig.Runner();
            runner.Guard = guard;
            // the watchdog (not the sensor path) decides during the Silencioso multi-core load
            rig.OnSleep = delegate(long now)
            {
                if (rig.Load.Running && rig.Fw.ModeValue == PerfMode.Silencioso && rig.Load.Threads == 4 && now > 150000) guard.TrippedNow = true;
            };
            PerfTestReport rep = runner.Run(null);
            PerfModeRun sil = rep.Find(PerfMode.Silencioso);
            Assert.True(sil.Aborted && sil.AbortReason.Contains("vigia"), sil.AbortReason);
            Assert.True(!rep.Aborted, "later runs still ran");
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_Guard_IsArmedOnlyWhileALoadRuns()
        {
            Rig rig = new Rig();
            FakeGuard guard = new FakeGuard();
            PerfTestRunner runner = rig.Runner();
            runner.Guard = guard;
            runner.Run(null);
            Assert.Equal(8, guard.Arms); // 4 runs x (multi + single)
            Assert.True(guard.Disarms >= 8, "disarmed after every load");
            Assert.True(!guard.Armed, "not armed at the end");
        }

        // ---- journal ----

        [Test]
        public static void Runner_Journal_StartsWithInicioEndsWithFim_AndNamesTheRestore()
        {
            Rig rig = new Rig();
            FakeJournal j = new FakeJournal(rig);
            PerfTestRunner runner = rig.Runner();
            runner.Journal = j;
            runner.Run(null);
            Assert.True(j.Lines[0].StartsWith("INICIO|"), j.Lines[0]);
            string last = j.Lines[j.Lines.Count - 1];
            Assert.True(last.StartsWith("FIM|completo") && last.Contains("modo ok") && last.Contains("ventoinhas ok"), last);
            Assert.True(j.Lines.Exists(delegate(string l) { return l.StartsWith("FASE|Silencioso carga em todos os núcleos"); }), "phase lines");
            Assert.True(j.Lines.Exists(delegate(string l) { return l.StartsWith("AMOSTRA|"); }), "heartbeat lines");
            Assert.Null(PerfJournal.CrashNote(JournalAsLines(j)));
        }

        private static List<string> JournalAsLines(FakeJournal j)
        {
            List<string> l = new List<string>();
            foreach (string s in j.Lines) l.Add("t|" + s);
            return l;
        }

        [Test]
        public static void Runner_Journal_IsWrittenBeforeEveryModeWrite()
        {
            Rig rig = new Rig();
            FakeJournal j = new FakeJournal(rig);
            PerfTestRunner runner = rig.Runner();
            runner.Journal = j;
            runner.Run(null);
            int checkedLines = 0;
            for (int i = 0; i < j.Lines.Count; i++)
            {
                if (j.Lines[i].EndsWith(": escrevendo o modo"))
                {
                    int next = j.CallsAtWrite[i];
                    Assert.True(rig.Fw.Calls[next].StartsWith("mode:"), "the firmware call right after '" + j.Lines[i] + "' is the mode write, was " + rig.Fw.Calls[next]);
                    checkedLines++;
                }
            }
            Assert.Equal(4, checkedLines);
        }

        [Test]
        public static void Runner_Journal_StillClosedWithFimWhenCancelled()
        {
            Rig rig = new Rig();
            FakeJournal j = new FakeJournal(rig);
            PerfTestRunner runner = rig.Runner();
            runner.Journal = j;
            rig.OnSleep = delegate(long now) { if (now > 40000) runner.Cancel(); };
            runner.Run(null);
            Assert.True(j.Lines.Exists(delegate(string l) { return l.StartsWith("ABORTO|"); }), "abort noted");
            Assert.True(j.Lines[j.Lines.Count - 1].StartsWith("FIM|abortado"), j.Lines[j.Lines.Count - 1]);
        }

        [Test]
        public static void Runner_JournalCutShort_MeansACrashAtThatPhase()
        {
            // a machine that dies in the Desempenho multi-core load: the journal just stops there
            Rig rig = new Rig();
            FakeJournal j = new FakeJournal(rig);
            PerfTestRunner runner = rig.Runner();
            runner.Journal = j;
            runner.Run(null);
            int cut = j.Lines.FindIndex(delegate(string l) { return l.StartsWith("FASE|Desempenho carga em todos os núcleos"); });
            Assert.True(cut > 0, "phase found");
            List<string> truncated = new List<string>();
            for (int i = 0; i <= cut; i++) truncated.Add("2026-11-17T03:00:00Z|" + j.Lines[i]);
            string note = PerfJournal.CrashNote(truncated);
            Assert.True(note != null && note.Contains("Desempenho carga em todos os núcleos"), note);
        }

        // ---- power ----

        [Test]
        public static void Runner_CablePulledDuringTheTest_AbortsAndRestores()
        {
            Rig rig = new Rig();
            PerfPreconditions pre = new PerfPreconditions();
            pre.OnBattery = false;
            rig.Probe.PowerAt = delegate(long now)
            {
                PerfPower p = new PerfPower();
                p.OnBattery = now > 60000;
                p.Percent = 90;
                return p;
            };
            PerfTestReport rep = rig.Runner().Run(pre);
            Assert.True(rep.Aborted && rep.Reason.Contains("cabo"), rep.Reason);
            Assert.Equal(1, ToolExit.ForPerf(rep));
            AssertSafeEnd(rig, rep);
        }

        [Test]
        public static void Runner_StartedOnBatteryAboveTheFloor_KeepsGoing_ButStopsBelow30()
        {
            Rig rig = new Rig();
            PerfPreconditions pre = new PerfPreconditions();
            pre.OnBattery = true;
            rig.Probe.PowerAt = delegate(long now)
            {
                PerfPower p = new PerfPower();
                p.OnBattery = true;
                p.Percent = 80;
                return p;
            };
            Assert.True(!rig.Runner().Run(pre).Aborted, "80 % on battery is fine");
            Rig rig2 = new Rig();
            rig2.Probe.PowerAt = delegate(long now)
            {
                PerfPower p = new PerfPower();
                p.OnBattery = true;
                p.Percent = now > 90000 ? 25 : 80;
                return p;
            };
            PerfTestReport low = rig2.Runner().Run(pre);
            Assert.True(low.Aborted && low.Reason.Contains("30"), low.Reason);
            AssertSafeEnd(rig2, low);
        }
    }
}
