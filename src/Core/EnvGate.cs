using System;

namespace Forja.Core
{
    // The support command lines that write to the firmware (--validar-ventoinha, --teste-desempenho) obey the
    // capability profile: they refuse, with a plain reason, unless the capability they exercise is at least
    // announced and not flagged dangerous for this notebook, and they never run on a profile that belongs to
    // another notebook or BIOS. The answer is null when the tool may run.
    public static class EnvGate
    {
        public const string Pointer = "Rode SenseMonitor.exe --teste-ambiente (ou Configurações → Refazer teste de ambiente).";

        // Evaluates the profile of the config for this machine (migrating/marking stale like the app does at start).
        // The config instance is not saved by this call.
        public static string ForFanTool(Config c, string model, string bios, string nowUtc)
        {
            string common = Common(c, model, bios, nowUtc);
            if (common != null) return common;
            CapState s = c.Env.State(Cap.FanControl);
            if (s != CapState.Untested && s != CapState.Confirmed)
            {
                return "Esta ferramenta das ventoinhas foi recusada: " + FirstSentence(c.Env.Reason(Cap.FanControl), "o controle das ventoinhas não está liberado neste notebook") + " " + Pointer;
            }
            return null;
        }

        public static string ForModeTool(Config c, string model, string bios, string nowUtc)
        {
            string common = Common(c, model, bios, nowUtc);
            if (common != null) return common;
            Cap[] needed = { Cap.ModeBalanced, Cap.ModeQuiet, Cap.ModePerformance };
            foreach (Cap cap in needed)
            {
                CapState s = c.Env.State(cap);
                if (s != CapState.Untested && s != CapState.Confirmed)
                {
                    return "Este teste foi recusado: " + FirstSentence(c.Env.Reason(cap), "o " + CapNames.Name(cap).ToLowerInvariant() + " não está liberado neste notebook")
                        + " O teste de desempenho usa os modos Silencioso, Dia a dia e Desempenho. " + Pointer;
                }
            }
            return null;
        }

        // Quiet, Balanced and Performance all announced and not dangerous: the mode check of the fan tool may run.
        public static bool ModesAnnounced(CapabilityProfile p)
        {
            if (p == null) return false;
            Cap[] needed = { Cap.ModeBalanced, Cap.ModeQuiet, Cap.ModePerformance };
            foreach (Cap cap in needed)
            {
                CapState s = p.State(cap);
                if (s != CapState.Untested && s != CapState.Confirmed) return false;
            }
            return true;
        }

        private static string Common(Config c, string model, string bios, string nowUtc)
        {
            if (c == null) return "Sem configuração. " + Pointer;
            EnvStatus s = EnvStartup.Apply(c, model, bios, nowUtc);
            if (s == EnvStatus.Stale) return "Esta ferramenta foi recusada: " + EnvCheck.StaleNote.Replace("em Configurações.", "em Configurações.") + " " + Pointer;
            if (c.Env == null) return "Esta ferramenta foi recusada: " + EnvCheck.NeverNote + " " + Pointer;
            return null;
        }

        private static string FirstSentence(string s, string fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback + ".";
            int i = s.IndexOf('.');
            return i > 0 ? s.Substring(0, i + 1) : s;
        }
    }
}
