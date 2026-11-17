using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    public class TempSample
    {
        public DateTime Time;
        public double? CpuC;
        public double? GpuC;
        public int? CpuRpm;
        public int? GpuRpm;
    }

    public class DaySummary
    {
        public DateTime Day;
        public int Samples;
        public double? CpuMax;
        public double? CpuAvg;
        public double? GpuMax;
        public double? GpuAvg;
        // Samples are one per minute; a minute counts once when either sensor is over the limit.
        public int MinutesAbove80;
        public int MinutesAbove90;
    }

    // Pure parts of the rolling temperature log: line format, parsing, summary math, retention.
    // Files are one CSV per day named yyyy-MM-dd.csv; each line starts with the time of day.
    public static class TempLog
    {
        public const string Header = "time,cpuC,gpuC,cpuRpm,gpuRpm";
        public const int KeepDays = 14;

        private const string FileFormat = "yyyy-MM-dd";
        private const string FileExt = ".csv";

        public static string FileNameFor(DateTime t)
        {
            return t.ToString(FileFormat, CultureInfo.InvariantCulture) + FileExt;
        }

        public static bool TryParseFileDay(string fileName, out DateTime day)
        {
            day = DateTime.MinValue;
            if (fileName == null) return false;
            if (!fileName.EndsWith(FileExt, StringComparison.Ordinal)) return false;
            string stem = fileName.Substring(0, fileName.Length - FileExt.Length);
            return DateTime.TryParseExact(stem, FileFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        public static string FormatLine(DateTime t, SensorReading r)
        {
            string cpu = "";
            string gpu = "";
            string cpuRpm = "";
            string gpuRpm = "";
            if (r != null)
            {
                if (r.CpuTempC.HasValue) cpu = r.CpuTempC.Value.ToString("0.0", CultureInfo.InvariantCulture);
                if (r.GpuTempC.HasValue) gpu = r.GpuTempC.Value.ToString("0.0", CultureInfo.InvariantCulture);
                if (r.CpuFanRpm.HasValue) cpuRpm = r.CpuFanRpm.Value.ToString(CultureInfo.InvariantCulture);
                if (r.GpuFanRpm.HasValue) gpuRpm = r.GpuFanRpm.Value.ToString(CultureInfo.InvariantCulture);
            }
            return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "," + cpu + "," + gpu + "," + cpuRpm + "," + gpuRpm;
        }

        // Returns null for the header, blanks and anything malformed (a torn last line after a power loss).
        public static TempSample ParseLine(DateTime day, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string[] f = line.Trim().Split(',');
            if (f.Length != 5) return null;
            DateTime tod;
            if (!DateTime.TryParseExact(f[0], "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out tod)) return null;
            TempSample s = new TempSample();
            s.Time = day.Date.Add(tod.TimeOfDay);
            if (!TryDouble(f[1], out s.CpuC)) return null;
            if (!TryDouble(f[2], out s.GpuC)) return null;
            if (!TryInt(f[3], out s.CpuRpm)) return null;
            if (!TryInt(f[4], out s.GpuRpm)) return null;
            return s;
        }

        private static bool TryDouble(string text, out double? value)
        {
            value = null;
            if (text.Length == 0) return true;
            double d;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return false;
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            value = d;
            return true;
        }

        private static bool TryInt(string text, out int? value)
        {
            value = null;
            if (text.Length == 0) return true;
            int i;
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out i)) return false;
            value = i;
            return true;
        }

        public static DaySummary Summarize(DateTime day, IEnumerable<TempSample> samples)
        {
            DaySummary d = new DaySummary();
            d.Day = day.Date;
            double cpuSum = 0;
            int cpuN = 0;
            double gpuSum = 0;
            int gpuN = 0;
            if (samples != null)
            {
                foreach (TempSample s in samples)
                {
                    if (s == null) continue;
                    d.Samples++;
                    double top = double.MinValue;
                    if (s.CpuC.HasValue)
                    {
                        cpuSum += s.CpuC.Value;
                        cpuN++;
                        if (!d.CpuMax.HasValue || s.CpuC.Value > d.CpuMax.Value) d.CpuMax = s.CpuC.Value;
                        top = Math.Max(top, s.CpuC.Value);
                    }
                    if (s.GpuC.HasValue)
                    {
                        gpuSum += s.GpuC.Value;
                        gpuN++;
                        if (!d.GpuMax.HasValue || s.GpuC.Value > d.GpuMax.Value) d.GpuMax = s.GpuC.Value;
                        top = Math.Max(top, s.GpuC.Value);
                    }
                    if (top > 80.0) d.MinutesAbove80++;
                    if (top > 90.0) d.MinutesAbove90++;
                }
            }
            if (cpuN > 0) d.CpuAvg = cpuSum / cpuN;
            if (gpuN > 0) d.GpuAvg = gpuSum / gpuN;
            return d;
        }

        // Names of daily files older than the retention window. Files that do not match the
        // yyyy-MM-dd.csv pattern are never selected, so foreign files are left alone.
        public static List<string> SelectExpired(IEnumerable<string> fileNames, DateTime today, int keepDays)
        {
            List<string> result = new List<string>();
            DateTime oldestKept = today.Date.AddDays(-(keepDays - 1));
            foreach (string name in fileNames)
            {
                DateTime day;
                if (TryParseFileDay(name, out day) && day < oldestKept) result.Add(name);
            }
            return result;
        }

        // Daily files within the last N days, today included.
        public static List<string> SelectForDays(IEnumerable<string> fileNames, DateTime today, int days)
        {
            List<string> result = new List<string>();
            DateTime oldest = today.Date.AddDays(-(days - 1));
            foreach (string name in fileNames)
            {
                DateTime day;
                if (TryParseFileDay(name, out day) && day >= oldest && day <= today.Date) result.Add(name);
            }
            return result;
        }
    }
}
