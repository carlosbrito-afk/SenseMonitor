using System;
using System.Drawing;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;

namespace Forja.App
{
    // Monitor page, mockup D: two gauge + readout panels (CPU | GPU), two 10 min charts
    // (usage + temperature) and the memory panel with "Limpar RAM".
    //
    // Threading: temperatures and fan RPM come from the firmware tick (TrayApp, UI thread);
    // usage and RAM come from the StatsPoller thread, marshaled here. The 10 min history holds
    // one sample per second (600 = 10 min), paced by SamplePacer: a 1 s timer feeds it while the
    // page is active; while the window is hidden only the firmware tick does (2 s, or 12 s when
    // the fans are Automático: the pacer repeats the reading for each elapsed second), so the tray
    // app runs no extra timer.
    //
    // Live telemetry (CPU clock, power, throttle state, power plan; GPU usage/VRAM/clock/power/
    // limit) comes from TelemetryHub as plain text (TelemetryView) shown in a two-line label under
    // each readout. The hub owns the poller thread: this page only acquires it while it is active.
    public sealed class MonitorPage : Page
    {
        private const int StatsFreshMs = 2500;

        private readonly TrayApp app;
        private readonly TempHistory history = new TempHistory();
        private readonly SamplePacer pacer = new SamplePacer();
        private readonly PanelBox cpuPanel = new PanelBox();
        private readonly PanelBox gpuPanel = new PanelBox();
        private readonly PanelBox cpuChartPanel = new PanelBox();
        private readonly PanelBox gpuChartPanel = new PanelBox();
        private readonly PanelBox ramPanel = new PanelBox();
        private readonly InstrumentGauge cpuGauge = new InstrumentGauge();
        private readonly InstrumentGauge gpuGauge = new InstrumentGauge();
        private readonly InstrumentReadout cpuReadout = new InstrumentReadout();
        private readonly InstrumentReadout gpuReadout = new InstrumentReadout();
        private readonly ComponentChart cpuChart = new ComponentChart();
        private readonly ComponentChart gpuChart = new ComponentChart();
        private readonly MemoryMeter memory = new MemoryMeter();
        private readonly NoteText cleanNote = new NoteText();
        private readonly FlatButton clean = new FlatButton();
        private readonly Timer sampler = new Timer();
        private readonly TelemetryHub hub;
        private readonly InfoText cpuInfo = new InfoText();
        private readonly InfoText gpuInfo = new InfoText();
        private StatsSnapshot stats;
        private int statsAt;
        private bool active;

        public MonitorPage(TrayApp app)
        {
            this.app = app;
            cpuGauge.SensorName = "CPU";
            gpuGauge.SensorName = "GPU";
            cpuReadout.Title = "CPU";
            gpuReadout.Title = "GPU";
            cpuReadout.LiveText = "1 s";
            gpuReadout.LiveText = "1 s";
            cpuChart.Part = Hardware.Cpu;
            gpuChart.Part = Hardware.Gpu;
            cpuChart.History = history;
            gpuChart.History = history;

            cpuPanel.Controls.Add(cpuGauge);
            cpuPanel.Controls.Add(cpuReadout);
            cpuPanel.Controls.Add(cpuInfo);
            gpuPanel.Controls.Add(gpuGauge);
            gpuPanel.Controls.Add(gpuReadout);
            gpuPanel.Controls.Add(gpuInfo);
            cpuChartPanel.Controls.Add(cpuChart);
            gpuChartPanel.Controls.Add(gpuChart);

            clean.Text = "Limpar RAM";
            clean.Icon = NavIcon.Broom;
            clean.BackColor = Palette.Panel;
            clean.Click += delegate { app.CleanRam(false); };
            cleanNote.BackColor = Palette.Panel;
            ramPanel.Controls.Add(memory);
            ramPanel.Controls.Add(cleanNote);
            ramPanel.Controls.Add(clean);

            Controls.AddRange(new Control[] { cpuPanel, gpuPanel, cpuChartPanel, gpuChartPanel, ramPanel });

            sampler.Interval = 1000;
            sampler.Tick += delegate { Sample(); };
            hub = app.Telemetry;
            hub.StatsArrived += OnStats;
            app.CleaningChanged += UpdateCleaning;
            UpdateCleaning();
            RefreshReadouts();
        }

        public override void SetActive(bool value)
        {
            if (value == active) return;
            active = value;
            if (active)
            {
                hub.Acquire(this, true);
                sampler.Start();
                Sample();
                RefreshReadouts();
                cpuChart.DataChanged();
                gpuChart.DataChanged();
            }
            else
            {
                sampler.Stop();
                hub.Release(this);
                stats = null; // stale numbers are not shown on the next open
            }
        }

