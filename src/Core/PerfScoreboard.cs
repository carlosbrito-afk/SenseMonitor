using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Forja.Core
{
    // Read model of teste-desempenho.json (written by PerfReport.Json). Only what the Modes page
    // shows; every other member of the file is ignored. Names match the file exactly.
    [DataContract]
    public class PerfFileRun
    {
        [DataMember(Name = "mode")] public string Mode;
        [DataMember(Name = "applied")] public bool Applied;
        [DataMember(Name = "aborted")] public bool Aborted;
        [DataMember(Name = "multiSustained")] public double? MultiSustained;
        [DataMember(Name = "burst")] public double? Burst;
        [DataMember(Name = "single")] public double? Single;
        [DataMember(Name = "avgCpuTemp")] public double? AvgCpuTemp;
        [DataMember(Name = "lateCpuTemp")] public double? LateCpuTemp;
        [DataMember(Name = "peakCpuTemp")] public double? PeakCpuTemp;
        [DataMember(Name = "avgCpuRpm")] public double? AvgCpuRpm;
        [DataMember(Name = "avgGpuRpm")] public double? AvgGpuRpm;
        [DataMember(Name = "avgClockMulti")] public double? AvgClockMulti;
    }

    [DataContract]
    public class PerfFileVerdict
    {
        [DataMember(Name = "reliable")] public bool Reliable;
        [DataMember(Name = "bandPct")] public double? BandPct;
    }

    [DataContract]
    public class PerfFilePre
    {
        [DataMember(Name = "onBattery")] public bool? OnBattery;
        [DataMember(Name = "powerSchemeName")] public string PowerSchemeName;
    }

    [DataContract]
    public class PerfFile
    {
        [DataMember(Name = "startedUtc")] public string StartedUtc;
        [DataMember(Name = "aborted")] public bool Aborted;
        // The canary: what the journal of an earlier unfinished test said (a crash during a mode).
        [DataMember(Name = "previousCrash")] public string PreviousCrash;
        [DataMember(Name = "preconditions")] public PerfFilePre Pre;
        [DataMember(Name = "modes")] public List<PerfFileRun> Modes;
        [DataMember(Name = "verdict")] public PerfFileVerdict Verdict;
    }

    // The last measured numbers of one mode, ready to print on its card.
    public class ModeScore
    {
        public PerfMode Mode;
        public bool HasData;
        public int Runs;
        // Work units per second of the 60 s multi-core load ("Multi 60 s"); higher is better.
        public double? Score;
        // Percent against the Dia a dia reference; null for Dia a dia itself or without a reference.
        public double? ScoreVsDiaPct;
        public double? ClockMHz;
        public double? LateCpuTempC;
        public double? PeakCpuTempC;
        public int? AvgRpm;
        // One sentence of data, no adjectives: "sem medição" when there is none.
        public string Line;
    }

    // The per-mode benchmark numbers from the last --teste-desempenho. Plain data for any UI:
    // Get(mode).Line for a card, DateText/Note for a caption.
    public class PerfScoreboard
    {
        public const string NoMeasurement = "sem medição";

        private static readonly CultureInfo Br = new CultureInfo("pt-BR");
        private static readonly PerfMode[] Order = { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };

        public bool Loaded;
        public bool Reliable = true;
        public DateTime? MeasuredLocal;
        // "medido em 16/11/2026 14:05" or "sem medição".
        public string DateText = NoMeasurement;
        // Caveat about the whole measurement (interrupted, unreliable, battery); null when none.
        public string Note;
        public readonly List<ModeScore> Modes = new List<ModeScore>();

        public ModeScore Get(PerfMode m)
        {
            foreach (ModeScore s in Modes)
            {
                if (s.Mode == m) return s;
            }
            return null;
        }

        public static PerfScoreboard Empty()
        {
            PerfScoreboard b = new PerfScoreboard();
            foreach (PerfMode m in Order)
            {
                ModeScore s = new ModeScore();
                s.Mode = m;
                s.Line = NoMeasurement;
                b.Modes.Add(s);
            }
            return b;
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Config.DefaultPath), "teste-desempenho.json"); }
        }

        // Never throws: a missing or damaged file is "sem medição".
        public static PerfScoreboard FromFile(string path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return Empty();
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception)
            {
                return Empty();
            }
        }

        // false when the file could not be read or parsed (a write in progress looks like this):
        // the caller must not cache that as "sem medição". A missing file is true with an empty board.
        public static bool TryFromFile(string path, Func<string, string> readAllText, out PerfScoreboard board)
        {
            board = Empty();
            try
            {
                if (path == null || !File.Exists(path)) return true;
                return TryParse((readAllText ?? delegate(string p) { return File.ReadAllText(p, Encoding.UTF8); })(path), out board);
            }
            catch (Exception)
            {
                board = Empty();
                return false;
            }
        }

        public static bool TryParse(string json, out PerfScoreboard board)
        {
            board = Empty();
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                PerfFile f;
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    f = (PerfFile)new DataContractJsonSerializer(typeof(PerfFile)).ReadObject(ms);
                }
                if (f == null) return false;
                board = Build(f);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static PerfScoreboard Parse(string json)
        {
            PerfFile f = null;
            try
            {
                if (string.IsNullOrEmpty(json)) return Empty();
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    f = (PerfFile)new DataContractJsonSerializer(typeof(PerfFile)).ReadObject(ms);
                }
            }
            catch (Exception)
            {
                return Empty();
            }
            return Build(f);
        }

        private static bool Usable(PerfFileRun r)
        {
            return r != null && r.Applied && !r.Aborted && r.MultiSustained.HasValue && r.MultiSustained.Value > 0;
        }

        public static PerfScoreboard Build(PerfFile f)
        {
            PerfScoreboard b = Empty();
            if (f == null || f.Modes == null) return b;
            b.Loaded = true;
            b.Reliable = f.Verdict == null || f.Verdict.Reliable;
            DateTime started;
            if (!string.IsNullOrEmpty(f.StartedUtc) && DateTime.TryParseExact(f.StartedUtc, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out started))
            {
                b.MeasuredLocal = started.ToLocalTime();
                b.DateText = "medido em " + b.MeasuredLocal.Value.ToString("dd/MM/yyyy HH:mm", Br);
            }
            List<string> notes = new List<string>();
            if (f.Aborted) notes.Add("o teste foi interrompido");
            if (!b.Reliable) notes.Add("medição não confiável: havia outros programas usando a CPU");
            if (f.Pre != null && f.Pre.OnBattery == true) notes.Add("medido na bateria");
            if (!string.IsNullOrEmpty(f.PreviousCrash)) notes.Add("queda registrada: " + f.PreviousCrash);
            if (notes.Count > 0) b.Note = string.Join("; ", notes.ToArray());

            foreach (ModeScore s in b.Modes)
            {
                List<PerfFileRun> runs = new List<PerfFileRun>();
                foreach (PerfFileRun r in f.Modes)
                {
                    if (r != null && r.Mode == PerfVerdict.ModeName(s.Mode) && Usable(r)) runs.Add(r);
                }
                s.Runs = runs.Count;
                s.HasData = runs.Count > 0;
                if (!s.HasData) continue;
                s.Score = Avg(runs, delegate(PerfFileRun r) { return r.MultiSustained; });
                s.ClockMHz = Avg(runs, delegate(PerfFileRun r) { return r.AvgClockMulti; });
                s.LateCpuTempC = Avg(runs, delegate(PerfFileRun r) { return r.LateCpuTemp ?? r.AvgCpuTemp; });
                s.PeakCpuTempC = Avg(runs, delegate(PerfFileRun r) { return r.PeakCpuTemp; });
                double? cpu = Avg(runs, delegate(PerfFileRun r) { return r.AvgCpuRpm; });
                double? gpu = Avg(runs, delegate(PerfFileRun r) { return r.AvgGpuRpm; });
                s.AvgRpm = PerfScoring.MeanRpm(cpu.HasValue ? (int?)(int)Math.Round(cpu.Value) : null,
                    gpu.HasValue ? (int?)(int)Math.Round(gpu.Value) : null);
            }

            ModeScore dia = b.Get(PerfMode.DiaADia);
            double band = f.Verdict != null && f.Verdict.BandPct.HasValue ? f.Verdict.BandPct.Value : PerfVerdict.BandPct;
            foreach (ModeScore s in b.Modes)
            {
                if (!s.HasData) continue;
                if (s.Mode != PerfMode.DiaADia && dia != null && dia.HasData) s.ScoreVsDiaPct = PerfVerdict.Diff(s.Score, dia.Score);
                s.Line = b.Reliable ? DataLine(s, band) : "medição não confiável";
            }
            return b;
        }

        private static string DataLine(ModeScore s, double band)
        {
            List<string> parts = new List<string>();
            string score = "Pontuação " + s.Score.Value.ToString("0.0", Br);
            if (s.Mode == PerfMode.DiaADia) score += " (referência)";
            else if (s.ScoreVsDiaPct.HasValue)
            {
                score += Math.Abs(s.ScoreVsDiaPct.Value) < band
                    ? " (igual a Dia a dia, margem de ±" + Math.Round(band, MidpointRounding.AwayFromZero).ToString("0", Br) + " %)"
                    : " (" + PerfVerdict.Pct(s.ScoreVsDiaPct.Value) + " sobre Dia a dia)";
            }
            parts.Add(score);
            if (s.ClockMHz.HasValue) parts.Add("clock " + InstrumentMath.FormatGrouped((long)Math.Round(s.ClockMHz.Value)) + " MHz");
            if (s.LateCpuTempC.HasValue) parts.Add("CPU " + ((int)Math.Round(s.LateCpuTempC.Value, MidpointRounding.AwayFromZero)).ToString(Br) + " °C");
            if (s.AvgRpm.HasValue) parts.Add(InstrumentMath.FormatGrouped(s.AvgRpm.Value) + " rpm");
            return string.Join(" · ", parts.ToArray());
        }

        private static double? Avg(List<PerfFileRun> runs, Func<PerfFileRun, double?> pick)
        {
            double sum = 0;
            int n = 0;
            foreach (PerfFileRun r in runs)
            {
                double? v = pick(r);
                if (v.HasValue) { sum += v.Value; n++; }
            }
            return n == 0 ? (double?)null : sum / n;
        }
    }
}
