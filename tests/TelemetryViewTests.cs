using System;
using Forja.Core;

namespace Forja.Tests
{
    // The plain view model any UI binds to: formatted pt-BR text for CPU, GPU and the mode proof.
    public static class TelemetryViewTests
    {
        [Test]
        public static void Clock_Formats()
        {
            Assert.Equal("3,2 GHz", TelemetryViewBuilder.Clock(3206.4));
            Assert.Equal("800 MHz", TelemetryViewBuilder.Clock(800));
            Assert.Equal("—", TelemetryViewBuilder.Clock(null));
            Assert.Equal("—", TelemetryViewBuilder.Clock(0));
        }

        [Test]
        public static void Watts_Vram_Percent_Formats()
        {
            Assert.Equal("54 W", TelemetryViewBuilder.Watts(53.6));
            Assert.Equal("3,2 W", TelemetryViewBuilder.Watts(3.17));
            Assert.Equal("—", TelemetryViewBuilder.Watts(null));
            Assert.Equal("3,0 / 4,0 GB", TelemetryViewBuilder.Vram(3120, 4096));
            Assert.Equal("0,0 / 4,0 GB", TelemetryViewBuilder.Vram(0, 4096));
            Assert.Equal("1,2 GB", TelemetryViewBuilder.Vram(1230, null));
            Assert.Equal("—", TelemetryViewBuilder.Vram(null, 4096));
            Assert.Equal("38 %", TelemetryViewBuilder.Percent(38.2));
            Assert.Equal("100 %", TelemetryViewBuilder.Percent(140));
            Assert.Equal("—", TelemetryViewBuilder.Percent(null));
        }

        [Test]
        public static void NullFrame_GivesDashesAndUnknown()
        {
            TelemetryView v = TelemetryViewBuilder.Build(null, ThrottleState.Unknown, null, null);
            Assert.Equal("—", v.CpuClockText);
            Assert.Equal("Desconhecido", v.ThrottleText);
            Assert.Equal(GpuState.Unavailable, v.GpuState);
            Assert.Null(v.EffectLine);
        }

