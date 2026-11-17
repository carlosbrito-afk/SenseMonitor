using System;
using System.Collections.Generic;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The review of the manual and shortened fan tests (4 Important): a stop or cancel never gets lost, Automático is
    // "confirmed" only by the firmware's read-back, the fan marker never shrinks, and one bad reading cannot pass a stuck fan.
    public static class FanReviewFixTests
    {
        private static List<int?> L(params int[] v)
        {
            List<int?> l = new List<int?>();
            foreach (int x in v) l.Add(x);
            return l;
        }

        // ---- I1: the stop/cancel is latched until the runner exists ----

        private sealed class Rig
        {
            public readonly FakeFirmware Fw = new FakeFirmware();
            public int Ms;
            public readonly ManualFanRunner Runner;

            public Rig()
            {
                Fw.ReadingSource = delegate
                {
                    SensorReading r = new SensorReading();
                    r.CpuFanRpm = 2700; r.GpuFanRpm = 2600; r.CpuTempC = 55; r.GpuTempC = 50;
                    return r;
                };
                Runner = new ManualFanRunner(Fw, delegate(int ms) { Ms += ms; }, FanLayout.Both);
            }
        }

        [Test]
        public static void Latch_StopBeforeTheRunnerExists_IsHonouredTheMomentItStarts()
        {
            ManualFanControl ctl = new ManualFanControl();
            ctl.RequestStop(); // "Voltar ao automático" pressed while the firmware is still being opened
            Rig rig = new Rig();
            ctl.Attach(rig.Runner);
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(!r.Applied, "Custom was never written");
            Assert.Equal(0, rig.Fw.Count("pct:80,80"));
            Assert.Equal("auto", rig.Fw.Last());
            Assert.True(r.AutoConfirmed, "Automático went through the guarded path and read back");
            Assert.True(rig.Ms <= 1000, "no 2 s baseline and no 120 s hold: " + rig.Ms);
        }

        [Test]
        public static void Latch_CancelBeforeTheRunnerExists_EndsItAtOnce()
        {
            ManualFanControl ctl = new ManualFanControl();
            ctl.Cancel(); // the wizard was closed in the same window
            Rig rig = new Rig();
            ctl.Attach(rig.Runner);
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 90);
            Assert.True(!r.Applied && r.SkipFurther, "nothing written, nothing more asked");
            Assert.Equal(0, rig.Fw.Count("pct:90,90"));
            Assert.Equal("auto", rig.Fw.Last());
            Assert.True(r.StopReason.Contains("cancelado"), r.StopReason);
        }

        [Test]
        public static void Latch_StopWhileHolding_StillReachesTheRunner()
        {
            ManualFanControl ctl = new ManualFanControl();
            Rig rig = new Rig();
            ctl.Attach(rig.Runner);
            FanProgressCounter c = new FanProgressCounter();
            rig.Runner.Progress = delegate(ManualFanProgress p) { c.N++; if (c.N == 3) ctl.RequestStop(); };
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(r.Applied, "written");
            Assert.True(r.HeldMs < 10000, "the stop ended the hold: " + r.HeldMs);
            Assert.Equal("auto", rig.Fw.Last());
        }

        private sealed class FanProgressCounter { public int N; }

        [Test]
        public static void Latch_DetachClearsTheStop_ButNotTheCancel()
        {
            ManualFanControl ctl = new ManualFanControl();
            ctl.RequestStop();
            ctl.Detach();
            Rig rig = new Rig();
            ctl.Attach(rig.Runner);
            rig.Runner.RequestStop(); // a fresh runner must not inherit the old stop
            ManualFanControl ctl2 = new ManualFanControl();
            ctl2.Cancel();
            ctl2.Detach();
            Rig rig2 = new Rig();
            ctl2.Attach(rig2.Runner);
            ManualFanResult r = rig2.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(!r.Applied, "a closed wizard stays cancelled");
        }

        // ---- I2: Automático is confirmed only by the firmware's read-back ----

        [Test]
        public static void AutoConfirmation_NeedsTheReadBackToSayAuto()
        {
            Assert.True(ManualFanRunner.ConfirmedByReadBack(true, true), "accepted and read back");
            Assert.True(!ManualFanRunner.ConfirmedByReadBack(true, false), "the firmware still reports Custom");
            Assert.True(!ManualFanRunner.ConfirmedByReadBack(true, null), "an unreadable answer is not a confirmation");
            Assert.True(!ManualFanRunner.ConfirmedByReadBack(false, true), "a refused write is not one either");
        }

        [Test]
        public static void Run_ReadBackUnreadable_IsNotConfirmed()
        {
            Rig rig = new Rig();
            rig.Runner.AutoReadBack = delegate { return null; };
            rig.Runner.RequestStop();
            ManualFanResult r = rig.Runner.Run(ManualFanTarget.Both, 80);
            Assert.True(!r.AutoConfirmed, "no read-back, no confirmation");
        }

        [Test]
        public static void Decide_AutoNotConfirmed_NeverPasses_EvenWhenHeardAndMeasured()
        {
            // the firmware accepted Automático but keeps reporting Custom: the step must not unlock anything
            Rig rig = new Rig();
            rig.Fw.ReadingSource = delegate
            {
                SensorReading s = new SensorReading();
                bool custom = false;
                foreach (string c in rig.Fw.Calls) { if (c.StartsWith("pct")) custom = true; }
                s.CpuFanRpm = custom ? 5100 : 2700; s.GpuFanRpm = custom ? 5000 : 2600; s.CpuTempC = 55; s.GpuTempC = 50;
                return s;
            };
            rig.Runner.AutoReadBack = delegate { return false; };
            int n = 0;
            rig.Runner.Progress = delegate(ManualFanProgress p) { if (++n == 6) rig.Runner.RequestStop(); };
            ManualFanAttempt a = new ManualFanAttempt();
            a.Result = rig.Runner.Run(ManualFanTarget.Both, 80);
            a.Heard = true;
            List<ManualFanAttempt> l = new List<ManualFanAttempt>();
            l.Add(a);
            FanTestResult res = ManualFanOutcome.Decide(FanLayout.Both, l);
            Assert.True(res.Status != StepStatus.Passed, "not confirmed: " + res.Status);
        }

        // ---- I3: the fan marker never shrinks ----

        private static string TempMarker()
        {
            return Path.Combine(TestRunner.Sandbox, "marker-" + Guid.NewGuid().ToString("N") + ".flag");
        }

        [Test]
        public static void Marker_ManualWriteOnOneFan_RecordsTheWholeNotebook()
        {
            FanMarker marker = new FanMarker(TempMarker());
            FakeFirmware fw = new FakeFirmware();
            GuardedFirmware g = new GuardedFirmware(fw, null, marker, false, null, null);
            g.HandBackLayout = FanLayout.Both;
            Assert.True(g.SetFanPercent(80, 80), "written");
            Assert.Equal("both", marker.Read().Layout);
            marker.Clear();
        }

        [Test]
        public static void Marker_LaterWriteOnAnotherFan_WidensTheExistingMarker()
        {
            FanMarker marker = new FanMarker(TempMarker());
            marker.Set(FanLayout.GpuOnly, null); // an earlier attempt left it GPU-only (its Auto read-back disagreed)
            FakeFirmware fw = new FakeFirmware();
            GuardedFirmware g = new GuardedFirmware(fw, null, marker, false, null, null);
            g.HandBackLayout = FanLayout.CpuOnly; // a firmware without a notebook-wide hand-back still widens
            Assert.True(g.SetFanPercent(70, 70), "written");
            Assert.Equal("both", marker.Read().Layout);
            marker.Clear();
        }

        [Test]
        public static void Marker_NeverShrinks_WhileAFanMayStillBeOffAuto()
        {
            FanMarker marker = new FanMarker(TempMarker());
            marker.Set(FanLayout.Both, null);
            FakeFirmware fw = new FakeFirmware();
            GuardedFirmware g = new GuardedFirmware(fw, null, marker, false, null, null);
            g.HandBackLayout = FanLayout.GpuOnly;
            Assert.True(g.SetFanPercent(70, 70), "written");
            Assert.Equal("both", marker.Read().Layout);
            marker.Clear();
        }

        // ---- I4: one reading cannot pass a stuck fan ----

        [Test]
        public static void ReturnedToAuto_OneZeroReading_DoesNotPassAStuckFan()
        {
            // the fan ignores Automático and stays near 5000 (Máximo 5000, baseline 2000); the WMI hands back a 0 once
            Assert.True(!FanValidation.ReturnedToAuto(L(5000, 5000, 0), 2000, 5000), "a single 0 is not a spin-down");
            Assert.True(!FanValidation.ReturnedToAuto(L(5000, 0, 5000), 2000, 5000), "nor a dip in the middle");
        }

        [Test]
        public static void ReturnedToAuto_NeedsTwoConsecutiveReadings()
        {
            Assert.True(!FanValidation.ReturnedToAuto(L(7000, 7000, 3500), 2000, 7000), "one reading halfway back is not enough");
            Assert.True(FanValidation.ReturnedToAuto(L(7000, 3800, 3500), 2000, 7000), "two readings halfway back are");
        }

        [Test]
        public static void ReturnedToAuto_WindingDownNeedsTwoReadingsUnderTheDrop()
        {
            Assert.True(!FanValidation.ReturnedToAuto(L(7461, 7200, 5700), 2743, 7461), "one reading under 80 % of Máximo");
            Assert.True(FanValidation.ReturnedToAuto(L(7461, 5900, 5700), 2743, 7461), "two readings under it and falling");
        }

        [Test]
        public static void Evaluate_StuckFanWithOneZeroReading_Fails()
        {
            FanCheck c = FanValidation.EvaluateFan("CPU", L(2000, 2000, 2000, 2000), L(3000, 3000, 3000, 3000), L(5000, 5000, 5000, 5000),
                L(5000, 5000, 5000, 5000, 5000, 5000, 0), 50);
            Assert.True(!c.Passed, "stuck at Máximo, one bad reading at the end");
        }

        [Test]
        public static void Runner_FanStuckAtMax_WithAZeroAtTheEnd_StillFailsAfterTheFullWayBack()
        {
            int ms = 0;
            FakeFirmware fw = new FakeFirmware();
            int calls = 0;
            int autoAt = -1;
            fw.ReadingSource = delegate
            {
                SensorReading r = new SensorReading();
                string st = "auto";
                for (int i = fw.Calls.Count - 1; i >= 0; i--)
                {
                    string c = fw.Calls[i];
                    if (c == "auto" || c == "max" || c.StartsWith("pct")) { st = c.StartsWith("pct") ? "pct" : c; break; }
                }
                int autos = 0;
                foreach (string c in fw.Calls) if (c == "auto") autos++;
                int v = st == "max" ? 5000 : st == "pct" ? 3000 : (autos >= 2 ? 5000 : 2000);
                calls++;
                if (autos >= 2 && autoAt < 0) autoAt = ms;
                if (autos >= 2 && ms - autoAt == 3000) v = 0; // one transient 0 on the way back
                r.CpuFanRpm = v; r.GpuFanRpm = v; r.CpuTempC = 55; r.GpuTempC = 50;
                return r;
            };
            FanValidationRunner run = new FanValidationRunner(fw, delegate { fw.Calls.Add("max"); return true; }, delegate(int d) { ms += d; }, 50);
            FanValidationReport rep = run.RunFans();
            Assert.True(!rep.Passed, "the stuck fan must not pass: " + rep.Summary());
            Assert.True(ms >= 3000 + 3000 + 3000 + FanValidationRunner.ReturnMaxMs, "it watched the whole way back: " + ms);
        }
    }
}
