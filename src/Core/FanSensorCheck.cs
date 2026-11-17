using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // The result of the "Conferindo os sensores…" moment that opens the fan step: are the temperatures that guard the test
    // (90 °C stops it) readable and believable right now? Read-only; nothing is written to the firmware.
    public sealed class SensorCheckResult
    {
        public bool Ok;
        public string Reason;
        public int Readings;
        public double? CpuTempC;
        public double? GpuTempC;
        public int? CpuRpm;
        public int? GpuRpm;

        public string Summary(FanLayout layout)
        {
            List<string> l = new List<string>();
            if (CpuTempC.HasValue) l.Add("CPU " + Math.Round(CpuTempC.Value).ToString(CultureInfo.InvariantCulture) + " °C");
            if (GpuTempC.HasValue) l.Add("GPU " + Math.Round(GpuTempC.Value).ToString(CultureInfo.InvariantCulture) + " °C");
            return string.Join(", ", l.ToArray());
        }
    }

    // Takes up to MaxReadings readings one second apart (at most 2 s of waiting, 3 s with the readings themselves) and
    // stops as soon as the temperatures of the fans' sides are trusted.
    public static class FanSensorCheck
    {
        public const int MaxReadings = 3;
        public const int MinReadings = 2;
        public const int SampleMs = 1000;
        public const double MaxJump = 15;

        public static SensorCheckResult Evaluate(List<SensorReading> samples, FanLayout layout)
        {
            SensorCheckResult r = new SensorCheckResult();
            if (layout == null) layout = FanLayout.Both;
            r.Readings = samples == null ? 0 : samples.Count;
            SensorReading last = r.Readings > 0 ? samples[r.Readings - 1] : null;
            if (last == null)
            {
                r.Reason = "nenhuma leitura dos sensores";
                return r;
            }
            r.CpuTempC = Valid(last.CpuTempC);
            r.GpuTempC = Valid(last.GpuTempC);
            r.CpuRpm = last.CpuFanRpm;
            r.GpuRpm = last.GpuFanRpm;
            // each fan of the layout needs the temperature of its side; the other side counts when it is there
            if (layout.Cpu && !r.CpuTempC.HasValue) { r.Reason = "a temperatura da CPU não está legível"; return r; }
            if (layout.Gpu && !r.GpuTempC.HasValue) { r.Reason = "a temperatura da GPU não está legível"; return r; }
            if (!r.CpuTempC.HasValue && !r.GpuTempC.HasValue) { r.Reason = "nenhuma temperatura legível"; return r; }
            if (!Steady(samples, true) || !Steady(samples, false))
            {
                r.Reason = "as temperaturas lidas não parecem de um sensor real";
                return r;
            }
            if (r.Readings < MinReadings)
            {
                r.Reason = "poucas leituras para confiar";
                return r;
            }
            r.Ok = true;
            return r;
        }

        // The readings of one sensor never jump more than MaxJump between two seconds (a made-up value would).
        private static bool Steady(List<SensorReading> samples, bool cpu)
        {
            double? prev = null;
            foreach (SensorReading s in samples)
            {
                if (s == null) continue;
                double? v = Valid(cpu ? s.CpuTempC : s.GpuTempC);
                if (!v.HasValue) continue;
                if (prev.HasValue && Math.Abs(v.Value - prev.Value) > MaxJump) return false;
                prev = v;
            }
            return true;
        }

        private static double? Valid(double? t)
        {
            if (!t.HasValue || double.IsNaN(t.Value) || double.IsInfinity(t.Value) || t.Value < SensorPlausibility.Min || t.Value > SensorPlausibility.Max) return null;
            return t.Value;
        }

        // progress(reading number, elapsed ms) after each reading.
        public static SensorCheckResult Run(Func<SensorReading> read, Action<int> sleep, FanLayout layout, Action<int, int> progress)
        {
            List<SensorReading> samples = new List<SensorReading>();
            SensorCheckResult r = Evaluate(samples, layout);
            for (int i = 0; i < MaxReadings; i++)
            {
                SensorReading s = null;
                try { s = read(); }
                catch (Exception) { s = null; }
                samples.Add(s);
                r = Evaluate(samples, layout);
                if (progress != null)
                {
                    try { progress(i + 1, i * SampleMs); }
                    catch (Exception) { }
                }
                if (r.Ok) return r;
                if (i < MaxReadings - 1) sleep(SampleMs);
            }
            return r;
        }
    }
}
