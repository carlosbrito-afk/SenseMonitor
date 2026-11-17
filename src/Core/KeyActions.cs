using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Forja.Core
{
    // What a gesture of the NitroSense key (or a global hotkey) can do. The order here is the order
    // of the dropdowns in Configurações.
    public enum KeyAction
    {
        None = 0,
        OpenApp,         // open the window, or hide it when it is already in front
        CycleMode,       // Econômico -> Silencioso -> Dia a dia -> Desempenho -> ..., confirmed modes only, never Turbo
        ModeEco,
        ModeSilent,
        ModeDaily,
        ModePerformance,
        FanToggle,       // Máximo <-> Automático
        FanMax,
        FanAuto,
        CleanRam,
        ToggleWidget
    }

    public static class KeyActions
    {
        public static readonly KeyAction[] All = new KeyAction[]
        {
            KeyAction.None, KeyAction.OpenApp, KeyAction.CycleMode, KeyAction.ModeEco, KeyAction.ModeSilent, KeyAction.ModeDaily,
            KeyAction.ModePerformance, KeyAction.FanToggle, KeyAction.FanMax, KeyAction.FanAuto,
            KeyAction.CleanRam, KeyAction.ToggleWidget
        };

        public static readonly NitroGesture[] Gestures = new NitroGesture[]
        {
            NitroGesture.Tap1, NitroGesture.Tap2, NitroGesture.Tap3, NitroGesture.Long
        };

        // The text stored in gestos.json (stable: never reword these).
        public static string Name(KeyAction a)
        {
            switch (a)
            {
                case KeyAction.OpenApp: return "abrir";
                case KeyAction.CycleMode: return "alternar-modo";
                case KeyAction.ModeEco: return "modo-economico";
                case KeyAction.ModeSilent: return "modo-silencioso";
                case KeyAction.ModeDaily: return "modo-dia-a-dia";
                case KeyAction.ModePerformance: return "modo-desempenho";
                case KeyAction.FanToggle: return "ventoinha-alternar";
                case KeyAction.FanMax: return "ventoinha-maximo";
                case KeyAction.FanAuto: return "ventoinha-auto";
                case KeyAction.CleanRam: return "limpar-ram";
                case KeyAction.ToggleWidget: return "widget";
                default: return "nada";
            }
        }

        // Unknown or missing text gives the fallback (a hand-edited file never breaks the key).
        public static KeyAction Parse(string name, KeyAction fallback)
        {
            if (name == null) return fallback;
            string n = name.Trim().ToLowerInvariant();
            foreach (KeyAction a in All)
            {
                if (Name(a) == n) return a;
            }
            return fallback;
        }

        // pt-BR text of the dropdown.
        public static string Label(KeyAction a)
        {
            switch (a)
            {
                case KeyAction.OpenApp: return "Abrir ou fechar o app";
                case KeyAction.CycleMode: return "Alternar modo";
                case KeyAction.ModeEco: return "Modo Econômico";
                case KeyAction.ModeSilent: return "Modo Silencioso";
                case KeyAction.ModeDaily: return "Modo Dia a dia";
                case KeyAction.ModePerformance: return "Modo Desempenho";
                case KeyAction.FanToggle: return "Ventoinha Máximo / Automática";
                case KeyAction.FanMax: return "Ventoinha Máximo";
                case KeyAction.FanAuto: return "Ventoinha Automática";
                case KeyAction.CleanRam: return "Limpar RAM";
                case KeyAction.ToggleWidget: return "Mostrar ou esconder o widget";
                default: return "Nada";
            }
        }

        public static string GestureLabel(NitroGesture g)
        {
            switch (g)
            {
                case NitroGesture.Tap1: return "1 toque";
                case NitroGesture.Tap2: return "2 toques";
                case NitroGesture.Tap3: return "3 toques";
                case NitroGesture.Long: return "Segurar";
                default: return "";
            }
        }

        public static KeyAction DefaultFor(NitroGesture g)
        {
            switch (g)
            {
                case NitroGesture.Tap1: return KeyAction.OpenApp;
                case NitroGesture.Tap2: return KeyAction.CycleMode;
                case NitroGesture.Tap3: return KeyAction.ToggleWidget;
                case NitroGesture.Long: return KeyAction.FanToggle;
                default: return KeyAction.None;
            }
        }
    }

    // gestos.json: what the user mapped. Missing members load with the defaults; unknown action
    // names fall back to the default of their gesture (Normalize).
    [DataContract]
    public sealed class GestureSettings
    {
        [DataMember] public string Tap1;
        [DataMember] public string Tap2;
        [DataMember] public string Tap3;
        [DataMember] public string Long;
        // Long press needs the key's keyup, which this key does not send reliably (it sends make/break
        // bursts). Experimental and OFF by default; with it off, holding the key does nothing.
        [DataMember] public bool LongPressEnabled;
        // Global keyboard shortcuts (RegisterHotKey) for the same actions. OFF by default.
        [DataMember] public bool HotkeysEnabled;
        // The on-screen message after an action.
        [DataMember] public bool OsdEnabled;

        public GestureSettings()
        {
            SetDefaults();
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SetDefaults();
        }

        private void SetDefaults()
        {
            Tap1 = KeyActions.Name(KeyActions.DefaultFor(NitroGesture.Tap1));
            Tap2 = KeyActions.Name(KeyActions.DefaultFor(NitroGesture.Tap2));
            Tap3 = KeyActions.Name(KeyActions.DefaultFor(NitroGesture.Tap3));
            Long = KeyActions.Name(KeyActions.DefaultFor(NitroGesture.Long));
            LongPressEnabled = false;
            HotkeysEnabled = false;
            OsdEnabled = true;
        }

        // What is stored for the gesture, whether or not it is in effect.
        public KeyAction Stored(NitroGesture g)
        {
            switch (g)
            {
                case NitroGesture.Tap1: return KeyActions.Parse(Tap1, KeyActions.DefaultFor(g));
                case NitroGesture.Tap2: return KeyActions.Parse(Tap2, KeyActions.DefaultFor(g));
                case NitroGesture.Tap3: return KeyActions.Parse(Tap3, KeyActions.DefaultFor(g));
                case NitroGesture.Long: return KeyActions.Parse(Long, KeyActions.DefaultFor(g));
                default: return KeyAction.None;
            }
        }

        // What the gesture does now: Long does nothing unless long press is switched on.
        public KeyAction Get(NitroGesture g)
        {
            if (g == NitroGesture.Long && !LongPressEnabled) return KeyAction.None;
            return Stored(g);
        }

        public void Set(NitroGesture g, KeyAction a)
        {
            string n = KeyActions.Name(a);
            switch (g)
            {
                case NitroGesture.Tap1: Tap1 = n; break;
                case NitroGesture.Tap2: Tap2 = n; break;
                case NitroGesture.Tap3: Tap3 = n; break;
                case NitroGesture.Long: Long = n; break;
            }
        }

        public GestureSettings Copy()
        {
            GestureSettings c = new GestureSettings();
            c.Tap1 = Tap1;
            c.Tap2 = Tap2;
            c.Tap3 = Tap3;
            c.Long = Long;
            c.LongPressEnabled = LongPressEnabled;
            c.HotkeysEnabled = HotkeysEnabled;
            c.OsdEnabled = OsdEnabled;
            return c;
        }

        // Puts every action name into its canonical text. Returns this.
        public GestureSettings Normalize()
        {
            foreach (NitroGesture g in KeyActions.Gestures) Set(g, Stored(g));
            return this;
        }
    }

    // One global shortcut. Fixed letters on purpose: Ctrl+Alt is AltGr on Brazilian keyboards, and
    // AltGr+Q/W/E/R/T... type characters (/ ? ° ® ...), so those letters are never used.
    public sealed class HotkeyBinding
    {
        public int Id;
        public KeyAction Action;
        public int Modifiers; // MOD_* of RegisterHotKey
        public int Vk;
        public string Text;   // "Ctrl+Alt+M"
    }

    public static class Hotkeys
    {
        public const int ModAlt = 0x1;
        public const int ModControl = 0x2;
        public const int ModNoRepeat = 0x4000;

        public static readonly HotkeyBinding[] Defaults = new HotkeyBinding[]
        {
            Make(1, KeyAction.OpenApp, 'N'),
            Make(2, KeyAction.CycleMode, 'M'),
            Make(3, KeyAction.FanToggle, 'V'),
            Make(4, KeyAction.ToggleWidget, 'G'),
            Make(5, KeyAction.CleanRam, 'L')
        };

        private static HotkeyBinding Make(int id, KeyAction action, char key)
        {
            HotkeyBinding b = new HotkeyBinding();
            b.Id = id;
            b.Action = action;
            b.Modifiers = ModControl | ModAlt | ModNoRepeat;
            b.Vk = key; // the virtual-key code of a letter is its upper-case ASCII
            b.Text = "Ctrl+Alt+" + key;
            return b;
        }

        public const int ErrorHotkeyAlreadyRegistered = 1409;

        // Why one shortcut was not registered, from RegisterHotKey's error code: only 1409 means another
        // program owns the combination; anything else is said as it is.
        public static string FailureText(string combo, int win32Error)
        {
            if (win32Error == ErrorHotkeyAlreadyRegistered) return combo + ": já em uso por outro programa";
            return combo + ": não foi possível registrar (erro " + win32Error.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        // "Ctrl+Alt + N abrir · M modo · V ventoinha · G widget · L RAM": fits one line of the settings card.
        public static string ShortSummary
        {
            get
            {
                List<string> parts = new List<string>();
                foreach (HotkeyBinding b in Defaults) parts.Add(((char)b.Vk) + " " + ShortName(b.Action));
                return "Ctrl+Alt + " + string.Join(" · ", parts.ToArray());
            }
        }

        private static string ShortName(KeyAction a)
        {
            switch (a)
            {
                case KeyAction.OpenApp: return "abrir";
                case KeyAction.CycleMode: return "modo";
                case KeyAction.FanToggle: return "ventoinha";
                case KeyAction.ToggleWidget: return "widget";
                case KeyAction.CleanRam: return "RAM";
                default: return KeyActions.Label(a).ToLowerInvariant();
            }
        }

        public static string Summary
        {
            get
            {
                List<string> parts = new List<string>();
                foreach (HotkeyBinding b in Defaults) parts.Add(b.Text + " " + KeyActions.Label(b.Action).ToLowerInvariant());
                return string.Join(" · ", parts.ToArray());
            }
        }
    }
}
