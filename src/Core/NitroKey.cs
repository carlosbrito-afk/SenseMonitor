using System;

namespace Forja.Core
{
    // Recognises the dedicated NitroSense hardware key. This OEM key emits repeated
    // make/break pairs while held, so a press is debounced by time: a target keydown counts
    // only if no target event (down OR up) was seen in the last DebounceMs. PRIVACY: the only
    // state is one timestamp of the last TARGET-key event. Nothing about any other key is
    // ever stored or logged.
    public class NitroKey
    {
        private const int WM_KEYDOWN = 0x100;
        private const int WM_KEYUP = 0x101;
        private const int WM_SYSKEYDOWN = 0x104;
        private const int WM_SYSKEYUP = 0x105;
        private const int TargetVk = 0xFF;
        private const int TargetScan = 0x75;

        public const int DebounceMs = 700;

        private readonly object gate = new object();
        private bool seen;
        private uint lastEventTime;

        // Uses the local tick count as the time source.
        public bool OnKey(int msg, int vk, int scan, int flags)
        {
            return OnKey(msg, vk, scan, flags, Environment.TickCount);
        }

        // time = KBDLLHOOKSTRUCT.time (ms). Safe to call from the hook thread.
        public bool OnKey(int msg, int vk, int scan, int flags, int time)
        {
            if (vk != TargetVk || scan != TargetScan) return false;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!isDown && !isUp) return false;
            uint now = unchecked((uint)time);
            lock (gate)
            {
                bool fire = isDown && (!seen || unchecked(now - lastEventTime) >= DebounceMs);
                seen = true;
                lastEventTime = now;
                return fire;
            }
        }

        // Forget the last target event (call after resume from sleep / session unlock).
        public void Reset()
        {
            lock (gate) { seen = false; }
        }
    }
}
