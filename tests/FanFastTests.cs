using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The faster automatic fan test: phases end as soon as the fans settle, the whole run is at most 25 s, and the
    // sensor check that opens the step takes at most 3 s.
    public static class FanFastTests
    {
        private static List<int?> L(params int[] v)
        {
            List<int?> l = new List<int?>();
            foreach (int x in v) l.Add(x);
            return l;
        }

        // ---- Settled / ReturnedToAuto ----

        [Test]
        public static void Settled_ThreeReadingsWithinThreePercentAndMoved_IsTrue()
        {
            Assert.True(FanValidation.Settled(L(2100, 2900, 3000, 3020, 2990), 2000), "settled at about 3000, far from 2000");
        }

        [Test]
        public static void Settled_FewerThanThreeReadings_IsFalse()
        {
            Assert.True(!FanValidation.Settled(L(3000, 3000), 2000), "two readings are not enough");
        }

        [Test]
        public static void Settled_StillRamping_IsFalse()
        {
            Assert.True(!FanValidation.Settled(L(2500, 3000, 3600), 2000), "spread over 3 %");
        }

        [Test]
        public static void Settled_FanHasNotReactedYet_IsFalse()
        {
            // the command is not felt for a second or two: three equal readings at the old speed prove nothing
            Assert.True(!FanValidation.Settled(L(2000, 2010, 1995), 2000), "no move from the reference");
            Assert.True(FanValidation.Settled(L(2000, 2010, 1995), null), "without a reference only the agreement counts");
        }

        [Test]
        public static void Settled_MissingReading_IsFalse()
        {
            List<int?> l = L(3000, 3000);
            l.Add(null);
            Assert.True(!FanValidation.Settled(l, 2000), "a gap in the last three");
        }

        [Test]
        public static void ReturnedToAuto_HalfwayBack_IsTrue()
        {
            Assert.True(FanValidation.ReturnedToAuto(L(7000, 4000, 3500), 2000, 6000), "3500 and 4000 are halfway between 6000 and 2000");
        }

        [Test]
        public static void ReturnedToAuto_WindingDown_IsTrue()
        {
            Assert.True(FanValidation.ReturnedToAuto(L(7235, 5900, 5734), 2743, 7461), "two readings 20 % under Máximo and falling");
        }

        [Test]
        public static void ReturnedToAuto_StuckAtMax_IsFalse()
        {
            Assert.True(!FanValidation.ReturnedToAuto(L(6000, 6000, 5900), 2000, 6000), "still at Máximo");
            Assert.True(!FanValidation.ReturnedToAuto(L(), 2000, 6000), "nothing read");
        }

        // ---- the runner on a virtual clock ----

        // RPM per fan as a function of the state ("auto", "pct", "max") and the milliseconds since that command.
        private sealed class Rig
        {
            public readonly FakeFirmware Fw = new FakeFirmware();
            public int Clock;
            public Func<string, int, int> Rpm;
            public double Temp = 55;
            private int lastCount;
            private int stateStart;

            public Rig(Func<string, int, int> rpm)
            {
                Rpm = rpm;
                Fw.ReadingSource = delegate
                {
                    SensorReading r = new SensorReading();
                    int v = Rpm(State(), Clock - stateStart);
                    r.CpuFanRpm = v;
                    r.GpuFanRpm = v - 100;
                    r.CpuTempC = Temp;
                    r.GpuTempC = Temp - 5;
                    return r;
                };
            }

            public string State()
            {
                for (int i = Fw.Calls.Count - 1; i >= 0; i--)
                {
                    string c = Fw.Calls[i];
                    if (c == "auto") return "auto";
                    if (c == "max") return "max";
                    if (c.StartsWith("pct")) return "pct";
                }
                return "auto";
            }

            public FanValidationRunner Runner()
            {
                return new FanValidationRunner(Fw,
                    delegate { Fw.Calls.Add("max"); return true; },
                    delegate(int ms)
                    {
                        if (Fw.Calls.Count != lastCount) { lastCount = Fw.Calls.Count; stateStart = Clock; }
                        Clock += ms;
                    },
                    50);
            }
        }

        // instant fans: each state has its speed at once
        private static int Instant(string s, int sinceMs)
        {
            return s == "max" ? 6000 : (s == "pct" ? 3000 : 2000);
        }

        [Test]
        public static void Runner_FansThatSettleAtOnce_FinishInElevenSeconds()
        {
            Rig rig = new Rig(Instant);
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Passed, rep.Summary());
            // baseline 3 s + three readings at 50 % + three at Máximo + two on the way back (already at the baseline)
            Assert.Equal(11000, rig.Clock);
        }

        [Test]
        public static void Runner_NeverTakesMoreThanTwentyFiveSeconds()
        {
            Assert.Equal(25000, FanValidationRunner.TotalMs);
            // fans that never settle (always drifting): every phase runs to its limit
            Rig rig = new Rig(delegate(string s, int since)
            {
                int top = s == "max" ? 6000 : (s == "pct" ? 3000 : 2000);
                if (s == "auto" && since > 0) return 2000;
                return top + (since / 1000) * (s == "auto" ? 0 : 200);
            });
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rig.Clock <= 25000, "total " + rig.Clock);
            Assert.True(rep.Aborted || rep.Cpu != null, "it still ends with a verdict or a stop");
        }

        [Test]
        public static void Runner_FanThatReactsAfterTwoSeconds_IsNotJudgedTooEarly()
        {
            // the Custom command is felt only after about 2 s: the first readings still show the baseline
            Rig rig = new Rig(delegate(string s, int since)
            {
                if (s == "pct") return since < 2500 ? 2000 : 3000;
                return Instant(s, since);
            });
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Passed, "the late reaction is still seen: " + rep.Summary());
            Assert.Equal(3000, rep.Cpu.Pct);
        }

        [Test]
        public static void Runner_SlowRamp_ReadsOnlyTheSettledSpeed()
        {
            Rig rig = new Rig(delegate(string s, int since)
            {
                if (s == "pct") return Math.Min(3000, 2000 + since / 2);
                if (s == "max") return Math.Min(6000, 3000 + since);
                return s == "auto" ? 2000 : 2000;
            });
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Passed, rep.Summary());
            Assert.Equal(3000, rep.Cpu.Pct);
            Assert.Equal(6000, rep.Cpu.Max);
        }

        [Test]
        public static void Runner_RealGradualSpinDown_StopsAsSoonAsItIsWindingDown()
        {
            // real run of this notebook (2026-11-17): baseline 2743, 50 % 4170, Máximo 7461, then a slow wind down
            int[] down = { 7235, 7000, 6759, 6494, 6286, 5972, 5734, 5472, 5200, 5000 };
            Rig rig = new Rig(delegate(string s, int since)
            {
                if (s == "auto") return 2743;
                if (s == "pct") return 4170;
                if (s == "max") return 7461;
                return 2743;
            });
            // after the second "auto" command the fan winds down
            int autos = 0;
            int lastCalls = 0;
            rig.Rpm = delegate(string s, int since)
            {
                if (rig.Fw.Calls.Count != lastCalls)
                {
                    lastCalls = rig.Fw.Calls.Count;
                    if (rig.Fw.Last() == "auto") autos++;
                }
                if (s == "pct") return 4170;
                if (s == "max") return 7461;
                if (autos >= 2) return down[Math.Min(down.Length - 1, since / 1000)];
                return 2743;
            };
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(rep.Passed, rep.Summary());
            // 3 + 3 + 3 + the way back (seven readings: 5734 is more than 20 % under Máximo and falling)
            Assert.True(rig.Clock <= 3000 + 3000 + 3000 + 9000, "stopped early on the way back: " + rig.Clock);
        }

        [Test]
        public static void Runner_StuckAtMaxAfterAuto_WaitsTheFullWayBackAndFails()
        {
            Rig rig = new Rig(delegate(string s, int since)
            {
                if (s == "pct") return 3000;
                if (s == "auto") return rig0.Clock < 3000 ? 2000 : 6000;
                return 6000;
            });
            rig0 = rig;
            FanValidationReport rep = rig.Runner().RunFans();
            Assert.True(!rep.Passed, "it never came back");
            Assert.True(rep.Cpu.Reason.Contains("não voltou"), rep.Cpu.Reason);
            Assert.True(rig.Clock >= 3000 + 3000 + 3000 + FanValidationRunner.ReturnMaxMs, "waited the whole way back: " + rig.Clock);
            Assert.Equal("auto", rig.Fw.Last());
        }

        private static Rig rig0;

        [Test]
        public static void Runner_ProgressNamesThePhaseCountsDownAndCarriesTemperatures()
        {
            Rig rig = new Rig(Instant);
            FanValidationRunner r = rig.Runner();
            List<FanProgress> seen = new List<FanProgress>();
            r.Progress = delegate(FanProgress p) { seen.Add(p); };
            r.RunFans();
            FanProgress first = seen[0];
            Assert.Equal("auto", first.Phase);
            Assert.Equal(1, first.PhaseNumber);
            Assert.Equal(4, first.PhaseCount);
            Assert.Equal("Medindo o automático", first.PhaseName);
            Assert.Equal(FanValidationRunner.BaselineMs - 1000, first.PhaseLeftMs);
            Assert.Equal(55.0, first.CpuTempC.Value);
            Assert.Equal(50.0, first.GpuTempC.Value);
            Assert.Equal(2000, first.CpuRpm.Value);
            FanProgress last = seen[seen.Count - 1];
            Assert.Equal(100, last.Percent);
            Assert.Equal(4, last.PhaseNumber);
            int prev = -1;
            foreach (FanProgress p in seen)
            {
                Assert.True(p.Percent >= prev, "the bar never goes back");
                prev = p.Percent;
            }
            bool named = false;
            foreach (FanProgress p in seen) if (p.Phase == "pct" && p.PhaseName == "Ventoinhas a 50%") named = true;
            Assert.True(named, "the 50 % phase says its percentage");
        }

        [Test]
        public static void Runner_HotAndCancelStillStopAtOnce()
        {
            Rig hot = new Rig(Instant);
            hot.Temp = 91;
            FanValidationReport rep = hot.Runner().RunFans();
            Assert.True(rep.Aborted && rep.SkipModeCheck, "90 °C still aborts");
            Assert.Equal("auto", hot.Fw.Last());
            Assert.True(hot.Clock <= 1000, "after the first reading: " + hot.Clock);
        }

        // ---- the sensor check that opens the step ----

        private static SensorReading R(double? cpu, double? gpu, int? rpm)
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = cpu;
            r.GpuTempC = gpu;
            r.CpuFanRpm = rpm;
            r.GpuFanRpm = rpm;
            return r;
        }

        [Test]
        public static void SensorCheck_TwoSteadyReadings_AreTrusted()
        {
            List<SensorReading> l = new List<SensorReading>();
            l.Add(R(48, 41, 2700));
            Assert.True(!FanSensorCheck.Evaluate(l, FanLayout.Both).Ok, "one reading is too few");
            l.Add(R(49, 41, 2710));
            SensorCheckResult r = FanSensorCheck.Evaluate(l, FanLayout.Both);
            Assert.True(r.Ok, r.Reason);
            Assert.Equal(49.0, r.CpuTempC.Value);
            Assert.True(r.Summary(FanLayout.Both).Contains("CPU 49"), r.Summary(FanLayout.Both));
        }

        [Test]
        public static void SensorCheck_MissingTemperatureOfAFan_IsNotTrusted()
        {
            List<SensorReading> l = new List<SensorReading>();
            l.Add(R(48, null, 2700));
            l.Add(R(49, null, 2700));
            SensorCheckResult both = FanSensorCheck.Evaluate(l, FanLayout.Both);
            Assert.True(!both.Ok && both.Reason.Contains("GPU"), both.Reason);
            Assert.True(FanSensorCheck.Evaluate(l, FanLayout.CpuOnly).Ok, "a CPU-only notebook needs only the CPU temperature");
        }

        [Test]
        public static void SensorCheck_ZeroOrInventedValues_AreNotTrusted()
        {
            List<SensorReading> zero = new List<SensorReading>();
            zero.Add(R(0, 0, 2700));
            zero.Add(R(0, 0, 2700));
            Assert.True(!FanSensorCheck.Evaluate(zero, FanLayout.Both).Ok, "0 °C is not a reading");
            List<SensorReading> jump = new List<SensorReading>();
            jump.Add(R(40, 40, 2700));
            jump.Add(R(80, 40, 2700));
            Assert.True(!FanSensorCheck.Evaluate(jump, FanLayout.Both).Ok, "a 40 degree jump in one second is not a sensor");
        }

        [Test]
        public static void SensorCheck_Run_StopsAsSoonAsTrusted_AndNeverWaitsMoreThanThreeSeconds()
        {
            int slept = 0;
            int reads = 0;
            SensorCheckResult ok = FanSensorCheck.Run(delegate { reads++; return R(50, 45, 2800); }, delegate(int ms) { slept += ms; }, FanLayout.Both, null);
            Assert.True(ok.Ok, "trusted");
            Assert.Equal(2, reads);
            Assert.Equal(1000, slept);

            slept = 0;
            reads = 0;
            SensorCheckResult bad = FanSensorCheck.Run(delegate { reads++; return R(null, null, 2800); }, delegate(int ms) { slept += ms; }, FanLayout.Both, null);
            Assert.True(!bad.Ok, "never trusted");
            Assert.Equal(3, reads);
            Assert.True(slept + reads * 0 <= 3000, "within 3 s: " + slept);
        }

        [Test]
        public static void SensorCheck_Run_ReaderThatThrows_IsNotTrustedAndDoesNotThrow()
        {
            SensorCheckResult r = FanSensorCheck.Run(delegate { throw new InvalidOperationException("wmi"); }, delegate(int ms) { }, FanLayout.Both, null);
            Assert.True(!r.Ok, "no trust without readings");
        }
    }
}
