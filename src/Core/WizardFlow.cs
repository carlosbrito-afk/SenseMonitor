using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    public enum WizardStep
    {
        Detect,
        Conflicts,
        Key,
        Fan,
        Mode,
        Summary
    }

    // Which steps of the "Teste de ambiente" apply to the notebook in front of us. A step that has
    // nothing to test is not shown at all (the summary says what could not be tested and why).
    public static class WizardFlow
    {
        public static List<WizardStep> Steps(DetectionResult d, CapabilityProfile pre, List<ConflictItem> conflicts, bool readOnly)
        {
            List<WizardStep> l = new List<WizardStep>();
            l.Add(WizardStep.Detect);
            bool firmware = d != null && d.IsAcer && d.HasGamingInterface;
            if (firmware && ConflictServices.AnyNeedsAction(conflicts)) l.Add(WizardStep.Conflicts);
            if (firmware) l.Add(WizardStep.Key);
            // The two steps that write to the firmware never run in diagnostic read-only mode.
            if (firmware && !readOnly && pre != null && pre.State(Cap.FanControl) == CapState.Untested) l.Add(WizardStep.Fan);
            if (firmware && !readOnly && ModeTestPlan.CanRun(pre)) l.Add(WizardStep.Mode);
            l.Add(WizardStep.Summary);
            return l;
        }

        // The step after current in the list (the last one stays).
        public static WizardStep Next(List<WizardStep> steps, WizardStep current)
        {
            int i = steps.IndexOf(current);
            if (i < 0 || i >= steps.Count - 1) return steps[steps.Count - 1];
            return steps[i + 1];
        }

        public static string Title(WizardStep s)
        {
            switch (s)
            {
                case WizardStep.Detect: return "Seu notebook";
                case WizardStep.Conflicts: return "Programas da Acer";
                case WizardStep.Key: return "Tecla do teclado";
                case WizardStep.Fan: return "Ventoinhas";
                case WizardStep.Mode: return "Modos";
                default: return "Resumo";
            }
        }
    }

    public sealed class SummaryRow
    {
        public Cap Cap;
        public string Name;
        public CapState State;
        public bool Enabled;
        public string Short;
    }

    // The words of the wizard that depend on data: short reasons for the summary and the lines of the
    // first step. Plain Portuguese, no jargon; the long reasons stay in the exported report.
    public static class WizardText
    {
        public const int MaxShort = 64;

        public static List<SummaryRow> Summary(CapabilityProfile p)
        {
            List<SummaryRow> rows = new List<SummaryRow>();
            foreach (Cap c in CapNames.All)
            {
                SummaryRow r = new SummaryRow();
                r.Cap = c;
                r.Name = CapNames.Name(c);
                r.State = p.State(c);
                r.Enabled = r.State == CapState.Confirmed;
                r.Short = ShortReason(c, r.State, p.Reason(c));
                rows.Add(r);
            }
            return rows;
        }

        public static string ShortReason(Cap cap, CapState state, string reason)
        {
            string r = (reason ?? "").ToLowerInvariant();
            switch (state)
            {
                case CapState.Confirmed:
                    if (cap == Cap.Sensors) return "Lendo as temperaturas agora.";
                    if (cap == Cap.FanRead) return "Lendo a rotação agora.";
                    if (cap == Cap.Key) return "A tecla respondeu no teste.";
                    return "Testado e funcionando.";
                case CapState.Untested:
                    if (r.Contains("somente leitura")) return "Modo somente leitura: não testado.";
                    if (r.Contains("pulad")) return "Teste pulado: continua bloqueado.";
                    if (r.Contains("já era esse")) return "Aceito, mas sem prova: continua bloqueado.";
                    if (cap == Cap.Battery) return "Existe, mas o app ainda não usa.";
                    if (cap == Cap.FanRead) return "Rotação zero ou ausente: sem prova.";
                    if (cap == Cap.Sensors) return "Sem leitura válida agora.";
                    return "Ainda não testado: continua bloqueado.";
                case CapState.Unavailable:
                    if (r.Contains("não é um notebook acer")) return "Só funciona em notebooks Acer.";
                    if (r.Contains("interface gamer do firmware") || r.Contains("acergamingfunction")) return "Este Acer não tem a interface gamer.";
                    if (r.Contains("você informou")) return "Você disse que não tem essa tecla.";
                    if (r.Contains("fora da tabela")) return "Só testado em modelos da tabela.";
                    if (r.Contains("overclock")) return "Bloqueado por segurança neste modelo.";
                    if (r.Contains("não anuncia")) return "O notebook não anuncia este modo.";
                    if (r.Contains("não lista")) return "A tabela do modelo não lista este modo.";
                    if (r.Contains("não confirmou")) return "O firmware não confirmou na leitura de volta.";
                    if (r.Contains("não seguiu") || r.Contains("no teste")) return "Falhou no teste.";
                    if (r.Contains("sem ventoinha") || r.Contains("nenhuma ventoinha")) return "Nenhuma ventoinha encontrada.";
                    if (r.Contains("não suportado") || r.Contains("não existe") || r.Contains("não expõe")) return "Não existe neste notebook.";
                    if (r.Contains("i2c")) return "Luz por I2C: o app não controla.";
                    if (r.Contains("tabela")) return "A tabela do modelo diz que não existe.";
                    return Truncate(FirstSentence(reason), MaxShort, "Não disponível neste notebook.");
                default:
                    if (r.Contains("administrador")) return "O firmware não respondeu (precisa de administrador).";
                    if (r.Contains("temperatura não está confirmada")) return "Temperatura não confirmada: sem teste.";
                    if (cap == Cap.Key) return "Nenhuma tecla conhecida respondeu.";
                    if (cap == Cap.KeyboardLight) return "Sem leitura confiável; o app não controla.";
                    if (r.Contains("outros números de sensor")) return "Este modelo usa outros números de sensor.";
                    return "Não foi possível saber neste notebook.";
            }
        }

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('.');
            return i > 0 ? s.Substring(0, i + 1) : s;
        }

        private static string Truncate(string s, int max, string fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1).TrimEnd() + "…";
        }

        // "Econômico, Silencioso, Dia a dia, Desempenho" as announced (the firmware's list when it is
        // believable, else the table's).
        public static string AnnouncedModes(DetectionResult d)
        {
            List<PerfMode> modes;
            if (d.Profiles.Plausible) modes = d.Profiles.Modes();
            else if (d.Table != null && d.Table.ProfilesKnown) modes = new List<PerfMode>(d.Table.Profiles);
            else return "nenhum informado de forma confiável";
            if (modes.Count == 0) return "nenhum (este modelo não tem perfis de desempenho)";
            List<string> names = new List<string>();
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                if (modes.Contains(m)) names.Add(ModeCatalog.Name(m));
            }
            return string.Join(", ", names.ToArray());
        }

        public static string AnnouncedSensors(DetectionResult d)
        {
            List<string> l = new List<string>();
            SensorBitmapInfo s = d.Sensors;
            SensorReading live = d.Raw.Live ?? new SensorReading();
            bool cpuT = s.Plausible ? s.CpuTemp : live.CpuTempC.HasValue;
            bool gpuT = s.Plausible ? s.GpuTemp : live.GpuTempC.HasValue;
            bool cpuF = s.Plausible ? s.CpuFan : (live.CpuFanRpm ?? 0) > 0;
            bool gpuF = s.Plausible ? s.GpuFan : (live.GpuFanRpm ?? 0) > 0;
            if (cpuT) l.Add("temperatura da CPU");
            if (gpuT) l.Add("temperatura da GPU");
            int fans = (cpuF ? 1 : 0) + (gpuF ? 1 : 0);
            if (fans == 2) l.Add("2 ventoinhas");
            else if (fans == 1) l.Add("1 ventoinha");
            if (l.Count == 0) return "nenhum sensor conhecido";
            return string.Join(", ", l.ToArray());
        }

        public static string ModelKnowledge(DetectionResult d)
        {
            if (d.Table == null)
                return "Modelo fora da tabela do SenseMonitor: os testes serão mais cuidadosos e nada é liberado sem teste.";
            string conf = d.Table.Confidence == 'A' ? "alta" : (d.Table.Confidence == 'M' ? "média" : "baixa");
            return "Modelo conhecido pela tabela (" + d.Table.Family + ", confiança " + conf + ").";
        }

        // A note for the key step when the model is known not to send an ordinary key.
        public static string KeyAdvice(DetectionResult d)
        {
            KeyKind k = d.Table == null ? KeyKind.Unknown : d.Table.Key;
            switch (k)
            {
                case KeyKind.TurboButtonWmi: return "Neste modelo o botão Turbo avisa por um evento do firmware, que este teste não enxerga: se nada aparecer, é normal.";
                case KeyKind.HidButton: return "Neste modelo o botão chega por HID, não como tecla comum: se nada aparecer, é normal.";
                case KeyKind.NoChannel: return "Neste modelo nenhum canal conhecido leva a tecla até o Windows: se nada aparecer, é normal.";
                default: return "";
            }
        }
    }
}
