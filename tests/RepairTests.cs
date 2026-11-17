using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Text;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class RepairParseTests
    {
        private static List<string> L(params string[] lines)
        {
            return new List<string>(lines);
        }

        // ---- percent ----

        [Test]
        public static void Percent_FromDismBar()
        {
            Assert.Equal(0, RepairParse.ParsePercent("[                           0.0%                           ]"));
            Assert.Equal(62, RepairParse.ParsePercent("[=====================62.3%=========                 ]"));
            Assert.Equal(100, RepairParse.ParsePercent("[==========================100.0%==========================]"));
        }

        [Test]
        public static void Percent_FromSfc_PtBrAndEnglish()
        {
            Assert.Equal(45, RepairParse.ParsePercent("Verificação 45% concluída."));
            Assert.Equal(7, RepairParse.ParsePercent("Verification 7% complete."));
            Assert.Equal(12, RepairParse.ParsePercent("Verification 12,5% complete."));
        }

        [Test]
        public static void Percent_NoneOrOutOfRange_IsMinusOne()
        {
            Assert.Equal(-1, RepairParse.ParsePercent("Ferramenta de Gerenciamento e Manutenção"));
            Assert.Equal(-1, RepairParse.ParsePercent(""));
            Assert.Equal(-1, RepairParse.ParsePercent(null));
            Assert.Equal(-1, RepairParse.ParsePercent("150% de nada"));
        }

        // ---- line splitting and decoding ----

        [Test]
        public static void LineSplitter_SplitsOnCarriageReturnAndNewline()
        {
            LineSplitter s = new LineSplitter();
            List<string> got = new List<string>();
            got.AddRange(s.Feed("[    10.0%    ]\r[    20.0%    ]\r[    3"));
            got.AddRange(s.Feed("0.0%    ]\r\nDone.\r\n\r\n"));
            string tail = s.Flush();
            Assert.Equal(4, got.Count);
            Assert.Equal("[    10.0%    ]", got[0]);
            Assert.Equal("[    30.0%    ]", got[2]);
            Assert.Equal("Done.", got[3]);
            Assert.Null(tail);
        }

        [Test]
        public static void LineSplitter_Flush_ReturnsUnterminatedTail()
        {
            LineSplitter s = new LineSplitter();
            s.Feed("Verification 100% complete.");
            Assert.Equal("Verification 100% complete.", s.Flush());
            Assert.Null(s.Flush());
        }

        [Test]
        public static void LineSplitter_StripsNulAndBom()
        {
            LineSplitter s = new LineSplitter();
            List<string> got = s.Feed("﻿abc\0\r\n");
            Assert.Equal(1, got.Count);
            Assert.Equal("abc", got[0]);
        }

        [Test]
        public static void Decoder_Oem850_DecodesAccents()
        {
            Encoding cp850 = Encoding.GetEncoding(850);
            byte[] bytes = cp850.GetBytes("Proteção de Recursos do Windows não encontrou");
            OutputDecoder d = new OutputDecoder(cp850, false);
            Assert.Equal("Proteção de Recursos do Windows não encontrou", d.Decode(bytes, bytes.Length));
        }

        [Test]
        public static void Decoder_AutoDetectsUtf16FromSfc()
        {
            Encoding cp850 = Encoding.GetEncoding(850);
            byte[] u16 = Encoding.Unicode.GetBytes("Verificação 5% concluída.\r\n");
            OutputDecoder d = new OutputDecoder(cp850, true);
            // split in the middle of a character pair to prove the decoder keeps state
            string a = d.Decode(u16, 7);
            string b = d.Decode(Slice(u16, 7), u16.Length - 7);
            Assert.Equal("Verificação 5% concluída.\r\n", a + b);
        }

        [Test]
        public static void Decoder_AutoDetect_PlainOemStaysOem()
        {
            Encoding cp850 = Encoding.GetEncoding(850);
            byte[] bytes = cp850.GetBytes("Iniciando verificação do sistema.");
            OutputDecoder d = new OutputDecoder(cp850, true);
            Assert.Equal("Iniciando verificação do sistema.", d.Decode(bytes, bytes.Length));
        }

        private static byte[] Slice(byte[] b, int from)
        {
            byte[] r = new byte[b.Length - from];
            Array.Copy(b, from, r, 0, r.Length);
            return r;
        }

        // ---- phases ----

        [Test]
        public static void Phase_SfcMarkers()
        {
            Assert.True(RepairParse.PhaseOf(RepairTool.Sfc, "Iniciando verificação do sistema. Esse processo levará algum tempo.").Contains("SFC"), "pt start");
            Assert.True(RepairParse.PhaseOf(RepairTool.Sfc, "Beginning system scan.  This process will take some time.").Contains("SFC"), "en start");
            Assert.True(RepairParse.PhaseOf(RepairTool.Sfc, "Iniciando a fase de verificação da verificação do sistema.").Contains("verificando"), "pt verify phase");
            Assert.True(RepairParse.PhaseOf(RepairTool.Sfc, "Beginning verification phase of system scan.").Contains("verificando"), "en verify phase");
            Assert.Null(RepairParse.PhaseOf(RepairTool.Sfc, "Verification 10% complete."));
        }

        [Test]
        public static void Phase_DismMarkers()
        {
            Assert.True(RepairParse.PhaseOf(RepairTool.Dism, "Versão da imagem: 10.0.26300.1").Contains("DISM"), "pt image version");
            Assert.True(RepairParse.PhaseOf(RepairTool.Dism, "Image Version: 10.0.26300.1").Contains("DISM"), "en image version");
            Assert.Null(RepairParse.PhaseOf(RepairTool.Dism, "algo qualquer"));
        }

        // ---- classification: DISM ----

        [Test]
        public static void Dism_Success_English_And_PtBr()
        {
            Assert.Equal(RepairOutcome.SemProblemas, RepairParse.ClassifyDism(0, L("[==100.0%==]", "The restore operation completed successfully.")).Outcome);
            Assert.Equal(RepairOutcome.SemProblemas, RepairParse.ClassifyDism(0, L("A operação de restauração foi concluída com êxito.")).Outcome);
        }

        [Test]
        public static void Dism_CorruptionRepaired()
        {
            Assert.Equal(RepairOutcome.Reparado, RepairParse.ClassifyDism(0, L("The component store corruption was repaired.", "The restore operation completed successfully.")).Outcome);
            Assert.Equal(RepairOutcome.Reparado, RepairParse.ClassifyDism(0, L("A corrupção do repositório de componentes foi reparada.", "A operação de restauração foi concluída com êxito.")).Outcome);
        }

        [Test]
        public static void Dism_SourceFilesNotFound_IsNotRepairable()
        {
            ToolOutcome o = RepairParse.ClassifyDism(unchecked((int)0x800F081F), L("Error: 0x800f081f", "The source files could not be found."));
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, o.Outcome);
            ToolOutcome pt = RepairParse.ClassifyDism(-2146498529, L("Erro: 0x800f081f", "Não foi possível encontrar os arquivos de origem."));
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, pt.Outcome);
            // the exit code alone is enough
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, RepairParse.ClassifyDism(unchecked((int)0x800F081F), L()).Outcome);
        }

        [Test]
        public static void Dism_NeedsElevation_IsErrorWithAdminAdvice()
        {
            ToolOutcome o = RepairParse.ClassifyDism(740, L("Erro: 740", "São necessárias permissões elevadas para executar o DISM."));
            Assert.Equal(RepairOutcome.Erro, o.Outcome);
            Assert.True(o.NeedsElevation, "flags elevation");
            Assert.True(o.Detail.ToLowerInvariant().Contains("administrador"), "advice");
        }

        [Test]
        public static void Dism_OtherFailure_IsErrorWithCode()
        {
            ToolOutcome o = RepairParse.ClassifyDism(87, L("Error: 87", "The restore option is unknown."));
            Assert.Equal(RepairOutcome.Erro, o.Outcome);
            Assert.True(o.Detail.Contains("87"), "code shown");
        }

        // ---- classification: SFC ----

        [Test]
        public static void Sfc_NoViolations_English_And_PtBr()
        {
            Assert.Equal(RepairOutcome.SemProblemas, RepairParse.ClassifySfc(0, L("Windows Resource Protection did not find any integrity violations.")).Outcome);
            Assert.Equal(RepairOutcome.SemProblemas, RepairParse.ClassifySfc(0, L("A Proteção de Recursos do Windows não encontrou nenhuma violação de integridade.")).Outcome);
        }

        [Test]
        public static void Sfc_Repaired_English_And_PtBr()
        {
            Assert.Equal(RepairOutcome.Reparado, RepairParse.ClassifySfc(0, L("Windows Resource Protection found corrupt files and successfully repaired them.")).Outcome);
            Assert.Equal(RepairOutcome.Reparado, RepairParse.ClassifySfc(0, L("A Proteção de Recursos do Windows encontrou arquivos corrompidos e os reparou com êxito.")).Outcome);
        }

        [Test]
        public static void Sfc_UnableToFix_IsNotRepairable_NotRepaired()
        {
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, RepairParse.ClassifySfc(0, L("Windows Resource Protection found corrupt files but was unable to fix some of them.")).Outcome);
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, RepairParse.ClassifySfc(0, L("A Proteção de Recursos do Windows encontrou arquivos corrompidos, mas não conseguiu corrigir alguns deles.")).Outcome);
        }

        [Test]
        public static void Sfc_CouldNotPerform_IsError()
        {
            ToolOutcome o = RepairParse.ClassifySfc(0, L("Windows Resource Protection could not perform the requested operation."));
            Assert.Equal(RepairOutcome.Erro, o.Outcome);
            Assert.Equal(RepairOutcome.Erro, RepairParse.ClassifySfc(0, L("A Proteção de Recursos do Windows não pôde executar a operação solicitada.")).Outcome);
        }

        [Test]
        public static void Sfc_PendingRepair_IsErrorWithRebootAdvice()
        {
            ToolOutcome o = RepairParse.ClassifySfc(0, L("There is a system repair pending which requires reboot to complete.  Restart Windows and run sfc again."));
            Assert.Equal(RepairOutcome.Erro, o.Outcome);
            Assert.True(o.Detail.ToLowerInvariant().Contains("reinicie"), "advice to restart");
        }

        [Test]
        public static void Sfc_NoRecognizedMessage_IsErrorNotSuccess()
        {
            ToolOutcome o = RepairParse.ClassifySfc(0, L("Verification 100% complete."));
            Assert.Equal(RepairOutcome.Erro, o.Outcome);
            Assert.Equal(RepairOutcome.Erro, RepairParse.ClassifySfc(1, L()).Outcome);
        }

        [Test]
        public static void Sfc_NotAdmin_FlagsElevation()
        {
            ToolOutcome o = RepairParse.ClassifySfc(1, L("You must be an administrator running a console session in order to use the sfc utility."));
            Assert.Equal(RepairOutcome.Erro, o.Outcome);
            Assert.True(o.NeedsElevation, "elevation");
        }

        // ---- combining ----

        private static ToolOutcome T(RepairOutcome o)
        {
            return new ToolOutcome(o, "x", true, false);
        }

        [Test]
        public static void Combine_Priorities()
        {
            Assert.Equal(RepairOutcome.SemProblemas, RepairParse.Combine(T(RepairOutcome.SemProblemas), T(RepairOutcome.SemProblemas), false));
            Assert.Equal(RepairOutcome.Reparado, RepairParse.Combine(T(RepairOutcome.SemProblemas), T(RepairOutcome.Reparado), false));
            Assert.Equal(RepairOutcome.Reparado, RepairParse.Combine(T(RepairOutcome.Reparado), T(RepairOutcome.SemProblemas), false));
            Assert.Equal(RepairOutcome.Erro, RepairParse.Combine(T(RepairOutcome.Erro), T(RepairOutcome.SemProblemas), false));
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, RepairParse.Combine(T(RepairOutcome.NaoFoiPossivelReparar), T(RepairOutcome.Erro), false));
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, RepairParse.Combine(T(RepairOutcome.Reparado), T(RepairOutcome.NaoFoiPossivelReparar), false));
            Assert.Equal(RepairOutcome.Cancelado, RepairParse.Combine(T(RepairOutcome.SemProblemas), T(RepairOutcome.SemProblemas), true));
        }

        [Test]
        public static void Combine_StepThatDidNotRunIsIgnored()
        {
            ToolOutcome notRun = new ToolOutcome(RepairOutcome.SemProblemas, null, false, false);
            Assert.Equal(RepairOutcome.Reparado, RepairParse.Combine(T(RepairOutcome.Reparado), notRun, false));
        }

        [Test]
        public static void Texts_FollowCopyRules_AndAdviseOnErrors()
        {
            foreach (RepairOutcome o in Enum.GetValues(typeof(RepairOutcome)))
            {
                string title = RepairParse.Title(o);
                string msg = RepairParse.Message(o, null);
                string all = (title + " " + msg).ToLowerInvariant();
                Assert.True(title.Length > 0 && msg.Length > 0, "texts for " + o);
                Assert.True(!all.Contains("please") && !all.Contains("por favor") && !all.Contains("successfully") && !all.Contains("com sucesso"), "copy rule for " + o);
                Assert.True(!all.Contains("ac" + "er") && !all.Contains("ni" + "tro") && !all.Contains("pred" + "ator"), "no brand words");
            }
            Assert.True(RepairParse.Message(RepairOutcome.Erro, null).ToLowerInvariant().Contains("tente"), "error says what to do");
            Assert.True(RepairParse.Message(RepairOutcome.NaoFoiPossivelReparar, null).Contains("CBS.log"), "points at the log");
        }
    }

    public static class RepairRunnerTests
    {
        private static RepairStep Step(RepairTool tool, string args)
        {
            RepairStep s = new RepairStep();
            s.Tool = tool;
            s.FileName = Environment.GetEnvironmentVariable("ComSpec");
            s.Arguments = args;
            return s;
        }

        private static RepairRunner Runner(ChangeLog log, params RepairStep[] steps)
        {
            return new RepairRunner(new List<RepairStep>(steps), Encoding.GetEncoding(850), log, false, null);
        }

        [Test]
        public static void Run_FakeTools_StreamsProgressAndClassifies()
        {
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            RepairRunner r = Runner(log,
                Step(RepairTool.Dism, "/c \"echo [=====25.0%=====]& echo [=====100.0%=====]& echo The restore operation completed successfully.\""),
                Step(RepairTool.Sfc, "/c \"echo Beginning verification phase of system scan.& echo Verification 40% complete.& echo Windows Resource Protection did not find any integrity violations.\""));
            List<RepairProgress> seen = new List<RepairProgress>();
            RepairResult res = r.Run(delegate(RepairProgress p) { lock (seen) { seen.Add(p); } }, CancellationToken.None);
            Assert.Equal(RepairOutcome.SemProblemas, res.Outcome);
            Assert.Equal(0, res.DismExit);
            Assert.Equal(0, res.SfcExit);
            Assert.True(res.DismRan && res.SfcRan, "both ran");
            bool dism25 = false, sfc40 = false, step2 = false;
            lock (seen)
            {
                foreach (RepairProgress p in seen)
                {
                    if (p.Tool == RepairTool.Dism && p.Percent == 25 && p.Step == 1) dism25 = true;
                    if (p.Tool == RepairTool.Sfc && p.Percent == 40 && p.Step == 2) sfc40 = true;
                    if (p.Step == 2 && p.StepCount == 2) step2 = true;
                }
            }
            Assert.True(dism25, "dism percent");
            Assert.True(sfc40, "sfc percent");
            Assert.True(step2, "step numbering");
            Assert.True(res.Tail.Count >= 2, "tail kept");
            Assert.Equal(1, log.List().Count);
            Assert.Equal(ChangeKind.Reparo, log.List()[0].Kind);
            Assert.True(!log.List()[0].CanUndo, "repairs are not undone");
        }

        [Test]
        public static void Run_ToolExitCodeAndMessage_FeedClassification()
        {
            RepairRunner r = Runner(null,
                Step(RepairTool.Dism, "/c \"echo The restore operation completed successfully.\""),
                Step(RepairTool.Sfc, "/c \"echo Windows Resource Protection found corrupt files but was unable to fix some of them.\""));
            RepairResult res = r.Run(null, CancellationToken.None);
            Assert.Equal(RepairOutcome.NaoFoiPossivelReparar, res.Outcome);
        }

        [Test]
        public static void Run_DismElevationError_SkipsSfc()
        {
            RepairRunner r = Runner(null,
                Step(RepairTool.Dism, "/c exit 740"),
                Step(RepairTool.Sfc, "/c \"echo should not run\""));
            RepairResult res = r.Run(null, CancellationToken.None);
            Assert.Equal(RepairOutcome.Erro, res.Outcome);
            Assert.Equal(740, res.DismExit);
            Assert.True(!res.SfcRan, "sfc skipped after an elevation error");
        }

        [Test]
        public static void Run_NotElevated_RefusesWithoutLaunching()
        {
            RepairStep s = Step(RepairTool.Dism, "/c \"echo launched\"");
            RepairRunner r = new RepairRunner(new List<RepairStep>(new RepairStep[] { s }), Encoding.GetEncoding(850), null, true, delegate { return false; });
            RepairResult res = r.Run(null, CancellationToken.None);
            Assert.Equal(RepairOutcome.Erro, res.Outcome);
            Assert.True(!res.DismRan, "nothing launched");
            Assert.True(res.Message.ToLowerInvariant().Contains("administrador"), "tells what to do");
        }

        [Test]
        public static void Run_Cancel_KillsProcessTreeAndReportsCancelled()
        {
            // unique argument so the process can be found afterwards
            RepairRunner r = Runner(null, Step(RepairTool.Sfc, "/c ping -n 37 127.0.0.1"));
            CancellationTokenSource cts = new CancellationTokenSource();
            Stopwatch sw = Stopwatch.StartNew();
            Thread t = new Thread(delegate() { Thread.Sleep(700); cts.Cancel(); });
            t.Start();
            RepairResult res = r.Run(null, cts.Token);
            t.Join();
            Assert.Equal(RepairOutcome.Cancelado, res.Outcome);
            Assert.True(sw.ElapsedMilliseconds < 15000, "returned long before the 37 pings finished");
            Thread.Sleep(300);
            Assert.Equal(0, CountProcessesWithCommandLine("-n 37 127.0.0.1"));
        }

        [Test]
        public static void DefaultSteps_AreDismRestoreHealthThenSfcScannow()
        {
            List<RepairStep> steps = RepairRunner.DefaultSteps();
            Assert.Equal(2, steps.Count);
            Assert.Equal(RepairTool.Dism, steps[0].Tool);
            Assert.True(steps[0].FileName.EndsWith("Dism.exe", StringComparison.OrdinalIgnoreCase), "dism path");
            Assert.Equal("/Online /Cleanup-Image /RestoreHealth", steps[0].Arguments);
            Assert.Equal(RepairTool.Sfc, steps[1].Tool);
            Assert.True(steps[1].FileName.EndsWith("sfc.exe", StringComparison.OrdinalIgnoreCase), "sfc path");
            Assert.Equal("/scannow", steps[1].Arguments);
        }

        private static int CountProcessesWithCommandLine(string fragment)
        {
            int n = 0;
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'PING.EXE'"))
            using (ManagementObjectCollection c = s.Get())
            {
                foreach (ManagementObject o in c)
                {
                    string cl = o["CommandLine"] as string;
                    if (cl != null && cl.Contains(fragment)) n++;
                    o.Dispose();
                }
            }
            return n;
        }
    }
}
