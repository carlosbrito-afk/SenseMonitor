using System;
using Forja.Core;

namespace Forja.Platform
{
    // Which fans the firmware commands of THIS process address. Read once from the saved environment
    // profile (read-only: nothing is created or rewritten) the first time a firmware object is built;
    // both fans until a profile says otherwise (the behaviour of every earlier version). The wizard sets it
    // as soon as it knows the fan count, before its fan test writes anything.
    public static class FanLayoutStore
    {
        private static readonly object sync = new object();
        private static FanLayout current;
        private static bool loaded;
        private static bool explicitlySet;

        // Test seam: where the profile is read from (null = the real config path).
        internal static string PathOverride = null;

        public static FanLayout Current
        {
            get
            {
                lock (sync)
                {
                    if (!loaded)
                    {
                        loaded = true;
                        current = ReadFromDisk();
                    }
                    return current ?? FanLayout.Both;
                }
            }
        }

        // The layout THIS process chose with Set (the tray from its profile, the wizard for its test), or null when it never did.
        // Reading Current from the saved profile does not count: a fresh process (the uninstaller, a crash before any fan write)
        // has no fans of its own to hand back, and must never fall back to the both-fan default (m3).
        public static FanLayout CurrentIfSet
        {
            get
            {
                lock (sync)
                {
                    return explicitlySet ? current : null;
                }
            }
        }

        public static void Set(FanLayout layout)
        {
            lock (sync)
            {
                loaded = true;
                current = layout;
                explicitlySet = true;
            }
        }

        // Back to "nothing chosen by this process" (the wizard after its test put nothing of its own in place).
        public static void Unset()
        {
            lock (sync)
            {
                loaded = false;
                current = null;
                explicitlySet = false;
            }
        }

        // Forget what was loaded (tests).
        internal static void Reset()
        {
            lock (sync)
            {
                loaded = false;
                current = null;
                explicitlySet = false;
            }
        }

        private static FanLayout ReadFromDisk()
        {
            CapabilityProfile p = ReadSavedProfile();
            return p == null ? null : p.Layout;
        }

        // The environment profile in the saved config (read-only: nothing is created or rewritten); null = none or unreadable.
        public static CapabilityProfile ReadSavedProfile()
        {
            try
            {
                Config c;
                string path = PathOverride ?? Config.DefaultPath;
                if (Config.TryLoad(path, out c) != ConfigRead.Ok || c == null) return null;
                return c.Env;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
