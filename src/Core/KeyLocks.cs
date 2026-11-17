using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // What the environment profile (CapabilityProfile) lets a key gesture or hotkey do, as the short
    // reasons the on-screen message shows. Pure: the shell passes the config and two flags. This is the
    // ONE place the key actions read the profile: only CONFIRMED modes are written, a stale or untested
    // profile locks everything, and the wizard owning the firmware locks every write.
    public static class KeyLocks
    {
        public const string ReadOnly = "modo somente leitura";
        public const string EnvOwns = "o teste de ambiente está usando o firmware";
        public const string NeverTested = "faça o teste de ambiente";
        public const string Stale = "o notebook mudou: refaça o teste de ambiente";

        // The modes the profile confirmed (empty while it is stale or missing). Turbo may be in the list: the
        // planner keeps it out of the cycle.
        public static PerfMode[] AllowedModes(Config c)
        {
            if (c == null || c.EnvStale || c.Env == null) return new PerfMode[0];
            return c.Env.ConfirmedModes().ToArray();
        }

        // null = mode writes are allowed.
        public static string Mode(Config c, bool readOnly, bool envOwnsFirmware)
        {
            if (envOwnsFirmware) return EnvOwns;
            if (readOnly) return ReadOnly;
            if (c == null || c.Env == null) return NeverTested;
            if (c.EnvStale) return Stale;
            if (AllowedModes(c).Length == 0)
            {
                string r = FirstSentence(c.Env.Reason(Cap.ModeBalanced));
                return string.IsNullOrEmpty(r) ? "nenhum modo liberado: refaça o teste de ambiente" : Trim(r);
            }
            return null;
        }

        // null = Máximo may be written (the fan test passed and the profile is current).
        public static string Fan(Config c, bool readOnly, bool envOwnsFirmware)
        {
            if (envOwnsFirmware) return EnvOwns;
            if (readOnly) return ReadOnly;
            if (c == null || c.Env == null) return NeverTested;
            if (c.EnvStale) return Stale;
            if (c.ManualFanAllowed) return null;
            if (c.Env.State(Cap.FanControl) == CapState.Unavailable)
            {
                string r = FirstSentence(c.Env.Reason(Cap.FanControl));
                if (!string.IsNullOrEmpty(r)) return Trim(r);
            }
            return NeverTested;
        }

        // What the shell knows right now, as the planner's context. Pure: every lock comes from the profile
        // (this class), nothing else. The shell reads the firmware mode only for mode actions (NeedsCurrentMode).
        public static KeyActionContext Context(Config c, bool readOnly, bool envOwnsFirmware, PerfMode? currentMode,
            bool ramBusy, bool fanIsMax, string fanCustomMode)
        {
            KeyActionContext x = new KeyActionContext();
            x.CurrentMode = currentMode;
            x.AllowedModes = AllowedModes(c);
            x.ModeLockReason = Mode(c, readOnly, envOwnsFirmware);
            x.PerfWarningShown = c != null && c.PerfWarningShown;
            x.FanLockReason = Fan(c, readOnly, envOwnsFirmware);
            x.FanIsMax = fanIsMax;
            x.FanCustomMode = fanCustomMode;
            x.RamLockReason = Ram(readOnly);
            x.RamBusy = ramBusy;
            return x;
        }

        // The actions that compare with the current firmware mode (so it is read once for them, and only them).
        public static bool NeedsCurrentMode(KeyAction a)
        {
            return a == KeyAction.CycleMode || a == KeyAction.ModeEco || a == KeyAction.ModeSilent
                || a == KeyAction.ModeDaily || a == KeyAction.ModePerformance;
        }

        // Limpar RAM does not touch the firmware: only read-only mode stops it.
        public static string Ram(bool readOnly)
        {
            return readOnly ? ReadOnly : null;
        }

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf('.');
            return i > 0 ? s.Substring(0, i + 1) : s;
        }

        // The message gets "Bloqueado: " in front: lower-case start, no final full stop.
        private static string Trim(string s)
        {
            s = s.Trim().TrimEnd('.');
            if (s.Length > 0) s = char.ToLowerInvariant(s[0]) + s.Substring(1);
            return s;
        }
    }
}
