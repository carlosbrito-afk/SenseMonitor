using System;
using System.Runtime.InteropServices;

namespace Forja.Platform
{
    // Keeps Windows from going to sleep while a long test runs (a busy CPU does not count as
    // activity for the idle timer). The state belongs to the calling thread: call Begin and End
    // from the same thread. The display may still turn off.
    public static class KeepAwake
    {
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        public static void Begin()
        {
            try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); }
            catch (Exception) { }
        }

        public static void End()
        {
            try { SetThreadExecutionState(ES_CONTINUOUS); }
            catch (Exception) { }
        }
    }
}
