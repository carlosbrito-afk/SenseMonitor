using System;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class KeyActionTests
    {
        private static KeyActionContext Open()
        {
            // every lock opened on purpose: a default context is closed (see ClosedByDefault)
            KeyActionContext c = new KeyActionContext();
            c.CurrentMode = PerfMode.DiaADia;
            c.PerfWarningShown = true;
            c.AllowedModes = new PerfMode[] { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };
            c.ModeLockReason = null;
            c.FanLockReason = null;
            c.RamLockReason = null;
            return c;
        }

        [Test]
        public static void ClosedByDefault_AnUnfilledContextLocksEveryWrite()
        {
            KeyActionContext c = new KeyActionContext();
            foreach (KeyAction a in new KeyAction[] { KeyAction.CycleMode, KeyAction.ModeEco, KeyAction.ModeSilent,
                KeyAction.ModeDaily, KeyAction.ModePerformance, KeyAction.FanMax, KeyAction.FanToggle, KeyAction.CleanRam })
            {
                KeyActionPlan p = KeyActionPlanner.Plan(a, c);
                Assert.True(p.Blocked, a + " must be blocked by an empty context");
                Assert.Equal("Bloqueado: " + KeyActionContext.NotProvided, p.Text);
            }
            Assert.Null(KeyActionPlanner.NextMode(PerfMode.DiaADia, null, true));
            Assert.True(KeyActionPlanner.Plan(KeyAction.ModeDaily, null).Blocked, "a missing context too");
            Assert.Equal(PlanKind.FanAuto, KeyActionPlanner.Plan(KeyAction.FanAuto, c).Kind); // handing the fans back is never locked
            Assert.Equal(PlanKind.ToggleWindow, KeyActionPlanner.Plan(KeyAction.OpenApp, c).Kind);
        }

        [Test]
        public static void FanMax_NeverReplacesTheUsersManualOrCurveMode()
        {
            KeyActionContext c = Open();
            c.FanCustomMode = "Curva";
            KeyActionPlan p = KeyActionPlanner.Plan(KeyAction.FanMax, c);
            Assert.True(p.Blocked, "blocked");
            Assert.Equal("Bloqueado: a ventoinha está em Curva: troque no app", p.Text);
            Assert.True(KeyActionPlanner.Plan(KeyAction.FanToggle, c).Blocked, "toggle goes to Máximo first");
            c.FanCustomMode = null;
            Assert.Equal(PlanKind.FanMax, KeyActionPlanner.Plan(KeyAction.FanMax, c).Kind);
        }

        // ---- the context the shell assembles (KeyLocks.Context) ----

        private static Config Fresh(bool fanTested)
        {
            Config c = new Config();
            c.Env = new CapabilityProfile();
            c.Env.CpuFan = true;
            c.Env.GpuFan = true;
            c.Env.Set(Cap.ModeBalanced, CapState.Confirmed, null);
            c.Env.Set(Cap.ModeQuiet, CapState.Confirmed, null);
            c.Env.Set(Cap.ModeEco, CapState.Confirmed, null);
            c.Env.Set(Cap.ModeTurbo, CapState.Confirmed, null);
            c.Env.Set(Cap.ModePerformance, CapState.Untested, null);
            c.Env.Set(Cap.FanControl, fanTested ? CapState.Confirmed : CapState.Untested, null);
            if (fanTested)
            {
                c.FanControlValidated = true;
                c.FanMaxRpmCpu = 4000;
                c.FanMaxRpmGpu = 4000;
            }
            c.EnvStale = false;
            return c;
        }

        [Test]
        public static void Context_FreshProfile_OpensWhatTheProfileConfirmed_AndNothingElse()
        {
            Config c = Fresh(true);
            KeyActionContext x = KeyLocks.Context(c, false, false, PerfMode.Silencioso, false, false, null);
            Assert.Null(x.ModeLockReason);
            Assert.Null(x.FanLockReason);
            Assert.Null(x.RamLockReason);
            Assert.True(Array.IndexOf(x.AllowedModes, PerfMode.Desempenho) < 0, "untested mode stays out");
            Assert.Equal(PlanKind.SetMode, KeyActionPlanner.Plan(KeyAction.ModeEco, x).Kind);
            Assert.True(KeyActionPlanner.Plan(KeyAction.ModePerformance, x).Blocked, "Desempenho was not confirmed");
            Assert.Equal(PlanKind.FanMax, KeyActionPlanner.Plan(KeyAction.FanMax, x).Kind);
            Assert.Equal(PlanKind.CleanRam, KeyActionPlanner.Plan(KeyAction.CleanRam, x).Kind);
            // the current mode feeds "already active" for the Econômico action too
            KeyActionContext same = KeyLocks.Context(c, false, false, PerfMode.Economico, false, false, null);
            Assert.Equal(PlanKind.Info, KeyActionPlanner.Plan(KeyAction.ModeEco, same).Kind);
            // Turbo is confirmed but no action and no cycle step reaches it
            // Desempenho is untested, so after Dia a dia the cycle wraps to Econômico, never to Turbo
            Assert.Equal(PerfMode.Economico, KeyActionPlanner.NextMode(PerfMode.DiaADia, x.AllowedModes, true));
            Assert.Equal(PerfMode.DiaADia, KeyActionPlanner.NextMode(PerfMode.Turbo, x.AllowedModes, true));
        }

        [Test]
        public static void Context_EveryAction_IsBlockedByStaleNeverTestedReadOnlyAndTheWizard()
        {
            Config stale = Fresh(true);
            stale.EnvStale = true;
            Config never = new Config();
            never.Env = null;
            Config ok = Fresh(true);
            KeyAction[] writes = new KeyAction[] { KeyAction.CycleMode, KeyAction.ModeEco, KeyAction.ModeSilent, KeyAction.ModeDaily,
                KeyAction.FanMax, KeyAction.FanToggle };
            foreach (KeyAction a in writes)
            {
                Assert.True(KeyActionPlanner.Plan(a, KeyLocks.Context(stale, false, false, PerfMode.DiaADia, false, false, null)).Blocked, "stale " + a);
                Assert.True(KeyActionPlanner.Plan(a, KeyLocks.Context(never, false, false, PerfMode.DiaADia, false, false, null)).Blocked, "never tested " + a);
                Assert.True(KeyActionPlanner.Plan(a, KeyLocks.Context(ok, true, false, PerfMode.DiaADia, false, false, null)).Blocked, "read-only " + a);
                KeyActionPlan own = KeyActionPlanner.Plan(a, KeyLocks.Context(ok, false, true, PerfMode.DiaADia, false, false, null));
                Assert.True(own.Blocked, "the wizard owns the firmware " + a);
                Assert.Equal("Bloqueado: " + KeyLocks.EnvOwns, own.Text);
            }
            Assert.True(KeyActionPlanner.Plan(KeyAction.CleanRam, KeyLocks.Context(ok, true, false, null, false, false, null)).Blocked, "RAM in read-only");
            Assert.Equal(PlanKind.CleanRam, KeyActionPlanner.Plan(KeyAction.CleanRam, KeyLocks.Context(stale, false, false, null, false, false, null)).Kind); // RAM needs no profile
            Assert.True(KeyActionPlanner.Plan(KeyAction.FanMax, KeyLocks.Context(Fresh(false), false, false, null, false, false, null)).Blocked, "fan not tested");
            Assert.True(KeyActionPlanner.Plan(KeyAction.FanMax, KeyLocks.Context(ok, false, false, null, false, false, "Manual")).Blocked, "user's manual mode");
        }

        [Test]
        public static void Context_NeedsCurrentMode_OnlyForModeActions()
        {
            Assert.True(KeyLocks.NeedsCurrentMode(KeyAction.ModeEco), "Econômico was missing before");
            Assert.True(KeyLocks.NeedsCurrentMode(KeyAction.CycleMode), "cycle");
            Assert.True(!KeyLocks.NeedsCurrentMode(KeyAction.FanMax), "fan");
            Assert.True(!KeyLocks.NeedsCurrentMode(KeyAction.OpenApp), "window");
        }

        // ---- names and defaults ----

        [Test]
        public static void Names_RoundTrip_AndAreUnique()
        {
            System.Collections.Generic.List<string> seen = new System.Collections.Generic.List<string>();
            foreach (KeyAction a in KeyActions.All)
            {
                string n = KeyActions.Name(a);
                Assert.True(!seen.Contains(n), "duplicate name " + n);
                seen.Add(n);
                Assert.Equal(a, KeyActions.Parse(n, KeyAction.CleanRam));
                Assert.True(KeyActions.Label(a).Length > 0, "label of " + n);
            }
            Assert.Equal(KeyAction.None, KeyActions.Parse(" NADA ", KeyAction.CleanRam));
            Assert.Equal(KeyAction.CleanRam, KeyActions.Parse("inventada", KeyAction.CleanRam));
            Assert.Equal(KeyAction.CleanRam, KeyActions.Parse(null, KeyAction.CleanRam));
        }

        [Test]
        public static void Defaults_AreTheAgreedMapping()
        {
            GestureSettings s = new GestureSettings();
            Assert.Equal(KeyAction.OpenApp, s.Get(NitroGesture.Tap1));
            Assert.Equal(KeyAction.CycleMode, s.Get(NitroGesture.Tap2));
            Assert.Equal(KeyAction.ToggleWidget, s.Get(NitroGesture.Tap3));
            // long press is off until switched on, so it does nothing, but the choice is kept
            Assert.Equal(KeyAction.None, s.Get(NitroGesture.Long));
            Assert.Equal(KeyAction.FanToggle, s.Stored(NitroGesture.Long));
            Assert.True(!s.LongPressEnabled, "long press off");
            Assert.True(!s.HotkeysEnabled, "global hotkeys off");
            Assert.True(s.OsdEnabled, "message on");
            s.LongPressEnabled = true;
            Assert.Equal(KeyAction.FanToggle, s.Get(NitroGesture.Long));
        }

        [Test]
        public static void Settings_SetCopyNormalize()
        {
            GestureSettings s = new GestureSettings();
            s.Set(NitroGesture.Tap2, KeyAction.ModePerformance);
            s.Tap3 = "lixo";
            GestureSettings c = s.Copy().Normalize();
            Assert.Equal(KeyAction.ModePerformance, c.Get(NitroGesture.Tap2));
            Assert.Equal(KeyAction.ToggleWidget, c.Get(NitroGesture.Tap3)); // unknown text: the gesture's default
            Assert.Equal("widget", c.Tap3);
            s.Set(NitroGesture.Tap1, KeyAction.None);
            Assert.Equal(KeyAction.None, s.Get(NitroGesture.Tap1));
            Assert.Equal(KeyAction.OpenApp, c.Get(NitroGesture.Tap1)); // the copy is independent
        }

        [Test]
        public static void Store_RoundTrip_MissingAndDamagedFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "gestos.json");
            try
            {
                Assert.Equal(KeyAction.OpenApp, OverlayStore.LoadGestures(path).Get(NitroGesture.Tap1)); // missing: defaults
                GestureSettings s = new GestureSettings();
                s.Set(NitroGesture.Tap1, KeyAction.CleanRam);
                s.HotkeysEnabled = true;
                s.OsdEnabled = false;
                s.LongPressEnabled = true;
                Assert.True(OverlayStore.SaveGestures(path, s), "saved");
                GestureSettings back = OverlayStore.LoadGestures(path);
                Assert.Equal(KeyAction.CleanRam, back.Get(NitroGesture.Tap1));
                Assert.True(back.HotkeysEnabled && !back.OsdEnabled && back.LongPressEnabled, "flags kept");
                File.WriteAllText(path, "{ nao e json");
                Assert.Equal(KeyAction.OpenApp, OverlayStore.LoadGestures(path).Get(NitroGesture.Tap1)); // damaged: defaults
                File.WriteAllText(path, "{\"Tap2\":\"modo-silencioso\"}"); // older/partial file: the rest keeps the defaults
                GestureSettings part = OverlayStore.LoadGestures(path);
                Assert.Equal(KeyAction.ModeSilent, part.Get(NitroGesture.Tap2));
                Assert.Equal(KeyAction.OpenApp, part.Get(NitroGesture.Tap1));
                Assert.True(part.OsdEnabled, "default kept");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void Hotkeys_AreUnique_AvoidAltGrLetters_AndOffByDefault()
        {
            System.Collections.Generic.List<int> ids = new System.Collections.Generic.List<int>();
            System.Collections.Generic.List<int> keys = new System.Collections.Generic.List<int>();
            foreach (HotkeyBinding b in Hotkeys.Defaults)
            {
                Assert.True(!ids.Contains(b.Id), "id");
                Assert.True(!keys.Contains(b.Vk), "key");
                ids.Add(b.Id);
                keys.Add(b.Vk);
                Assert.True("QWERT".IndexOf((char)b.Vk) < 0, "AltGr letter " + b.Text);
                Assert.True((b.Modifiers & Hotkeys.ModControl) != 0 && (b.Modifiers & Hotkeys.ModAlt) != 0, "Ctrl+Alt");
                Assert.True(b.Action != KeyAction.None, "has an action");
            }
            Assert.Equal("Ctrl+Alt+M", Hotkeys.Defaults[1].Text);
            Assert.Equal(KeyAction.CycleMode, Hotkeys.Defaults[1].Action);
            Assert.True(Hotkeys.Summary.Contains("Ctrl+Alt+M"), "summary");
            Assert.Equal("Ctrl+Alt + N abrir · M modo · V ventoinha · G widget · L RAM", Hotkeys.ShortSummary);
        }

        // ---- profile locks (CapabilityProfile through KeyLocks) ----

        private static readonly PerfMode[] Three = new PerfMode[] { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };

        private static Config WithProfile(Action<CapabilityProfile> fill)
        {
            Config c = new Config();
            c.Env = new CapabilityProfile();
            c.Env.CpuFan = true;
            c.Env.GpuFan = true;
            fill(c.Env);
            c.EnvStale = false;
            return c;
        }

        [Test]
        public static void KeyLocks_NeverTested_And_Stale_LockEverythingWithTheProfilesReason()
        {
            Config none = new Config();
            none.Env = null;
            Assert.Equal(KeyLocks.NeverTested, KeyLocks.Mode(none, false, false));
            Assert.Equal(KeyLocks.NeverTested, KeyLocks.Fan(none, false, false));
            Assert.Equal(0, KeyLocks.AllowedModes(none).Length);
            Config stale = WithProfile(delegate(CapabilityProfile p) { p.Set(Cap.ModeBalanced, CapState.Confirmed, null); });
            stale.EnvStale = true;
            Assert.Equal(KeyLocks.Stale, KeyLocks.Mode(stale, false, false));
            Assert.Equal(KeyLocks.Stale, KeyLocks.Fan(stale, false, false));
            Assert.Equal(0, KeyLocks.AllowedModes(stale).Length); // a stale profile confirms nothing
        }

        [Test]
        public static void KeyLocks_OnlyConfirmedModes_AreAllowed_UntestedAreNot()
        {
            Config c = WithProfile(delegate(CapabilityProfile p)
            {
                p.Set(Cap.ModeBalanced, CapState.Confirmed, null);
                p.Set(Cap.ModeQuiet, CapState.Confirmed, null);
                p.Set(Cap.ModePerformance, CapState.Untested, null);
                p.Set(Cap.ModeTurbo, CapState.Confirmed, null);
            });
            PerfMode[] allowed = KeyLocks.AllowedModes(c);
            Assert.Equal(3, allowed.Length);
            Assert.True(Array.IndexOf(allowed, PerfMode.Desempenho) < 0, "untested Desempenho is not allowed");
            Assert.Null(KeyLocks.Mode(c, false, false));
            // Turbo is confirmed but is never in the cycle
            KeyActionContext ctx = new KeyActionContext();
            ctx.AllowedModes = allowed;
            ctx.PerfWarningShown = true;
            ctx.CurrentMode = PerfMode.DiaADia;
            Assert.Equal(PerfMode.Silencioso, KeyActionPlanner.NextMode(ctx.CurrentMode, ctx.AllowedModes, true));
            Assert.Equal(PerfMode.DiaADia, KeyActionPlanner.NextMode(PerfMode.Turbo, ctx.AllowedModes, true)); // out of Turbo: down to Dia a dia
        }

        [Test]
        public static void KeyLocks_NoModeConfirmed_UsesTheProfilesReason()
        {
            Config c = WithProfile(delegate(CapabilityProfile p)
            {
                p.Set(Cap.ModeBalanced, CapState.Unavailable, "O firmware recusou este perfil. Nada foi gravado.");
            });
            Assert.Equal("o firmware recusou este perfil", KeyLocks.Mode(c, false, false));
        }

        [Test]
        public static void KeyLocks_Fan_NeedsTheFanTestAndAFreshProfile_ReasonsInPriorityOrder()
        {
            Config c = WithProfile(delegate(CapabilityProfile p) { p.Set(Cap.FanControl, CapState.Untested, null); });
            Assert.Equal(KeyLocks.NeverTested, KeyLocks.Fan(c, false, false));
            Config bad = WithProfile(delegate(CapabilityProfile p) { p.Set(Cap.FanControl, CapState.Unavailable, "As ventoinhas não seguiram o teste. Fica em automático."); });
            Assert.Equal("as ventoinhas não seguiram o teste", KeyLocks.Fan(bad, false, false));
            Assert.Equal(KeyLocks.ReadOnly, KeyLocks.Fan(bad, true, false));
            Assert.Equal(KeyLocks.EnvOwns, KeyLocks.Fan(bad, true, true)); // the wizard owning the firmware wins
            Assert.Equal(KeyLocks.EnvOwns, KeyLocks.Mode(bad, false, true));
            Assert.Equal(KeyLocks.ReadOnly, KeyLocks.Ram(true));
            Assert.Null(KeyLocks.Ram(false));
        }

        [Test]
        public static void Planner_Cycle_StartsAtEconomico_AndTheNewDirectAction()
        {
            PerfMode[] all = new PerfMode[] { PerfMode.Economico, PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho, PerfMode.Turbo };
            Assert.Equal(PerfMode.Economico, KeyActionPlanner.NextMode(PerfMode.Desempenho, all, true));
            Assert.Equal(PerfMode.Silencioso, KeyActionPlanner.NextMode(PerfMode.Economico, all, true));
            KeyActionContext c = Open();
            c.AllowedModes = new PerfMode[] { PerfMode.DiaADia };
            KeyActionPlan p = KeyActionPlanner.Plan(KeyAction.ModeEco, c);
            Assert.True(p.Blocked, "Econômico not confirmed");
            Assert.Equal("Bloqueado: o modo Econômico não está liberado", p.Text);
            Assert.Equal(KeyAction.ModeEco, KeyActions.Parse("modo-economico", KeyAction.None));
        }

        // ---- planner ----

        [Test]
        public static void NextMode_CyclesAllowedModes_SkipsWhatIsNotAllowed()
        {
            Assert.Equal(PerfMode.DiaADia, KeyActionPlanner.NextMode(PerfMode.Silencioso, Three, true));
            Assert.Equal(PerfMode.Desempenho, KeyActionPlanner.NextMode(PerfMode.DiaADia, Three, true));
            Assert.Equal(PerfMode.Silencioso, KeyActionPlanner.NextMode(PerfMode.Desempenho, Three, true));
            PerfMode[] onlyTwo = new PerfMode[] { PerfMode.Silencioso, PerfMode.Desempenho };
            Assert.Equal(PerfMode.Desempenho, KeyActionPlanner.NextMode(PerfMode.Silencioso, onlyTwo, true));
            Assert.Equal(PerfMode.Silencioso, KeyActionPlanner.NextMode(PerfMode.Desempenho, onlyTwo, true));
            // the current mode is not in the allowed list: start from Dia a dia, else the first
            Assert.Equal(PerfMode.Silencioso, KeyActionPlanner.NextMode(PerfMode.DiaADia, onlyTwo, true));
            Assert.Equal(PerfMode.DiaADia, KeyActionPlanner.NextMode(null, Three, true));
            // one mode only, or none: nothing to switch to
            Assert.Null(KeyActionPlanner.NextMode(PerfMode.Silencioso, new PerfMode[] { PerfMode.Silencioso }, true));
            Assert.Null(KeyActionPlanner.NextMode(PerfMode.Silencioso, new PerfMode[0], true));
        }

        [Test]
        public static void NextMode_SkipsDesempenhoUntilItsWarningWasConfirmed()
        {
            Assert.Equal(PerfMode.Silencioso, KeyActionPlanner.NextMode(PerfMode.DiaADia, Three, false));
            Assert.Equal(PerfMode.DiaADia, KeyActionPlanner.NextMode(PerfMode.Silencioso, Three, false));
        }

        [Test]
        public static void CycleMode_PlansASetMode_WithTheOsdText()
        {
            KeyActionContext c = Open();
            KeyActionPlan p = KeyActionPlanner.Plan(KeyAction.CycleMode, c);
            Assert.Equal(PlanKind.SetMode, p.Kind);
            Assert.Equal(PerfMode.Desempenho, p.Mode);
            Assert.Equal("Modo: Desempenho", p.Text);
        }

        [Test]
        public static void LockedMode_BlocksWithTheReason_NoWrite()
        {
            KeyActionContext c = Open();
            c.ModeLockReason = "modo somente leitura";
            foreach (KeyAction a in new KeyAction[] { KeyAction.CycleMode, KeyAction.ModeSilent, KeyAction.ModePerformance })
            {
                KeyActionPlan p = KeyActionPlanner.Plan(a, c);
                Assert.True(p.Blocked, a + " blocked");
                Assert.Equal("Bloqueado: modo somente leitura", p.Text);
            }
        }

        [Test]
        public static void ModeNotInTheProfile_IsBlocked_AndAlreadyActiveIsOnlyInfo()
        {
            KeyActionContext c = Open();
            c.AllowedModes = new PerfMode[] { PerfMode.Silencioso, PerfMode.DiaADia };
            KeyActionPlan p = KeyActionPlanner.Plan(KeyAction.ModePerformance, c);
            Assert.True(p.Blocked, "not allowed");
            Assert.Equal("Bloqueado: o modo Desempenho não está liberado", p.Text);
            KeyActionPlan same = KeyActionPlanner.Plan(KeyAction.ModeDaily, c);
            Assert.Equal(PlanKind.Info, same.Kind);
            Assert.Equal("Modo: Dia a dia (já ativo)", same.Text);
        }

        [Test]
        public static void Desempenho_NeedsTheWarningConfirmedInTheApp_NeverADialogOverAGame()
        {
            KeyActionContext c = Open();
            c.PerfWarningShown = false;
            KeyActionPlan p = KeyActionPlanner.Plan(KeyAction.ModePerformance, c);
            Assert.True(p.Blocked, "blocked");
            Assert.Equal("Bloqueado: confirme o aviso do modo Desempenho no app", p.Text);
        }

        [Test]
        public static void CycleWithOneAllowedMode_SaysSo()
        {
            KeyActionContext c = Open();
            c.AllowedModes = new PerfMode[] { PerfMode.DiaADia };
            KeyActionPlan p = KeyActionPlanner.Plan(KeyAction.CycleMode, c);
            Assert.True(p.Blocked, "blocked");
            Assert.Equal("Bloqueado: só um modo está liberado", p.Text);
        }

        [Test]
        public static void Fan_LockedBlocksMax_ButAutomaticIsNeverLocked()
        {
            KeyActionContext c = Open();
            c.FanLockReason = "faça o teste de ambiente";
            KeyActionPlan max = KeyActionPlanner.Plan(KeyAction.FanMax, c);
            Assert.True(max.Blocked, "max blocked");
            Assert.Equal("Bloqueado: faça o teste de ambiente", max.Text);
            Assert.True(KeyActionPlanner.Plan(KeyAction.FanToggle, c).Blocked, "toggle from Auto goes to Max: blocked");
            KeyActionPlan auto = KeyActionPlanner.Plan(KeyAction.FanAuto, c);
            Assert.Equal(PlanKind.FanAuto, auto.Kind);
            c.FanIsMax = true; // e.g. confirmed earlier, then locked: handing back is always allowed
            Assert.Equal(PlanKind.FanAuto, KeyActionPlanner.Plan(KeyAction.FanToggle, c).Kind);
        }

        [Test]
        public static void Fan_ConfirmedToggles_MaxAndAuto()
        {
            KeyActionContext c = Open();
            KeyActionPlan a = KeyActionPlanner.Plan(KeyAction.FanToggle, c);
            Assert.Equal(PlanKind.FanMax, a.Kind);
            Assert.Equal("Ventoinha: Máximo", a.Text);
            c.FanIsMax = true;
            KeyActionPlan b = KeyActionPlanner.Plan(KeyAction.FanToggle, c);
            Assert.Equal(PlanKind.FanAuto, b.Kind);
            Assert.Equal("Ventoinha: Automática", b.Text);
            Assert.Equal(PlanKind.Info, KeyActionPlanner.Plan(KeyAction.FanMax, c).Kind); // already at Máximo
        }

        [Test]
        public static void Ram_BlockedBusyAndRun()
        {
            KeyActionContext c = Open();
            Assert.Equal(PlanKind.CleanRam, KeyActionPlanner.Plan(KeyAction.CleanRam, c).Kind);
            c.RamBusy = true;
            Assert.Equal(PlanKind.Info, KeyActionPlanner.Plan(KeyAction.CleanRam, c).Kind);
            c.RamLockReason = "modo somente leitura";
            Assert.Equal("Bloqueado: modo somente leitura", KeyActionPlanner.Plan(KeyAction.CleanRam, c).Text);
        }

        // The whole path with real-shaped events: raw key events -> NitroKey -> mapping -> plan.
        private static void Press(NitroKey k, int t)
        {
            k.OnKeyGesture(0x100, 0xFF, 0x75, 0x1, t);
            k.OnKeyGesture(0x101, 0xFF, 0x75, 0x81, t + 30);
            k.OnKeyGesture(0x100, 0xFF, 0x75, 0x1, t + 60);
            k.OnKeyGesture(0x101, 0xFF, 0x75, 0x81, t + 90);
        }

        [Test]
        public static void Pipeline_DoubleTapOnTheKey_CyclesTheMode_AndALockedFanSaysWhy()
        {
            GestureSettings map = new GestureSettings();
            NitroKey key = new NitroKey();
            key.Recognizer.DoubleTapEnabled = map.Get(NitroGesture.Tap2) != KeyAction.None;
            key.Recognizer.TripleTapEnabled = map.Get(NitroGesture.Tap3) != KeyAction.None;
            Press(key, 20000);
            Press(key, 20300);
            NitroGesture g = key.Poll(21000);
            Assert.Equal(NitroGesture.Tap2, g);
            KeyActionContext c = Open();
            c.FanLockReason = "faça o teste de ambiente";
            KeyActionPlan p = KeyActionPlanner.Plan(map.Get(g), c);
            Assert.Equal(PlanKind.SetMode, p.Kind);
            Assert.Equal("Modo: Desempenho", p.Text);
            // with the long press switched on and mapped to the fan, the hold is blocked, not written
            map.LongPressEnabled = true;
            key.Recognizer.LongPressEnabled = true;
            key.OnKeyGesture(0x100, 0xFF, 0x75, 0x1, 30000);
            NitroGesture hold = key.Poll(30950);
            Assert.Equal(NitroGesture.Long, hold);
            KeyActionPlan fan = KeyActionPlanner.Plan(map.Get(hold), c);
            Assert.True(fan.Blocked, "locked");
            Assert.Equal("Bloqueado: faça o teste de ambiente", fan.Text);
        }

        [Test]
        public static void WindowWidgetAndNothing()
        {
            KeyActionContext c = Open();
            c.ModeLockReason = "tudo travado";
            c.FanLockReason = "tudo travado";
            Assert.Equal(PlanKind.ToggleWindow, KeyActionPlanner.Plan(KeyAction.OpenApp, c).Kind);
            Assert.Equal(PlanKind.ToggleWidget, KeyActionPlanner.Plan(KeyAction.ToggleWidget, c).Kind);
            Assert.Equal(PlanKind.Nothing, KeyActionPlanner.Plan(KeyAction.None, c).Kind);
            Assert.Equal(PlanKind.ToggleWindow, KeyActionPlanner.Plan(KeyAction.OpenApp, null).Kind); // a missing context is the open one
        }
    }
}
