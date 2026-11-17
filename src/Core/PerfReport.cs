using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Forja.Core
{
    // Text and JSON of the performance test (teste-desempenho.txt / .json and the final dialog).
    public static class PerfReport
    {
        private static readonly CultureInfo Br = new CultureInfo("pt-BR");
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string F(double? v, string fmt)
        {
            return v.HasValue ? v.Value.ToString(fmt, Br) : "-";
        }

        private static string I(int? v)
        {
            return v.HasValue ? v.Value.ToString(Br) : "-";
        }

        private static string Pair(double? a, double? b, string fmt)
        {
            return F(a, fmt) + " / " + F(b, fmt);
        }

        // The comparison table (fixed-width: show it in a monospaced font).
        public static string Table(PerfTestReport rep)
        {
            string[] head = { "Modo", "Multi 60 s", "Pico 10 s", "1 núcleo", "CPU °C méd/máx", "GPU °C máx", "RPM CPU/GPU", "Clock aprox. MHz" };
            List<string[]> rows = new List<string[]>();
            foreach (PerfModeRun r in rep.Runs)
            {
                PerfStats s = r.Stats ?? PerfScoring.Summarize(r);
                rows.Add(Row(PerfVerdict.RunLabel(rep, r) + (r.Applied ? (r.Aborted ? " (interrompido)" : "") : " (não aplicado)"), s));
            }
            PerfModeRun reference = PerfVerdict.Reference(rep);
            if (reference != null && rep.FindAll(PerfMode.DiaADia).Count >= 2 && !rep.Runs.Contains(reference))
            {
                rows.Add(Row("Dia a dia (média)", reference.Stats));
            }
            int[] w = new int[head.Length];
            for (int c = 0; c < head.Length; c++)
            {
                w[c] = head[c].Length;
                foreach (string[] row in rows) { if (row[c].Length > w[c]) w[c] = row[c].Length; }
            }
            StringBuilder sb = new StringBuilder();
            AppendRow(sb, head, w);
            foreach (string[] row in rows) AppendRow(sb, row, w);
            sb.AppendLine("(rendimento em unidades de trabalho por segundo; mais alto = melhor)");
            sb.AppendLine("CPU, janela de 60 s: a nota 'Multi 60 s' vem dos 45 s finais de uma carga de 60 s; inclui o turbo e o início do regime sustentado, não o desempenho sustentado de longo prazo.");
            return sb.ToString();
        }

        private static string[] Row(string label, PerfStats s)
        {
            string[] row = new string[8];
            row[0] = label;
            row[1] = F(s.MultiSustained, "0.0");
            row[2] = F(s.Burst, "0.0");
            row[3] = F(s.Single, "0.0");
            row[4] = Pair(s.AvgCpuTemp, s.PeakCpuTemp, "0");
            row[5] = F(s.PeakGpuTemp, "0");
            row[6] = I(s.AvgCpuRpm) + " / " + I(s.AvgGpuRpm);
            row[7] = F(s.AvgClockMulti, "0");
            return row;
        }

        private static void AppendRow(StringBuilder sb, string[] cells, int[] w)
        {
            for (int c = 0; c < cells.Length; c++)
            {
                sb.Append(c == 0 ? cells[c].PadRight(w[c]) : cells[c].PadLeft(w[c]));
                if (c < cells.Length - 1) sb.Append("  ");
            }
            sb.AppendLine();
        }

        // Header, preconditions, table and verdict (what the final dialog shows).
        public static string Summary(PerfTestReport rep, PerfVerdictResult verdict)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Teste de desempenho dos modos — " + rep.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " UTC");
            if (rep.Aborted) sb.AppendLine("INTERROMPIDO: " + rep.Reason);
            if (rep.PreviousCrashNote != null) sb.AppendLine("ATENÇÃO: " + rep.PreviousCrashNote + ".");
            sb.AppendLine("Duração: " + ((int)Math.Round(rep.TotalSec)).ToString(Inv) + " s");
            PerfPreconditions p = rep.Pre;
            if (p.AppVersion != null) sb.AppendLine("Versão do SenseMonitor: " + p.AppVersion);
            sb.AppendLine("Energia: " + p.PowerSource + (p.BatteryPercent.HasValue ? ", bateria em " + p.BatteryPercent.Value + " %" : "") + (p.OnBattery == true ? " (na bateria: o resultado pode ser limitado)" : ""));
            sb.AppendLine("Uso de CPU de fundo antes do teste: " + (p.BackgroundCpuPct.HasValue ? F(p.BackgroundCpuPct, "0.0") + " % (média de " + PerfPlan.BackgroundSec + " s)" : "não medido"));
            sb.AppendLine("Plano de energia: " + (p.PowerSchemeName ?? "desconhecido") + (p.PowerSchemeGuid != null ? " (" + p.PowerSchemeGuid + ")" : ""));
            sb.AppendLine("CPU: " + (p.CpuName ?? "desconhecida") + " — " + p.PhysicalCores + " núcleos, " + p.LogicalCores + " threads; carga em " + p.LoadThreads + " threads");
            sb.AppendLine(p.Fans);
            sb.AppendLine(p.Gpu);
            if (rep.BaselineCpuTempC.HasValue) sb.AppendLine("Temperatura de repouso no início: " + F(rep.BaselineCpuTempC, "0") + " °C");
            sb.AppendLine();
            sb.Append(Table(rep));
            sb.AppendLine();
            sb.AppendLine("Veredito:");
            foreach (string line in verdict.Lines) sb.AppendLine("- " + line);
            if (verdict.Lines.Count == 0) sb.AppendLine("- (sem comparação possível)");
            sb.AppendLine();
            sb.AppendLine("Ao final: modo Dia a dia " + (rep.FinalModeOk ? "confirmado" : "NÃO confirmado") + "; ventoinhas " + (rep.FinalFansAuto ? "em Automático" : "NÃO confirmadas em Automático") + ".");
            foreach (PerfModeRun r in rep.Runs)
            {
                sb.AppendLine(PerfVerdict.RunLabel(rep, r) + ": " + (r.SwitchNote ?? "não chegou a ser aplicado")
                    + (r.Aborted ? "; interrompido — " + r.AbortReason : "")
                    + "; espera para esfriar " + ((int)Math.Round(r.CooldownSec)).ToString(Inv) + " s" + (r.CooldownReached ? "" : " (meta não atingida)")
                    + "; CPU ao começar a carga " + F(r.StartTempC, "0") + " °C"
                    + (r.Stats != null && r.Stats.IdleCpuLoad.HasValue ? "; CPU de fundo no repouso " + F(r.Stats.IdleCpuLoad, "0.0") + " %" : ""));
            }
            return sb.ToString();
        }

        // The whole file: summary plus every per-second sample (CSV, ';' separated).
        public static string FullText(PerfTestReport rep, PerfVerdictResult verdict)
        {
            StringBuilder sb = new StringBuilder(Summary(rep, verdict));
            sb.AppendLine();
            sb.AppendLine("Amostras por segundo (modo;fase;t_s;cpu_c;gpu_c;rpm_cpu;rpm_gpu;clock_aprox_mhz;descarga_mw;cpu_uso_pct;unidades)");
            foreach (PerfModeRun r in rep.Runs)
            {
                foreach (PerfSample s in r.Samples)
                {
                    sb.Append(PerfVerdict.RunLabel(rep, r)).Append(';').Append(s.Phase).Append(';')
                      .Append(s.T.ToString("0.00", Inv)).Append(';')
                      .Append(Raw(s.CpuTempC)).Append(';').Append(Raw(s.GpuTempC)).Append(';')
                      .Append(s.CpuRpm.HasValue ? s.CpuRpm.Value.ToString(Inv) : "").Append(';')
                      .Append(s.GpuRpm.HasValue ? s.GpuRpm.Value.ToString(Inv) : "").Append(';')
                      .Append(Raw(s.ClockMHz)).Append(';').Append(Raw(s.DischargeMw)).Append(';')
                      .Append(Raw(s.CpuLoadPct)).Append(';')
                      .Append(s.Units.ToString(Inv)).AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string Raw(double? v)
        {
            return v.HasValue ? v.Value.ToString("0.##", Inv) : "";
        }

        // ---- JSON (hand-written: numbers with '.', null for missing) ----

        public static string Json(PerfTestReport rep, PerfVerdictResult verdict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"startedUtc\":").Append(Str(rep.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)));
            sb.Append(",\"totalSec\":").Append(Num(rep.TotalSec));
            sb.Append(",\"aborted\":").Append(Bool(rep.Aborted));
            sb.Append(",\"cancelled\":").Append(Bool(rep.Cancelled));
            sb.Append(",\"reason\":").Append(Str(rep.Reason));
            sb.Append(",\"finalModeOk\":").Append(Bool(rep.FinalModeOk));
            sb.Append(",\"finalFansAuto\":").Append(Bool(rep.FinalFansAuto));
            sb.Append(",\"baselineCpuTempC\":").Append(Num(rep.BaselineCpuTempC));
            sb.Append(",\"restoreOk\":").Append(Bool(rep.RestoreOk));
            sb.Append(",\"previousCrash\":").Append(Str(rep.PreviousCrashNote));
            PerfPreconditions p = rep.Pre;
            sb.Append(",\"preconditions\":{");
            sb.Append("\"appVersion\":").Append(Str(p.AppVersion));
            sb.Append(",\"backgroundCpuPct\":").Append(Num(p.BackgroundCpuPct));
            sb.Append(",\"batteryPercent\":").Append(Num(p.BatteryPercent));
            sb.Append(",\"onBattery\":").Append(p.OnBattery.HasValue ? Bool(p.OnBattery.Value) : "null");
            sb.Append(",\"powerSource\":").Append(Str(p.PowerSource));
            sb.Append(",\"powerSchemeName\":").Append(Str(p.PowerSchemeName));
            sb.Append(",\"powerSchemeGuid\":").Append(Str(p.PowerSchemeGuid));
            sb.Append(",\"cpuName\":").Append(Str(p.CpuName));
            sb.Append(",\"logicalCores\":").Append(p.LogicalCores.ToString(Inv));
            sb.Append(",\"physicalCores\":").Append(p.PhysicalCores.ToString(Inv));
            sb.Append(",\"loadThreads\":").Append(p.LoadThreads.ToString(Inv));
            sb.Append(",\"fans\":").Append(Str(p.Fans));
            sb.Append(",\"gpu\":").Append(Str(p.Gpu));
            sb.Append("}");
            sb.Append(",\"modes\":[");
            for (int i = 0; i < rep.Runs.Count; i++)
            {
                if (i > 0) sb.Append(",");
                AppendRun(sb, rep, rep.Runs[i]);
            }
            sb.Append("]");
            sb.Append(",\"verdict\":{");
            sb.Append("\"reliable\":").Append(Bool(verdict.Reliable));
            sb.Append(",\"driftPct\":").Append(Num(verdict.DriftPct));
            sb.Append(",\"bandPct\":").Append(Num(verdict.BandPct));
            sb.Append(",\"desempenho\":").Append(Str(verdict.Desempenho.ToString()));
            sb.Append(",\"silencioso\":").Append(Str(verdict.Silencioso.ToString()));
            sb.Append(",\"lines\":[");
            for (int i = 0; i < verdict.Lines.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Str(verdict.Lines[i]));
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        private static void AppendRun(StringBuilder sb, PerfTestReport rep, PerfModeRun r)
        {
            PerfStats s = r.Stats ?? PerfScoring.Summarize(r);
            sb.Append("{\"mode\":").Append(Str(PerfVerdict.ModeName(r.Mode)));
            sb.Append(",\"label\":").Append(Str(PerfVerdict.RunLabel(rep, r)));
            sb.Append(",\"order\":").Append(r.Order.ToString(Inv));
            sb.Append(",\"cooldownStartTempC\":").Append(Num(r.CooldownStartTempC));
            sb.Append(",\"startTempC\":").Append(Num(r.StartTempC));
            sb.Append(",\"idleCpuLoadPct\":").Append(Num(s.IdleCpuLoad));
            sb.Append(",\"lateCpuTempC\":").Append(Num(s.LateCpuTemp));
            sb.Append(",\"applied\":").Append(Bool(r.Applied));
            sb.Append(",\"switchNote\":").Append(Str(r.SwitchNote));
            sb.Append(",\"aborted\":").Append(Bool(r.Aborted));
            sb.Append(",\"abortReason\":").Append(Str(r.AbortReason));
            sb.Append(",\"cooldownSec\":").Append(Num(r.CooldownSec));
            sb.Append(",\"cooldownReached\":").Append(Bool(r.CooldownReached));
            sb.Append(",\"multiSustained\":").Append(Num(s.MultiSustained));
            sb.Append(",\"burst\":").Append(Num(s.Burst));
            sb.Append(",\"single\":").Append(Num(s.Single));
            sb.Append(",\"avgCpuTemp\":").Append(Num(s.AvgCpuTemp));
            sb.Append(",\"peakCpuTemp\":").Append(Num(s.PeakCpuTemp));
            sb.Append(",\"peakGpuTemp\":").Append(Num(s.PeakGpuTemp));
            sb.Append(",\"avgCpuRpm\":").Append(Num(s.AvgCpuRpm));
            sb.Append(",\"avgGpuRpm\":").Append(Num(s.AvgGpuRpm));
            sb.Append(",\"idleCpuRpm\":").Append(Num(s.IdleCpuRpm));
            sb.Append(",\"idleGpuRpm\":").Append(Num(s.IdleGpuRpm));
            sb.Append(",\"avgClockMulti\":").Append(Num(s.AvgClockMulti));
            sb.Append(",\"avgClockSingle\":").Append(Num(s.AvgClockSingle));
            sb.Append(",\"avgDischargeMw\":").Append(Num(s.AvgDischargeMw));
            sb.Append(",\"samples\":[");
            for (int i = 0; i < r.Samples.Count; i++)
            {
                PerfSample x = r.Samples[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"phase\":").Append(Str(x.Phase.ToString()));
                sb.Append(",\"t\":").Append(Num(x.T));
                sb.Append(",\"cpuTempC\":").Append(Num(x.CpuTempC));
                sb.Append(",\"gpuTempC\":").Append(Num(x.GpuTempC));
                sb.Append(",\"cpuRpm\":").Append(Num(x.CpuRpm));
                sb.Append(",\"gpuRpm\":").Append(Num(x.GpuRpm));
                sb.Append(",\"clockMHz\":").Append(Num(x.ClockMHz));
                sb.Append(",\"dischargeMw\":").Append(Num(x.DischargeMw));
                sb.Append(",\"cpuLoadPct\":").Append(Num(x.CpuLoadPct));
                sb.Append(",\"units\":").Append(x.Units.ToString(Inv));
                sb.Append("}");
            }
            sb.Append("]}");
        }

        public static string Bool(bool b)
        {
            return b ? "true" : "false";
        }

        public static string Num(double? v)
        {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "null";
            return v.Value.ToString("0.###", Inv);
        }

        public static string Num(int? v)
        {
            return v.HasValue ? v.Value.ToString(Inv) : "null";
        }

        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
