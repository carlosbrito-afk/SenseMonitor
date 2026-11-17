using System;
using System.Runtime.InteropServices;
using Forja.Core;

namespace Forja.Platform
{
    // Active Windows power plan and the "Modo de energia" slider (overlay), read through powrprof.dll
    // (documented calls, no process started). Never throws; whatever cannot be read stays null.
    public static class PowerPlanReader
    {
        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayPolicyGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static PowerPlanInfo Read(bool? onBattery)
        {
            string scheme = null;
            string overlay = null;
            try
            {
                IntPtr p;
                if (PowerGetActiveScheme(IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
                {
                    try { scheme = ((Guid)Marshal.PtrToStructure(p, typeof(Guid))).ToString("D"); }
                    finally { LocalFree(p); }
                }
            }
            catch (Exception)
            {
            }
            try
            {
                Guid g;
                if (PowerGetEffectiveOverlayScheme(out g) == 0) overlay = g.ToString("D");
            }
            catch (Exception)
            {
                // Older Windows has no power mode slider.
            }
            return PowerPlanNames.Named(scheme, overlay, onBattery);
        }
    }
}
