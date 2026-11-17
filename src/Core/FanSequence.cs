using System;

namespace Forja.Core
{
    public static class FanSequence
    {
        // set(method, input) performs one firmware write and returns true on success.
        // On any failed step Auto behavior is written last, so the fans never stay
        // in an unverified Custom state.
        public static bool SetPercent(Func<string, ulong, bool> set, int cpuPct, int gpuPct)
        {
            return SetPercent(set, cpuPct, gpuPct, FanLayout.Both);
        }

        // Only the fans of the layout are written (a one-fan notebook is never asked for the other).
        public static bool SetPercent(Func<string, ulong, bool> set, int cpuPct, int gpuPct, FanLayout layout)
        {
            if (layout == null) layout = FanLayout.Both;
            bool ok = set("SetGamingFanBehavior", Codes.FanBehavior(FanMode.Custom, FanMode.Custom, layout))
                && (!layout.Cpu || set("SetGamingFanSpeed", Codes.FanSpeed(Codes.FanCpu, cpuPct)))
                && (!layout.Gpu || set("SetGamingFanSpeed", Codes.FanSpeed(Codes.FanGpu, gpuPct)));
            if (!ok) set("SetGamingFanBehavior", Codes.FanBehavior(FanMode.Auto, FanMode.Auto, layout));
            return ok;
        }
    }
}
