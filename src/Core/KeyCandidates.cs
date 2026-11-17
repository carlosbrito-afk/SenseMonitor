using System;

namespace Forja.Core
{
    // A key code the app may listen for. ONLY these codes from the research are ever looked at;
    // every other key is ignored without being stored or logged.
    public sealed class KeyCandidate
    {
        public readonly string Id;
        // -1 = any virtual-key code.
        public readonly int Vk;
        public readonly int Scan;
        public readonly string Description;

        public KeyCandidate(string id, int vk, int scan, string description)
        {
            Id = id;
            Vk = vk;
            Scan = scan;
            Description = description;
        }

        public bool Matches(int vk, int scan)
        {
            return scan == Scan && (Vk < 0 || vk == Vk);
        }
    }

    // The dedicated NitroSense / PredatorSense key (docs\2026-11-17-pesquisa-modelos-acer.md, section 5):
    // our own reading on the Nitro ANV15-51 (vk 0xFF, scan 0x75) and the AT scan code 0xF5 that
    // the Linux hwdb maps to the same button on the Nitro and Predator Helios Neo families (the 0x75 of
    // Windows is probably the same code without the high bit). Predator models with a Turbo button send a
    // firmware event instead of a key, and some models send nothing the system can see.
    public static class KeyCandidates
    {
        public const string OursId = "vk0xFF-scan0x75";
        public const string AtF5Id = "scan0xF5";

        public static readonly KeyCandidate Ours = new KeyCandidate(OursId, 0xFF, 0x75, "código 0xFF / 0x75 (confirmado no Nitro V 15)");
        public static readonly KeyCandidate AtF5 = new KeyCandidate(AtF5Id, -1, 0xF5, "código de tecla 0xF5 (Nitro e Predator Helios Neo)");

        public static readonly KeyCandidate[] All = { Ours, AtF5 };

        public static KeyCandidate ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (KeyCandidate c in All)
            {
                if (string.Equals(c.Id, id, StringComparison.Ordinal)) return c;
            }
            return null;
        }

        // What the running app listens for. A profile that never tested the key (or none yet) keeps the
        // code the app always used; a confirmed key listens for that one code only; a notebook without the key,
        // or whose key never answered, listens for nothing.
        public static KeyCandidate[] ForProfile(CapabilityProfile p)
        {
            if (p == null) return new KeyCandidate[] { Ours };
            CapState s = p.State(Cap.Key);
            if (s == CapState.Confirmed)
            {
                KeyCandidate c = ById(p.KeyCandidateId);
                return c == null ? new KeyCandidate[0] : new KeyCandidate[] { c };
            }
            // Untested, or a timeout in the test (Unknown): the code the app always used keeps working. Only an
            // explicit "my notebook has no such key" (Unavailable) listens for nothing.
            if (s == CapState.Untested || s == CapState.Unknown) return new KeyCandidate[] { Ours };
            return new KeyCandidate[0];
        }
    }
}
