using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // Owns the monitoring widget: settings (widget.json), placement per monitor, and the cost rules.
    //
    // Cost: the widget asks the telemetry layer for samples ONLY while it is on screen (Acquire /
    // Release, the same switch the Monitor page uses: one 1 Hz sampler thread). Hidden, off, or hidden
    // by a full-screen game, it holds nothing and starts nothing. The firmware tick the tray already
    // runs (2 s while the widget shows, otherwise as before) is the only thing that looks at the
    // full-screen state to bring it back. UI thread only.
    internal sealed class WidgetController : IDisposable
    {
        private readonly TrayApp app;
        private WidgetSettings settings;
        private WidgetForm form;
        private bool suppressed;      // a full-screen game / presentation is in front
        private bool acquired;
        private long ramUsed;
        private long ramTotal;
        private bool disposed;
        private readonly SynchronizationContext ui;
        // Monitors come and go in bursts of events: one re-place, a moment after the last one.
        private readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer();

        // The widget was switched or its settings changed (tray, key, its own menu, Configurações): the
        // settings cards refresh from it.
        public event Action Changed;

        public WidgetController(TrayApp app, WidgetSettings initial)
        {
            this.app = app;
            ui = SynchronizationContext.Current;
            settings = (initial ?? new WidgetSettings()).Copy().Normalize();
            app.Telemetry.StatsArrived += OnStats;
            displayTimer.Interval = 700;
            displayTimer.Tick += delegate { displayTimer.Stop(); ReplaceAfterDisplayChange(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        // True while the tray should keep its 2 s firmware tick for us: the widget is on screen, or is
        // waiting out a full-screen game and has to notice that it ended.
        public bool NeedsFastTick { get { return Shown || (settings.Visible && suppressed); } }

        // On screen right now.
        public bool Shown { get { return form != null && form.Visible && !form.IsDisposed; } }

        // The user wants it (it may still be out of sight because of a full-screen game).
        public bool WantVisible { get { return settings.Visible; } }

        public bool Suppressed { get { return suppressed; } }

        public WidgetSettings SettingsCopy() { return settings.Copy(); }

        public void Start()
        {
            Apply();
        }

        public void SetVisible(bool on)
        {
            if (settings.Visible == on) return;
            settings.Visible = on;
            Save();
            if (on) suppressed = OverlayPolicy.HideWidget(ScreenInfo.NotifyState());
            Apply();
            RaiseChanged();
        }

        public void Toggle()
        {
            SetVisible(!settings.Visible);
        }

        // From Configurações or the widget's own menu.
        public void ApplySettings(WidgetSettings s)
        {
            WidgetSettings n = (s ?? new WidgetSettings()).Copy().Normalize();
            // The position list belongs to the controller (the drag), not to the settings card.
            n.Positions = settings.Positions;
            n.LastMonitor = settings.LastMonitor;
            bool visibleChanged = n.Visible != settings.Visible;
            settings = n;
            Save();
            if (visibleChanged && settings.Visible) suppressed = OverlayPolicy.HideWidget(ScreenInfo.NotifyState());
            Apply();
            if (form != null && !form.IsDisposed) ApplyToForm();
            RaiseChanged();
        }

        // One question to the shell, one place: has a game gone full screen, or left? Applies the change.
        // Returns true when the widget was shown or hidden because of it.
        private bool CheckSuppression()
        {
            bool hide = OverlayPolicy.HideWidget(ScreenInfo.NotifyState());
            if (hide == suppressed) return false;
            suppressed = hide;
            Apply();
            return true;
        }

        // Firmware tick: only while the widget is NOT on screen (hidden by a game): the 1 Hz sample asks
        // while it shows. TrayApp keeps this tick at 2 s meanwhile (NeedsFastTick).
        public void OnTick()
        {
            if (disposed || !settings.Visible || Shown) return;
            CheckSuppression();
        }

        private void Save()
        {
            try { OverlayStore.SaveWidget(settings); }
            catch (Exception) { }
        }

        private void Apply()
        {
            if (disposed) return;
            bool want = settings.Visible && !suppressed;
            if (want)
            {
                EnsureForm();
                ApplyToForm();
                if (!form.Visible)
                {
                    Place();
                    form.Show(); // ShowWithoutActivation: never takes the focus
                    Reclamp();   // the window's real DPI is known now
                }
                if (!acquired)
                {
                    acquired = true;
                    app.Telemetry.Acquire(this, GpuWant.Counters); // never nvidia-smi: it stays for the Monitor page
                }
                Refresh();
            }
            else
            {
                if (form != null && !form.IsDisposed && form.Visible) form.Hide();
                if (acquired)
                {
                    acquired = false;
                    app.Telemetry.Release(this);
                }
            }
            app.UpdatePollRate();
        }

        private void EnsureForm()
        {
            if (form != null && !form.IsDisposed) return;
            form = new WidgetForm();
            form.PositionCommitted += OnDropped;
            form.Rescaled += Reclamp;
            form.HideRequested += delegate { SetVisible(false); };
            form.AlwaysOnTopChosen += delegate(bool v) { ChangeFromMenu(delegate(WidgetSettings s) { s.AlwaysOnTop = v; }); };
            form.ClickThroughChosen += delegate(bool v) { ChangeFromMenu(delegate(WidgetSettings s) { s.ClickThrough = v; }); };
            form.OpacityChosen += delegate(int v) { ChangeFromMenu(delegate(WidgetSettings s) { s.OpacityPct = v; }); };
        }

        private void ChangeFromMenu(Action<WidgetSettings> change)
        {
            WidgetSettings s = settings.Copy();
            change(s);
            ApplySettings(s);
        }

        private void ApplyToForm()
        {
            if (form.TopMost != settings.AlwaysOnTop) form.TopMost = settings.AlwaysOnTop; // a repeat would re-order the windows
            form.OpacityPct = settings.OpacityPct;
            form.ClickThrough = settings.ClickThrough;
        }

        // Opens on the monitor it was last on, where it was left, kept fully inside its work area. The size
        // is the one at the DPI of THAT monitor (a 150 % screen gets a 1,5 times wider window), so the clamp
        // is right at any scale before the window exists.
        private void Place()
        {
            List<MonitorInfo> monitors = ScreenInfo.Monitors();
            WidgetPlace p = WidgetPlacement.Resolve(settings, monitors, SizeOn);
            if (p == null) return;
            form.SetBounds(p.Location.X, p.Location.Y, p.Size.Width, p.Size.Height);
        }

        private static Size SizeOn(MonitorInfo m)
        {
            return WidgetRenderer.SizeFor(ScreenInfo.DpiOf(m.Bounds));
        }

        // The window is where it is (just shown, or its DPI changed on a move between monitors): pull it
        // back fully inside the work area of the monitor that holds it. Moves only when it hangs out.
        private void Reclamp()
        {
            if (disposed || form == null || form.IsDisposed || !form.Visible) return;
            MonitorInfo m = WidgetPlacement.MonitorOf(form.Bounds, ScreenInfo.Monitors());
            if (m == null) return;
            Point p = WidgetPlacement.Clamp(m.WorkArea, form.Size, form.Location);
            if (p != form.Location) form.Location = p;
        }

        // The user dropped the widget: remember where, relative to its monitor. Saves only when it moved.
        private void OnDropped()
        {
            if (form == null || form.IsDisposed) return;
            List<MonitorInfo> monitors = ScreenInfo.Monitors();
            MonitorInfo m = WidgetPlacement.MonitorOf(form.Bounds, monitors);
            if (m == null) return;
            Point p = WidgetPlacement.Clamp(m.WorkArea, form.Size, form.Location);
            if (p != form.Location) form.Location = p; // never left half off screen
            int x = form.Location.X - m.Bounds.X;
            int y = form.Location.Y - m.Bounds.Y;
            WidgetPosition old = settings.PositionOn(m.Id);
            if (old != null && old.X == x && old.Y == y && settings.LastMonitor == m.Id) return; // a click, not a move
            settings.RememberPosition(m.Id, x, y);
            Save();
        }

        // SystemEvents delivers this on ITS thread, never the UI one: only hand over to the UI thread (it
        // starts the debounce timer); the form is not touched here.
        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (disposed || ui == null) return;
            ui.Post(delegate
            {
                if (disposed) return;
                displayTimer.Stop();
                displayTimer.Start();
            }, null);
        }

        // UI thread, 700 ms after the last display event (Screen.AllScreens is fresh by then).
        private void ReplaceAfterDisplayChange()
        {
            if (disposed || form == null || form.IsDisposed || !form.Visible) return;
            try { Place(); Reclamp(); }
            catch (Exception ex) { Program.LogError(ex); }
        }

        // ---- data (1 Hz, only while the telemetry sampler runs for us or for a page) ----

        private void OnStats(StatsSnapshot s)
        {
            if (disposed || !Shown) return;
            if (s != null)
            {
                ramUsed = s.RamUsed;
                ramTotal = s.RamTotal;
            }
            // Cheap (one shell call), asked here once a second while shown: a game that went full screen
            // hides the widget within a second.
            if (CheckSuppression()) return;
            Refresh();
        }

        public void Refresh()
        {
            if (disposed || !Shown) return;
            try
            {
                form.SetData(WidgetModel.Build(app.Latest, app.Telemetry.CurrentView(), app.ModeLabel, ramUsed, ramTotal));
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            displayTimer.Stop();
            displayTimer.Dispose();
            try { app.Telemetry.StatsArrived -= OnStats; }
            catch (Exception) { }
            if (acquired)
            {
                acquired = false;
                try { app.Telemetry.Release(this); }
                catch (Exception) { }
            }
            if (form != null && !form.IsDisposed) form.Dispose();
            form = null;
        }
    }
}
