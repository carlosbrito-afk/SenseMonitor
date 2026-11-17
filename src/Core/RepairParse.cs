using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Forja.Core
{
    public enum RepairTool
    {
        Dism,
        Sfc
    }

    public enum RepairOutcome
    {
        SemProblemas,
        Reparado,
        NaoFoiPossivelReparar,
        Erro,
        Cancelado
    }

    // Result of one tool (DISM or SFC).
    public class ToolOutcome
    {
        public readonly RepairOutcome Outcome;
        public readonly string Detail;          // extra sentence for the user (what happened / what to do)
        public readonly bool Ran;
        public readonly bool NeedsElevation;

        public ToolOutcome(RepairOutcome outcome, string detail, bool ran, bool needsElevation)
        {
            Outcome = outcome;
            Detail = detail;
            Ran = ran;
            NeedsElevation = needsElevation;
        }
    }

    public class RepairProgress
    {
        public RepairTool Tool;
        public int Step;            // 1-based
        public int StepCount;
        public string Phase;        // pt-BR, e.g. "SFC: verificando arquivos do sistema"
        public int Percent = -1;    // -1 when the tool has not reported one yet
        public string LastLine;
        public TimeSpan Elapsed;
    }

    public class RepairResult
    {
        public RepairOutcome Outcome;
        public string Title;
        public string Message;
        public int DismExit = -1;
        public int SfcExit = -1;
        public bool DismRan;
        // Cancelled (or the app closed) while DISM ran: DISM was left running on its own, never killed.
        public bool DismDetached;
        public bool SfcRan;
        public TimeSpan Duration;
        public List<string> Tail = new List<string>();
    }

    public interface IRepairService
    {
        RepairResult Run(Action<RepairProgress> progress, CancellationToken ct);
    }

    // Turns a byte stream from DISM or SFC into text. DISM writes in the console OEM code
    // page (CP 850 on pt-BR); SFC writes UTF-16 when its output is redirected. Stateful, so a
    // character split between two reads is decoded correctly.
    public class OutputDecoder
    {
        private readonly Encoding oem;
        private readonly bool autoUtf16;
        private Decoder decoder;

        public OutputDecoder(Encoding oem, bool autoUtf16)
        {
            if (oem == null) throw new ArgumentNullException("oem");
            this.oem = oem;
            this.autoUtf16 = autoUtf16;
        }

        public string Decode(byte[] buffer, int count)
        {
            if (count <= 0) return "";
            if (decoder == null)
            {
                bool wide = false;
                if (autoUtf16)
                {
                    if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) wide = true;
                    else
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (buffer[i] == 0) { wide = true; break; }
                        }
                    }
                }
                decoder = wide ? Encoding.Unicode.GetDecoder() : oem.GetDecoder();
            }
            int n = decoder.GetCharCount(buffer, 0, count, false);
            char[] chars = new char[n];
            n = decoder.GetChars(buffer, 0, count, chars, 0, false);
            return new string(chars, 0, n);
        }
    }

    // Splits text on '\r' and '\n'. DISM and SFC redraw their progress with a bare '\r', so a
    // plain line reader would only see the last update.
    public class LineSplitter
    {
        private readonly StringBuilder current = new StringBuilder();

        public List<string> Feed(string chunk)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(chunk)) return lines;
            foreach (char ch in chunk)
            {
                if (ch == '\r' || ch == '\n')
                {
                    Emit(lines);
                }
                else if (ch != '\0' && ch != '\uFEFF')
                {
                    current.Append(ch);
                }
            }
            return lines;
        }

        // The unterminated rest, or null.
        public string Flush()
        {
            string s = current.ToString().Trim();
            current.Length = 0;
            return s.Length == 0 ? null : s;
        }

        private void Emit(List<string> lines)
        {
            string s = current.ToString().Trim();
            current.Length = 0;
            if (s.Length > 0) lines.Add(s);
        }
    }

    // Reads what the tools print. Matching is done on text with accents and case removed, so
    // it works for pt-BR and English output and survives code page mishaps.
    public static class RepairParse
    {
        private static readonly Regex PercentRx = new Regex(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(d.Length);
            bool space = false;
            foreach (char c in d)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(c))
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                    continue;
                }
                space = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Trim();
        }

        // 0..100 from "[=====62.3%=====]" or "Verificacao 45% concluida"; -1 when there is none.
        public static int ParsePercent(string line)
        {
            if (string.IsNullOrEmpty(line)) return -1;
            Match m = PercentRx.Match(line);
            if (!m.Success) return -1;
            double v;
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return -1;
            if (v < 0 || v > 100) return -1;
            return (int)v;
        }

        public static string DefaultPhase(RepairTool tool)
        {
            return tool == RepairTool.Dism ? "DISM: restaurando o reposit\u00f3rio de componentes" : "SFC: verificando arquivos do sistema";
        }

        // The new phase text when the line marks one, otherwise null.
        public static string PhaseOf(RepairTool tool, string line)
        {
            string n = Normalize(line);
            if (n.Length == 0) return null;
            if (tool == RepairTool.Sfc)
            {
                if (n.Contains("verification phase") || n.Contains("fase de verificacao"))
                    return "SFC: verificando arquivos do sistema";
                if (n.Contains("beginning system scan") || n.Contains("iniciando verificacao do sistema") || n.Contains("iniciando a verificacao do sistema"))
                    return "SFC: iniciando a verifica\u00e7\u00e3o do sistema";
                return null;
            }
            if (n.Contains("image version") || n.Contains("versao da imagem"))
                return "DISM: lendo a imagem do Windows";
            if (n.Contains("deployment image servicing") || n.Contains("gerenciamento e manutencao de imagens"))
                return "DISM: iniciando";
            return null;
        }

        // ---- DISM ----

        private static readonly string[] DismSourceMissingCodes =
        {
            "0x800f081f", "0x800f0906", "0x800f0907", "0x800f0950", "0x800f0915", "0x800f0900"
        };

        public static ToolOutcome ClassifyDism(int exitCode, IList<string> lines)
        {
            string all = Join(lines);
            uint code = unchecked((uint)exitCode);
            bool elevation = exitCode == 740 || all.Contains("error: 740") || all.Contains("erro: 740")
                || all.Contains("elevated permissions") || all.Contains("permissoes elevadas");
            if (elevation)
            {
                return new ToolOutcome(RepairOutcome.Erro,
                    "O DISM precisa de permiss\u00e3o de administrador. Abra o app como administrador e tente de novo.", true, true);
            }
            bool sourceMissing = false;
            foreach (string c in DismSourceMissingCodes)
            {
                if (all.Contains(c) || code == Convert.ToUInt32(c.Substring(2), 16)) sourceMissing = true;
            }
            if (all.Contains("source files could not be found") || all.Contains("nao foi possivel encontrar os arquivos de origem")
                || all.Contains("arquivos de origem nao foram encontrados") || all.Contains("component store cannot be repaired")
                || all.Contains("repositorio de componentes nao pode ser reparado"))
            {
                sourceMissing = true;
            }
            if (sourceMissing)
            {
                return new ToolOutcome(RepairOutcome.NaoFoiPossivelReparar,
                    "O DISM n\u00e3o achou os arquivos de origem para refazer o que est\u00e1 corrompido. Ele precisa do Windows Update funcionando ou de uma imagem ISO do Windows.", true, false);
            }
            if (exitCode != 0)
            {
                return new ToolOutcome(RepairOutcome.Erro,
                    "O DISM terminou com erro (c\u00f3digo " + exitCode + ", 0x" + code.ToString("x8") + ").", true, false);
            }
            if (all.Contains("component store corruption was repaired") || all.Contains("corrupcao do repositorio de componentes foi reparada"))
            {
                return new ToolOutcome(RepairOutcome.Reparado, "O DISM corrigiu o reposit\u00f3rio de componentes.", true, false);
            }
            return new ToolOutcome(RepairOutcome.SemProblemas, null, true, false);
        }

        // ---- SFC ----

        public static ToolOutcome ClassifySfc(int exitCode, IList<string> lines)
        {
            string all = Join(lines);
            if (all.Contains("must be an administrator") || all.Contains("voce deve ser um administrador") || all.Contains("deve ser administrador"))
            {
                return new ToolOutcome(RepairOutcome.Erro,
                    "O SFC precisa de permiss\u00e3o de administrador. Abra o app como administrador e tente de novo.", true, true);
            }
            if (all.Contains("system repair pending") || all.Contains("reparo do sistema pendente") || all.Contains("reparo pendente"))
            {
                return new ToolOutcome(RepairOutcome.Erro,
                    "H\u00e1 um reparo do Windows pendente. Reinicie o PC e rode o reparo de novo.", true, false);
            }
            if (all.Contains("unable to fix") || all.Contains("nao conseguiu corrigir") || all.Contains("nao foi possivel corrigir"))
            {
                return new ToolOutcome(RepairOutcome.NaoFoiPossivelReparar,
                    "O SFC achou arquivos corrompidos e n\u00e3o conseguiu corrigir alguns.", true, false);
            }
            if (all.Contains("found corrupt files and successfully repaired") || (all.Contains("arquivos corrompidos") && all.Contains("reparou")))
            {
                return new ToolOutcome(RepairOutcome.Reparado, "O SFC corrigiu arquivos corrompidos.", true, false);
            }
            if (all.Contains("did not find any integrity violations") || all.Contains("nao encontrou nenhuma violacao"))
            {
                return new ToolOutcome(RepairOutcome.SemProblemas, null, true, false);
            }
            if (all.Contains("could not perform the requested operation") || all.Contains("executar a operacao solicitada"))
            {
                return new ToolOutcome(RepairOutcome.Erro,
                    "O SFC n\u00e3o conseguiu fazer a verifica\u00e7\u00e3o. Reinicie o PC e tente de novo.", true, false);
            }
            return new ToolOutcome(RepairOutcome.Erro,
                "O SFC terminou sem um resultado que o app reconhe\u00e7a (c\u00f3digo " + exitCode + ").", true, false);
        }

        private static string Join(IList<string> lines)
        {
            if (lines == null || lines.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (string l in lines)
            {
                sb.Append(Normalize(l)).Append('\n');
            }
            return sb.ToString();
        }

        // ---- final result ----

        // NaoFoiPossivelReparar > Erro > Reparado > SemProblemas; a step that did not run is ignored.
        public static RepairOutcome Combine(ToolOutcome dism, ToolOutcome sfc, bool cancelled)
        {
            if (cancelled) return RepairOutcome.Cancelado;
            bool any = false;
            bool notRepairable = false, error = false, repaired = false;
            ToolOutcome[] all = { dism, sfc };
            foreach (ToolOutcome o in all)
            {
                if (o == null || !o.Ran) continue;
                any = true;
                if (o.Outcome == RepairOutcome.NaoFoiPossivelReparar) notRepairable = true;
                else if (o.Outcome == RepairOutcome.Erro) error = true;
                else if (o.Outcome == RepairOutcome.Reparado) repaired = true;
            }
            if (!any) return RepairOutcome.Erro;
            if (notRepairable) return RepairOutcome.NaoFoiPossivelReparar;
            if (error) return RepairOutcome.Erro;
            if (repaired) return RepairOutcome.Reparado;
            return RepairOutcome.SemProblemas;
        }

        public static string Title(RepairOutcome o)
        {
            switch (o)
            {
                case RepairOutcome.SemProblemas: return "Nenhum problema encontrado";
                case RepairOutcome.Reparado: return "Problemas encontrados e reparados";
                case RepairOutcome.NaoFoiPossivelReparar: return "N\u00e3o foi poss\u00edvel reparar tudo";
                case RepairOutcome.Cancelado: return "Reparo cancelado";
                default: return "O reparo n\u00e3o terminou";
            }
        }

        // Appended to the result when DISM was left running instead of being stopped.
        public const string DismDetachedNote = "O DISM continua rodando em segundo plano at\u00e9 terminar (ele n\u00e3o \u00e9 interrompido no meio); o SFC n\u00e3o foi iniciado.";

        // Confirmation text for "Sair" while maintenance runs.
        public static string QuitPrompt(bool dismRunning)
        {
            if (dismRunning)
                return "O reparo do Windows est\u00e1 na etapa do DISM. O DISM n\u00e3o \u00e9 interrompido no meio: ele continua rodando sozinho depois que o app fechar e termina normalmente, mas o resultado n\u00e3o vai aparecer aqui e o SFC n\u00e3o ser\u00e1 iniciado.\n\nSair mesmo assim?";
            return "Uma limpeza ou o SFC est\u00e1 em andamento. Sair agora cancela e encerra esse trabalho.\n\nSair mesmo assim?";
        }

        // detail: sentences from the tool classifiers, appended when present.
        public static string Message(RepairOutcome o, string detail)
        {
            string d = string.IsNullOrEmpty(detail) ? "" : detail + " ";
            switch (o)
            {
                case RepairOutcome.SemProblemas:
                    return "O DISM e o SFC n\u00e3o acharam arquivos do sistema corrompidos.";
                case RepairOutcome.Reparado:
                    return d + "Reinicie o PC para concluir os reparos.";
                case RepairOutcome.NaoFoiPossivelReparar:
                    return d + "Procure as linhas com [SR] em C:\\Windows\\Logs\\CBS\\CBS.log. Se o problema continuar, use a op\u00e7\u00e3o de reparar o Windows nas Configura\u00e7\u00f5es ou reinstale mantendo seus arquivos.";
                case RepairOutcome.Cancelado:
                    return "Voc\u00ea cancelou. Rode de novo quando quiser.";
                default:
                    return d + "Feche instaladores abertos, abra o app como administrador e tente de novo.";
            }
        }
    }
}
