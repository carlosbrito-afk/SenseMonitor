using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    public enum PerfPhase
    {
        Cooldown,
        Idle,
        Multi,
        Single
    }

    // One reading per second. T = seconds since the start of the phase; Units = work units
    // done since the load of that phase started (0 outside the load phases). Units and T are
    // captured together, before the sensor calls.
    public class PerfSample
    {
        public PerfPhase Phase;
        public double T;
        public double? CpuTempC;
        public double? GpuTempC;
        public int? CpuRpm;
        public int? GpuRpm;
        public double? ClockMHz;       // approximate (hybrid CPUs mix P and E cores)
        public double? DischargeMw;
        public double? CpuLoadPct;     // total CPU use of the machine (background load while idle)
        public long Units;
    }

    // Everything measured for one run of one mode.
    public class PerfModeRun
    {
        public PerfMode Mode;
        // Position in the test order (0-based).
        public int Order;
        // The firmware accepted the mode AND the read-back showed it.
        public bool Applied;
        public string SwitchNote;
        public bool Aborted;
        public string AbortReason;
        public double CooldownSec;
        public bool CooldownReached;
        public double? CooldownStartTempC;
        // CPU temperature when the load phases begin (end of the cool-down).
        public double? StartTempC;
        public readonly List<PerfSample> Samples = new List<PerfSample>();
        public PerfStats Stats;
    }

    // Scores and averages of one run (see PerfScoring). Rates are work units per second.
    public class PerfStats
    {
        public double? MultiSustained;   // seconds 15-60 of the 60 s multi-core load (turbo + early sustained)
        public double? Burst;            // first 10 s of the multi-core load
        public double? Single;           // average over the single-core load
        public bool MultiComplete;       // the full 60 s multi-core load ran
        public bool SingleComplete;
        public double? AvgCpuTemp;       // during the multi-core load
        public double? LateCpuTemp;      // mean of the last 30 s of the multi-core load
        public double? PeakCpuTemp;      // over both loads (single hottest reading)
        public double? PeakGpuTemp;
        public double? AvgGpuTemp;
        public int? AvgCpuRpm;           // during the multi-core load
        public int? AvgGpuRpm;
        public int? IdleCpuRpm;
        public int? IdleGpuRpm;
        public double? IdleCpuTemp;
        public double? IdleCpuLoad;      // background CPU use while idle (%)
        public double? AvgClockMulti;
        public double? AvgClockSingle;
        public double? AvgDischargeMw;

        // Mean of the two fans under load (one if only one reads).
        public int? AvgRpm
        {
            get { return PerfScoring.MeanRpm(AvgCpuRpm, AvgGpuRpm); }
        }

        public int? IdleRpm
        {
            get { return PerfScoring.MeanRpm(IdleCpuRpm, IdleGpuRpm); }
        }
    }

    public class PerfPreconditions
    {
        public bool? OnBattery;            // null = unknown
        public int? BatteryPercent;
        public string PowerSource = "desconhecida";
        public string PowerSchemeName;
        public string PowerSchemeGuid;
        public string CpuName;
        public int LogicalCores;
        public int PhysicalCores;
        public int LoadThreads;
        public string AppVersion;
        // Average CPU use of the machine over the 10 s before the test (background load).
        public double? BackgroundCpuPct;
        public string Gpu = "GPU: sem carga de teste (só temperatura)";
        public string Fans = "ventoinhas forçadas em Automático durante todo o teste";
    }

    public class PerfTestReport
    {
        public DateTime StartedUtc;
        public double TotalSec;
        public PerfPreconditions Pre = new PerfPreconditions();
        public double? BaselineCpuTempC;
        public readonly List<PerfModeRun> Runs = new List<PerfModeRun>();
        public bool Aborted;
        public bool Cancelled;
        public string Reason;
        public bool FinalModeOk;
        public bool FinalFansAuto;
        // From the journal of an earlier run that never finished (a crash); null = none.
        public string PreviousCrashNote;

        // Dia a dia / mode and fans both confirmed.
        public bool RestoreOk
        {
            get { return FinalModeOk && FinalFansAuto; }
        }

        public PerfModeRun Find(PerfMode m)
        {
            foreach (PerfModeRun r in Runs)
            {
                if (r.Mode == m) return r;
            }
            return null;
        }

        public List<PerfModeRun> FindAll(PerfMode m)
        {
            List<PerfModeRun> l = new List<PerfModeRun>();
            foreach (PerfModeRun r in Runs)
            {
                if (r.Mode == m) l.Add(r);
            }
            return l;
        }
    }

    // The Windows power plan. powercfg prints it localized, so the GUID decides first.
    public static class PerfPowerScheme
    {
        public const string SaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";

        // "Power Scheme GUID: 381b4222-...  (Balanced)" in any language.
        public static bool Parse(string output, out string guid, out string name)
        {
            guid = null;
            name = null;
            if (string.IsNullOrEmpty(output)) return false;
            Match m = Regex.Match(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*(?:\(([^)]*)\))?");
            if (!m.Success) return false;
            guid = m.Groups[1].Value.ToLowerInvariant();
            name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            if (string.IsNullOrEmpty(name)) name = null;
            return true;
        }

        public static bool IsSaver(string guid, string name)
        {
            if (!string.IsNullOrEmpty(guid) && string.Equals(guid, SaverGuid, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("saver") || n.Contains("econom");
        }
    }

    // Power state read once per second during the test.
    public class PerfPower
    {
        public bool? OnBattery;
        public int? Percent;
    }

    // The checks before the test starts: refuse or warn.
    public class PreflightResult
    {
        public bool Allowed;
        public string Refusal;
        public string Warning;
    }

    // Timing and safety rules of the test.
    public static class PerfPlan
    {
        public const int IdleSec = 15;
        public const int MultiSec = 60;
        public const int SingleSec = 20;
        public const int BurstSec = 10;
        public const int SustainedSec = 45;
        public const int LateWindowSec = 30;
        // The cool-down is never shortened: every run, the Dia a dia reference included,
        // gets the same wait.
        public const int CooldownMaxSec = 90;
        public const double CooldownMarginC = 4;
        public const int TypicalCooldownSec = 45;
        public const double AbortTempC = 90;
        // Never start a load on an already hot machine.
        public const double StartLoadMaxC = 80;
        // Consecutive unreadable CPU temperatures (1 s apart) that stop a load.
        public const int BlindSamplesLimit = 3;

        // Pre-test refusal and warning limits.
        public const double BackgroundMaxPct = 10;
        public const int BackgroundSec = 10;
        public const int BatteryMinPct = 50;
        public const int BatteryAbortPct = 30;
        // Background CPU use while idle during the test that makes the comparison unreliable.
        public const double IdleLoadMaxPct = 15;
        // Start temperatures of the runs may differ by this much.
        public const double StartSpreadMaxC = 3;
        // The two Dia a dia runs may differ by this much (percent of the score).
        public const double DriftMaxPct = 3;

        public const int LoadedSecPerMode = IdleSec + MultiSec + SingleSec;

        // Cool-down target: the idle temperature measured before the first run + 4 °C.
        public static double CooldownTarget(double baselineC)
        {
            return baselineC + CooldownMarginC;
        }

        public static bool SafeToStartLoad(double? cpuTempC)
        {
            return cpuTempC.HasValue && cpuTempC.Value < StartLoadMaxC;
        }

        // Temperature that stops a load at once: >= 90 °C on either sensor. A missing CPU
        // reading is handled by the caller (BlindSamplesLimit in a row).
        public static bool MustStopLoad(double? cpuTempC, double? gpuTempC, out string reason)
        {
            reason = null;
            double hot = cpuTempC.HasValue ? cpuTempC.Value : 0;
            if (gpuTempC.HasValue && gpuTempC.Value > hot) hot = gpuTempC.Value;
            if (hot >= AbortTempC)
            {
                reason = "temperatura chegou a " + ((int)hot).ToString(CultureInfo.InvariantCulture) + " °C (limite 90 °C)";
                return true;
            }
            return false;
        }

        // The power state changed under the test: started on AC and the cable came out, or on
        // battery and the charge fell below 30 %. null reading = no change known.
        public static bool PowerAbort(bool startedOnBattery, PerfPower now, out string reason)
        {
            reason = null;
            if (now == null) return false;
            if (!startedOnBattery && now.OnBattery == true)
            {
                reason = "o cabo de energia foi desconectado durante o teste";
                return true;
            }
            if (now.OnBattery == true && now.Percent.HasValue && now.Percent.Value < BatteryAbortPct)
            {
                reason = "a bateria caiu abaixo de " + BatteryAbortPct + " %";
                return true;
            }
            return false;
        }

        // Refuse or warn before starting. Unattended (--auto) needs AC power and a measured quiet CPU.
        public static PreflightResult Preflight(bool? onBattery, int? batteryPct, double? backgroundCpuPct, bool unattended)
        {
            PreflightResult r = new PreflightResult();
            if (onBattery == true)
            {
                if (unattended)
                {
                    r.Refusal = "o notebook está na bateria e o modo --auto exige a tomada";
                    return r;
                }
                if (!batteryPct.HasValue)
                {
                    r.Refusal = "o notebook está na bateria e o nível da carga não pôde ser lido; ligue na tomada";
                    return r;
                }
                if (batteryPct.Value < BatteryMinPct)
                {
                    r.Refusal = "a bateria está em " + batteryPct.Value + " % (mínimo " + BatteryMinPct + " % para testar fora da tomada); ligue na tomada";
                    return r;
                }
                r.Warning = "O notebook está na bateria (" + batteryPct.Value + " %): o Windows costuma limitar o desempenho assim; para uma comparação justa, ligue na tomada.";
            }
            else if (onBattery == null)
            {
                if (unattended)
                {
                    r.Refusal = "não foi possível confirmar que o notebook está na tomada (o modo --auto exige a tomada)";
                    return r;
                }
                r.Warning = "Não foi possível saber se o notebook está na tomada.";
            }
            if (!backgroundCpuPct.HasValue)
            {
                if (unattended)
                {
                    r.Refusal = "não foi possível medir o uso de CPU de fundo antes de começar";
                    return r;
                }
                r.Warning = (r.Warning == null ? "" : r.Warning + " ") + "Não foi possível medir o uso de CPU de fundo.";
            }
            else if (backgroundCpuPct.Value > BackgroundMaxPct)
            {
                r.Refusal = "a CPU já está em uso por outros programas (média de " + ((int)Math.Round(backgroundCpuPct.Value)).ToString(CultureInfo.InvariantCulture)
                    + " % em " + BackgroundSec + " s; o limite é " + ((int)BackgroundMaxPct).ToString(CultureInfo.InvariantCulture) + " %); feche compilações, navegador e antivírus pesado e repita";
                return r;
            }
            r.Allowed = true;
            return r;
        }

        // Estimated seconds left, from the run being executed (0-based), its phase and how long
        // the phase has run. Future cool-downs count as a typical 45 s each.
        public static int RemainingSec(int modeIndex, int modeCount, PerfPhase phase, double phaseElapsedSec)
        {
            double left = 0;
            switch (phase)
            {
                case PerfPhase.Cooldown:
                    left += Math.Max(0, TypicalCooldownSec - phaseElapsedSec) + LoadedSecPerMode;
                    break;
                case PerfPhase.Idle:
                    left += Math.Max(0, IdleSec - phaseElapsedSec) + MultiSec + SingleSec;
                    break;
                case PerfPhase.Multi:
                    left += Math.Max(0, MultiSec - phaseElapsedSec) + SingleSec;
                    break;
                default:
                    left += Math.Max(0, SingleSec - phaseElapsedSec);
                    break;
            }
            int modesAfter = modeCount - modeIndex - 1;
            if (modesAfter > 0) left += modesAfter * (TypicalCooldownSec + LoadedSecPerMode);
            return (int)Math.Ceiling(left);
        }
    }

    // One unit of work for the load: integer mixing + floating point, fully deterministic
    // (IEEE-754 add/mul/div/sqrt only), so every mode is given exactly the same job.
    public static class PerfKernel
    {
        public const int IntRounds = 2000;
        public const int FpRounds = 1000;

        // Returns a value that depends on all the work, so the JIT cannot drop it.
        public static ulong Unit(ulong state)
        {
            ulong x = state | 1UL;
            unchecked
            {
                for (int i = 0; i < IntRounds; i++)
                {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                    x += (ulong)i * 0x9E3779B97F4A7C15UL;
                }
                double a = 1.0 + (x & 0xFFFFUL) / 65536.0;
                double s = 0.0;
                for (int i = 1; i <= FpRounds; i++)
                {
                    a = a * 1.0000001 + 1e-9;
                    s += Math.Sqrt(a) / (i + 0.5);
                }
                return x ^ (ulong)BitConverter.DoubleToInt64Bits(s);
            }
        }
    }
}
