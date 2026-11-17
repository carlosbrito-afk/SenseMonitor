using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Records every write call in order: "auto", "pct:CPU,GPU" or "mode:Name".
    public class FakeFirmware : IFirmware
    {
        public List<string> Calls = new List<string>();
        public bool SetPercentResult = true;
        public bool SetAutoResult = true;
        public SensorReading Reading = new SensorReading();

        // Modes: SetMode succeeds (and changes ModeValue) unless SetModeResult is false.
        public bool SetModeResult = true;
        public PerfMode? ModeValue;
        public bool ThrowOnMode;
        // Modes the firmware silently ignores (accepted, but the read-back does not change).
        public List<PerfMode> IgnoredModes = new List<PerfMode>();

        // Optional scripted readings (validation tests): called on every ReadSensors.
        public Func<SensorReading> ReadingSource;

        public SensorReading ReadSensors()
        {
            return ReadingSource != null ? ReadingSource() : Reading;
        }

        public PerfMode? GetMode()
        {
            if (ThrowOnMode) throw new InvalidOperationException("mode read failed");
            return ModeValue;
        }

        public bool SetMode(PerfMode m)
        {
            if (ThrowOnMode) throw new InvalidOperationException("mode write failed");
            Calls.Add("mode:" + m);
            if (!SetModeResult) return false;
            if (IgnoredModes.Contains(m)) return false; // read-back mismatch, as WmiFirmware reports it
            ModeValue = m;
            return true;
        }

        public bool SetFanAuto()
        {
            Calls.Add("auto");
            return SetAutoResult;
        }

        public bool SetFanPercent(int cpuPct, int gpuPct)
        {
            Calls.Add("pct:" + cpuPct + "," + gpuPct);
            return SetPercentResult;
        }

        public bool Available { get { return true; } }

        public int Count(string call)
        {
            int n = 0;
            foreach (string c in Calls) { if (c == call) n++; }
            return n;
        }

        public string Last()
        {
            return Calls.Count == 0 ? null : Calls[Calls.Count - 1];
        }
    }

    public static class TestFans
    {
        // A controller whose Custom writes were validated on hardware (Manual/Curva allowed).
        public static FanController Validated(IFirmware fw)
        {
            FanController c = new FanController(fw);
            c.CustomWritesValidated = true;
            return c;
        }
    }
}
