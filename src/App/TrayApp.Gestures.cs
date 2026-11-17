using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // NitroSense-key gestures, global shortcuts, the on-screen message and the monitoring widget.
    // Kept out of TrayApp.cs: the main file only has the few lines that call into it.
    //
    // Every gesture / shortcut goes through KeyActionPlanner, which looks at the SAME state the pages
    // use (read-only mode, fan validation, the Desempenho warning): a locked capability shows a short
    // "Bloqueado: ..." message instead of writing, and nothing ever opens a dialog over a game. Mode and
    // fan changes run the pages' own code paths (MainForm.ChooseMode / ChooseFanMax / ChooseFanAuto), so
    // the journal, the watchdog arming and the failure alert are the same as a click.
    public sealed partial class TrayApp : IOverlayHost
    {
        private GestureSettings gestureSettings = new GestureSettings();
        private OsdWindow osd;
        private WidgetController widget;
        private GlobalHotkeys hotkeys;
        private string hotkeyStatus;
        private bool cleanFromGesture;

        // Any flow that owns the firmware or listens to the key itself (the installer, a tool) sets this so a
        // press or a hotkey does not also run its mapped action. The environment wizard needs no flag: it is
        // covered below (its window is open, or it owns the firmware).
        public bool GesturesSuspended { get; set; }

        // True while key gestures and hotkeys must do nothing at all: no action, no message, no journal line.
        private bool GesturesBlocked
        {
            get { return exiting || GesturesSuspended || envOwns || wizardForm != null; }
        }

        public event Action OverlayChanged;

        // Constructor, after the window, telemetry and key hook exist.
        private void InitGestures()
        {
            gestureSettings = OverlayStore.LoadGestures();
            osd = new OsdWindow();
            osd.Enabled = gestureSettings.OsdEnabled;
            ApplyRecognizerOptions(); // the hook itself is built by InstallKeyHook (follows the profile)
            hotkeys = new GlobalHotkeys();
            hotkeys.Pressed += OnHotkey;
            ApplyHotkeys();
            CleaningChanged += OnCleaningForGesture;
            widget = new WidgetController(this, OverlayStore.LoadWidget());
            widget.Changed += RaiseOverlayChanged;
            // After the message loop starts: a window shown inside the constructor would race it.
            ui.Post(delegate { if (!exiting && widget != null) widget.Start(); }, null);
        }

        private void ShutdownGestures()
        {
            CleaningChanged -= OnCleaningForGesture;
            if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
            if (widget != null) { widget.Dispose(); widget = null; }
            if (osd != null) { osd.Dispose(); osd = null; }
        }

        // Firmware tick: has a full-screen game hidden or released the widget?
        private void WidgetTick()
        {
            if (widget != null) widget.OnTick();
        }

        // The firmware tick stays at 2 s while the widget shows, or waits out a full-screen game.
        private bool WidgetShownNow { get { return widget != null && widget.NeedsFastTick; } }

        // ---- settings (Configurações) -------------------------------------------------------------

        public GestureSettings GestureSettingsCopy() { return gestureSettings.Copy(); }

        public void ApplyGestureSettings(GestureSettings s)
        {
            gestureSettings = (s ?? new GestureSettings()).Copy().Normalize();
            OverlayStore.SaveGestures(gestureSettings);
            if (osd != null) osd.Enabled = gestureSettings.OsdEnabled;
            ApplyRecognizerOptions();
            ApplyHotkeys();
            Safety.Journal.Write(JournalKind.Config, "tecla: 1 toque=" + KeyActions.Name(gestureSettings.Get(NitroGesture.Tap1))
                + ", 2 toques=" + KeyActions.Name(gestureSettings.Get(NitroGesture.Tap2))
                + ", 3 toques=" + KeyActions.Name(gestureSettings.Get(NitroGesture.Tap3))
                + ", segurar=" + KeyActions.Name(gestureSettings.Get(NitroGesture.Long))
                + "; atalhos " + (gestureSettings.HotkeysEnabled ? "ligados" : "desligados")
                + "; aviso na tela " + (gestureSettings.OsdEnabled ? "ligado" : "desligado"));
        }

        public string HotkeyStatus { get { return hotkeyStatus; } }

        public WidgetSettings WidgetSettingsCopy()
        {
            return widget != null ? widget.SettingsCopy() : OverlayStore.LoadWidget();
        }

        public void ApplyWidgetSettings(WidgetSettings s)
        {
            if (widget == null) return;
            widget.ApplySettings(s); // raises Changed
        }

        public bool WidgetSuppressed { get { return widget != null && widget.WantVisible && widget.Suppressed; } }

        public bool WidgetWanted { get { return widget != null && widget.WantVisible; } }

        // Tray menu, key gesture, shortcut.
        public void ToggleWidget()
        {
            if (exiting || widget == null) return;
            widget.Toggle(); // raises Changed
        }

        private void RaiseOverlayChanged()
        {
            Action h = OverlayChanged;
            if (h != null) h();
        }

        private void ApplyRecognizerOptions()
        {
            if (keyHook == null) return; // this notebook has no such key: nothing is listened for
            GestureRecognizer r = keyHook.Filter.Recognizer;
            // Held = long press only when the user switched it on AND mapped it to something.
            r.LongPressEnabled = gestureSettings.Get(NitroGesture.Long) != KeyAction.None;
            // No double / triple mapped: the shorter gesture fires at once instead of waiting for one.
            r.DoubleTapEnabled = gestureSettings.Get(NitroGesture.Tap2) != KeyAction.None;
            r.TripleTapEnabled = gestureSettings.Get(NitroGesture.Tap3) != KeyAction.None;
        }

        private void ApplyHotkeys()
        {
            if (hotkeys == null) return;
            hotkeys.UnregisterAll();
            hotkeyStatus = null;
            if (!gestureSettings.HotkeysEnabled) return;
            List<string> failed = hotkeys.RegisterAll(Hotkeys.Defaults);
            if (failed.Count > 0)
            {
                hotkeyStatus = string.Join("; ", failed.ToArray());
                Safety.Journal.Write(JournalKind.Config, "atalhos globais não registrados: " + hotkeyStatus);
            }
        }

        // ---- gestures and shortcuts ---------------------------------------------------------------

        private void OnKeyGesture(NitroGesture g)
        {
            if (GesturesBlocked) return;
            KeyAction a = gestureSettings.Get(g);
            string how = KeyActions.GestureLabel(g);
            KeyHook hook = keyHook;
            string trace = hook == null ? "" : hook.Filter.Recognizer.LastTrace;
            Safety.Journal.Write(JournalKind.State, "tecla NitroSense: " + how + " -> " + KeyActions.Name(a) + (trace.Length > 0 ? " (" + trace + ")" : ""));
            if (a == KeyAction.None) return;
            RunKeyAction(a, "tecla");
        }

        private void OnHotkey(int id)
        {
            if (GesturesBlocked || !gestureSettings.HotkeysEnabled) return;
            foreach (HotkeyBinding b in Hotkeys.Defaults)
            {
                if (b.Id == id)
                {
                    RunKeyAction(b.Action, "atalho " + b.Text);
                    return;
                }
            }
        }

        // UI thread. Plans first (pure), then does exactly what the plan says.
        private void RunKeyAction(KeyAction a, string source)
        {
            if (GesturesBlocked || form == null || form.IsDisposed) return;
            KeyActionPlan plan = KeyActionPlanner.Plan(a, BuildKeyContext(a));
            switch (plan.Kind)
            {
                case PlanKind.Nothing:
                    return;
                case PlanKind.Blocked:
                    Safety.Journal.Write(JournalKind.Blocked, source + ": " + KeyActions.Name(a) + " bloqueado (" + plan.Text + ")");
                    ShowOsd(plan.Text, OsdKind.Blocked);
                    return;
                case PlanKind.Info:
                    ShowOsd(plan.Text, OsdKind.Info);
                    return;
                case PlanKind.ToggleWindow:
                    ToggleMainWindow();
                    return;
                case PlanKind.ToggleWidget:
                    ToggleWidget();
                    ShowOsd(widget == null ? "Widget indisponível" : (widget.Shown ? "Widget: ligado" : (widget.WantVisible ? "Widget: ligado (escondido em tela cheia)" : "Widget: desligado")), OsdKind.Info);
                    return;
                case PlanKind.SetMode:
                    Safety.Journal.Write(JournalKind.Action, source + ": modo " + KeyActionPlanner.ModeName(plan.Mode));
                    form.ChooseMode(plan.Mode);
                    Finish(lastMode.HasValue && lastMode.Value == plan.Mode, plan.Text, "não foi possível trocar para " + KeyActionPlanner.ModeName(plan.Mode));
                    return;
                case PlanKind.FanMax:
                    Safety.Journal.Write(JournalKind.Action, source + ": ventoinha Máximo");
                    form.ChooseFanMax();
                    Finish(form.FanMaxActive, plan.Text, "não foi possível pôr a ventoinha no máximo");
                    return;
                case PlanKind.FanAuto:
                    Safety.Journal.Write(JournalKind.Action, source + ": ventoinha Automática");
                    form.ChooseFanAuto();
                    Finish(!form.FanMaxActive && fan.FirmwareAutoConfirmed, plan.Text, "não foi possível voltar a ventoinha ao automático");
                    return;
                case PlanKind.CleanRam:
                    Safety.Journal.Write(JournalKind.Action, source + ": limpar RAM");
                    cleanFromGesture = true;
                    CleanRam(false);
                    ShowOsd(plan.Text, OsdKind.Info);
                    return;
            }
        }

        private void Finish(bool ok, string success, string failure)
        {
            ShowOsd(ok ? success : "Falhou: " + failure, ok ? OsdKind.Info : OsdKind.Blocked);
        }

        private void ShowOsd(string text, OsdKind kind)
        {
            if (osd != null && !exiting) osd.Show(text, kind);
        }

        // "Limpar RAM" started from the key: say how it ended.
        private void OnCleaningForGesture()
        {
            if (cleaning || !cleanFromGesture) return;
            cleanFromGesture = false;
            ShowOsd(string.IsNullOrEmpty(lastCleanText) ? "RAM limpa" : lastCleanText, OsdKind.Info);
        }

        // What the planner needs, from the same state the pages and the tray use. The locks and the allowed
        // modes come from the environment profile (CapabilityProfile) through KeyLocks, the one place that
        // reads it for key actions: only confirmed modes, a stale or untested profile locks everything.
        private KeyActionContext BuildKeyContext(KeyAction a)
        {
            PerfMode? cur = KeyLocks.NeedsCurrentMode(a) ? ReadMode() : lastMode; // one firmware read, only when it matters
            string fanMode = form.IsDisposed ? null : form.FanModeLabel;
            bool custom = fanMode == "Manual" || fanMode == "Curva";
            return KeyLocks.Context(config, readOnly, envOwns, cur, cleaning,
                !form.IsDisposed && form.FanMaxActive, custom ? fanMode : null);
        }

        // Key / shortcut: the window opens, or hides when it is already the one in front. (A tray click
        // and a second launch still only ever show it.)
        public void ToggleMainWindow()
        {
            if (exiting || form.IsDisposed) return;
            bool inFront = form.Visible && form.WindowState != FormWindowState.Minimized && ScreenInfo.ForegroundWindow() == form.Handle;
            if (inFront) form.Hide();
            else ShowMainWindow();
        }
    }
}
