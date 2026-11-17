using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // What the platform reader hands over once per sample (UI-independent raw data).
    public class TelemetryFrame
    {
        public CpuSample Cpu;
        // Null when no GPU reading was taken (window hidden, no nvidia-smi and no counters, or the GPU sleeps).
        public GpuSample Gpu;
        // The firmware temperature sensor reads 0: the GPU is off (the gauge shows "em repouso").
        public bool GpuSleeping;
        // The discrete GPU has no activity on any driver-free signal, so nvidia-smi was not started;
        // Gpu then holds the numbers of the Windows counters (or null).
        public bool GpuIdle;
        public PowerPlanInfo Power;
    }

    public enum GpuState
    {
        // No source answered.
        Unavailable,
        // The discrete GPU is off (firmware sensor reads 0) or idle (no driver-free sign of use).
        // Nothing was asked of the driver.
        Resting,
        // A reading exists (nvidia-smi or the Windows counters).
        Active
    }

    // Everything the live Monitor shows, as plain numbers plus ready-made pt-BR text. A future UI
    // binds to these fields (TelemetryViewBuilder.Build fills them); no field needs WinForms.
    public class TelemetryView
    {
        public const string Dash = InstrumentMath.Dash;

        // ---- CPU ----
        public double? CpuClockMHz;
        public string CpuClockText = Dash;           // "3,2 GHz"
        public double? CpuLoadPct;
        public double? CpuMaxCoreLoadPct;
        public string CpuMaxCoreText = Dash;         // busiest logical processor: "93 %"
        public double? CpuPackageWatts;
        public string CpuPackageWattsText = Dash;    // "54 W"
        public ThrottleState Throttle = ThrottleState.Unknown;
        public string ThrottleText = Forja.Core.ThrottleText.Of(ThrottleState.Unknown);   // "Limitado por temperatura"
        public string ThrottleEvidence;              // "limite de desempenho em 80 %" (tooltip)
        public string PowerContextLine;              // "Plano Equilibrado · Energia: Melhor desempenho · na tomada"
        public string CpuLine = Dash;                // one line for a simple label

        // ---- GPU ----
        public GpuState GpuState = GpuState.Unavailable;
        public string GpuStateText = "GPU indisponível";   // "GPU em repouso" / "GPU ativa"
        // The firmware says the GPU is off (gauge state); false while it is merely idle.
        public bool GpuFirmwareOff;
        public string GpuName;
        public double? GpuUsagePct;
        public string GpuUsageText = Dash;           // "38 %"
        public double? GpuVramUsedMiB;
        public double? GpuVramTotalMiB;
        public string GpuVramText = Dash;            // "3,0 / 4,0 GB"
        public string GpuClockText = Dash;           // "1.470 MHz"
        public string GpuPowerText = Dash;           // "55 W"
        public string GpuThrottleText;               // "limitada por energia" / "sem limitação" / null
        public string GpuSource;
        public string GpuLine = Dash;

        // ---- mode proof ----
        public ModeSwitchEffect Effect = new ModeSwitchEffect();
        public string EffectLine;                    // null before any switch
    }

    public static class TelemetryViewBuilder
    {
        private static readonly CultureInfo Br = new CultureInfo("pt-BR");

        public static TelemetryView Build(TelemetryFrame f, ThrottleState throttle, string throttleEvidence, ModeSwitchEffect effect)
        {
            TelemetryView v = new TelemetryView();
            v.Throttle = throttle;
            v.ThrottleText = Forja.Core.ThrottleText.Of(throttle);
            v.ThrottleEvidence = throttleEvidence;
            v.Effect = effect ?? new ModeSwitchEffect();
            v.EffectLine = EffectLine(v.Effect);
            if (f == null) return v;

            CpuSample c = f.Cpu;
            if (c != null)
            {
                v.CpuClockMHz = c.BusyClockMHz ?? c.EffectiveMHz; // the busy cores: what a game runs at
                v.CpuClockText = Clock(v.CpuClockMHz);
                v.CpuLoadPct = c.LoadPct;
                v.CpuMaxCoreLoadPct = c.MaxCoreLoadPct;
                v.CpuPackageWatts = c.PackageWatts;
                v.CpuPackageWattsText = Watts(c.PackageWatts);
                v.CpuMaxCoreText = Percent(c.MaxCoreLoadPct);
            }
            v.PowerContextLine = f.Power == null ? null : f.Power.Line;
            // Power limited while Windows itself asks for efficiency (slider or battery): say so.
            if (throttle == ThrottleState.PowerLimited && f.Power != null)
            {
                string why = null;
                if (PowerPlanNames.IsEfficiencyOverlay(f.Power.OverlayGuid)) why = "o Windows está no modo de energia de eficiência";
                else if (f.Power.OnBattery == true) why = "o notebook está na bateria";
                if (why != null) v.ThrottleEvidence = v.ThrottleEvidence == null ? why : v.ThrottleEvidence + "; " + why;
            }
            List<string> cpu = new List<string>();
            if (c != null && v.CpuClockMHz.HasValue) cpu.Add("Clock " + v.CpuClockText);
            if (c != null && c.PackageWatts.HasValue) cpu.Add(v.CpuPackageWattsText);
            cpu.Add(v.ThrottleText);
            v.CpuLine = string.Join(" · ", cpu.ToArray());

            GpuSample g = f.Gpu;
            if (g != null)
            {
                v.GpuState = GpuState.Active;
                v.GpuStateText = "GPU ativa";
                v.GpuName = g.Name;
                v.GpuUsagePct = g.UsagePct;
                v.GpuUsageText = Percent(g.UsagePct);
                v.GpuVramUsedMiB = g.VramUsedMiB;
                v.GpuVramTotalMiB = g.VramTotalMiB;
                v.GpuVramText = Vram(g.VramUsedMiB, g.VramTotalMiB);
                v.GpuClockText = g.ClockMHz.HasValue ? InstrumentMath.FormatGrouped((long)Math.Round(g.ClockMHz.Value)) + " MHz" : TelemetryView.Dash;
                v.GpuPowerText = Watts(g.PowerW);
                v.GpuThrottleText = GpuThrottle.Text(g.ThrottleMask);
                v.GpuSource = g.Source;
                List<string> parts = new List<string>();
                parts.Add("GPU " + v.GpuUsageText);
                if (g.VramUsedMiB.HasValue) parts.Add("VRAM " + v.GpuVramText);
                if (g.ClockMHz.HasValue) parts.Add(v.GpuClockText);
                if (g.PowerW.HasValue) parts.Add(v.GpuPowerText);
                if (v.GpuThrottleText != null && v.GpuThrottleText != "sem limitação") parts.Add(v.GpuThrottleText);
                v.GpuLine = string.Join(" · ", parts.ToArray());
            }
            // Resting: the firmware says the GPU is off, or nothing driver-free shows it in use (no
            // nvidia-smi was started). Counter numbers, when there are any, stay visible.
            v.GpuFirmwareOff = f.GpuSleeping;
            // Measured activity beats a "resting" flag (a firmware sensor at 0 with a game running).
            bool measuredBusy = g != null && g.UsagePct.HasValue && g.UsagePct.Value > GpuEngineMath.ActiveAbovePct;
            if ((f.GpuSleeping || f.GpuIdle) && !measuredBusy)
            {
                v.GpuState = GpuState.Resting;
                v.GpuStateText = "GPU em repouso";
                v.GpuLine = g == null ? "GPU em repouso" : "GPU em repouso · " + v.GpuLine;
            }
            return v;
        }

        public static string Clock(double? mhz)
        {
            if (!mhz.HasValue || mhz.Value <= 0 || double.IsNaN(mhz.Value) || double.IsInfinity(mhz.Value)) return TelemetryView.Dash;
            if (mhz.Value >= 1000.0) return InstrumentMath.FormatDecimal1(mhz.Value / 1000.0) + " GHz";
            return InstrumentMath.FormatGrouped((long)Math.Round(mhz.Value)) + " MHz";
        }

        public static string Watts(double? w)
        {
            if (!w.HasValue || w.Value < 0 || double.IsNaN(w.Value) || double.IsInfinity(w.Value)) return TelemetryView.Dash;
            return w.Value < 10.0
                ? InstrumentMath.FormatDecimal1(w.Value) + " W"
                : ((long)Math.Round(w.Value, MidpointRounding.AwayFromZero)).ToString(Br) + " W";
        }

        public static string Percent(double? p)
        {
            double? s = InstrumentMath.SanitizeUsage(p);
            return s.HasValue ? ((int)Math.Round(s.Value, MidpointRounding.AwayFromZero)).ToString(Br) + " %" : TelemetryView.Dash;
        }

        // "3,0 / 4,0 GB" (binary, as Windows shows VRAM); only the used part when the total is unknown.
        public static string Vram(double? usedMiB, double? totalMiB)
        {
            if (!usedMiB.HasValue || usedMiB.Value < 0) return TelemetryView.Dash;
            string used = InstrumentMath.FormatDecimal1(usedMiB.Value / 1024.0);
            if (!totalMiB.HasValue || totalMiB.Value <= 0) return used + " GB";
            return used + " / " + InstrumentMath.FormatDecimal1(totalMiB.Value / 1024.0) + " GB";
        }

        // The line for the "proof" under the mode cards; null before any switch.
        public static string EffectLine(ModeSwitchEffect e)
        {
            if (e == null || e.Status == EffectStatus.None) return null;
            if (e.Status == EffectStatus.Ready) return e.Verdict;
            return "Troca para " + PerfVerdict.ModeName(e.To) + ": " + e.Verdict;
        }
    }

    // What the Modes page shows beyond the three cards: per-mode benchmark lines and the last
    // switch's proof. Plain strings, one per card (index 0 = Silencioso, 1 = Dia a dia, 2 = Desempenho).
    public class ModesView
    {
        // Indexed by Index(mode): Silencioso, Dia a dia, Desempenho, Econômico, Turbo (the last two have no benchmark yet).
        public string[] CardLines = new string[5];
        public string Caption;       // "medido em 16/11/2026 14:05; ..." or "sem medição"
        public string EffectLine;    // null before any switch

        public static int Index(PerfMode m)
        {
            if (m == PerfMode.Silencioso) return 0;
            if (m == PerfMode.DiaADia) return 1;
            if (m == PerfMode.Desempenho) return 2;
            if (m == PerfMode.Economico) return 3;
            return 4;
        }

        // The data line of a card, by MODE (never by card position: the page shows only the confirmed modes).
        public string LineFor(PerfMode m)
        {
            int i = Index(m);
            return i < CardLines.Length ? CardLines[i] : null;
        }

        public static ModesView Build(PerfScoreboard board, ModeSwitchEffect effect)
        {
            ModesView v = new ModesView();
            PerfScoreboard b = board ?? PerfScoreboard.Empty();
            PerfMode[] modes = { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho, PerfMode.Economico, PerfMode.Turbo };
            for (int i = 0; i < modes.Length; i++)
            {
                ModeScore s = b.Get(modes[i]);
                v.CardLines[Index(modes[i])] = s == null ? PerfScoreboard.NoMeasurement : s.Line;
            }
            v.Caption = b.Note == null ? b.DateText : b.DateText + "; " + b.Note;
            v.EffectLine = TelemetryViewBuilder.EffectLine(effect);
            return v;
        }
    }
}