        public override void Shutdown()
        {
            sampler.Stop();
            sampler.Dispose();
            hub.Release(this);
            hub.StatsArrived -= OnStats; // the hub itself is stopped by TrayApp.Cleanup
            app.CleaningChanged -= UpdateCleaning;
        }

        public override void OnFirmwareTick()
        {
            if (active) RefreshReadouts();
            else Sample(); // hidden: the 2 s tick keeps the 10 min history going
        }

        private void OnStats(StatsSnapshot s)
        {
            stats = s;
            statsAt = Environment.TickCount;
            if (active) RefreshReadouts();
        }

        private StatsSnapshot FreshStats
        {
            get
            {
                StatsSnapshot s = stats;
                if (s == null || unchecked(Environment.TickCount - statsAt) > StatsFreshMs) return null;
                return s;
            }
        }

        // GPU temperature: the firmware sensor, or nvidia-smi while the page polls it.
        public double? GpuTempFor(SensorReading r)
        {
            if (InstrumentMath.IsValidTemp(r.GpuTempC)) return r.GpuTempC;
            StatsSnapshot s = FreshStats;
            if (s != null && s.Gpu != null && InstrumentMath.IsValidTemp(s.Gpu.TempC)) return s.Gpu.TempC;
            return r.GpuTempC;
        }

        private double? CpuUse()
        {
            StatsSnapshot s = FreshStats;
            return s != null && s.CpuReady ? (double?)s.Cpu : null;
        }

        private double? GpuUse()
        {
            StatsSnapshot s = FreshStats;
            if (s == null || !s.GpuReady || s.Gpu == null || !s.Gpu.UsagePct.HasValue) return null;
            return s.Gpu.UsagePct.Value;
        }

        // UI thread. Adds as many 1 s samples as are due (usage is a gap while the poller sleeps).
        private void Sample()
        {
            int due = pacer.Due(Environment.TickCount);
            if (due <= 0) return;
            SensorReading r = app.Latest;
            double? gpuT = GpuTempFor(r), cpuU = CpuUse(), gpuU = GpuUse();
            for (int i = 0; i < due; i++) history.Add(r.CpuTempC, gpuT, cpuU, gpuU);
            if (!active) return;
            RefreshReadouts();
            cpuChart.DataChanged();
            gpuChart.DataChanged();
        }

        private void RefreshReadouts()
        {
            SensorReading r = app.Latest;
            double? gpuT = GpuTempFor(r);
            TelemetryView tv = hub.CurrentView();
            cpuGauge.SetReading(r.CpuTempC, history.Peak(Hardware.Cpu, Metric.Temp), false);
            gpuGauge.SetReading(gpuT, history.Peak(Hardware.Gpu, Metric.Temp), tv.GpuFirmwareOff); // "em repouso" only when the firmware sensor says the GPU is off
            cpuInfo.SetInfo(tv.PowerContextLine == null ? tv.CpuLine : tv.CpuLine + Environment.NewLine + tv.PowerContextLine, ThrottleText.IsLimited(tv.Throttle));
            gpuInfo.SetInfo(tv.GpuLine, false);
            FillReadout(cpuReadout, CpuUse(), r.CpuFanRpm, history.Peak(Hardware.Cpu, Metric.Temp));
            FillReadout(gpuReadout, GpuUse(), r.GpuFanRpm, history.Peak(Hardware.Gpu, Metric.Temp));

            StatsSnapshot s = FreshStats;
            if (s != null && s.RamTotal > 0) memory.SetMemory(s.RamUsed / 1073741824.0, s.RamTotal / 1073741824.0);
            else if (!active) memory.SetMemory(null, null);
        }

        private static void FillReadout(InstrumentReadout ro, double? use, int? rpm, double? peak)
        {
            double? u = InstrumentMath.SanitizeUsage(use);
            ro.SetRow(0, "Uso", u.HasValue ? ((int)Math.Round(u.Value, MidpointRounding.AwayFromZero)).ToString() : InstrumentMath.Dash, "%");
            ro.SetRow(1, "Ventoinha", rpm.HasValue && rpm.Value >= 0 ? InstrumentMath.FormatGrouped(rpm.Value) : InstrumentMath.Dash, "rpm");
            ro.SetRow(2, "Pico 10 min", InstrumentMath.FormatTemp(peak), "°C");
        }

