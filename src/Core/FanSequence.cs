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
            bool ok = set("SetGamingFanBehavior", Codes.FanBehavior(FanMode.Custom, FanMode.Custom))
                && set("SetGamingFanSpeed", Codes.FanSpeed(Codes.FanCpu, cpuPct))
                && set("SetGamingFanSpeed", Codes.FanSpeed(Codes.FanGpu, gpuPct));
            if (!ok) set("SetGamingFanBehavior", Codes.FanBehavior(FanMode.Auto, FanMode.Auto));
            return ok;
        }
    }
}
