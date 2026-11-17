using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    public enum AutoStatus
    {
        Ok,
        Falhou,
        Pulado
    }

    public class AutoCheck
    {
        public string Name;
        public AutoStatus Status;
        public string Detail;

        public AutoCheck(string name, AutoStatus status, string detail)
        {
            Name = name;
            Status = status;
            Detail = detail;
        }

        public string Label
        {
            get { return AutoChecks.StatusText(Status); }
        }
    }

    public class AutoTestReport
    {
        public DateTime StartedUtc;
        public double ElapsedSec;
        public bool Aborted;
        public string Reason;
        public readonly List<AutoCheck> Checks = new List<AutoCheck>();

        public int Count(AutoStatus s)
        {
            int n = 0;
            foreach (AutoCheck c in Checks) { if (c.Status == s) n++; }
            return n;
        }

        public string Summary()
        {
            return Count(AutoStatus.Ok) + " OK, " + Count(AutoStatus.Falhou) + " FALHOU, " + Count(AutoStatus.Pulado) + " PULADO";
        }

        // The text of autoteste.txt and of the final dialog.
        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Autoteste do SenseMonitor — " + StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
            if (Aborted) sb.AppendLine("INTERROMPIDO: " + Reason);
            sb.AppendLine("Resultado: " + Summary() + " (" + ((int)Math.Round(ElapsedSec)).ToString(CultureInfo.InvariantCulture) + " s)");
            sb.AppendLine();
            foreach (AutoCheck c in Checks)
            {
                sb.AppendLine(c.Label.PadRight(7) + " " + c.Name + " — " + c.Detail);
            }
            return sb.ToString();
        }

        // autoteste.json: date, totals and every check.
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"startedUtc\":").Append(PerfReport.Str(StartedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            sb.Append(",\"elapsedSec\":").Append(PerfReport.Num(ElapsedSec));
            sb.Append(",\"aborted\":").Append(PerfReport.Bool(Aborted));
            sb.Append(",\"reason\":").Append(PerfReport.Str(Reason));
            sb.Append(",\"ok\":").Append(Count(AutoStatus.Ok).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"falhou\":").Append(Count(AutoStatus.Falhou).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pulado\":").Append(Count(AutoStatus.Pulado).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"checks\":[");
            for (int i = 0; i < Checks.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":").Append(PerfReport.Str(Checks[i].Name));
                sb.Append(",\"status\":").Append(PerfReport.Str(Checks[i].Label));
                sb.Append(",\"detail\":").Append(PerfReport.Str(Checks[i].Detail)).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    // The 90 s ceiling of the whole self-test.
    public static class AutoBudget
    {
        public const int TotalSec = 90;

        // Seconds a step may take: its own limit, cut to what is left of the ceiling; 0 = no time.
        public static int StepLimitSec(int stepLimitSec, double elapsedSec)
        {
            double left = TotalSec - elapsedSec;
            if (left < 1) return 0;
            return (int)Math.Min(stepLimitSec, Math.Floor(left));
        }
    }

    // Pure decisions of the self-test: each takes what was read and says OK / FALHOU / PULADO
    // with a one-line plain reason. A check is never OK for lack of data.
    public static class AutoChecks
    {
        public const double TempMinC = 10;
        public const double TempMaxC = 110;
        public const int RpmMax = 9999;

        public static string StatusText(AutoStatus s)
        {
            if (s == AutoStatus.Ok) return "OK";
            if (s == AutoStatus.Falhou) return "FALHOU";
            return "PULADO";
        }

        private static AutoCheck Ok(string n, string d) { return new AutoCheck(n, AutoStatus.Ok, d); }
        private static AutoCheck Fail(string n, string d) { return new AutoCheck(n, AutoStatus.Falhou, d); }
        private static AutoCheck Skip(string n, string d) { return new AutoCheck(n, AutoStatus.Pulado, d); }

        private static string N(double v) { return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture); }

        // No time left in the 90 s ceiling.
        public static AutoCheck NoTime(string name)
        {
            return Skip(name, "sem tempo (limite de " + AutoBudget.TotalSec + " s do autoteste)");
        }

        public static AutoCheck Sensors(SensorReading r)
        {
            const string name = "Sensores";
            if (r == null) return Fail(name, "o firmware não devolveu leitura");
            List<string> bad = new List<string>();
            CheckTemp("temperatura da CPU", r.CpuTempC, bad);
            CheckTemp("temperatura da GPU", r.GpuTempC, bad);
            CheckRpm("ventoinha da CPU", r.CpuFanRpm, bad);
            CheckRpm("ventoinha da GPU", r.GpuFanRpm, bad);
            if (bad.Count > 0) return Fail(name, string.Join("; ", bad.ToArray()));
            return Ok(name, "CPU " + N(r.CpuTempC.Value) + " °C, GPU " + N(r.GpuTempC.Value) + " °C, ventoinhas "
                + r.CpuFanRpm.Value.ToString(CultureInfo.InvariantCulture) + " / " + r.GpuFanRpm.Value.ToString(CultureInfo.InvariantCulture) + " RPM");
        }

        private static void CheckTemp(string what, double? v, List<string> bad)
        {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) { bad.Add(what + " sem leitura"); return; }
            if (v.Value < TempMinC || v.Value > TempMaxC) bad.Add(what + " fora do normal (" + N(v.Value) + " °C)");
        }

        private static void CheckRpm(string what, int? v, List<string> bad)
        {
            if (!v.HasValue) { bad.Add(what + " sem leitura"); return; }
            if (v.Value < 0 || v.Value > RpmMax) bad.Add(what + " fora do normal (" + v.Value.ToString(CultureInfo.InvariantCulture) + " RPM)");
        }

        public static AutoCheck ModeRead(PerfMode? m)
        {
            const string name = "Leitura do modo";
            if (!m.HasValue) return Fail(name, "o firmware não informou o modo atual");
            return Ok(name, "modo atual: " + PerfVerdict.ModeName(m.Value));
        }

        public static AutoCheck FanState(bool read, FanMode? cpu, FanMode? gpu)
        {
            const string name = "Estado das ventoinhas";
            if (!read || !cpu.HasValue || !gpu.HasValue) return Fail(name, "o firmware não informou o comportamento das ventoinhas");
            return Ok(name, "CPU " + cpu.Value + ", GPU " + gpu.Value);
        }

        // From config.json read WITHOUT touching it. Missing = the app never ran; corrupt = FALHOU
        // (the file is left as it is for inspection).
        public static AutoCheck FanValidation(ConfigRead status, Config c)
        {
            const string name = "Validação das ventoinhas";
            if (status == ConfigRead.Missing) return Skip(name, "não existe config.json ainda (o app nunca rodou); nada foi criado");
            if (status == ConfigRead.Corrupt || c == null) return Fail(name, "o config.json não pôde ser lido (corrompido?); o arquivo não foi tocado");
            if (c.FanControlValidated && c.ManualFanAllowed)
            {
                return Ok(name, "aprovada" + (string.IsNullOrEmpty(c.FanValidationUtc) ? "" : " em " + c.FanValidationUtc)
                    + "; modos " + (c.ModeCheckPassed ? "confirmados" : "ainda não confirmados"));
            }
            if (!string.IsNullOrEmpty(c.FanValidationUtc))
            {
                return Fail(name, "a última validação (" + c.FanValidationUtc + ") não foi aprovada; rode --validar-ventoinha de novo");
            }
            return Skip(name, "ainda não foi feita (rode --validar-ventoinha); o controle manual segue bloqueado");
        }

        // Without --com-limpeza-ram the cleaner is not run at all.
        public static AutoCheck RamSkipped()
        {
            return Skip("Limpeza de RAM", "só roda com --com-limpeza-ram (esvazia a memória de todos os programas do sistema); não foi executada");
        }

        // The read-only memory measurement the cleaner would rely on.
        public static AutoCheck MemoryRead(MemorySnapshot s)
        {
            const string name = "Leitura de memória";
            if (s == null || s.Total <= 0 || s.Available < 0 || s.InUse < 0 || s.InUse > s.Total)
                return Fail(name, "não foi possível medir a memória em uso");
            return Ok(name, "em uso " + Mb(s.InUse) + " MB de " + Mb(s.Total) + " MB (só leitura)");
        }

        // Only with --com-limpeza-ram: every step must have worked, with a sane measurement.
        public static AutoCheck Ram(RamCleanResult r, bool elevated)
        {
            const string name = "Limpeza de RAM";
            if (r == null) return Fail(name, "a limpeza não retornou resultado");
            if (r.AllFailed)
            {
                if (!elevated) return Skip(name, "precisa de administrador");
                return Fail(name, "o Windows recusou todos os comandos (" + r.Steps + ")");
            }
            if (r.EmptyStatus != 0 || r.FlushStatus != 0 || r.PurgeStatus != 0 || r.UsedFallback)
                return Fail(name, "algum passo da limpeza falhou (" + r.Steps + ")");
            long a = r.Before.InUse;
            long b = r.After.InUse;
            long total = r.Before.Total;
            if (a < 0 || b < 0 || total <= 0 || a > total || b > total)
                return Fail(name, "a limpeza rodou, mas a medição de memória em uso não é confiável");
            long drop = r.InUseDrop;
            return Ok(name, "rodou; em uso " + Mb(a) + " → " + Mb(b) + " MB, liberou " + Mb(drop) + " MB");
        }

        private static string Mb(long bytes)
        {
            return (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        }

        // removed: the hook thread really ended (not just a flag).
        public static AutoCheck Hook(bool installed, bool removed)
        {
            const string name = "Tecla de hardware (hook)";
            if (!installed) return Fail(name, "o Windows não instalou o gancho do teclado");
            if (!removed) return Fail(name, "o gancho foi instalado, mas a thread dele não terminou ao ser desligado");
            return Ok(name, "gancho instalado por 1 s e removido (nenhuma tecla foi registrada)");
        }

        // exitCode: schtasks exit code (0 found, 1 not found), -1 = could not run or timed out.
        public static AutoCheck Autostart(bool exeExists, string installedExe, int exitCode, string xml)
        {
            const string name = "Início automático";
            if (!exeExists) return Skip(name, "o app não está instalado em Arquivos de Programas (rode deploy.ps1)");
            if (exitCode < 0) return Fail(name, "não foi possível consultar o Agendador de Tarefas (tempo esgotado ou sem acesso)");
            if (exitCode != 0) return Fail(name, "a tarefa SenseMonitor não existe no Agendador de Tarefas");
            string cmd = TaskCommand(xml);
            if (cmd == null) return Fail(name, "a tarefa existe, mas não dá para ler o programa que ela abre");
            if (!string.Equals(cmd, installedExe, StringComparison.OrdinalIgnoreCase))
                return Fail(name, "a tarefa abre outro programa (" + cmd + ") em vez da cópia instalada");
            return Ok(name, "a tarefa existe e abre a cópia instalada");
        }

        public static string TaskCommand(string taskXml)
        {
            if (string.IsNullOrEmpty(taskXml)) return null;
            Match m = Regex.Match(taskXml, @"<Command>\s*(.*?)\s*</Command>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            string cmd = m.Groups[1].Value.Replace("&quot;", "\"").Replace("&amp;", "&").Trim().Trim('"').Trim();
            return cmd.Length == 0 ? null : cmd;
        }

        // heldByUs: this process owns the single-instance mutex. secondCopyBlocked: a real second
        // process was started and was turned away (null = it could not be started).
        public static AutoCheck Mutex(bool heldByUs, bool? secondCopyBlocked)
        {
            const string name = "Instância única";
            if (!heldByUs) return Fail(name, "este processo não conseguiu o controle de instância única");
            if (!secondCopyBlocked.HasValue) return Fail(name, "não foi possível abrir uma segunda cópia do app para testar");
            if (!secondCopyBlocked.Value) return Fail(name, "uma segunda cópia do app conseguiu abrir (o controle de instância única não barrou)");
            return Ok(name, "uma segunda cópia do app foi aberta de verdade e barrada");
        }

        public static AutoCheck Battery(bool hasBattery, BatteryInfo b)
        {
            const string name = "Relatório da bateria";
            if (!hasBattery) return Skip(name, "este computador não tem bateria");
            if (b == null) return Fail(name, "o relatório de bateria do Windows não pôde ser lido");
            if (b.DesignMwh <= 0 || b.FullChargeMwh <= 0) return Fail(name, "o relatório veio sem as capacidades da bateria");
            return Ok(name, "saúde " + N(b.HealthPct) + " %" + (b.CycleCount.HasValue ? ", " + b.CycleCount.Value.ToString(CultureInfo.InvariantCulture) + " ciclos" : ""));
        }

        // Any disk that is not reported healthy fails; a disk with no health data proves nothing.
        public static AutoCheck Disks(DiskInfo[] disks)
        {
            const string name = "Saúde dos discos";
            if (disks == null) return Fail(name, "o Windows não respondeu à consulta de discos");
            if (disks.Length == 0) return Fail(name, "nenhum disco encontrado");
            List<string> parts = new List<string>();
            List<string> problems = new List<string>();
            List<string> unknown = new List<string>();
            foreach (DiskInfo d in disks)
            {
                string n = d.Name ?? "Disco";
                parts.Add(n + ": " + (d.HealthText ?? "?"));
                DiskHealthLevel lv = d.Level;
                if (lv == DiskHealthLevel.Warning || lv == DiskHealthLevel.Bad) problems.Add(n + " (" + d.HealthText + ")");
                else if (lv != DiskHealthLevel.Ok) unknown.Add(n);
            }
            string list = string.Join("; ", parts.ToArray());
            if (problems.Count > 0) return Fail(name, "disco com problema: " + string.Join(", ", problems.ToArray()) + " — " + list);
            if (unknown.Count > 0) return Skip(name, "sem informação de saúde de " + string.Join(", ", unknown.ToArray()));
            return Ok(name, disks.Length + (disks.Length == 1 ? " disco (" : " discos (") + list + ")");
        }

        public static AutoCheck Crashes(bool read, int count, int days)
        {
            const string name = "Histórico de quedas";
            if (!read) return Fail(name, "não foi possível ler o log de eventos do sistema");
            return Ok(name, "log lido; " + count + (count == 1 ? " queda" : " quedas") + " nos últimos " + days + " dias");
        }

        // inOwnFolder false = the real folder did not exist, so a temporary one was used.
        public static AutoCheck TempLog(bool wrote, bool readBack, string error, bool inOwnFolder)
        {
            const string name = "Registro de temperatura";
            if (!wrote) return Fail(name, "não foi possível gravar na pasta do registro" + (string.IsNullOrEmpty(error) ? "" : " (" + error + ")"));
            if (!readBack) return Fail(name, "gravou na pasta do registro, mas não conseguiu ler de volta");
            return Ok(name, inOwnFolder
                ? "a pasta do registro aceita gravar e ler (arquivo de teste removido)"
                : "a pasta do registro ainda não existe (será criada no primeiro uso); a gravação e a leitura funcionaram numa pasta temporária, já removida");
        }

        public static AutoCheck Startup(int? count, string error)
        {
            const string name = "Itens de inicialização";
            if (!count.HasValue) return Fail(name, "não foi possível listar" + (string.IsNullOrEmpty(error) ? "" : " (" + error + ")"));
            return Ok(name, count.Value + " itens listados (nada foi ligado nem desligado)");
        }

        public static AutoCheck Cleanup(CleanupPlan plan)
        {
            const string name = "Análise de limpeza";
            if (plan == null) return Fail(name, "a análise não retornou resultado");
            if (plan.Cancelled) return Fail(name, "a análise passou do tempo e foi interrompida");
            int analyzed = 0;
            long bytes = 0;
            foreach (CleanupTarget t in plan.Targets)
            {
                if (t.Available && t.Analyzed) { analyzed++; bytes += t.Bytes; }
            }
            if (analyzed == 0) return Fail(name, "nenhuma pasta pôde ser analisada");
            return Ok(name, analyzed + " pastas medidas, " + Mb(bytes) + " MB que dá para limpar (nada foi apagado)");
        }

        public static AutoCheck Repair(bool dism, bool sfc)
        {
            const string name = "Ferramentas de reparo";
            if (dism && sfc) return Ok(name, "dism.exe e sfc.exe presentes (não foram executados)");
            List<string> missing = new List<string>();
            if (!dism) missing.Add("dism.exe");
            if (!sfc) missing.Add("sfc.exe");
            return Fail(name, "faltando: " + string.Join(", ", missing.ToArray()));
        }

        // disabledByPolicy: registry says System Restore is off (null = unreadable);
        // points: existing restore points (null = could not list).
        public static AutoCheck Restore(bool? disabledByPolicy, int? points)
        {
            const string name = "Ponto de restauração";
            if (disabledByPolicy == true) return Fail(name, "a Proteção do Sistema está desligada no Windows (nenhum ponto será criado)");
            if (points.HasValue && points.Value > 0) return Ok(name, "Proteção do Sistema ligada; " + points.Value + (points.Value == 1 ? " ponto existente" : " pontos existentes") + " (nenhum foi criado)");
            if (points.HasValue) return Skip(name, "nenhum ponto existente; não dá para saber se está ligada sem criar um");
            return Skip(name, "não foi possível consultar a Proteção do Sistema sem criar um ponto");
        }

        // The journal of the performance test: a test that never wrote its FIM line ended in a crash.
        public static AutoCheck PerfJournalState(bool exists, bool readable, string crashNote)
        {
            const string name = "Diário do teste de desempenho";
            if (!exists) return Skip(name, "nenhum teste de desempenho foi rodado ainda (sem diário)");
            if (!readable) return Fail(name, "o diário existe, mas não pôde ser lido");
            if (crashNote != null) return Fail(name, crashNote);
            return Ok(name, "nenhum teste de desempenho terminou numa queda");
        }
    }
}
