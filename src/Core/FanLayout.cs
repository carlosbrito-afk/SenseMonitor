namespace Forja.Core
{
    // Which fans this notebook has, as far as the firmware commands are concerned: the bitmap of
    // fans in SetGamingFanBehavior (CPU = bit 0, GPU = bit 3) and which speed ids to write. The
    // confirmed machine has both; a one-fan model must not be asked for a fan it does not have.
    // Instances are singletons, so == works.
    public sealed class FanLayout
    {
        public static readonly FanLayout Both = new FanLayout(true, true);
        public static readonly FanLayout CpuOnly = new FanLayout(true, false);
        public static readonly FanLayout GpuOnly = new FanLayout(false, true);

        public readonly bool Cpu;
        public readonly bool Gpu;

        private FanLayout(bool cpu, bool gpu)
        {
            Cpu = cpu;
            Gpu = gpu;
        }

        public int Count
        {
            get { return (Cpu ? 1 : 0) + (Gpu ? 1 : 0); }
        }

        // null = no fan at all (nothing to control).
        public static FanLayout From(bool cpu, bool gpu)
        {
            if (cpu && gpu) return Both;
            if (cpu) return CpuOnly;
            if (gpu) return GpuOnly;
            return null;
        }
    }
}
