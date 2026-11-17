using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // What the app knows when a gesture arrives. Built by the shell from the real state (read-only
    // mode, the environment test / capability state, the Desempenho warning, the firmware readings).
    //
    // CLOSED by default: a field nobody filled in locks the action ("contexto não informado") instead of
    // opening it. Only KeyLocks.Context (or a test) opens a lock, by setting its reason to null.
    public sealed class KeyActionContext
    {
        public const string NotProvided = "contexto não informado";

        public PerfMode? CurrentMode;
        // The modes the machine's profile confirmed. Empty = none (null counts as empty).
        public PerfMode[] AllowedModes = new PerfMode[0];
        // null = mode writes are allowed; otherwise the short reason ("modo somente leitura").
        public string ModeLockReason = NotProvided;
        // The one-time Desempenho warning was already confirmed in the app. A gesture never opens a
        // dialog over a game, so Desempenho stays out of reach until the warning has been confirmed.
        public bool PerfWarningShown;
        // null = fan control is confirmed (validated, not read-only); otherwise the short reason.
        public string FanLockReason = NotProvided;
        public bool FanIsMax;
        // "Manual" or "Curva" while the user's own fan mode drives the fans, else null. A tap never replaces it:
        // going to Máximo and back would end in Automático and lose the user's curve.
        public string FanCustomMode;
        public string RamLockReason = NotProvided;
        public bool RamBusy;
    }

    public enum PlanKind
    {
        Nothing,
        ToggleWindow,
        SetMode,
        FanMax,
        FanAuto,
        CleanRam,
        ToggleWidget,
        // Nothing is written; the text is only information ("already active").
        Info,
        // The action is locked: the text says why (no write happens).
        Blocked
    }

    public sealed class KeyActionPlan
    {
        public PlanKind Kind;
        public PerfMode Mode;
        // What the on-screen message says when the action is done (null: none; the shell decides).
        public string Text;

        public bool Blocked { get { return Kind == PlanKind.Blocked; } }

        public static KeyActionPlan Of(PlanKind kind, string text)
        {
            KeyActionPlan p = new KeyActionPlan();
            p.Kind = kind;
            p.Text = text;
            return p;
        }
    }

    // Decides what an action does right now, without doing it. Pure. Every locked capability gives
    // Blocked + a short "Bloqueado: ..." text instead of a write.
    public static class KeyActionPlanner
    {
        public const string BlockedPrefix = "Bloqueado: ";
        public const string PerfWarningReason = "confirme o aviso do modo Desempenho no app";
        public const string OneModeReason = "só um modo está liberado";

        public static string ModeName(PerfMode m)
        {
            return ModeCatalog.Name(m);
        }

        public static string ModeText(PerfMode m)
        {
            return "Modo: " + ModeName(m);
        }

        public const string FanMaxText = "Ventoinha: Máximo";
        public const string FanAutoText = "Ventoinha: Automática";
        public const string RamText = "Limpando a RAM…";

        public static KeyActionPlan Plan(KeyAction action, KeyActionContext c)
        {
            if (c == null) c = new KeyActionContext();
            switch (action)
            {
                case KeyAction.OpenApp:
                    return KeyActionPlan.Of(PlanKind.ToggleWindow, null);
                case KeyAction.CycleMode:
                    return PlanCycle(c);
                case KeyAction.ModeEco:
                    return PlanMode(PerfMode.Economico, c);
                case KeyAction.ModeSilent:
                    return PlanMode(PerfMode.Silencioso, c);
                case KeyAction.ModeDaily:
                    return PlanMode(PerfMode.DiaADia, c);
                case KeyAction.ModePerformance:
                    return PlanMode(PerfMode.Desempenho, c);
                case KeyAction.FanToggle:
                    return c.FanIsMax ? PlanFanAuto(c) : PlanFanMax(c);
                case KeyAction.FanMax:
                    return PlanFanMax(c);
                case KeyAction.FanAuto:
                    return PlanFanAuto(c);
                case KeyAction.CleanRam:
                    if (c.RamLockReason != null) return Block(c.RamLockReason);
                    if (c.RamBusy) return KeyActionPlan.Of(PlanKind.Info, RamText);
                    return KeyActionPlan.Of(PlanKind.CleanRam, RamText);
                case KeyAction.ToggleWidget:
                    return KeyActionPlan.Of(PlanKind.ToggleWidget, null);
                default:
                    return KeyActionPlan.Of(PlanKind.Nothing, null);
            }
        }

        // The next mode in Econômico -> Silencioso -> Dia a dia -> Desempenho -> Econômico, skipping what the
        // profile did not confirm and Desempenho while its warning is unconfirmed. Turbo is NEVER in the cycle
        // (too much noise and heat to reach by a tap): from Turbo the cycle goes down to Dia a dia. null when
        // there is nothing to switch to. When the current mode is unknown or not in the list the cycle starts
        // at Dia a dia.
        public static PerfMode? NextMode(PerfMode? current, PerfMode[] allowed, bool perfWarningShown)
        {
            PerfMode[] order = new PerfMode[] { PerfMode.Economico, PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };
            List<PerfMode> list = new List<PerfMode>();
            foreach (PerfMode m in order)
            {
                if (allowed == null || Array.IndexOf(allowed, m) < 0) continue;
                if (m == PerfMode.Desempenho && !perfWarningShown) continue;
                list.Add(m);
            }
            if (list.Count == 0) return null;
            int at = current.HasValue ? list.IndexOf(current.Value) : -1;
            if (at < 0)
            {
                if (list.Contains(PerfMode.DiaADia)) return PerfMode.DiaADia;
                return list[0];
            }
            if (list.Count < 2) return null;
            return list[(at + 1) % list.Count];
        }

        private static KeyActionPlan PlanCycle(KeyActionContext c)
        {
            if (c.ModeLockReason != null) return Block(c.ModeLockReason);
            PerfMode? next = NextMode(c.CurrentMode, c.AllowedModes, c.PerfWarningShown);
            if (!next.HasValue) return Block(OneModeReason);
            return SetMode(next.Value);
        }

        private static KeyActionPlan PlanMode(PerfMode m, KeyActionContext c)
        {
            if (c.ModeLockReason != null) return Block(c.ModeLockReason);
            if (c.AllowedModes == null || Array.IndexOf(c.AllowedModes, m) < 0) return Block("o modo " + ModeName(m) + " não está liberado");
            if (c.CurrentMode.HasValue && c.CurrentMode.Value == m) return KeyActionPlan.Of(PlanKind.Info, ModeText(m) + " (já ativo)");
            if (m == PerfMode.Desempenho && !c.PerfWarningShown) return Block(PerfWarningReason);
            return SetMode(m);
        }

        private static KeyActionPlan SetMode(PerfMode m)
        {
            KeyActionPlan p = KeyActionPlan.Of(PlanKind.SetMode, ModeText(m));
            p.Mode = m;
            return p;
        }

        private static KeyActionPlan PlanFanMax(KeyActionContext c)
        {
            if (c.FanLockReason != null) return Block(c.FanLockReason);
            if (c.FanIsMax) return KeyActionPlan.Of(PlanKind.Info, FanMaxText + " (já ativo)");
            if (c.FanCustomMode != null) return Block("a ventoinha está em " + c.FanCustomMode + ": troque no app");
            return KeyActionPlan.Of(PlanKind.FanMax, FanMaxText);
        }

        // Automático is the safe state: it is never locked (the tray offers it in every state too).
        private static KeyActionPlan PlanFanAuto(KeyActionContext c)
        {
            return KeyActionPlan.Of(PlanKind.FanAuto, FanAutoText);
        }

        private static KeyActionPlan Block(string reason)
        {
            return KeyActionPlan.Of(PlanKind.Blocked, BlockedPrefix + reason);
        }
    }
}
