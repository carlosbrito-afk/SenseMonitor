using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class FailureReportTests
    {
        private static readonly DateTime Down = new DateTime(2026, 11, 17, 1, 35, 24);

        private static CrashEntry Bsod()
        {
            CrashEntry e = new CrashEntry();
            e.When = new DateTime(2026, 11, 17, 1, 38, 27);
            e.DownAt = Down;
            e.Code = 0xEF;
            e.Kind = FailureKind.BlueScreen;
            e.HasDump = true;
            e.DumpPath = "C:\\WINDOWS\\Minidump\\111726-18578-01.dmp";
            return e;
        }

        private static ReportEvent Ev(DateTime t, string provider, int id, int level, string msg)
        {
            ReportEvent e = new ReportEvent();
            e.When = t;
            e.Provider = provider;
            e.Id = id;
            e.Level = level;
            e.Message = msg;
            return e;
        }

        private static JournalEntry J(DateTime t, string kind, string text)
        {
            JournalEntry j = new JournalEntry();
            j.Time = t;
            j.Source = "app";
            j.Kind = kind;
            j.Text = text;
            return j;
        }

        private static ReportSection Find(FailureReport r, string headingStart)
        {
            foreach (ReportSection s in r.Sections)
            {
                if (s.Heading.StartsWith(headingStart, StringComparison.Ordinal)) return s;
            }
            throw new AssertionException("no section " + headingStart);
        }

        private static string All(ReportSection s)
        {
            return string.Join("\n", s.Lines.ToArray());
        }

        private static FailureReportInput Input()
        {
            FailureReportInput i = new FailureReportInput();
            i.Entry = Bsod();
            i.EventsRead = true;
            i.JournalRead = true;
            i.TempsRead = true;
            i.Events = new List<ReportEvent>();
            i.Journal = new List<JournalEntry>();
            i.Temps = new List<TempSample>();
            return i;
        }

        [Test]
        public static void InjectedWheaEvent_AppearsInItsSection_AndNothingElse()
        {
            FailureReportInput i = Input();
            i.Events.Add(Ev(Down.AddMinutes(-2), "Microsoft-Windows-WHEA-Logger", 18, 2, "Um erro de hardware fatal foi relatado. Origem: Machine Check Exception"));
            FailureReport r = FailureReport.Build(i);
            string whea = All(Find(r, "Erros de hardware (WHEA)"));
            Assert.True(whea.Contains("01:33:24") && whea.Contains("WHEA-Logger #18 (erro)") && whea.Contains("Machine Check"), whea);
            Assert.True(All(Find(r, "Disco")).Contains("Nenhum evento encontrado"), "disk empty");
            Assert.True(All(Find(r, "Placa de vídeo")).Contains("Nenhum evento encontrado"), "gpu empty");
        }

        [Test]
        public static void EventsAreSortedIntoTheirSections_AndOutsideTheWindowIsDropped()
        {
            FailureReportInput i = Input();
            i.Events.Add(Ev(Down.AddMinutes(-1), "stornvme", 129, 3, "Foi emitido um reset para o dispositivo"));
            i.Events.Add(Ev(Down.AddMinutes(-9), "disk", 7, 2, "O dispositivo tem um bloco inválido"));
            i.Events.Add(Ev(Down.AddSeconds(-30), "Display", 4101, 3, "O driver de vídeo parou de responder e se recuperou"));
            i.Events.Add(Ev(Down.AddSeconds(-20), "nvlddmkm", 13, 2, "Graphics SM Warp Exception"));
            i.Events.Add(Ev(Down.AddSeconds(-10), "Microsoft-Windows-Kernel-Power", 41, 1, "reiniciado sem desligamento correto"));
            i.Events.Add(Ev(Down.AddMinutes(-11), "disk", 7, 2, "antes da janela"));
            i.Events.Add(Ev(Down.AddMinutes(5), "disk", 7, 2, "depois da janela"));
            i.Events.Add(Ev(Down.AddMinutes(-3), "Microsoft-Windows-Ntfs", 98, 4, "volume íntegro"));
            i.Events.Add(Ev(Down.AddMinutes(-3), "Display", 4102, 3, "outro evento Display"));
            FailureReport r = FailureReport.Build(i);
            string disk = All(Find(r, "Disco"));
            Assert.True(disk.Contains("stornvme #129 (aviso)") && disk.Contains("disk #7 (erro)"), disk);
            Assert.True(!disk.Contains("antes da janela") && !disk.Contains("depois da janela") && !disk.Contains("volume íntegro"), disk);
            Assert.True(disk.IndexOf("disk #7", StringComparison.Ordinal) < disk.IndexOf("stornvme #129", StringComparison.Ordinal), "oldest first");
            string gpu = All(Find(r, "Placa de vídeo"));
            Assert.True(gpu.Contains("Display #4101") && gpu.Contains("nvlddmkm #13") && !gpu.Contains("4102"), gpu);
            Assert.True(All(Find(r, "Energia")).Contains("Kernel-Power #41 (crítico)"), "power");
        }

        [Test]
        public static void UnreadableEventLog_SaysSo_NeverNothingFound()
        {
            FailureReportInput i = Input();
            i.EventsRead = false;
            FailureReport r = FailureReport.Build(i);
            ReportSection s = Find(r, "Erros de hardware (WHEA)");
            Assert.True(s.Unreadable, "flag");
            Assert.True(All(s).Contains("NÃO significa"), All(s));
            Assert.True(!All(s).Contains("Nenhum evento encontrado"), "no false all-clear");
            Assert.True(Find(r, "Disco").Unreadable && Find(r, "Placa de vídeo").Unreadable && Find(r, "Energia").Unreadable, "all event sections");
        }

        [Test]
        public static void Summary_HasCodeMeaningAdviceDumpAndTimes()
        {
            FailureReport r = FailureReport.Build(Input());
            string sum = All(Find(r, "Resumo"));
            Assert.True(sum.Contains("17/11/2026 01:35:24"), sum);
            Assert.True(sum.Contains("Windows reiniciado às: 01:38:27"), sum);
            Assert.True(sum.Contains("0xEF (CRITICAL_PROCESS_DIED)"), sum);
            Assert.True(sum.Contains("O que significa:") && sum.Contains("processo essencial"), sum);
            Assert.True(sum.Contains("O que costuma ajudar:"), sum);
            Assert.True(sum.Contains("Processo que encerrou: não consta"), sum);
            Assert.True(sum.Contains("Minidump): sim — 111726-18578-01.dmp"), sum);
            Assert.True(!sum.Contains("WINDOWS\\Minidump"), "file name only, never the folder");
        }

        [Test]
        public static void Summary_DyingProcessWhenTheTextHadOne()
        {
            FailureReportInput i = Input();
            i.Entry.DyingProcess = "csrss.exe";
            Assert.True(All(Find(FailureReport.Build(i), "Resumo")).Contains("Processo que encerrou: csrss.exe"), "named");
        }

        [Test]
        public static void Summary_NoCode_ExplainsByKind()
        {
            FailureReportInput i = Input();
            i.Entry.Code = null;
            i.Entry.Kind = FailureKind.HangPowerButton;
            i.Entry.HasDump = false;
            i.Entry.SleepInProgress = 1;
            i.Entry.WheaBootErrorCount = 2;
            string sum = All(Find(FailureReport.Build(i), "Resumo"));
            Assert.True(sum.Contains("nenhum registrado") && sum.Contains("travou e foi desligado à força"), sum);
            Assert.True(sum.Contains("suspensão") && sum.Contains("WHEA) registrados na inicialização seguinte: 2"), sum);
            Assert.True(sum.Contains("Minidump): não encontrado"), sum);
            i.Entry.Kind = FailureKind.PowerLoss;
            Assert.True(All(Find(FailureReport.Build(i), "Resumo")).Contains("perdeu energia"), "power loss wording");
            i.Entry.Kind = FailureKind.Unknown;
            Assert.True(All(Find(FailureReport.Build(i), "Resumo")).Contains("não permite dizer"), "unknown wording");
        }

        [Test]
        public static void Journal_LastWriteBeforeTheStop_AndTheLinesInTheWindow()
        {
            FailureReportInput i = Input();
            i.Journal.Add(J(Down.AddMinutes(-30), JournalKind.WriteBefore, "fora da janela"));
            i.Journal.Add(J(Down.AddMinutes(-4).AddSeconds(-14), JournalKind.WriteBefore, "modo Desempenho"));
            i.Journal.Add(J(Down.AddMinutes(-4).AddSeconds(-13), JournalKind.WriteAfter, "modo Desempenho confirmado"));
            i.Journal.Add(J(Down.AddMinutes(-1), JournalKind.State, "modo=Desempenho; ventoinha=Automático"));
            FailureReport r = FailureReport.Build(i);
            string j = All(Find(r, "Diário do app"));
            Assert.True(j.Contains("Última escrita no firmware: 01:31:11, 4 min 13 s antes da parada"), j);
            Assert.True(j.Contains("ESCRITA-ANTES  modo Desempenho") && j.Contains("ESTADO"), j);
            Assert.True(!j.Contains("fora da janela"), j);
        }

        [Test]
        public static void Journal_EmptyWindow_SaysTheAppSentNothing_OnlyWhenTheJournalCoversIt()
        {
            FailureReportInput i = Input();
            i.Journal.Add(J(Down.AddDays(-2), JournalKind.Start, "app iniciado"));   // the journal reaches back past the window
            Assert.True(All(Find(FailureReport.Build(i), "Diário do app")).Contains("o app não enviou nada ao firmware"), "covered and empty");
            Assert.True(!Find(FailureReport.Build(i), "Diário do app").Unreadable, "covered");
            i.Journal.Add(J(Down.AddMinutes(-1), JournalKind.State, "modo=x"));
            Assert.True(All(Find(FailureReport.Build(i), "Diário do app")).Contains("Nenhuma escrita no firmware nesse intervalo"), "reads only");
            i.JournalRead = false;
            ReportSection s = Find(FailureReport.Build(i), "Diário do app");
            Assert.True(s.Unreadable && All(s).Contains("não foi encontrado"), All(s));
        }

        [Test]
        public static void Journal_NotCoveringTheWindow_NeverClaimsNothingWasSent()
        {
            FailureReportInput i = Input();
            // journal starts 3 minutes before the stop: the first 7 minutes of the window are not covered
            i.Journal.Add(J(Down.AddMinutes(-3), JournalKind.Start, "app iniciado"));
            ReportSection s = Find(FailureReport.Build(i), "Diário do app");
            string t = All(s);
            Assert.True(s.Unreadable, "flagged");
            Assert.True(t.Contains("O diário do app não cobre esse horário (o registro mais antigo é de 17/11 às 01:32)"), t);
            Assert.True(!t.Contains("o app não enviou nada"), t);
            Assert.True(t.Contains("ESTADO") || t.Contains("INICIO"), "lines inside the window are still listed: " + t);
            // journal only newer than the window: nothing to list, still no claim
            i.Journal.Clear();
            i.Journal.Add(J(Down.AddHours(1), JournalKind.Start, "app iniciado"));
            ReportSection later = Find(FailureReport.Build(i), "Diário do app");
            Assert.True(All(later).Contains("não cobre esse horário") && !All(later).Contains("o app não enviou nada"), All(later));
            // empty journal
            i.Journal.Clear();
            Assert.True(All(Find(FailureReport.Build(i), "Diário do app")).Contains("está vazio"), "empty file");
        }

        [Test]
        public static void ListRow_NeverShowsTheErrorCode()
        {
            CrashEntry e = Bsod();
            Assert.True(!FailureText.RowDetail(e).Contains("0x") && !FailureText.RowDetail(e).Contains("EF"), FailureText.RowDetail(e));
            foreach (FailureKind k in new FailureKind[] { FailureKind.Unknown, FailureKind.BlueScreen, FailureKind.HangPowerButton, FailureKind.PowerLoss })
            {
                e.Kind = k;
                Assert.True(FailureText.RowDetail(e).Length > 10 && !FailureText.RowDetail(e).Contains("0x"), k.ToString());
            }
        }

        [Test]
        public static void TooManyEvents_TheSectionSaysSo_AndKeepsTheNewest()
        {
            FailureReportInput i = Input();
            for (int k = 0; k < 5; k++) i.Events.Add(Ev(Down.AddSeconds(-30 - k), "stornvme", 129, 3, "reset " + k));
            i.EventsDropped = new Dictionary<ReportCategory, int>();
            i.EventsDropped[ReportCategory.Storage] = 360;
            string disk = All(Find(FailureReport.Build(i), "Disco"));
            Assert.True(disk.Contains("aparecem só os 5 mais recentes (360 mais antigos não são mostrados)"), disk);
            Assert.True(!All(Find(FailureReport.Build(i), "Placa de vídeo")).Contains("mais antigos"), "other sections unaffected");
            i.EventsLimitHit = true;
            Assert.True(All(Find(FailureReport.Build(i), "Energia")).Contains("lista pode estar incompleta"), "global limit");
        }

        [Test]
        public static void WindowFromTheRestartTime_IsSaid()
        {
            FailureReportInput i = Input();
            Assert.True(!All(Find(FailureReport.Build(i), "Disco")).Contains("hora do reinício"), "stop time known");
            i.Entry.DownAt = null;
            Assert.True(All(Find(FailureReport.Build(i), "Disco")).Contains("usa a hora do reinício, não a da queda"), "approximate window");
        }

        [Test]
        public static void Temperatures_FiveMinutesBefore_OrExplicitNone()
        {
            FailureReportInput i = Input();
            Assert.True(All(Find(FailureReport.Build(i), "Temperatura")).Contains("Sem registro"), "none");
            TempSample a = new TempSample();
            a.Time = new DateTime(2026, 11, 17, 1, 31, 0);
            a.CpuC = 78.4;
            a.GpuC = null;
            a.CpuRpm = 4100;
            TempSample old = new TempSample();
            old.Time = new DateTime(2026, 11, 17, 1, 20, 0);
            old.CpuC = 50;
            i.Temps.Add(old);
            i.Temps.Add(a);
            string t = All(Find(FailureReport.Build(i), "Temperatura"));
            Assert.True(t.Contains("01:31  CPU 78 °C  GPU —  ventoinhas 4100 / — rpm"), t);
            Assert.True(!t.Contains("01:20"), t);
        }

        [Test]
        public static void ToText_HasEveryBlock_ScrubbedAndWithoutBlueScreenWording()
        {
            FailureReportInput i = Input();
            i.Journal.Add(J(Down.AddMinutes(-1), JournalKind.Action, "arquivo C:\\Users\\maria\\x.txt aberto"));
            FailureReport r = FailureReport.Build(i);
            string text = r.ToText(delegate(string s) { return LogText.Scrub(s, "C:\\Users\\maria", "maria"); });
            Assert.True(text.StartsWith("Relatório de reinício inesperado — 17/11 às 01:35 (2026)"), text.Substring(0, 60));
            foreach (string h in new string[] { "RESUMO", "ERROS DE HARDWARE (WHEA)", "DISCO E ARMAZENAMENTO", "PLACA DE VÍDEO", "ENERGIA (KERNEL-POWER)", "DIÁRIO DO APP", "TEMPERATURA E VENTOINHA", "OBSERVAÇÃO" })
            {
                Assert.True(text.Contains(h), "missing " + h);
            }
            Assert.True(text.Contains("%USERPROFILE%") && !text.Contains("maria"), "scrubbed");
            Assert.True(!text.ToLowerInvariant().Contains("tela azul"), "neutral wording");
        }

        [Test]
        public static void BugcheckGuide_CoversTheCommonCodes_WithMeaningAndAdvice()
        {
            long[] codes = new long[] { 0xA, 0x7E, 0xEF, 0x1C7, 0x50, 0x3B, 0x124, 0x9F, 0x133, 0x1A, 0x1E, 0xD1, 0x116, 0x117, 0x139, 0x154, 0x7A, 0x101 };
            foreach (long c in codes)
            {
                BugcheckInfo i = BugcheckGuide.Find(c);
                Assert.True(i != null, "0x" + c.ToString("X"));
                Assert.True(i.Name.Length > 3 && i.Meaning.Length > 20 && i.Advice.Length > 20, i.Name);
                Assert.True(!i.Meaning.ToLowerInvariant().Contains("tela azul") && !i.Advice.ToLowerInvariant().Contains("tela azul"), i.Name);
            }
            Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", BugcheckGuide.Find(0x1000007E).Name);
            Assert.Null(BugcheckGuide.Find(0x12345678));
        }

        [Test]
        public static void EventFilter_Classification()
        {
            Assert.Equal(ReportCategory.Whea, ReportEventFilter.Classify("Microsoft-Windows-WHEA-Logger", 17, 4).Value);
            Assert.Equal(ReportCategory.Storage, ReportEventFilter.Classify("disk", 153, 3).Value);
            Assert.Equal(ReportCategory.Storage, ReportEventFilter.Classify("stornvme", 129, 2).Value);
            Assert.Null(ReportEventFilter.Classify("Microsoft-Windows-Ntfs", 98, 4));
            Assert.Equal(ReportCategory.Gpu, ReportEventFilter.Classify("Display", 4101, 3).Value);
            Assert.Null(ReportEventFilter.Classify("Display", 4102, 3));
            Assert.Equal(ReportCategory.Gpu, ReportEventFilter.Classify("nvlddmkm", 14, 2).Value);
            Assert.Equal(ReportCategory.Power, ReportEventFilter.Classify("Microsoft-Windows-Kernel-Power", 41, 1).Value);
            Assert.Null(ReportEventFilter.Classify("Microsoft-Windows-Kernel-Power", 566, 4));
            Assert.Null(ReportEventFilter.Classify("SomethingElse", 1, 1));
            Assert.Null(ReportEventFilter.Classify(null, 1, 1));
        }

        [Test]
        public static void Window_IsTenMinutesBeforeAndOneAfterTheEstimatedStop()
        {
            CrashEntry e = Bsod();
            Assert.Equal(Down.AddMinutes(-10), FailureReport.WindowStart(e));
            Assert.Equal(Down.AddSeconds(60), FailureReport.WindowEnd(e));
            e.DownAt = null; // falls back to the log time
            Assert.Equal(e.When.AddMinutes(-10), FailureReport.WindowStart(e));
        }
    }
}
