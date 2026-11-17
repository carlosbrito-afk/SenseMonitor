using System;
using System.Drawing;
using Forja.Core;

namespace Forja.App.Controls
{
    // Mockup D palette and fonts: the app's ONE palette (shell, Theme, every page and control).
    // Colors are opaque on purpose: no glow, no gradients.
    public static class Palette
    {
        public static readonly Color Deep = Color.FromArgb(0x10, 0x12, 0x16);
        public static readonly Color Bg = Color.FromArgb(0x15, 0x17, 0x1C);
        public static readonly Color Panel = Color.FromArgb(0x1E, 0x21, 0x28);
        public static readonly Color Line = Color.FromArgb(0x2C, 0x31, 0x3B);
        public static readonly Color Line2 = Color.FromArgb(0x3A, 0x40, 0x4C);
        public static readonly Color Text = Color.FromArgb(0xE6, 0xE8, 0xEC);
        public static readonly Color Sec = Color.FromArgb(0xA3, 0xAA, 0xB6);
        public static readonly Color Mute = Color.FromArgb(0x8E, 0x95, 0xA2);
        public static readonly Color Accent = Color.FromArgb(0x3B, 0xA7, 0xFF);
        public static readonly Color Warn = Color.FromArgb(0xF2, 0xA9, 0x3B);
        public static readonly Color Hot = Color.FromArgb(0xFF, 0x7A, 0x45);
        public static readonly Color Ok = Color.FromArgb(0x5D, 0xCA, 0xA5);
        public static readonly Color NeutralFill = Color.FromArgb(0xD5, 0xD9, 0xE0);
        public static readonly Color Limit = Color.FromArgb(0xC9, 0x78, 0x4F);
        public static readonly Color LimitText = Color.FromArgb(0xE3, 0x9A, 0x74);

        // interaction states (nav, buttons, tabs, cards)
        public static readonly Color Hover = Color.FromArgb(0x25, 0x29, 0x32);
        public static readonly Color Selected = Color.FromArgb(0x2A, 0x30, 0x3B);
        public static readonly Color AccentBg = Color.FromArgb(0x1C, 0x26, 0x32);
        public static readonly Color AccentHover = Color.FromArgb(0x62, 0xB9, 0xFF);
        public static readonly Color AccentPressed = Color.FromArgb(0x2C, 0x8F, 0xE0);
        public static readonly Color OnAccent = Color.FromArgb(0x0D, 0x11, 0x17);

        // gauge details from the mockup
        public static readonly Color TickMinor = Color.FromArgb(0x4A, 0x50, 0x5C);
        public static readonly Color Dim = TickMinor;
        public static readonly Color PeakTick = Color.FromArgb(0xC9, 0xCE, 0xD6);
        // hot at 55 % over the track, pre-blended so the hatch stays opaque
        public static readonly Color HatchStripe = Blend(Hot, Line, 0.55);

        public const string UiFamily = "Segoe UI";
        public const string UiSemiboldFamily = "Segoe UI Semibold";

        private static string _mono;

        // "Cascadia Mono" when installed, otherwise "Consolas" (always present on Windows).
        public static string MonoFamily
        {
            get
            {
                if (_mono == null)
                {
                    string pick = "Consolas";
                    try
                    {
                        using (FontFamily f = new FontFamily("Cascadia Mono"))
                        {
                            pick = f.Name;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                    _mono = pick;
                }
                return _mono;
            }
        }

        public static Color ForState(TempState s)
        {
            switch (s)
            {
                case TempState.Amber: return Warn;
                case TempState.Hot: return Hot;
                case TempState.Sleeping: return Dim;
                case TempState.Unavailable: return Mute;
                default: return NeutralFill;
            }
        }

        public static Color ForVerdict(Verdict v)
        {
            switch (v)
            {
                case Verdict.Stable: return Ok;
                case Verdict.Attention: return Warn;
                case Verdict.Hot: return Hot;
                default: return Mute;
            }
        }

        public static Color Blend(Color top, Color under, double alpha)
        {
            return Color.FromArgb(
                (int)Math.Round(top.R * alpha + under.R * (1 - alpha)),
                (int)Math.Round(top.G * alpha + under.G * (1 - alpha)),
                (int)Math.Round(top.B * alpha + under.B * (1 - alpha)));
        }
    }
}
