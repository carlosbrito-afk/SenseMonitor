using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // Fan page around a FanController. Self-contained: the shell supplies the controller,
    // the Config instance and persistence/telemetry delegates.
    //
    // FanController.Mode is assigned ONLY from user actions here (never from RefreshState),
    // so the controller's fallback notice (Auto after a failure) stays visible.
    // Call RefreshState() from the 2 s loop (after fan.Tick) and when the page is shown.
    public class FanPage : UserControl
    {
        public const string NoTempText = "Curva indisponível: aguardando a leitura de temperatura";
        public const string MaxFailText = "Não foi possível ativar o modo máximo";
        public const string MaxLostText = "Máximo desativado: o firmware voltou ao automático";
        public const string AutoFailText = "Não foi possível voltar ao automático";

        private enum View { Auto, Manual, Curve, Max }

        private readonly FanController fan;
        private readonly Config cfg;
        private readonly Action<Config> save;
        private readonly Func<double?> getTemp;
        private readonly bool manualAllowed;
        private readonly Func<bool> setFanMax;
        private readonly Func<bool> setFanAuto;
        private readonly Func<bool?> isFanMax;

        private readonly PlainLabel title;
        private readonly PlainLabel status;
        private readonly PlainLabel info;
        private readonly SegmentButton[] tabs = new SegmentButton[4];
        private readonly View[] tabViews = new View[4];
        private readonly PlainLabel cpuLabel;
        private readonly PlainLabel gpuLabel;
        private readonly PlainLabel cpuValue;
        private readonly PlainLabel gpuValue;
        private readonly FlatSlider cpuSlider;
        private readonly FlatSlider gpuSlider;
        private readonly CurveEditor editor;
        private readonly PlainLabel hint;
        private readonly SegmentButton restore;
        private readonly Font titleFont;
        private readonly Font bodyFont;
        private readonly Font monoFont;

        private const int MaxCheckMs = 2000;
        private bool maxActive;
        private int maxAutoBase;
        private int lastMaxCheck;
        private string maxMessage;
        private bool syncing;

        // fan: the controller (same thread only). cfg/save: the shared Config and a callback
        // that persists it (this page never calls Config.Save itself). getTemp: hottest valid
        // temperature for the curve marker, or null.
        // manualAllowed: true only after the hardware validation passed (Config.ManualFanAllowed).
        // Until then the page offers ONLY Automático, with a note that manual control comes after
        // a quick test with the user. When allowed: Automático, Manual, Curva and Máximo; Máximo
        // is applied through setFanMax (FanBehavior(Max, Max)) and undone with setFanAuto
        // (null setFanMax = Máximo disabled).
        // isFanMax: firmware read-back (WmiFirmware.IsFanMax): true/false, or null when unreadable.
        // RefreshState drops "Máximo" when it reports false. May be null (then only a forced
        // Auto from the controller drops it).
        // Max is dropped only when the firmware read reports not-Max/Max (read at most every 2 s,
        // only while Max is active) or when fan.AutoWriteCount shows the controller wrote Auto
        // since Max was applied.
        // THREADING: every delegate (setFanMax, setFanAuto, isFanMax, getTemp, save) is invoked
        // on the calling thread and must run on the same thread as fan.Tick; the shell guarantees it.
        // SHELL NOTE: cfg.FanMode "Max" must NOT be restored at startup without re-applying
        // it (setFanMax); the firmware starts in Auto and the controller asserts Auto.
        public FanPage(FanController fan, Config cfg, Action<Config> save, Func<double?> getTemp,
            bool manualAllowed, Func<bool> setFanMax, Func<bool> setFanAuto, Func<bool?> isFanMax)
        {
            if (fan == null) throw new ArgumentNullException("fan");
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (save == null) throw new ArgumentNullException("save");
            this.fan = fan;
            this.cfg = cfg;
            this.save = save;
            this.getTemp = getTemp;
            this.manualAllowed = manualAllowed;
            this.setFanMax = setFanMax;
            this.isFanMax = isFanMax;
            this.setFanAuto = setFanAuto;

            // Load the saved values only (never the mode: that is the shell's call, after
            // the first valid temperature, and the controller starts in Auto).
            fan.ManualCpuPct = cfg.ManualCpuPct;
            fan.ManualGpuPct = cfg.ManualGpuPct;
            if (cfg.Curve != null) fan.Curve = new FanCurve { Points = CopyPoints(cfg.Curve) };

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = FanPageColors.Bg;
            ForeColor = FanPageColors.Text;
            titleFont = FanPageColors.Ui(9f, FontStyle.Bold);
            bodyFont = FanPageColors.Ui(9.5f, FontStyle.Regular);
            monoFont = FanPageColors.Mono(10f, FontStyle.Regular);

            title = MakeLabel("VENTOINHAS", titleFont, Theme.Text);
            title.ThemePoints = 9f; // same heading face as the other pages
            title.ThemeSemibold = true;
            status = MakeLabel("", bodyFont, FanPageColors.Sec); // sentences: UI face, numerals stay mono below
            info = MakeLabel("", bodyFont, FanPageColors.Sec);
            info.Wrap = true;
            hint = MakeLabel("Arraste os pontos · duplo clique adiciona · botão direito remove (mínimo 2)",
                bodyFont, FanPageColors.Mute);

            if (manualAllowed)
            {
                tabViews[0] = View.Auto;
                tabViews[1] = View.Manual;
                tabViews[2] = View.Curve;
                tabViews[3] = View.Max;
                tabs[0] = new SegmentButton("Automático");
                tabs[1] = new SegmentButton("Manual");
                tabs[2] = new SegmentButton("Curva");
                tabs[3] = new SegmentButton("Máximo");
            }
            else
            {
                // Not validated on this hardware yet: Automático only.
                tabViews[0] = View.Auto;
                tabs[0] = new SegmentButton("Automático");
                tabs[1] = null;
                tabs[2] = null;
                tabs[3] = null;
            }
            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] == null) continue;
                tabs[i].Tag = tabViews[i];
                tabs[i].Click += OnTabClick;
                Controls.Add(tabs[i]);
            }

            cpuLabel = MakeLabel("CPU", titleFont, FanPageColors.Sec);
            gpuLabel = MakeLabel("GPU", titleFont, FanPageColors.Sec);
            cpuValue = MakeLabel("", monoFont, FanPageColors.Text);
            gpuValue = MakeLabel("", monoFont, FanPageColors.Text);
            cpuValue.TextAlign = ContentAlignment.MiddleRight;
            gpuValue.TextAlign = ContentAlignment.MiddleRight;
            cpuSlider = new FlatSlider(30, 100);
            gpuSlider = new FlatSlider(30, 100);
            cpuSlider.AccessibleName = "Ventoinha da CPU";
            gpuSlider.AccessibleName = "Ventoinha da GPU";
            cpuSlider.ValueChanged += OnSliderChanged;
            gpuSlider.ValueChanged += OnSliderChanged;
            cpuSlider.ValueCommitted += OnSliderCommitted;
            gpuSlider.ValueCommitted += OnSliderCommitted;
            Controls.Add(cpuSlider);
            Controls.Add(gpuSlider);

            editor = new CurveEditor();
            editor.CurveChanged += OnCurveChanged;
            editor.CurveCommitted += OnCurveCommitted;
            Controls.Add(editor);

            restore = new SegmentButton("Restaurar padrão");
            restore.Click += OnRestore;
            Controls.Add(restore);

            Size = new Size(640, 420);
            RefreshState();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                bodyFont.Dispose();
                monoFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private PlainLabel MakeLabel(string text, Font font, Color color)
        {
            PlainLabel l = new PlainLabel();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            Controls.Add(l);
            return l;
        }

        private int S(int v)
        {
            return (int)Math.Round(v * DeviceDpi / 96.0);
        }

        // Which view the controls reflect: always derived from the controller state.
        private View CurrentView()
        {
            if (maxActive) return View.Max;
            if (fan.Mode == FanUserMode.Manual) return View.Manual;
            if (fan.Mode == FanUserMode.Curve) return View.Curve;
            return View.Auto;
        }

        // ---- layout ----

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // Called while the constructor is still adding controls (restore is added last).
            if (restore == null) return;
            int pad = S(14);
            int w = Math.Max(10, ClientSize.Width - pad * 2);
            int y = pad;
            int th = TextRenderer.MeasureText("Ag", titleFont).Height + S(4);
            title.SetBounds(pad, y, w, th);
            y += th + S(8);

            int tabH = S(40);
            int gap = S(8);
            int count = 0;
            for (int i = 0; i < tabs.Length; i++) { if (tabs[i] != null) count++; }
            int tw = Math.Min(S(180), (w - gap * (count - 1)) / count);
            int x = pad;
            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] == null) continue;
                tabs[i].SetBounds(x, y, tw, tabH);
                x += tw + gap;
            }
            y += tabH + S(10);

            int sh = TextRenderer.MeasureText("Ag", bodyFont).Height + S(4);
            status.SetBounds(pad, y, w, sh);
            y += sh + S(12);

            int bodyTop = y;
            int bodyBottom = ClientSize.Height - pad;

            // info text (Auto / Max / locked / no temperature)
            int ih = TextRenderer.MeasureText("Ag", bodyFont).Height * 3 + S(6);
            info.SetBounds(pad, bodyTop, w, ih);

            // manual sliders
            int rowH = S(36);
            int labelW = S(44);
            int valueW = Math.Max(S(56), TextRenderer.MeasureText("100%", monoFont).Width + S(8));
            int sliderW = Math.Max(S(40), w - labelW - valueW - S(16));
            LayoutSliderRow(cpuLabel, cpuSlider, cpuValue, pad, bodyTop, rowH, labelW, sliderW, valueW, S(8));
            LayoutSliderRow(gpuLabel, gpuSlider, gpuValue, pad, bodyTop + rowH + S(6), rowH, labelW, sliderW, valueW, S(8));

            // curve editor + footer
            int footerH = S(32);
            int ey = bodyTop;
            int eh = Math.Max(S(120), bodyBottom - ey - footerH - S(8));
            editor.SetBounds(pad, ey, w, eh);
            int fy = ey + eh + S(8);
            int rw = Math.Max(S(120), TextRenderer.MeasureText(restore.Text, bodyFont).Width + S(32));
            restore.SetBounds(pad + w - rw, fy, rw, footerH);
            hint.SetBounds(pad, fy, Math.Max(10, w - rw - S(12)), footerH);
            hint.TextAlign = ContentAlignment.MiddleLeft;
            // Manual view shares the restore button row below the sliders.
            if (CurrentView() == View.Manual)
            {
                int my = bodyTop + (rowH + S(6)) * 2 + S(8);
                restore.SetBounds(pad, my, rw, footerH);
            }
        }

        private static void LayoutSliderRow(Label name, FlatSlider slider, Label value, int x, int y, int h,
            int labelW, int sliderW, int valueW, int gap)
        {
            name.SetBounds(x, y, labelW, h);
            name.TextAlign = ContentAlignment.MiddleLeft;
            slider.SetBounds(x + labelW + gap, y, sliderW, h);
            value.SetBounds(x + labelW + gap + sliderW + gap, y, valueW, h);
        }

        // ---- state ----

        // Reads the fan state and updates every control. Never assigns fan.Mode.
        public void RefreshState()
        {
            ReconcileMax();
            View v = CurrentView();

            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] == null) continue;
                View tv = tabViews[i];
                tabs[i].Selected = (tv == v);
                bool enabled = true;
                if (tv == View.Curve) enabled = fan.CurveAvailable;
                else if (tv == View.Max) enabled = setFanMax != null;
                tabs[i].Locked = !enabled;
            }

            status.Text = maxActive && maxMessage == null ? "Máximo" : (maxMessage ?? fan.Status);
            status.ForeColor = (maxMessage != null || (!maxActive && fan.StatusIsWarning))
                ? FanPageColors.Warn : FanPageColors.Sec;

            bool showSliders = v == View.Manual;
            bool showCurve = v == View.Curve;
            cpuLabel.Visible = gpuLabel.Visible = cpuSlider.Visible = gpuSlider.Visible = showSliders;
            cpuValue.Visible = gpuValue.Visible = showSliders;
            editor.Visible = hint.Visible = showCurve;
            restore.Visible = showSliders || showCurve;

            string infoText = null;
            if (v == View.Auto)
            {
                infoText = "O firmware controla as ventoinhas. É o modo mais seguro e o padrão do app.";
                if (!manualAllowed) infoText += "\n" + FanValidation.LockedNote + ".";
                else if (!fan.CurveAvailable) infoText += "\n" + NoTempText;
            }
            else if (v == View.Max) infoText = "Ventoinhas no máximo. Mais ruído e mais refrigeração; volte ao Automático quando quiser.";
            info.Text = infoText ?? "";
            info.ForeColor = FanPageColors.Sec;
            info.Visible = infoText != null && !showSliders && !showCurve;

            syncing = true;
            try
            {
                cpuSlider.Value = fan.ManualCpuPct;
                gpuSlider.Value = fan.ManualGpuPct;
                cpuValue.Text = cpuSlider.Value + "%";
                gpuValue.Text = gpuSlider.Value + "%";
                if (!editor.IsDragging) editor.Curve = fan.Curve;
            }
            finally
            {
                syncing = false;
            }
            editor.CurrentTempC = SafeTemp();
            PerformLayout();
        }

        // "Máximo" lives in the firmware and the controller does not know about it, so the
        // page must confirm it. It is dropped (with a notice and the persisted mode reset to
        // Auto) when the controller forced Auto (failure retry, suspend, shutdown) or when the
        // firmware read-back no longer reports Max on both fans. An unreadable firmware keeps it.
        private void ReconcileMax()
        {
            if (!maxActive) return;
            // (b) the controller wrote Auto to the firmware since Max was applied.
            bool lost = fan.AutoWriteCount != maxAutoBase;
            // (a) the firmware itself reports it is no longer Max/Max (read failure keeps
            // the state). The read is throttled to once per 2 s while Max is active.
            if (!lost && isFanMax != null && unchecked(Environment.TickCount - lastMaxCheck) >= MaxCheckMs)
            {
                lastMaxCheck = Environment.TickCount;
                bool? still;
                try { still = isFanMax(); }
                catch (Exception) { still = null; }
                lost = still.HasValue && !still.Value;
            }
            if (!lost) return;
            maxActive = false;
            maxMessage = MaxLostText;
            // Persist once (maxActive is false from here on, so this does not repeat).
            if (cfg.FanMode != "Auto")
            {
                cfg.FanMode = "Auto";
                Persist();
            }
        }

        private double? SafeTemp()
        {
            if (getTemp == null) return null;
            try { return getTemp(); }
            catch (Exception) { return null; }
        }

        // Cheap refresh of the live parts only (status line + temperature marker).
        // While the page is hidden: only the "Máximo" reconcile (throttled firmware read) and
        // the status text; no control updates or layout.
        public void RefreshHidden()
        {
            ReconcileMax();
            status.Text = maxActive && maxMessage == null ? "Máximo" : (maxMessage ?? fan.Status);
        }

        public void RefreshLive()
        {
            if (fan.Mode == FanUserMode.Auto) { RefreshState(); return; }
            editor.CurrentTempC = SafeTemp();
            status.Text = maxActive && maxMessage == null ? "Máximo" : (maxMessage ?? fan.Status);
        }

        // ---- user actions ----

        private void Persist()
        {
            try { save(cfg); }
            catch (Exception) { }
        }

        // Shell entry points (tray menu): the same rules as clicking the tabs.
        public void ChooseAuto() { SelectView(View.Auto); }
        public void ChooseMax() { SelectView(View.Max); }

        public bool MaxActive { get { return maxActive; } }

        // Short pt-BR name of what drives the fans now ("Automática", "Máximo", "Manual", "Curva").
        public string ModeLabel
        {
            get
            {
                View v = CurrentView();
                if (v == View.Max) return "Máximo";
                if (v == View.Manual) return "Manual";
                if (v == View.Curve) return "Curva";
                return "Automática";
            }
        }

        private void OnTabClick(object sender, EventArgs e)
        {
            SegmentButton b = sender as SegmentButton;
            if (b == null || b.Locked || !(b.Tag is View)) return;
            SelectView((View)b.Tag);
        }

        public bool ManualAllowed { get { return manualAllowed; } }

        // Raised after every user choice of fan mode (tab or tray), so the shell can arm the
        // watchdog and the poll rate at once instead of on the next firmware tick.
        public event Action ModeChosen;

        private void SelectView(View target)
        {
            try { ApplyView(target); }
            finally
            {
                Action h = ModeChosen;
                if (h != null) h();
            }
        }

        private void ApplyView(View target)
        {
            maxMessage = null;
            if (!manualAllowed && target != View.Auto)
            {
                // Not validated: nothing but Automático (tray or a stale caller).
                RefreshState();
                return;
            }

            if (target == View.Max)
            {
                if (setFanMax == null) { RefreshState(); return; }
                // Leave any controller-driven mode first (writes Auto), then ask for Max.
                // Assert Auto first (also when already Auto), so no Auto write can follow Max.
                fan.Mode = FanUserMode.Auto;
                bool ok;
                try { ok = setFanMax(); }
                catch (Exception) { ok = false; }
                if (ok)
                {
                    maxActive = true;
                    maxAutoBase = fan.AutoWriteCount;
                    lastMaxCheck = Environment.TickCount;
                    cfg.FanMode = "Max";
                }
                else
                {
                    maxActive = false;
                    maxMessage = MaxFailText;
                    TryAuto();
                }
                Persist();
                RefreshState();
                return;
            }

            if (maxActive)
            {
                // Max lives in the firmware, outside the controller: hand the fans back first.
                if (!TryAuto())
                {
                    maxMessage = AutoFailText;
                    RefreshState();
                    return;
                }
                maxActive = false;
            }

            if (target == View.Auto)
            {
                fan.Mode = FanUserMode.Auto;
                cfg.FanMode = "Auto";
            }
            else if (target == View.Manual)
            {
                fan.ManualCpuPct = cfg.ManualCpuPct;
                fan.ManualGpuPct = cfg.ManualGpuPct;
                fan.Mode = FanUserMode.Manual;
                cfg.FanMode = "Manual";
            }
            else if (target == View.Curve)
            {
                fan.Curve = new FanCurve { Points = CopyPoints(cfg.Curve) };
                fan.Mode = FanUserMode.Curve;
                // The controller refuses Curve without a valid temperature and falls back to Auto.
                cfg.FanMode = fan.Mode == FanUserMode.Curve ? "Curve" : "Auto";
            }
            Persist();
            RefreshState();
        }

        private bool TryAuto()
        {
            if (setFanAuto == null) return true;
            try { return setFanAuto(); }
            catch (Exception) { return false; }
        }

        private static List<CurvePoint> CopyPoints(List<CurvePoint> src)
        {
            List<CurvePoint> l = new List<CurvePoint>();
            if (src != null)
            {
                foreach (CurvePoint p in src)
                {
                    if (p != null) l.Add(new CurvePoint(p.TempC, p.Pct));
                }
            }
            return l;
        }

        private void OnSliderChanged(object sender, EventArgs e)
        {
            if (syncing) return;
            fan.ManualCpuPct = cpuSlider.Value;
            fan.ManualGpuPct = gpuSlider.Value;
            cpuValue.Text = cpuSlider.Value + "%";
            gpuValue.Text = gpuSlider.Value + "%";
        }

        private void OnSliderCommitted(object sender, EventArgs e)
        {
            if (syncing) return;
            cfg.ManualCpuPct = fan.ManualCpuPct;
            cfg.ManualGpuPct = fan.ManualGpuPct;
            Persist();
        }

        private void OnCurveChanged(object sender, EventArgs e)
        {
            if (syncing) return;
            fan.Curve = editor.Curve;
        }

        private void OnCurveCommitted(object sender, EventArgs e)
        {
            if (syncing) return;
            FanCurve c = editor.Curve.Normalize();
            fan.Curve = c;
            cfg.Curve = c.Points;
            Persist();
        }

        private void OnRestore(object sender, EventArgs e)
        {
            View v = CurrentView();
            if (v == View.Curve)
            {
                FanCurve d = FanCurve.Default();
                fan.Curve = d;
                cfg.Curve = d.Points;
            }
            else if (v == View.Manual)
            {
                fan.ManualCpuPct = 50;
                fan.ManualGpuPct = 50;
                cfg.ManualCpuPct = 50;
                cfg.ManualGpuPct = 50;
            }
            else return;
            Persist();
            RefreshState();
        }
    }
}
