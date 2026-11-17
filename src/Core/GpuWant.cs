using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // How much GPU data a telemetry owner (a page, the widget) asks for.
    public enum GpuWant
    {
        // CPU and RAM only: the GPU counters are not read.
        None = 0,
        // Driver-free signals only: the Windows "GPU Engine" and "GPU Adapter Memory" counters. No
        // nvidia-smi process is ever started for this level (the widget uses it: it stays open during a game,
        // when a new process every 2 s would cost frame time).
        Counters = 1,
        // Counters plus nvidia-smi when its gate allows it (the Monitor page).
        Full = 2
    }

    public static class GpuWants
    {
        // The strongest request among the owners.
        public static GpuWant Combine(IEnumerable<GpuWant> wants)
        {
            GpuWant best = GpuWant.None;
            if (wants == null) return best;
            foreach (GpuWant w in wants)
            {
                if (w > best) best = w;
            }
            return best;
        }

        public static bool ReadsGpu(GpuWant want)
        {
            return want != GpuWant.None;
        }

        // nvidia-smi may start only for a Full request, when it exists and the NVIDIA adapter was found.
        public static bool SmiPossible(GpuWant want, bool smiExists, bool nvidiaFound)
        {
            return want == GpuWant.Full && smiExists && nvidiaFound;
        }
    }
}