        private void UpdateCleaning()
        {
            bool wasCleaning = !clean.Enabled;
            clean.Enabled = !app.Cleaning && !app.ReadOnly;
            clean.Text = app.Cleaning ? "Limpando…" : "Limpar RAM";
            cleanNote.Text = app.ReadOnly ? SafetyText.ReadOnlyShort : app.Cleaning ? "Isso pode levar alguns segundos" : app.LastCleanText;
            if (wasCleaning && !app.Cleaning) hub.Kick(); // show the drop right away
        }

        // Logical layout of mockup D (860 px window): 14 px gutter, two 322 px columns, 12 px gaps.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.S(14);
            int gap = Theme.S(12);
            int w = Math.Max(Theme.S(200), Width - pad * 2);
            int col = (w - gap) / 2;
            int y = pad;

            int infoH = Theme.S(30); // two lines of telemetry text under each readout
            int ih = Theme.S(154) + infoH;
            cpuPanel.SetBounds(pad, y, col, ih);
            gpuPanel.SetBounds(pad + col + gap, y, w - col - gap, ih);
            LayoutInstrument(cpuPanel, cpuGauge, cpuReadout, cpuInfo);
            LayoutInstrument(gpuPanel, gpuGauge, gpuReadout, gpuInfo);
            y += ih + gap;

            int ch = Theme.S(206) - infoH;
            cpuChartPanel.SetBounds(pad, y, col, ch);
            gpuChartPanel.SetBounds(pad + col + gap, y, w - col - gap, ch);
            cpuChart.SetBounds(Theme.S(15), Theme.S(11), cpuChartPanel.Width - Theme.S(30), ch - Theme.S(18));
            gpuChart.SetBounds(Theme.S(15), Theme.S(11), gpuChartPanel.Width - Theme.S(30), ch - Theme.S(18));
            y += ch + gap;

            int rh = Theme.S(68);
            ramPanel.SetBounds(pad, y, w, rh);
            int bw = Theme.S(124);
            int bh = Theme.S(34);
            int right = ramPanel.Width - Theme.S(14);
            clean.SetBounds(right - bw, (rh - bh) / 2, bw, bh);
            int noteW = Theme.S(230);
            int noteX = clean.Left - Theme.S(14) - noteW;
            cleanNote.SetBounds(noteX, Theme.S(8), noteW, rh - Theme.S(16));
            memory.SetBounds(Theme.S(15), Theme.S(13), Math.Max(Theme.S(60), noteX - Theme.S(18) - Theme.S(15)), Theme.S(MemoryMeter.LogicalHeight));
        }

        private static void LayoutInstrument(PanelBox panel, InstrumentGauge gauge, InstrumentReadout readout, InfoText info)
        {
            int side = Theme.S(InstrumentGauge.LogicalSize);
            gauge.SetBounds(Theme.S(9), Theme.S(11), side, side);
            readout.SetBounds(Theme.S(153), Theme.S(11), Math.Max(Theme.S(40), panel.Width - Theme.S(168)), side);
            int top = Theme.S(11) + side + Theme.S(3);
            info.SetBounds(Theme.S(15), top, Math.Max(Theme.S(40), panel.Width - Theme.S(30)), Math.Max(Theme.S(16), panel.Height - top - Theme.S(3)));
        }

        // Up to two lines of live telemetry text (left aligned, ellipsized); amber while limited.
        private sealed class InfoText : Control
        {
            private bool warn;

            public InfoText()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Palette.Panel;
                AccessibleRole = AccessibleRole.StaticText;
            }

            public void SetInfo(string text, bool limited)
            {
                text = text ?? "";
                if (text == Text && limited == warn) return;
                warn = limited;
                Text = text;
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                AccessibleName = Text;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                if (string.IsNullOrEmpty(Text)) return;
                Font f = Theme.Font(9f, false);
                TextFormatFlags fl = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(e.Graphics, Text, f, new Rectangle(0, 0, Width, Height), warn ? Palette.Warn : Palette.Sec, fl);
            }
        }

        // Right-aligned, vertically centered result text of the RAM cleaner (up to two lines).
        private sealed class NoteText : Control
        {
            public NoteText()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                AccessibleRole = AccessibleRole.StaticText;
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                AccessibleName = Text;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                if (string.IsNullOrEmpty(Text)) return;
                Font f = Theme.Font(9.5f, false);
                TextFormatFlags fl = TextFormatFlags.Right | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                int h = Math.Min(Height, TextRenderer.MeasureText(e.Graphics, Text, f, new Size(Width, int.MaxValue), fl).Height);
                TextRenderer.DrawText(e.Graphics, Text, f, new Rectangle(0, (Height - h) / 2, Width, h), Palette.Sec, fl);
            }
        }
    }
}
