using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // Older versions kept two single-machine flags (FanControlValidated, ModeCheckPassed) from a test
    // that only ever ran on the Nitro ANV15-51. The first start of this version turns them into a profile
    // for the machine that is running now, so nothing already proved is lost.
    public static class EnvMigration
    {
        public const string LegacyModelCode = "ANV15-51";

        // null when nothing was validated before, or the identity cannot be read.
        public static CapabilityProfile FromLegacy(Config c, string model, string bios, string nowUtc)
        {
            if (c == null || (!c.FanControlValidated && !c.ModeCheckPassed)) return null;
            // The legacy routine ever ran on one machine only: the Nitro ANV15-51. config.json travels (roaming
            // profile, restored apps, a copied folder), so any other model must not inherit that validation.
            if (ModelTable.ExtractCode(model) != LegacyModelCode) return null;
            string fp = MachineFingerprint.Make(model, bios);
            if (fp == null) return null;

            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = fp;
            p.Model = model;
            p.Bios = bios;
            p.CreatedUtc = nowUtc;
            p.Migrated = true;
            const string old = "Migrado do teste antigo deste notebook";

            if (c.FanControlValidated)
            {
                p.CpuFan = true;
                p.GpuFan = true;
                p.Set(Cap.Sensors, CapState.Confirmed, old + ": as leituras de temperatura e rotação já funcionavam.");
                p.Set(Cap.FanRead, CapState.Confirmed, old + ": a rotação das duas ventoinhas foi medida.");
                p.Set(Cap.FanControl, CapState.Confirmed, old + ": as ventoinhas seguiram os comandos.");
            }
            else
            {
                p.Set(Cap.Sensors, CapState.Untested, "Refaça o teste de ambiente para confirmar.");
                p.Set(Cap.FanRead, CapState.Untested, "Refaça o teste de ambiente para confirmar.");
                p.Set(Cap.FanControl, CapState.Untested, "O controle manual nunca foi validado: refaça o teste de ambiente.");
            }

            ModelEntry table = ModelTable.Find(model);
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                Cap cap = ModeCatalog.CapOf(m);
                bool legacyMode = m == PerfMode.Silencioso || m == PerfMode.DiaADia || m == PerfMode.Desempenho;
                if (legacyMode && c.ModeCheckPassed)
                {
                    p.Set(cap, CapState.Confirmed, old + ": o modo foi trocado e confirmado na leitura de volta.");
                }
                else if (m == PerfMode.Turbo && table != null && table.ProfilesKnown && !new System.Collections.Generic.List<PerfMode>(table.Profiles).Contains(m))
                {
                    p.Set(cap, CapState.Unavailable, "A tabela do modelo não lista o perfil Turbo.");
                }
                else
                {
                    p.Set(cap, CapState.Untested, "Este modo não foi coberto pelo teste antigo: refaça o teste de ambiente.");
                }
            }
            p.Set(Cap.Key, CapState.Untested, "Ainda não testado: o app continua ouvindo só o código que sempre usou.");
            p.Set(Cap.Battery, CapState.Unknown, "Refaça o teste de ambiente para detectar.");
            p.Set(Cap.KeyboardLight, CapState.Unknown, "Refaça o teste de ambiente para detectar.");
            return p;
        }
    }

    // What the app decides at start, from the saved config and the machine it is running on.
    public static class EnvStartup
    {
        // Sets Config.Env (migrating the old flags when there is no profile) and Config.EnvStale; returns the status.
        public static EnvStatus Apply(Config c, string model, string bios, string nowUtc)
        {
            if (c == null) return EnvStatus.NeverTested;
            string fp = MachineFingerprint.Make(model, bios);
            if (c.Env == null)
            {
                CapabilityProfile m = EnvMigration.FromLegacy(c, model, bios, nowUtc);
                if (m != null)
                {
                    c.Env = m;
                    c.EnvStale = false;
                    return EnvStatus.Fresh;
                }
                bool legacy = c.FanControlValidated || c.ModeCheckPassed;
                // An old validation that cannot be vouched for (identity unreadable, or a model other than the
                // one that was validated): it stays locked and the wizard is offered.
                c.EnvStale = legacy;
                return c.EnvStale ? EnvStatus.Stale : EnvStatus.NeverTested;
            }
            EnvStatus s = EnvCheck.Evaluate(c.Env, fp);
            c.EnvStale = s == EnvStatus.Stale;
            return s;
        }
    }

    // The one-time reminder on the first open after a wizard (or an installer run) left tests pending.
    public static class EnvReminder
    {
        public static bool Due(Config c)
        {
            return c != null && c.Env != null && !c.EnvStale && !c.EnvReminderShown && c.Env.PendingTests().Count > 0;
        }

        public const string Text = "Faltam alguns testes do ambiente (ventoinhas, modos ou tecla). Abra Configurações e use “Refazer teste de ambiente”.";
    }

    // A finished wizard run goes into the config: the profile, and the single-machine flags the rest of
    // the app (fan controller, RPM guard, sentinel) still reads.
    public static class EnvApply
    {
        // Undo records collected before a config existed (the installer's detection runs before the install):
        // added to the config, never replacing an older record of the same service (that one holds the original state).
        public static void MergeServiceUndo(Config c, List<ServiceUndo> records)
        {
            if (c == null || records == null) return;
            if (c.ServiceUndo == null) c.ServiceUndo = new List<ServiceUndo>();
            foreach (ServiceUndo r in records)
            {
                if (!ConflictServices.ValidRecord(r)) continue; // only Acer's five services, only Auto/Manual
                bool have = false;
                foreach (ServiceUndo e in c.ServiceUndo)
                {
                    if (e != null && string.Equals(e.Name, r.Name, StringComparison.OrdinalIgnoreCase)) { have = true; break; }
                }
                if (!have) c.ServiceUndo.Add(r);
            }
        }

        public static void Apply(Config c, CapabilityProfile p, EnvTestResults t, string nowUtc)
        {
            if (c == null || p == null) return;
            if (t == null) t = new EnvTestResults();
            c.Env = p;
            // A profile without a fingerprint (the identity could not be read) can never be matched later: it
            // stays stale, so nothing it confirms is unlocked.
            c.EnvStale = string.IsNullOrEmpty(p.Fingerprint);
            c.EnvReminderShown = false;

            bool fanOk = p.Allows(Cap.FanControl);
            c.FanControlValidated = fanOk;
            c.FanMaxRpmCpu = fanOk && p.CpuFan ? t.Fan.MaxRpmCpu : 0;
            c.FanMaxRpmGpu = fanOk && p.GpuFan ? t.Fan.MaxRpmGpu : 0;
            c.FanValidationUtc = nowUtc;
            c.FanValidationNotes = "Teste de ambiente: controle das ventoinhas "
                + (fanOk ? "aprovado" : CapNames.State(p.State(Cap.FanControl)).ToLowerInvariant());
            if (!fanOk && c.FanMode != "Auto") c.FanMode = "Auto";

            bool anyTested = false;
            bool anyFailed = false;
            foreach (ModeStepResult r in t.Modes)
            {
                if (r.Status == StepStatus.Passed) anyTested = true;
                if (r.Status == StepStatus.Failed) anyFailed = true;
            }
            c.ModeCheckPassed = anyTested && !anyFailed && p.Allows(Cap.ModeBalanced);
            c.ModeCheckUtc = nowUtc;
            c.ModeCheckNotes = "Teste de ambiente: " + p.ConfirmedModes().Count.ToString(CultureInfo.InvariantCulture) + " modo(s) confirmado(s)";
        }
    }
}
