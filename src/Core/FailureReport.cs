using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Forja.Core
{
    // What a bugcheck code means, in plain pt-BR, and what usually helps. The wording is hedged on
    // purpose: a code points to a family of causes, it never names the culprit (only the dump does).
    public sealed class BugcheckInfo
    {
        public long Code;
        public string Name;
        public string Meaning;
        public string Advice;
    }

    public static class BugcheckGuide
    {
        private static readonly BugcheckInfo[] Table = new BugcheckInfo[]
        {
            Row(0xA, "IRQL_NOT_LESS_OR_EQUAL",
                "Um driver ou o núcleo do Windows acessou memória num momento em que não podia.",
                "Costuma ser driver (rede, vídeo, USB, antivírus) e, mais raramente, memória RAM com defeito. Atualize os drivers instalados recentemente e rode o Diagnóstico de Memória do Windows."),
            Row(0x1A, "MEMORY_MANAGEMENT",
                "O gerenciador de memória encontrou uma situação inválida.",
                "A memória RAM com defeito é uma causa comum. Rode o Diagnóstico de Memória do Windows ou o MemTest86; se usa perfil de memória overclock (XMP/EXPO), volte ao padrão."),
            Row(0x1E, "KMODE_EXCEPTION_NOT_HANDLED",
                "Um componente do sistema gerou um erro que ninguém tratou.",
                "Em geral é um driver. Atualize os drivers recentes; o nome do driver só aparece no arquivo de despejo."),
            Row(0x3B, "SYSTEM_SERVICE_EXCEPTION",
                "Uma chamada ao sistema falhou dentro de um driver ou do núcleo.",
                "Costuma envolver o driver de vídeo ou outro driver de núcleo. Reinstale o driver de vídeo mais recente."),
            Row(0x50, "PAGE_FAULT_IN_NONPAGED_AREA",
                "O sistema tentou ler uma área de memória que não existe.",
                "Pode vir de RAM defeituosa, de erro no disco ou de um driver. Teste a memória e verifique o disco."),
            Row(0x7A, "KERNEL_DATA_INPAGE_ERROR",
                "O Windows não conseguiu ler dados do disco para a memória.",
                "Aponta para o disco/SSD, o cabo ou o controlador. Veja os eventos de disco abaixo e o estado SMART do SSD."),
            Row(0x7B, "INACCESSIBLE_BOOT_DEVICE",
                "O Windows perdeu o acesso ao disco de inicialização.",
                "Verifique o SSD, o modo de armazenamento na BIOS e atualizações recentes de driver de armazenamento."),
            Row(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
                "Uma tarefa interna do sistema gerou um erro que ninguém tratou.",
                "Normalmente é um driver; o nome dele só aparece no arquivo de despejo. Atualize os drivers de vídeo, rede e chipset."),
            Row(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP",
                "O processador encontrou uma condição que o sistema não esperava.",
                "Pode ser memória RAM, superaquecimento ou overclock. Volte a BIOS e a memória aos valores padrão e teste a RAM."),
            Row(0x9F, "DRIVER_POWER_STATE_FAILURE",
                "Um driver não terminou a tempo uma troca de estado de energia (suspender, retomar, desligar).",
                "Acontece em suspensão e retomada. Atualize drivers de chipset, vídeo e Wi-Fi; teste desligar a inicialização rápida."),
            Row(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY",
                "Um driver tentou gravar numa área de memória protegida.",
                "Costuma ser driver. Atualize ou remova o driver instalado por último."),
            Row(0xC2, "BAD_POOL_CALLER",
                "Um driver pediu ou devolveu memória do sistema de forma incorreta.",
                "Costuma ser driver. Atualize ou remova o driver instalado por último."),
            Row(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
                "Um driver acessou memória num momento inválido.",
                "O driver é o suspeito principal (rede, vídeo, armazenamento). Atualize os drivers."),
            Row(0xEA, "THREAD_STUCK_IN_DEVICE_DRIVER",
                "Um driver, em geral o de vídeo, travou o sistema.",
                "Atualize o driver de vídeo; confira a temperatura da GPU."),
            Row(0xEF, "CRITICAL_PROCESS_DIED",
                "Um processo essencial do Windows (como csrss, wininit ou services) encerrou.",
                "Causas comuns: erro de leitura no disco/SSD, memória com defeito ou arquivos de sistema corrompidos. Verifique o SMART do SSD, rode o Diagnóstico de Memória e o comando sfc /scannow."),
            Row(0xF4, "CRITICAL_OBJECT_TERMINATION",
                "Um processo ou thread essencial do Windows foi encerrado.",
                "Costuma indicar falha de disco ou de memória. Verifique o SSD e teste a RAM."),
            Row(0x101, "CLOCK_WATCHDOG_TIMEOUT",
                "Um núcleo do processador parou de responder.",
                "Pode ser BIOS desatualizada, overclock/undervolt ou defeito do processador. Atualize a BIOS e volte aos valores padrão."),
            Row(0x109, "CRITICAL_STRUCTURE_CORRUPTION",
                "Dados críticos do sistema foram alterados.",
                "Pode vir de driver com defeito ou de memória. Teste a RAM e atualize drivers."),
            Row(0x116, "VIDEO_TDR_FAILURE",
                "O driver de vídeo parou de responder e não conseguiu se recuperar.",
                "Atualize o driver de vídeo, confira a temperatura e a energia da GPU."),
            Row(0x117, "VIDEO_TDR_TIMEOUT_DETECTED",
                "O driver de vídeo demorou demais para responder.",
                "Atualize o driver de vídeo, confira a temperatura e a energia da GPU."),
            Row(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR",
                "O agendador de vídeo encontrou um erro interno.",
                "Atualize o driver de vídeo."),
            Row(0x124, "WHEA_UNCORRECTABLE_ERROR",
                "O hardware reportou um erro que não conseguiu corrigir (processador, memória, disco, PCIe ou energia).",
                "Veja os eventos WHEA abaixo: eles dizem qual componente. Confira temperatura, overclock/undervolt e atualize a BIOS."),
            Row(0x133, "DPC_WATCHDOG_VIOLATION",
                "Um driver demorou demais numa rotina de alta prioridade.",
                "Costuma ser driver de SSD/NVMe, rede ou vídeo. Atualize o firmware do SSD e os drivers."),
            Row(0x139, "KERNEL_SECURITY_CHECK_FAILURE",
                "O sistema detectou dados corrompidos em memória.",
                "Pode vir de driver ou de RAM. Teste a memória e atualize drivers."),
            Row(0x13A, "KERNEL_MODE_HEAP_CORRUPTION",
                "A memória usada pelo sistema foi corrompida.",
                "Pode vir de driver ou de RAM. Teste a memória e atualize drivers."),
            Row(0x154, "UNEXPECTED_STORE_EXCEPTION",
                "O componente de armazenamento do sistema encontrou um erro inesperado.",
                "Aponta para o SSD ou para o driver de armazenamento. Verifique o SMART e atualize o firmware do SSD."),
            Row(0x1C7, "STORE_DATA_STRUCTURE_CORRUPTION",
                "Dados de armazenamento mantidos em memória foram corrompidos.",
                "Pode estar ligado a RAM ou ao SSD. Rode o Diagnóstico de Memória e verifique o SMART do SSD."),
            Row(0xC000021A, "STATUS_SYSTEM_PROCESS_TERMINATED",
                "Um processo essencial do Windows foi encerrado.",
                "Costuma indicar arquivos de sistema danificados. Rode sfc /scannow e DISM /RestoreHealth.")
        };

        private static BugcheckInfo Row(long code, string name, string meaning, string advice)
        {
            BugcheckInfo i = new BugcheckInfo();
            i.Code = code;
            i.Name = name;
            i.Meaning = meaning;
            i.Advice = advice;
            return i;
        }

        // null when the code is not in the table. A code carrying the 0x10000000 flag
        // (0x1000007E) is looked up without it.
        public static BugcheckInfo Find(long code)
        {
            BugcheckInfo i = Exact(code);
            if (i == null && code > 0 && (code & 0xF0000000L) == 0x10000000L) i = Exact(code & 0x0FFFFFFFL);
            return i;
        }

        private static BugcheckInfo Exact(long code)
        {
            for (int k = 0; k < Table.Length; k++)
            {
                if (Table[k].Code == code) return Table[k];
            }
            return null;
        }

        public static int Count { get { return Table.Length; } }
    }

    public enum ReportCategory
    {
        Whea,
        Storage,
        Gpu,
        Power
    }

    // One System-log event near the failure.
    public sealed class ReportEvent
    {
        public DateTime When;     // local
        public string Provider;
        public int Id;
        public int Level;         // 1 critical, 2 error, 3 warning, 4 information
        public string Message;    // first line, already shortened
    }

    // Which System-log events belong in the report, and under which heading.
    public static class ReportEventFilter
    {
        public static readonly string[] Providers = new string[]
        {
            "Microsoft-Windows-WHEA-Logger", "disk", "stornvme", "storahci", "iaStorAVC", "Microsoft-Windows-Ntfs", "Ntfs",
            "partmgr", "Display", "nvlddmkm", "amdkmdag", "atikmdag", "Microsoft-Windows-Kernel-Power"
        };

        // null = not worth a line. Information-level noise is dropped everywhere except WHEA.
        public static ReportCategory? Classify(string provider, int id, int level)
        {
            if (string.IsNullOrEmpty(provider)) return null;
            string p = provider.ToLowerInvariant();
            if (p == "microsoft-windows-whea-logger") return ReportCategory.Whea;
            if (level < 1 || level > 3) return null;
            if (p == "disk" || p == "stornvme" || p == "storahci" || p == "iastoravc" || p == "partmgr"
                || p == "microsoft-windows-ntfs" || p == "ntfs") return ReportCategory.Storage;
            if (p == "nvlddmkm" || p == "amdkmdag" || p == "atikmdag" || (p == "display" && id == 4101)) return ReportCategory.Gpu;
            if (p == "microsoft-windows-kernel-power") return ReportCategory.Power;
            return null;
        }
    }

    public sealed class ReportSection
    {
        public string Heading;
        public readonly List<string> Lines = new List<string>();
        // The source could not be read: the section says so instead of "nothing found".
        public bool Unreadable;
    }

    public sealed class FailureReportInput
    {
        public CrashEntry Entry;
        public List<ReportEvent> Events;      // System log events inside the window (any category)
        public bool EventsRead;
        // Matching events the reader left out per category (it keeps the most recent ones), and whether it
        // stopped reading at its overall limit: the sections say so instead of looking complete.
        public Dictionary<ReportCategory, int> EventsDropped;
        public bool EventsLimitHit;
        public List<JournalEntry> Journal;    // app journal lines (any time; the builder cuts the window)
        public bool JournalRead;
        public List<TempSample> Temps;        // temperature log samples (any time)
        public bool TempsRead;
    }

    // The report of one unexpected restart: what happened in the minutes before. A plain model that
    // any UI can show; ToText is what "Copiar relatório" puts on the clipboard.
    public sealed class FailureReport
    {
        public const int WindowBeforeMinutes = 10;
        public const int WindowAfterSeconds = 60;
        public const int TempMinutes = 5;
        public const int MaxEventsPerSection = 40;

        public string Title;
        public string Headline;
        public DateTime When;     // best estimate of when the PC stopped
        public readonly List<ReportSection> Sections = new List<ReportSection>();

        public static DateTime WindowStart(CrashEntry e)
        {
            return e.EffectiveTime.AddMinutes(-WindowBeforeMinutes);
        }

        public static DateTime WindowEnd(CrashEntry e)
        {
            return e.EffectiveTime.AddSeconds(WindowAfterSeconds);
        }

        // scrub removes anything personal (user name, profile path); null = text as is.
        public string ToText(Func<string, string> scrub)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Title);
            sb.AppendLine(Headline);
            foreach (ReportSection s in Sections)
            {
                sb.AppendLine();
                sb.AppendLine(s.Heading.ToUpperInvariant());
                foreach (string l in s.Lines) sb.AppendLine("  " + l);
            }
            string text = sb.ToString();
            return scrub != null ? scrub(text) : text;
        }

        public static FailureReport Build(FailureReportInput input)
        {
            CrashEntry e = input.Entry;
            FailureReport r = new FailureReport();
            r.When = e.EffectiveTime;
            r.Title = "Relatório de reinício inesperado — " + FailureText.DayAndTime(e.EffectiveTime) + " (" + e.EffectiveTime.ToString("yyyy", CultureInfo.InvariantCulture) + ")";
            r.Headline = FailureText.KindLong(e.Kind) + ".";
            r.Sections.Add(Summary(e));

            List<ReportEvent> events = input.Events ?? new List<ReportEvent>();
            r.Sections.Add(EventSection("Erros de hardware (WHEA), nos 10 min antes", ReportCategory.Whea, events, input, e));
            r.Sections.Add(EventSection("Disco e armazenamento, nos 10 min antes", ReportCategory.Storage, events, input, e));
            r.Sections.Add(EventSection("Placa de vídeo (reinício do driver), nos 10 min antes", ReportCategory.Gpu, events, input, e));
            r.Sections.Add(EventSection("Energia (Kernel-Power), nos 10 min antes", ReportCategory.Power, events, input, e));
            r.Sections.Add(JournalSection(e, input.Journal, input.JournalRead));
            r.Sections.Add(TempSection(e, input.Temps, input.TempsRead));

            ReportSection note = new ReportSection();
            note.Heading = "Observação";
            note.Lines.Add("O app não lê o arquivo de despejo (Minidump). Para saber qual driver falhou, abra-o com um depurador (WinDbg).");
            note.Lines.Add("Linhas ausentes não provam que nada aconteceu: o Windows nem sempre registra o que precede uma queda brusca.");
            r.Sections.Add(note);
            return r;
        }

        private static ReportSection Summary(CrashEntry e)
        {
            ReportSection s = new ReportSection();
            s.Heading = "Resumo";
            s.Lines.Add("Parou por volta de: " + e.EffectiveTime.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                + (e.DownAt.HasValue ? " (último instante em que o Windows estava ativo)" : " (hora do registro; a hora exata não consta)"));
            if (e.DownAt.HasValue) s.Lines.Add("Windows reiniciado às: " + e.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (e.Code.HasValue)
            {
                BugcheckInfo i = BugcheckGuide.Find(e.Code.Value);
                s.Lines.Add("Código de erro do Windows: " + CrashParse.FormatCode(e.Code.Value) + (i != null ? " (" + i.Name + ")" : ""));
                if (i != null)
                {
                    s.Lines.Add("O que significa: " + i.Meaning);
                    s.Lines.Add("O que costuma ajudar: " + i.Advice);
                }
                else
                {
                    s.Lines.Add("Esse código ainda não tem descrição neste app; procure por ele na documentação da Microsoft (\"bug check code reference\").");
                }
                if (!string.IsNullOrEmpty(e.DyingProcess)) s.Lines.Add("Processo que encerrou: " + e.DyingProcess);
                else if (e.Code.Value == 0xEF) s.Lines.Add("Processo que encerrou: não consta no registro (só o arquivo de despejo identifica).");
            }
            else
            {
                s.Lines.Add("Código de erro do Windows: nenhum registrado.");
                if (e.Kind == FailureKind.HangPowerButton) s.Lines.Add("O botão de energia foi pressionado depois que o Windows parou de responder: o PC provavelmente travou e foi desligado à força.");
                else if (e.Kind == FailureKind.PowerLoss) s.Lines.Add("O botão de energia não foi usado: o PC perdeu energia ou foi desligado pelo hardware (proteção térmica, bateria, fonte).");
                else s.Lines.Add("O registro não permite dizer se foi travamento ou queda de energia.");
            }
            if (e.SleepInProgress != 0) s.Lines.Add("O PC estava entrando em suspensão ou saindo dela quando parou.");
            if (e.LongPowerButtonPress) s.Lines.Add("O Windows detectou o botão de energia mantido pressionado.");
            if (e.WheaBootErrorCount > 0) s.Lines.Add("Erros de hardware (WHEA) registrados na inicialização seguinte: " + e.WheaBootErrorCount + ".");
            s.Lines.Add(e.HasDump
                ? "Despejo de memória (Minidump): sim" + (string.IsNullOrEmpty(e.DumpPath) ? "" : " — " + System.IO.Path.GetFileName(e.DumpPath))
                : "Despejo de memória (Minidump): não encontrado");
            return s;
        }

        private static ReportSection EventSection(string heading, ReportCategory cat, List<ReportEvent> events, FailureReportInput input, CrashEntry e)
        {
            bool read = input.EventsRead;
            ReportSection s = new ReportSection();
            s.Heading = heading;
            if (!read)
            {
                s.Unreadable = true;
                s.Lines.Add("Não foi possível ler o registro de eventos do Windows. Isso NÃO significa que não houve erros.");
                return s;
            }
            DateTime from = WindowStart(e);
            DateTime to = WindowEnd(e);
            List<ReportEvent> mine = new List<ReportEvent>();
            foreach (ReportEvent ev in events)
            {
                if (ev == null || ev.When < from || ev.When > to) continue;
                ReportCategory? c = ReportEventFilter.Classify(ev.Provider, ev.Id, ev.Level);
                if (c.HasValue && c.Value == cat) mine.Add(ev);
            }
            mine.Sort(delegate(ReportEvent a, ReportEvent b) { return a.When.CompareTo(b.When); });
            if (!e.DownAt.HasValue)
            {
                s.Lines.Add("A janela de 10 minutos usa a hora do reinício, não a da queda (a hora exata da queda não consta no registro).");
            }
            int dropped = 0;
            if (input.EventsDropped != null && input.EventsDropped.TryGetValue(cat, out dropped) && dropped > 0)
            {
                s.Lines.Add("Havia mais eventos nessa janela: aparecem só os " + mine.Count + " mais recentes (" + dropped + " mais antigos não são mostrados).");
            }
            if (input.EventsLimitHit)
            {
                s.Lines.Add("O registro tinha eventos demais nessa janela; a leitura parou no limite, então a lista pode estar incompleta.");
            }
            if (mine.Count == 0)
            {
                s.Lines.Add("Nenhum evento encontrado nesse intervalo.");
                return s;
            }
            foreach (ReportEvent ev in mine)
            {
                string line = ev.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + ProviderShort(ev.Provider) + " #" + ev.Id + " (" + LevelWord(ev.Level) + ")";
                if (!string.IsNullOrEmpty(ev.Message)) line += ": " + ev.Message;
                s.Lines.Add(line);
            }
            return s;
        }

        private static string ProviderShort(string p)
        {
            if (p == null) return "";
            return p.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase) ? p.Substring("Microsoft-Windows-".Length) : p;
        }

        public static string LevelWord(int level)
        {
            switch (level)
            {
                case 1: return "crítico";
                case 2: return "erro";
                case 3: return "aviso";
                default: return "informação";
            }
        }

        private static bool IsWrite(JournalEntry j)
        {
            return j.Kind == JournalKind.WriteBefore || j.Kind == JournalKind.WriteAfter;
        }

        private static ReportSection JournalSection(CrashEntry e, List<JournalEntry> journal, bool read)
        {
            ReportSection s = new ReportSection();
            s.Heading = "Diário do app (o que o app enviou ao firmware), nos 10 min antes";
            if (!read)
            {
                s.Unreadable = true;
                s.Lines.Add("O diário do app não foi encontrado ou não pôde ser lido (o app só passou a registrar a partir da versão com diário).");
                return s;
            }
            DateTime from = WindowStart(e);
            DateTime to = e.EffectiveTime.AddSeconds(5);
            List<JournalEntry> mine = new List<JournalEntry>();
            JournalEntry lastWrite = null;
            DateTime? oldest = null;
            if (journal != null)
            {
                foreach (JournalEntry j in journal)
                {
                    if (j == null) continue;
                    if (!oldest.HasValue || j.Time < oldest.Value) oldest = j.Time;
                    if (j.Time < from || j.Time > to) continue;
                    mine.Add(j);
                    if (IsWrite(j) && (lastWrite == null || j.Time > lastWrite.Time)) lastWrite = j;
                }
            }
            mine.Sort(delegate(JournalEntry a, JournalEntry b) { return a.Time.CompareTo(b.Time); });
            // "The app sent nothing" can only be said when the journal reaches back past the start of the
            // window: it keeps two small files, so an old restart (or an app that was not installed yet) has no coverage.
            bool covered = oldest.HasValue && oldest.Value <= from;
            if (!covered)
            {
                s.Unreadable = true;
                s.Lines.Add(oldest.HasValue
                    ? "O diário do app não cobre esse horário (o registro mais antigo é de " + FailureText.DayAndTime(oldest.Value) + "); não dá para dizer o que o app enviou ao firmware nesse intervalo."
                    : "O diário do app está vazio: não dá para dizer o que o app enviou ao firmware nesse intervalo.");
                if (mine.Count == 0) return s;
            }
            else if (mine.Count == 0)
            {
                s.Lines.Add("Nenhuma linha do diário nesse intervalo: o app não enviou nada ao firmware nesses minutos.");
                return s;
            }
            if (lastWrite != null)
            {
                s.Lines.Add("Última escrita no firmware: " + lastWrite.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + ", " + Gap(e.EffectiveTime - lastWrite.Time) + " antes da parada.");
            }
            else
            {
                s.Lines.Add("Nenhuma escrita no firmware nesse intervalo (só leituras e mudanças de estado).");
            }
            int shown = 0;
            foreach (JournalEntry j in mine)
            {
                if (shown >= 40)
                {
                    s.Lines.Add("… e mais " + (mine.Count - shown) + " linhas.");
                    break;
                }
                s.Lines.Add(j.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + j.Source + "  " + j.Kind + "  " + j.Text);
                shown++;
            }
            return s;
        }

        private static string Gap(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " min " + t.Seconds + " s";
            return (int)t.TotalSeconds + " s";
        }

        private static ReportSection TempSection(CrashEntry e, List<TempSample> temps, bool read)
        {
            ReportSection s = new ReportSection();
            s.Heading = "Temperatura e ventoinha, nos " + TempMinutes + " min antes";
            if (!read)
            {
                s.Unreadable = true;
                s.Lines.Add("O registro de temperatura não pôde ser lido.");
                return s;
            }
            DateTime from = e.EffectiveTime.AddMinutes(-TempMinutes);
            List<TempSample> mine = new List<TempSample>();
            if (temps != null)
            {
                foreach (TempSample t in temps)
                {
                    if (t != null && t.Time >= from && t.Time <= e.EffectiveTime.AddSeconds(60)) mine.Add(t);
                }
            }
            mine.Sort(delegate(TempSample a, TempSample b) { return a.Time.CompareTo(b.Time); });
            if (mine.Count == 0)
            {
                s.Lines.Add("Sem registro de temperatura nesse intervalo (o app grava uma linha por minuto enquanto está aberto).");
                return s;
            }
            foreach (TempSample t in mine)
            {
                s.Lines.Add(t.Time.ToString("HH:mm", CultureInfo.InvariantCulture) + "  CPU " + Temp(t.CpuC) + "  GPU " + Temp(t.GpuC)
                    + "  ventoinhas " + Rpm(t.CpuRpm) + " / " + Rpm(t.GpuRpm) + " rpm");
            }
            return s;
        }

        private static string Temp(double? v)
        {
            return v.HasValue && v.Value > 0 ? Math.Round(v.Value).ToString(CultureInfo.InvariantCulture) + " °C" : "—";
        }

        private static string Rpm(int? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "—";
        }
    }
}
