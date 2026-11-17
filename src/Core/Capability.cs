using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Forja.Core
{
    // What the app may offer on a notebook. Every capability is decided from three sources:
    // what the firmware announces, what the model table expects and what the guided test proved.
    public enum Cap
    {
        Sensors,
        FanRead,
        FanControl,
        ModeQuiet,
        ModeBalanced,
        ModePerformance,
        ModeTurbo,
        ModeEco,
        Key,
        Battery,
        KeyboardLight
    }

    public enum CapState
    {
        // Proved on this notebook (a reading made sense, or a guided test passed).
        Confirmed,
        // Announced or expected, but no test proved it yet (or the user skipped the test): stays locked.
        Untested,
        // The firmware does not announce it, the table says the model lacks it, or a test failed.
        Unavailable,
        // Nothing reliable to say (unknown model, bitmap garbage, no safe way to probe).
        Unknown
    }

    // One row of the profile. State and Id are stored as text so the config file stays readable and
    // survives a reorder of the enums.
    [DataContract]
    public sealed class CapEntry
    {
        [DataMember] public string Id;
        [DataMember] public string State;
        [DataMember] public string Reason;
        // When the guided test confirmed it (UTC, kept when a later run skips the step) and how: "manual" or "automático". null = not recorded.
        [DataMember] public string Utc;
        [DataMember] public string Method;

        public CapEntry()
        {
        }

        public CapEntry(Cap cap, CapState state, string reason)
        {
            Id = cap.ToString();
            State = state.ToString();
            Reason = reason;
        }

        public Cap? CapValue
        {
            get
            {
                try { return (Cap)Enum.Parse(typeof(Cap), Id ?? "", false); }
                catch (ArgumentException) { return null; }
            }
        }

        // An unreadable state is "unknown": never "confirmed".
        public CapState StateValue
        {
            get
            {
                try { return (CapState)Enum.Parse(typeof(CapState), State ?? "", false); }
                catch (ArgumentException) { return CapState.Unknown; }
            }
        }
    }

    // The result of the environment test for one machine (stored in config.json). Identity is the
    // fingerprint (model + BIOS version, no serial numbers and no user name).
    [DataContract]
    public sealed class CapabilityProfile
    {
        [DataMember] public string Fingerprint;
        [DataMember] public string Model;
        [DataMember] public string Bios;
        [DataMember] public string CreatedUtc;
        // Filled from the single-machine flags of an older version (FanControlValidated, ModeCheckPassed).
        [DataMember] public bool Migrated;
        [DataMember] public bool CpuFan;
        [DataMember] public bool GpuFan;
        // Raw bitmaps as hex text (what the firmware announced) and whether the profile bitmap was believed.
        [DataMember] public string SensorBitmap;
        [DataMember] public string ProfileBitmap;
        [DataMember] public bool ProfileBitmapTrusted;
        // Id of the key candidate that answered in the key test ("" = none).
        [DataMember] public string KeyCandidateId;
        [DataMember] public List<CapEntry> Entries;

        public CapabilityProfile()
        {
            Entries = new List<CapEntry>();
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            Entries = new List<CapEntry>();
        }

        public CapEntry Find(Cap cap)
        {
            if (Entries == null) return null;
            string id = cap.ToString();
            foreach (CapEntry e in Entries)
            {
                if (e != null && e.Id == id) return e;
            }
            return null;
        }

        // A capability the profile has no row for is unknown.
        public CapState State(Cap cap)
        {
            CapEntry e = Find(cap);
            return e == null ? CapState.Unknown : e.StateValue;
        }

        public string Reason(Cap cap)
        {
            CapEntry e = Find(cap);
            return e == null ? null : e.Reason;
        }

        public void Set(Cap cap, CapState state, string reason)
        {
            if (Entries == null) Entries = new List<CapEntry>();
            CapEntry e = Find(cap);
            if (e == null)
            {
                Entries.Add(new CapEntry(cap, state, reason));
                return;
            }
            e.State = state.ToString();
            e.Reason = reason;
        }

        // The app offers a capability only when it is confirmed.
        public bool Allows(Cap cap)
        {
            return State(cap) == CapState.Confirmed;
        }

        public bool AllowsMode(PerfMode m)
        {
            return Allows(ModeCatalog.CapOf(m));
        }

        // Modes the app may write, in display order.
        public List<PerfMode> ConfirmedModes()
        {
            List<PerfMode> l = new List<PerfMode>();
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                if (AllowsMode(m)) l.Add(m);
            }
            return l;
        }

        // Guided capabilities still waiting for a test (skipped with "Fazer depois", or never run): the app reminds
        // the user once. Battery and keyboard light are information only and never pending.
        public List<Cap> PendingTests()
        {
            List<Cap> l = new List<Cap>();
            Cap[] guided = { Cap.FanControl, Cap.ModeEco, Cap.ModeQuiet, Cap.ModeBalanced, Cap.ModePerformance, Cap.ModeTurbo, Cap.Key };
            foreach (Cap c in guided)
            {
                if (State(c) == CapState.Untested) l.Add(c);
            }
            return l;
        }

        // null = no fan to control.
        public FanLayout Layout
        {
            get { return FanLayout.From(CpuFan, GpuFan); }
        }

        public int FanCount
        {
            get { return (CpuFan ? 1 : 0) + (GpuFan ? 1 : 0); }
        }
    }

    public static class CapNames
    {
        public static string Name(Cap c)
        {
            switch (c)
            {
                case Cap.Sensors: return "Temperaturas";
                case Cap.FanRead: return "Rotação das ventoinhas";
                case Cap.FanControl: return "Controle das ventoinhas";
                case Cap.ModeQuiet: return "Modo Silencioso";
                case Cap.ModeBalanced: return "Modo Dia a dia";
                case Cap.ModePerformance: return "Modo Desempenho";
                case Cap.ModeTurbo: return "Modo Turbo";
                case Cap.ModeEco: return "Modo Econômico";
                case Cap.Key: return "Tecla NitroSense / PredatorSense";
                case Cap.Battery: return "Limite de carga da bateria";
                case Cap.KeyboardLight: return "Iluminação do teclado";
                default: return c.ToString();
            }
        }

        public static string State(CapState s)
        {
            switch (s)
            {
                case CapState.Confirmed: return "Disponível";
                case CapState.Untested: return "Não testado";
                case CapState.Unavailable: return "Indisponível";
                default: return "Desconhecido";
            }
        }

        // Display order of the summary.
        public static readonly Cap[] All =
        {
            Cap.Sensors, Cap.FanRead, Cap.FanControl,
            Cap.ModeEco, Cap.ModeQuiet, Cap.ModeBalanced, Cap.ModePerformance, Cap.ModeTurbo,
            Cap.Key, Cap.Battery, Cap.KeyboardLight
        };
    }
}