        [Test]
        public static void CpuLine_ClockPowerAndThrottle()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Cpu = new CpuSample();
            f.Cpu.EffectiveMHz = 3206;
            f.Cpu.PackageWatts = 53.6;
            f.Power = PowerPlanNames.Named("381b4222-f694-41f0-9685-ff5bb260df2e", "ded574b5-45a0-4f42-8737-46345c09c238", false);
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.TemperatureLimited, "limite de desempenho em 80 %", null);
            Assert.Equal("Clock 3,2 GHz · 54 W · Limitado por temperatura", v.CpuLine);
            Assert.Equal("Plano Equilibrado · Energia: Desempenho máximo · na tomada", v.PowerContextLine);
            Assert.Equal("limite de desempenho em 80 %", v.ThrottleEvidence);
        }

        [Test]
        public static void CpuLine_WithoutWatts_OmitsThem()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Cpu = new CpuSample();
            f.Cpu.EffectiveMHz = 1800;
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal("Clock 1,8 GHz · Normal", v.CpuLine);
        }

        [Test]
        public static void MaxCoreLoad_IsExposedAsText()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Cpu = new CpuSample();
            f.Cpu.MaxCoreLoadPct = 93.4;
            Assert.Equal("93 %", TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null).CpuMaxCoreText);
        }

        [Test]
        public static void PowerLimited_NamesWindowsEfficiencyModeOrBattery()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Cpu = new CpuSample();
            f.Power = PowerPlanNames.Named("381b4222-f694-41f0-9685-ff5bb260df2e", "961cc777-2547-4f9d-8174-7d86181b8a7a", false);
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.PowerLimited, "limite de desempenho em 60 %", null);
            Assert.Equal("limite de desempenho em 60 %; o Windows está no modo de energia de eficiência", v.ThrottleEvidence);
            f.Power = PowerPlanNames.Named("381b4222-f694-41f0-9685-ff5bb260df2e", PowerPlanNames.NoOverlay, true);
            Assert.Equal("o notebook está na bateria", TelemetryViewBuilder.Build(f, ThrottleState.PowerLimited, null, null).ThrottleEvidence);
            Assert.Null(TelemetryViewBuilder.Build(f, ThrottleState.TemperatureLimited, null, null).ThrottleEvidence);
        }

        [Test]
        public static void GpuLine_FromNvidiaSmiSample()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Gpu = NvidiaSmiParser.Parse("NVIDIA GeForce RTX 2050, 98, 78, 3120, 4096, 1470, 2100, 55.20, 60.00, 0x0000000000000004, P0");
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal(GpuState.Active, v.GpuState);
            Assert.Equal("GPU 98 % · VRAM 3,0 / 4,0 GB · 1.470 MHz · 55 W · limitada por energia", v.GpuLine);
            Assert.Equal("limitada por energia", v.GpuThrottleText);
            Assert.Equal("nvidia-smi", v.GpuSource);
        }

        [Test]
        public static void GpuLine_IdleHidesTheNoLimitText()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Gpu = NvidiaSmiParser.Parse("NVIDIA GeForce RTX 2050, 0, 47.84, 0, 4096, 210, 2100, 3.17, [N/A], 0x0000000000000001, P8");
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal("GPU 0 % · VRAM 0,0 / 4,0 GB · 210 MHz · 3,2 W", v.GpuLine);
            Assert.Equal("sem limitação", v.GpuThrottleText);
        }

        [Test]
        public static void GpuLine_FromCounters_HasNoClockOrPower()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Gpu = new GpuSample();
            f.Gpu.Source = GpuSample.SourceCounters;
            f.Gpu.UsagePct = 12;
            f.Gpu.VramUsedMiB = 800;
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal("GPU 12 % · VRAM 0,8 GB", v.GpuLine);
            Assert.Null(v.GpuThrottleText);
        }

        [Test]
        public static void CpuClock_PrefersTheBusyCoresOverTheAverageOfAllThreads()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Cpu = new CpuSample();
            f.Cpu.EffectiveMHz = 2500;   // 4 busy threads of 12 pull the average down
            f.Cpu.BusyClockMHz = 4500;
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal("4,5 GHz", v.CpuClockText);
            Assert.Equal("Clock 4,5 GHz · Normal", v.CpuLine);
        }

        [Test]
        public static void MeasuredGpuActivity_BeatsARestingFlag()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.GpuSleeping = true; // firmware sensor reads 0 ...
            f.Gpu = new GpuSample();
            f.Gpu.Source = GpuSample.SourceCounters;
            f.Gpu.UsagePct = 98; // ... but the counters show a game
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal(GpuState.Active, v.GpuState);
            Assert.True(!v.GpuLine.Contains("repouso"), v.GpuLine);
            Assert.True(v.GpuFirmwareOff, "the sensor state is still reported for the gauge");
        }

        [Test]
        public static void GpuSleeping_IsRestingAndSaysSo()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.GpuSleeping = true;
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal(GpuState.Resting, v.GpuState);
            Assert.Equal("GPU em repouso", v.GpuStateText);
            Assert.Equal("GPU em repouso", v.GpuLine);
            Assert.True(v.GpuFirmwareOff, "the firmware sensor says the GPU is off");
        }

        [Test]
        public static void GpuIdle_ShowsCounterNumbersAndRestingText_WithoutTouchingTheGauge()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.GpuIdle = true; // dGPU not active by any driver-free signal: nvidia-smi was not started
            f.Gpu = new GpuSample();
            f.Gpu.Source = GpuSample.SourceCounters;
            f.Gpu.UsagePct = 0;
            f.Gpu.VramUsedMiB = 0;
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal(GpuState.Resting, v.GpuState);
            Assert.Equal("GPU em repouso", v.GpuStateText);
            Assert.Equal("GPU em repouso · GPU 0 % · VRAM 0,0 GB", v.GpuLine);
            Assert.Equal("0 %", v.GpuUsageText);
            Assert.Equal("0,0 GB", v.GpuVramText);
            Assert.Equal("contadores do Windows", v.GpuSource);
            Assert.True(!v.GpuFirmwareOff, "the temperature sensor still reads: the gauge keeps showing it");
        }

        [Test]
        public static void GpuIdle_WithoutAnyCounter_StillSaysResting()
        {
            TelemetryFrame f = new TelemetryFrame();
            f.GpuIdle = true;
            TelemetryView v = TelemetryViewBuilder.Build(f, ThrottleState.Normal, null, null);
            Assert.Equal(GpuState.Resting, v.GpuState);
            Assert.Equal("GPU em repouso", v.GpuLine);
        }

        [Test]
        public static void EffectLine_ByStatus()
        {
            ModeSwitchEffect ready = new ModeSwitchEffect();
            ready.Status = EffectStatus.Ready;
            ready.To = PerfMode.Desempenho;
            ready.Verdict = "Desempenho: +18 % de clock sob carga";
            Assert.Equal("Desempenho: +18 % de clock sob carga", TelemetryViewBuilder.EffectLine(ready));

            ModeSwitchEffect m = new ModeSwitchEffect();
            m.Status = EffectStatus.Measuring;
            m.To = PerfMode.Silencioso;
            m.Verdict = "ainda medindo (faltam 25 s)";
            Assert.Equal("Troca para Silencioso: ainda medindo (faltam 25 s)", TelemetryViewBuilder.EffectLine(m));

            ModeSwitchEffect n = new ModeSwitchEffect();
            n.Status = EffectStatus.NoLoad;
            n.To = PerfMode.Desempenho;
            n.Verdict = "sem carga para comparar";
            Assert.Equal("Troca para Desempenho: sem carga para comparar", TelemetryViewBuilder.EffectLine(n));

            Assert.Null(TelemetryViewBuilder.EffectLine(new ModeSwitchEffect()));
            Assert.Null(TelemetryViewBuilder.EffectLine(null));
        }

        [Test]
        public static void ModesView_NoBenchmark_SaysNoMeasurement()
        {
            ModesView v = ModesView.Build(PerfScoreboard.Empty(), null);
            Assert.Equal("sem medição", v.CardLines[0]);
            Assert.Equal("sem medição", v.CardLines[1]);
            Assert.Equal("sem medição", v.CardLines[2]);
            Assert.Equal("sem medição", v.Caption);
            Assert.Null(v.EffectLine);
            ModesView n = ModesView.Build(null, null);
            Assert.Equal("sem medição", n.CardLines[2]);
        }

        [Test]
        public static void ModesView_IndexMatchesTheCardOrder()
        {
            Assert.Equal(0, ModesView.Index(PerfMode.Silencioso));
            Assert.Equal(1, ModesView.Index(PerfMode.DiaADia));
            Assert.Equal(2, ModesView.Index(PerfMode.Desempenho));
        }

        [Test]
        public static void ModesView_CarriesTheScoreboardLinesAndNote()
        {
            string json = "{\"startedUtc\":\"2026-11-16T17:05:00Z\",\"aborted\":true,\"preconditions\":{\"onBattery\":false},"
                + "\"modes\":[{\"mode\":\"Dia a dia\",\"applied\":true,\"aborted\":false,\"multiSustained\":118.5}],"
                + "\"verdict\":{\"reliable\":true,\"bandPct\":3}}";
            ModesView v = ModesView.Build(PerfScoreboard.Parse(json), null);
            Assert.Equal("Pontuação 118,5 (referência)", v.CardLines[1]);
            Assert.Equal("sem medição", v.CardLines[2]);
            Assert.True(v.Caption.StartsWith("medido em "), v.Caption);
            Assert.True(v.Caption.EndsWith("; o teste foi interrompido"), v.Caption);
        }
    }
}
