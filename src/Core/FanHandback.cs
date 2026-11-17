using System;

namespace Forja.Core
{
    // Policy for handing the fans back to the firmware on exit/suspend: try the normal
    // (UI-thread) write; if it is not confirmed, use the isolated writer. Only a confirmed
    // result lets the caller consider the fans handed back; otherwise it retries later.
    public static class FanHandback
    {
        public sealed class Result
        {
            public bool Confirmed;
            public bool UsedIsolated;
        }

        public static Result Run(Func<bool> normalWrite, Func<bool> isolatedWrite)
        {
            Result r = new Result();
            r.Confirmed = Safe(normalWrite);
            if (!r.Confirmed)
            {
                r.UsedIsolated = true;
                r.Confirmed = Safe(isolatedWrite);
            }
            return r;
        }

        private static bool Safe(Func<bool> step)
        {
            try { return step != null && step(); }
            catch (Exception) { return false; }
        }
    }
}
