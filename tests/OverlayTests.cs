using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The on-screen message and the monitoring widget: timeline, layout, texts, placement, policy.
    public static class OverlayTests
    {
        // ---- OSD ----

        [Test]
        public static void OsdTimeline_FadesInHoldsAndFadesOut()
        {
            Assert.Equal(0.0, OsdTimeline.Alpha(0));
            Assert.True(Math.Abs(OsdTimeline.Alpha(60) - 0.5) < 1e-9, "half way in");
            Assert.Equal(1.0, OsdTimeline.Alpha(OsdTimeline.FadeInMs));
            Assert.Equal(1.0, OsdTimeline.Alpha(1000));
            Assert.Equal(1.0, OsdTimeline.Alpha(OsdTimeline.HoldUntilMs - 1));
            Assert.True(Math.Abs(OsdTimeline.Alpha(OsdTimeline.HoldUntilMs + 150) - 0.5) < 1e-9, "half way out");
            Assert.Equal(0.0, OsdTimeline.Alpha(OsdTimeline.TotalMs));
            Assert.True(!OsdTimeline.IsDone(OsdTimeline.TotalMs - 1), "not done");
            Assert.True(OsdTimeline.IsDone(OsdTimeline.TotalMs), "done");
            Assert.True(OsdTimeline.HoldUntilMs >= 1400 && OsdTimeline.HoldUntilMs <= 1600, "about 1,5 s");
            double last = -1;
            for (int t = 0; t <= OsdTimeline.FadeInMs; t += 10)
            {
                double a = OsdTimeline.Alpha(t);
                Assert.True(a >= last, "fade in never goes down");
                last = a;
            }
        }

        [Test]
        public static void OsdTimeline_ANewMessageContinuesFromTheCurrentOpacity()
        {
            Assert.Equal(0, OsdTimeline.StartOffsetFor(0.0));
            Assert.Equal(OsdTimeline.FadeInMs, OsdTimeline.StartOffsetFor(1.0));
            Assert.Equal(OsdTimeline.FadeInMs / 2, OsdTimeline.StartOffsetFor(0.5)); // fading in or out: no flash to 0
            Assert.Equal(OsdTimeline.FadeInMs, OsdTimeline.StartOffsetFor(1.7));
        }

        [Test]
        public static void OsdLayout_BottomCenter_AndTopRight_AtSeveralDpis()
        {
            Rectangle work = new Rectangle(0, 0, 1920, 1040);
            foreach (int dpi in new int[] { 96, 120, 144 })
            {
                Size box = OsdLayout.BoxSize(new Size(200, 24), dpi, work);
                Point bc = OsdLayout.Place(work, box, OsdAnchor.BottomCenter, dpi);
                Assert.Equal((1920 - box.Width) / 2, bc.X);
                Assert.True(bc.Y + box.Height < work.Bottom, "above the edge");
                Assert.True(work.Bottom - (bc.Y + box.Height) >= (int)Math.Round(OsdLayout.EdgeMargin * dpi / 96.0) - 1, "margin grows with the DPI");
                Point tr = OsdLayout.Place(work, box, OsdAnchor.TopRight, dpi);
                Assert.True(tr.X + box.Width <= work.Right && tr.Y >= work.Top, "inside, top right");
                Assert.True(tr.X > work.Width / 2, "on the right");
            }
        }

        [Test]
        public static void OsdLayout_BoxIsBiggerThanTheText_HasAMinimumAndAMaximum()
        {
            Rectangle work = new Rectangle(0, 0, 1000, 600);
            Size small = OsdLayout.BoxSize(new Size(40, 20), 96, work);
            Assert.Equal(OsdLayout.MinWidth, small.Width);
            Assert.Equal(20 + OsdLayout.PadY * 2, small.Height);
            Size normal = OsdLayout.BoxSize(new Size(300, 20), 96, work);
            Assert.Equal(300 + OsdLayout.PadX * 2, normal.Width);
            Size huge = OsdLayout.BoxSize(new Size(5000, 20), 96, work);
            Assert.Equal(800, huge.Width); // 80 % of the area
        }

        [Test]
        public static void OsdLayout_OnASecondMonitorWithNegativeCoordinates()
        {
            Rectangle work = new Rectangle(-1920, 100, 1920, 1000);
            Size box = new Size(300, 60);
            Point p = OsdLayout.Place(work, box, OsdAnchor.BottomCenter, 96);
            Assert.True(p.X >= work.Left && p.X + box.Width <= work.Right, "inside horizontally");
            Assert.True(p.Y >= work.Top && p.Y + box.Height <= work.Bottom, "inside vertically");
            Point tiny = OsdLayout.Place(new Rectangle(0, 0, 100, 50), new Size(300, 60), OsdAnchor.BottomCenter, 96);
            Assert.Equal(0, tiny.X); // bigger than the area: pinned to its top-left
            Assert.Equal(0, tiny.Y);
        }

        // ---- overlay policy ----

        [Test]
        public static void OverlayPolicy_WidgetHidesOnlyOverExclusiveFullscreenPresentationAndLock()
        {
            Assert.True(OverlayPolicy.HideWidget(3), "Direct3D full screen");
            Assert.True(OverlayPolicy.HideWidget(4), "presentation");
            Assert.True(OverlayPolicy.HideWidget(1), "locked");
            Assert.True(!OverlayPolicy.HideWidget(5), "normal");
            Assert.True(!OverlayPolicy.HideWidget(2), "borderless full-screen window keeps the widget");
            Assert.True(!OverlayPolicy.HideWidget(6), "quiet time");
            Assert.True(!OverlayPolicy.HideWidget(7), "store app");
        }

        [Test]
        public static void OverlayPolicy_OsdIsShownOverGames_NotOverPresentations()
        {
            Assert.True(!OverlayPolicy.HideOsd(3), "game");
            Assert.True(!OverlayPolicy.HideOsd(2), "full screen");
            Assert.True(!OverlayPolicy.HideOsd(5), "normal");
            Assert.True(OverlayPolicy.HideOsd(4), "presentation");
            Assert.True(OverlayPolicy.HideOsd(1), "locked");
        }

        [Test]
        public static void GlobalHotkeys_RegisterAndUnregister_AnUnusualCombination()
        {
            using (GlobalHotkeys h = new GlobalHotkeys())
            {
                HotkeyBinding b = new HotkeyBinding();
                b.Id = 77;
                b.Action = KeyAction.None;
                b.Modifiers = Hotkeys.ModControl | Hotkeys.ModAlt | 0x4 | Hotkeys.ModNoRepeat; // + Shift
                b.Vk = 0x87; // F24
                b.Text = "Ctrl+Alt+Shift+F24";
                bool ok = h.Register(b);
                // always checked, whichever way it went (a session with no desktop may refuse)
                Assert.Equal(ok, h.IsRegistered(77));
                if (ok)
                {
                    Assert.Equal(0, h.LastError);
                    Assert.True(h.Register(b), "registering the same binding again is a no-op");
                    h.UnregisterAll();
                    Assert.True(!h.IsRegistered(77), "unregistered");
                }
                else
                {
                    Assert.True(h.LastError != 0, "a refusal carries its error code");
                    Assert.True(Hotkeys.FailureText(b.Text, h.LastError).StartsWith(b.Text), "and a sentence");
                }
                // when another program owns it, Register simply returns false (never forced)
                Assert.True(!h.Register(null), "null");
            }
        }

        // ---- widget text ----

        private static TelemetryView ViewOf(double? cpu, double? gpu, bool resting)
        {
            TelemetryFrame f = new TelemetryFrame();
            f.Cpu = new CpuSample();
            f.Cpu.LoadPct = cpu;
            if (gpu.HasValue)
            {
                f.Gpu = new GpuSample();
                f.Gpu.UsagePct = gpu;
            }
            f.GpuSleeping = resting;
            return TelemetryViewBuilder.Build(f, ThrottleState.Unknown, null, null);
        }

        [Test]
        public static void WidgetModel_FormatsTheSixValues()
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = 62.4;
            r.GpuTempC = 71.6;
            r.CpuFanRpm = 2800;
            r.GpuFanRpm = 3100;
            long gib = 1024L * 1024 * 1024;
            WidgetData d = WidgetModel.Build(r, ViewOf(38.2, 12.0, false), "Dia a dia", 12 * gib, 16 * gib);
            Assert.Equal("62 °C", d.CpuTemp);
            Assert.Equal("72 °C", d.GpuTemp);
            Assert.Equal("38 %", d.CpuUsage);
            Assert.Equal("12 %", d.GpuUsage);
            Assert.Equal("2.800 / 3.100 rpm", d.Fan);
            Assert.Equal("Dia a dia", d.Mode);
            Assert.Equal("12,0 / 16,0 GB", d.Ram);
            Assert.True(Math.Abs(d.RamFraction.Value - 0.75) < 1e-9, "fraction");
            Assert.Equal(TempState.Neutral, d.CpuTempState);
        }

        [Test]
        public static void WidgetModel_MissingReadings_AreDashesNeverZeros()
        {
            WidgetData d = WidgetModel.Build(null, null, null, 0, 0);
            Assert.Equal("—", d.CpuTemp);
            Assert.Equal("—", d.GpuTemp);
            Assert.Equal("—", d.CpuUsage);
            Assert.Equal("—", d.GpuUsage);
            Assert.Equal("—", d.Fan);
            Assert.Equal("—", d.Mode);
            Assert.Equal("—", d.Ram);
            Assert.Null(d.RamFraction);
            SensorReading r = new SensorReading();
            r.CpuTempC = 0; // the firmware's "no sensor"
            r.CpuFanRpm = 2500;
            WidgetData e = WidgetModel.Build(r, null, "Silencioso", 5, 4); // used > total: unreadable
            Assert.Equal("—", e.CpuTemp);
            Assert.Equal("2.500 / — rpm", e.Fan);
            Assert.Equal("—", e.Ram);
            Assert.Equal(TempState.Unavailable, e.CpuTempState);
        }

        [Test]
        public static void WidgetModel_GpuAsleep_SaysRestNotAZeroTemperature()
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = 55;
            r.GpuTempC = 0;
            WidgetData d = WidgetModel.Build(r, ViewOf(10, null, true), "Dia a dia", 0, 0);
            Assert.Equal("—", d.GpuTemp);
            Assert.Equal("repouso", d.GpuUsage);
            Assert.Equal(TempState.Sleeping, d.GpuTempState);
        }

        [Test]
        public static void WidgetModel_HotTemperatureColorsFollowTheGauges()
        {
            SensorReading r = new SensorReading();
            r.CpuTempC = 82;
            r.GpuTempC = 90;
            WidgetData d = WidgetModel.Build(r, null, "Desempenho", 0, 0);
            Assert.Equal(TempState.Amber, d.CpuTempState);
            Assert.Equal(TempState.Hot, d.GpuTempState);
        }

        // ---- widget settings and placement ----

        [Test]
        public static void WidgetSettings_Defaults_Normalize_RoundTrip()
        {
            WidgetSettings s = new WidgetSettings();
            Assert.True(!s.Visible, "hidden until the user turns it on");
            Assert.True(s.AlwaysOnTop, "always visible by default");
            Assert.True(!s.ClickThrough, "draggable by default");
            Assert.Equal(90, s.OpacityPct);
            s.OpacityPct = 5;
            Assert.Equal(WidgetSettings.MinOpacity, s.Normalize().OpacityPct);
            s.OpacityPct = 400;
            Assert.Equal(WidgetSettings.MaxOpacity, s.Normalize().OpacityPct);

            string dir = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "widget.json");
            try
            {
                s.Visible = true;
                s.ClickThrough = true;
                s.OpacityPct = 70;
                s.RememberPosition(@"\\.\DISPLAY2", 40, 50);
                Assert.True(OverlayStore.SaveWidget(path, s), "saved");
                WidgetSettings b = OverlayStore.LoadWidget(path);
                Assert.True(b.Visible && b.ClickThrough, "flags");
                Assert.Equal(70, b.OpacityPct);
                Assert.Equal(@"\\.\DISPLAY2", b.LastMonitor);
                Assert.Equal(40, b.PositionOn(@"\\.\DISPLAY2").X);
                Assert.Equal(50, b.PositionOn(@"\\.\DISPLAY2").Y);
                File.WriteAllText(path, "{ quebrado");
                Assert.True(!OverlayStore.LoadWidget(path).Visible, "damaged file: defaults");
                File.WriteAllText(path, "{\"OpacityPct\":55}");
                WidgetSettings p = OverlayStore.LoadWidget(path);
                Assert.Equal(55, p.OpacityPct);
                Assert.True(p.AlwaysOnTop, "member missing from an older file keeps its default");
                Assert.Equal(0, p.Positions.Count);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void WidgetSettings_RememberPosition_PerMonitor_ReplacesAndCaps()
        {
            WidgetSettings s = new WidgetSettings();
            s.RememberPosition("A", 1, 2);
            s.RememberPosition("B", 3, 4);
            s.RememberPosition("A", 5, 6);
            Assert.Equal(2, s.Positions.Count);
            Assert.Equal(5, s.PositionOn("A").X);
            Assert.Equal("A", s.LastMonitor);
            for (int i = 0; i < 20; i++) s.RememberPosition("M" + i, i, i);
            Assert.True(s.Positions.Count <= 8, "capped");
            Assert.True(s.PositionOn("M19") != null, "newest kept");
            s.RememberPosition("", 1, 1);
            Assert.True(s.PositionOn("") == null, "no empty ids");
            WidgetSettings c = s.Copy();
            c.RememberPosition("Z", 9, 9);
            Assert.True(s.PositionOn("Z") == null, "the copy is independent");
        }

        private static List<MonitorInfo> TwoMonitors()
        {
            List<MonitorInfo> l = new List<MonitorInfo>();
            l.Add(new MonitorInfo("M1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true));
            l.Add(new MonitorInfo("M2", new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), false));
            return l;
        }

        [Test]
        public static void WidgetPlacement_FirstRun_TopRightOfThePrimaryWorkArea()
        {
            WidgetPlace p = WidgetPlacement.Resolve(new WidgetSettings(), TwoMonitors(), new Size(220, 180));
            Assert.Equal("M1", p.Monitor);
            Assert.Equal(1920 - 220 - WidgetPlacement.DefaultMargin, p.Location.X);
            Assert.Equal(WidgetPlacement.DefaultMargin, p.Location.Y);
        }

        [Test]
        public static void WidgetPlacement_RemembersEachMonitorSeparately()
        {
            WidgetSettings s = new WidgetSettings();
            s.RememberPosition("M1", 100, 200);
            s.RememberPosition("M2", 300, 400); // last used
            WidgetPlace p = WidgetPlacement.Resolve(s, TwoMonitors(), new Size(220, 180));
            Assert.Equal("M2", p.Monitor);
            Assert.Equal(1920 + 300, p.Location.X);
            Assert.Equal(400, p.Location.Y);
            s.LastMonitor = "M1";
            WidgetPlace q = WidgetPlacement.Resolve(s, TwoMonitors(), new Size(220, 180));
            Assert.Equal(100, q.Location.X);
            Assert.Equal(200, q.Location.Y);
        }

        [Test]
        public static void WidgetPlacement_UnpluggedMonitor_FallsBackToThePrimary_AndStaysOnScreen()
        {
            WidgetSettings s = new WidgetSettings();
            s.RememberPosition("M2", 2400, 1300); // M2 was big and is gone
            List<MonitorInfo> only = new List<MonitorInfo>();
            only.Add(TwoMonitors()[0]);
            WidgetPlace p = WidgetPlacement.Resolve(s, only, new Size(220, 180));
            Assert.Equal("M1", p.Monitor);
            Assert.True(new Rectangle(0, 0, 1920, 1040).Contains(new Rectangle(p.Location, new Size(220, 180))), "inside the work area");
        }

        [Test]
        public static void WidgetPlacement_SmallerResolution_PullsTheWidgetBackInside()
        {
            WidgetSettings s = new WidgetSettings();
            s.RememberPosition("M1", 1800, 1000);
            List<MonitorInfo> small = new List<MonitorInfo>();
            small.Add(new MonitorInfo("M1", new Rectangle(0, 0, 1280, 720), new Rectangle(0, 0, 1280, 680), true));
            WidgetPlace p = WidgetPlacement.Resolve(s, small, new Size(220, 180));
            Assert.Equal(1280 - 220, p.Location.X);
            Assert.Equal(680 - 180, p.Location.Y);
        }

        // physical size of the widget at a scale (236 x 168 logical, as WidgetRenderer.SizeFor)
        private static Size At(int dpi)
        {
            return new Size((int)Math.Round(236 * dpi / 96.0), (int)Math.Round(168 * dpi / 96.0));
        }

        [Test]
        public static void WidgetPlacement_FirstRun_On150PercentMonitor_IsFullyInside_ByTheMonitorsOwnSize()
        {
            List<MonitorInfo> ms = new List<MonitorInfo>();
            ms.Add(new MonitorInfo("M1", new Rectangle(0, 0, 2880, 1800), new Rectangle(0, 0, 2880, 1740), true)); // 150 %
            WidgetPlace p = WidgetPlacement.Resolve(new WidgetSettings(), ms, delegate(MonitorInfo m) { return At(144); });
            Assert.Equal(At(144), p.Size);
            Assert.True(ms[0].WorkArea.Contains(new Rectangle(p.Location, p.Size)), "fully inside at 150 %");
            // the old way (the 96-DPI size) would have hung 118 px off the right edge once the window grew
            WidgetPlace old = WidgetPlacement.Resolve(new WidgetSettings(), ms, At(96));
            Assert.True(!ms[0].WorkArea.Contains(new Rectangle(old.Location, At(144))), "the 96-DPI placement does not fit the real window");
        }

        [Test]
        public static void WidgetPlacement_EachMonitorUsesItsOwnScale_AtAnyScaleAndSavedPosition()
        {
            List<MonitorInfo> ms = new List<MonitorInfo>();
            ms.Add(new MonitorInfo("A", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true));        // 100 %
            ms.Add(new MonitorInfo("B", new Rectangle(1920, 0, 3000, 2000), new Rectangle(1920, 0, 3000, 1960), false)); // 125 %
            Func<MonitorInfo, Size> sizeFor = delegate(MonitorInfo m) { return m.Id == "A" ? At(96) : At(120); };
            foreach (int dx in new int[] { 0, 100, 2000, 2900, 5000 })
            {
                foreach (string id in new string[] { "A", "B" })
                {
                    WidgetSettings s = new WidgetSettings();
                    s.RememberPosition(id, dx, dx / 2 + 900);
                    WidgetPlace p = WidgetPlacement.Resolve(s, ms, sizeFor);
                    MonitorInfo m = ms[id == "A" ? 0 : 1];
                    Assert.Equal(id, p.Monitor);
                    Assert.Equal(sizeFor(m), p.Size);
                    Assert.True(m.WorkArea.Contains(new Rectangle(p.Location, p.Size)), "inside " + id + " for offset " + dx);
                }
            }
        }

        [Test]
        public static void Hotkeys_FailureText_OnlyCode1409MeansInUse()
        {
            Assert.Equal("Ctrl+Alt+M: já em uso por outro programa", Hotkeys.FailureText("Ctrl+Alt+M", 1409));
            Assert.Equal("Ctrl+Alt+M: não foi possível registrar (erro 5)", Hotkeys.FailureText("Ctrl+Alt+M", 5));
        }

        [Test]
        public static void WidgetPlacement_NoMonitors_IsNull_MonitorOfPicksTheBiggestOverlap()
        {
            Assert.Null(WidgetPlacement.Resolve(new WidgetSettings(), new List<MonitorInfo>(), new Size(10, 10)));
            Assert.Null(WidgetPlacement.MonitorOf(new Rectangle(0, 0, 10, 10), null));
            List<MonitorInfo> ms = TwoMonitors();
            Assert.Equal("M2", WidgetPlacement.MonitorOf(new Rectangle(1900, 100, 220, 180), ms).Id); // 20 px on M1, 200 on M2
            Assert.Equal("M1", WidgetPlacement.MonitorOf(new Rectangle(100, 100, 220, 180), ms).Id);
            Assert.Equal("M1", WidgetPlacement.MonitorOf(new Rectangle(5000, 5000, 220, 180), ms).Id); // off every monitor: primary
        }
    }
}
