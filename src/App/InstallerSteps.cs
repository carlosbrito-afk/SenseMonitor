using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // What the installer's own steps share with each other and with the code that runs the window. Everything the
    // elevated copy needs to know came on its command line as a step token (--passo=reconhecer); nothing here is read
    // from a file a user-level process could have written.
    internal sealed class InstallContext
    {
        // This process runs with administrator rights (false only in the first stage, the welcome window).
        public bool Elevated;
        public string Version = AppVersion.Text;

        // First stage: "Instalar" and "Abrir sem instalar" ask Windows for elevation (one function, one companion check).
        public Func<ElevateResult> ElevateToInstall;
        public Func<ElevateResult> ElevateToRunOnly;
        // Something that stops the install before it starts (another SenseMonitor is open): the text, or null.
        public Func<string> Blocker;
        // The exe being installed, HELD open (FileShare.Read) since the start of the process; the copy comes from this handle.
        public SourceHold Source;
        // The install steps of the real machine (a fake in the screenshot harness).
        public Func<IInstallOps> CreateOps;
        // Is another copy of the app running (decided on the worker thread; the install then closes it first)?
        public Func<bool> AppIsRunning = delegate { return false; };

        // Results the steps leave for the runner.
        public volatile bool Installed;
        public volatile bool OpenRequested;
        public volatile bool RunWithoutInstall;
        public InstallOutcome Result;
    }

    // ---- 1. Bem-vindo --------------------------------------------------------------------------------------

    internal sealed class WelcomeStep : StepBase
    {
        private readonly InstallContext ctx;
        private string note;

        public WelcomeStep(InstallContext ctx)
        {
            this.ctx = ctx;
        }

        public override string Key { get { return "welcome"; } }
        public override string ShortName { get { return "Bem-vindo"; } }
        public override string Title { get { return "Bem-vindo"; } }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.PrimaryText = "Instalar";
            u.AVisible = true;
            u.AText = "Abrir sem instalar";
            return u;
        }

        // The primary button: install. Not elevated yet: this is where Windows is asked, and the elevated copy resumes
        // at the second step. Already elevated: simply go on.
        public override bool Next()
        {
            if (ctx.Elevated) return true;
            string block = ctx.Blocker == null ? null : ctx.Blocker();
            if (block != null)
            {
                note = block;
                return false;
            }
            return Hand(ctx.ElevateToInstall, "Sem a permissão de administrador não dá para instalar: o firmware só responde a quem a tem. Clique em Instalar e escolha Sim na pergunta do Windows.");
        }

        public override void ActionA()
        {
            if (ctx.Elevated)
            {
                ctx.RunWithoutInstall = true;
                host.CloseWindow();
                return;
            }
            string block = ctx.Blocker == null ? null : ctx.Blocker();
            if (block != null)
            {
                note = block;
                return;
            }
            Hand(ctx.ElevateToRunOnly, "Sem a permissão de administrador o SenseMonitor não consegue controlar as ventoinhas. Tente de novo e escolha Sim na pergunta do Windows.");
        }

        // false always: either the window closes (the elevated copy took over) or it stays with a note.
        private bool Hand(Func<ElevateResult> elevate, string declinedNote)
        {
            ElevateResult r = elevate == null ? ElevateResult.Declined : elevate();
            if (r == ElevateResult.Started)
            {
                Environment.ExitCode = 0;
                host.CloseWindow();
                return false;
            }
            if (r == ElevateResult.Refused)
            {
                // The message about the files next to the exe was shown; there is nothing more to do from this window.
                host.CloseWindow();
                return false;
            }
            note = declinedNote;
            return false;
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            y += WizUi.Heading(g, x, y, w, "Bem-vindo ao SenseMonitor", "Versão " + ctx.Version + "  ·  a instalação leva poucos minutos");
            y += WizDraw.Para(g, "O SenseMonitor é uma central de controle leve para notebooks gamer da Acer (Nitro, Nitro V, Predator e afins): mostra temperatura, uso e ventoinhas, troca os modos de desempenho, controla as ventoinhas e cuida da saúde e da manutenção do Windows. Fica na bandeja, perto do relógio.",
                Theme.Font(10.5f, false), Theme.Text, x, y, w) + WizUi.S(12);

            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
            rows.Add(WizUi.KV("1. Reconhecer", "o modelo, a BIOS e o que o firmware do notebook anuncia (só leitura)"));
            rows.Add(WizUi.KV("2. Programas", "serviços da Acer que disputam as ventoinhas: você decide se desativa"));
            rows.Add(WizUi.KV("3. Instalar", "em Arquivos de Programas, com início automático, atalhos e entrada em Apps e recursos"));
            rows.Add(WizUi.KV("4. Testar", "a tecla, as ventoinhas e os modos deste notebook; cada teste pode ficar para depois"));
            y += WizUi.InfoCard(g, x, y, w, "O que vai acontecer", rows) + WizUi.S(12);

            y += WizUi.Note(g, WizIcon.Ok, "Projeto independente e de domínio público: use, copie e modifique como quiser. Não é afiliado à Acer.", Theme.Muted, x, y, w);
            if (!ctx.Elevated)
                y += WizUi.Note(g, WizIcon.Wait, "Ao clicar em Instalar o Windows pede permissão de administrador: o firmware só responde a quem a tem. “Abrir sem instalar” usa o app só desta vez.", Theme.Muted, x, y, w);
            if (note != null) y += WizUi.Note(g, WizIcon.Warn, note, Palette.Warn, x, y, w);
        }
    }

    // ---- 4. Instalando --------------------------------------------------------------------------------------

    // Runs the install on a worker thread (InstallEngine), shows one line per step and undoes everything when a step
    // fails or the user cancels before it is done. After it, the window goes on with the guided tests.
    internal sealed class InstallingStep : StepBase
    {
        private enum Phase { Idle, Running, Done, Failed, Cancelled }

        private sealed class Line
        {
            public InstallStep Step;
            public InstallStepState? State; // null = waiting
            public string Message;
        }

        private readonly InstallContext ctx;
        private readonly List<Line> lines = new List<Line>();
        private volatile bool cancelRequested;
        private Phase phase = Phase.Idle;
        private int done;

        public InstallingStep(InstallContext ctx)
        {
            this.ctx = ctx;
        }

        public override string Key { get { return "installing"; } }
        public override string ShortName { get { return "Instalando"; } }
        public override string Title { get { return "Instalando"; } }

        protected override void OnEnter()
        {
            if (phase == Phase.Idle || phase == Phase.Failed) Start();
        }

        private void Start()
        {
            cancelRequested = false;
            phase = Phase.Running;
            done = 0;
            lines.Clear();
            foreach (InstallStep s in InstallPlanner.ForWizardInstall(false))
            {
                Line l = new Line();
                l.Step = s;
                lines.Add(l);
            }
            ctx.Result = null;
            InstallOutcome outcome = null;
            IWizardHost h = host;
            host.RunWorker("Install", delegate
            {
                bool running = false;
                try { running = ctx.AppIsRunning(); }
                catch (Exception) { }
                List<InstallStep> plan = InstallPlanner.ForWizardInstall(running);
                IInstallOps ops = ctx.CreateOps();
                Log("instalação " + AppVersion.Text + " (janela): " + string.Join(", ", plan.ConvertAll<string>(delegate(InstallStep st) { return st.ToString(); }).ToArray()));
                outcome = InstallEngine.Run(ops, plan, false, running,
                    delegate { return cancelRequested; },
                    delegate(InstallProgress p) { h.Post(delegate { Apply(p); h.Changed(); }); });
            }, delegate
            {
                ctx.Result = outcome ?? Crashed();
                Log(ctx.Result.Ok ? "instalação (janela): concluída" : "instalação (janela): " + (ctx.Result.Cancelled ? "cancelada" : "falhou no passo " + ctx.Result.FailedStep + ": " + ctx.Result.Error)
                    + (ctx.Result.RollbackClean ? "; desfeita" : "; restos: " + string.Join(" | ", ctx.Result.RollbackProblems.ToArray())));
                if (ctx.Result.Ok) { ctx.Installed = true; phase = Phase.Done; }
                else phase = ctx.Result.Cancelled ? Phase.Cancelled : Phase.Failed;
            });
        }

        // The updater's admin-only journal (the same place the command-line install writes to). Never throws.
        private static void Log(string line)
        {
            try { Forja.Platform.UpdateLog.Append(line); }
            catch (Exception) { }
        }

        private static InstallOutcome Crashed()
        {
            InstallOutcome o = new InstallOutcome();
            o.Error = "A instalação parou por um erro inesperado.";
            o.RollbackClean = false;
            o.RollbackProblems.Add("não deu para conferir o que foi feito; veja o diário do SenseMonitor");
            return o;
        }

        // UI thread.
        private void Apply(InstallProgress p)
        {
            Line l = lines.Find(delegate(Line x) { return x.Step == p.Step; });
            if (l == null)
            {
                // The close-the-running-app step is only planned once the worker saw the app running.
                l = new Line();
                l.Step = p.Step;
                lines.Insert(0, l);
            }
            l.State = p.State;
            l.Message = p.Message;
            if (p.State == InstallStepState.Done || p.State == InstallStepState.Warning) done++;
        }

        public override void Cancel()
        {
            cancelRequested = true;
        }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            if (phase == Phase.Failed)
            {
                u.PrimaryText = "Fechar";
                u.AVisible = ctx.Result != null && ctx.Result.RollbackClean;
                u.AText = "Tentar de novo";
                return u;
            }
            u.PrimaryText = "Continuar";
            u.PrimaryEnabled = phase == Phase.Done;
            return u;
        }

        public override bool Next()
        {
            if (phase == Phase.Failed)
            {
                host.RequestCancelNow();
                return false;
            }
            return phase == Phase.Done;
        }

        public override void ActionA()
        {
            if (phase == Phase.Failed) Start();
        }

        private static WizIcon IconFor(Line l)
        {
            if (!l.State.HasValue) return WizIcon.Off;
            switch (l.State.Value)
            {
                case InstallStepState.Done: return WizIcon.Ok;
                case InstallStepState.Warning: return WizIcon.Warn;
                case InstallStepState.Failed: return WizIcon.Fail;
                default: return WizIcon.Wait;
            }
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            string sub = phase == Phase.Done ? "Instalado." : phase == Phase.Failed ? "Não foi possível instalar." : phase == Phase.Cancelled ? "Instalação cancelada." : "Isso leva alguns segundos. Não feche esta janela.";
            y += WizUi.Heading(g, x, y, w, "Instalando o SenseMonitor", sub);

            int padIn = WizUi.S(14);
            int rowH = WizUi.S(28);
            int h = padIn * 2 + Math.Max(1, lines.Count) * rowH - WizUi.S(6);
            WizDraw.Card(g, new Rectangle(x, y, w, h));
            int cy = y + padIn;
            // After a failure or a cancel what had been done was undone: those lines say so instead of showing a check.
            bool undone = (phase == Phase.Failed || phase == Phase.Cancelled) && ctx.Result != null && ctx.Result.RollbackClean;
            foreach (Line l in lines)
            {
                bool wasDone = undone && l.State.HasValue && (l.State.Value == InstallStepState.Done || l.State.Value == InstallStepState.Warning);
                WizDraw.Icon(g, wasDone ? WizIcon.Off : IconFor(l), new Rectangle(x + padIn, cy, WizUi.S(18), WizUi.S(18)));
                bool active = l.State.HasValue && l.State.Value == InstallStepState.Running;
                Color c = wasDone || !l.State.HasValue ? Theme.Muted : l.State.Value == InstallStepState.Failed ? Palette.Hot : active ? Theme.Accent : Theme.Text;
                WizDraw.Line(g, InstallerText.Line(l.Step) + (wasDone ? " (desfeito)" : ""), Theme.Font(10f, active), c, x + padIn + WizUi.S(28), cy + WizUi.S(1), w - padIn * 2 - WizUi.S(28));
                cy += rowH;
            }
            y += h + WizUi.S(14);

            if (phase == Phase.Running)
            {
                double frac = lines.Count == 0 ? 0 : Math.Min(1.0, (done + 0.4) / lines.Count);
                WizDraw.Bar(g, new Rectangle(x, y, w, WizUi.S(8)), frac, 0, Theme.Accent);
                y += WizUi.S(22);
                if (cancelRequested) WizUi.Note(g, WizIcon.Warn, "Cancelando: desfazendo o que já foi feito…", Palette.Warn, x, y, w);
                return;
            }

            InstallOutcome r = ctx.Result;
            if (phase == Phase.Done)
            {
                y += WizUi.Note(g, WizIcon.Ok, "O SenseMonitor ficou em Arquivos de Programas e vai abrir sozinho quando você entrar no Windows.", Theme.Text, x, y, w);
                if (r != null)
                {
                    foreach (string wn in r.Warnings)
                    {
                        if (y > limit - WizUi.S(30)) break;
                        y += WizUi.Note(g, WizIcon.Warn, wn, Palette.Warn, x, y, w);
                    }
                }
                if (y < limit - WizUi.S(30) && Sess.Detected && (Sess.Flow.Contains(WizardStep.Key) || Sess.Flow.Contains(WizardStep.Fan) || Sess.Flow.Contains(WizardStep.Mode)))
                    WizUi.Note(g, WizIcon.Wait, "Agora vamos testar a tecla, as ventoinhas e os modos deste notebook. Cada teste pode ficar para depois.", Theme.Muted, x, y, w);
                return;
            }
            if (phase == Phase.Failed && r != null)
            {
                y += WizUi.Note(g, WizIcon.Fail, r.Error ?? "A instalação falhou.", Palette.Hot, x, y, w);
                if (r.RollbackClean)
                    y += WizUi.Note(g, WizIcon.Ok, "Nada ficou instalado: o que já tinha sido feito foi desfeito. Você pode tentar de novo ou fechar.", Theme.Text, x, y, w);
                else
                {
                    y += WizUi.Note(g, WizIcon.Warn, "Não consegui desfazer tudo. O que sobrou:", Palette.Warn, x, y, w);
                    foreach (string pr in r.RollbackProblems)
                    {
                        if (y > limit - WizUi.S(30)) break;
                        y += WizUi.Note(g, WizIcon.Warn, pr, Theme.Muted, x, y, w);
                    }
                }
            }
        }
    }

    // ---- 8. Pronto ---------------------------------------------------------------------------------------------

    // The existing summary, with the installer's closing: the result is saved into the installed config when the
    // window finishes, and "Abrir o SenseMonitor" starts the installed app through its task.
    internal sealed class InstallerSummaryStep : SummaryStep
    {
        private readonly InstallContext ctx;

        public InstallerSummaryStep(InstallContext ctx)
        {
            this.ctx = ctx;
        }

        public override string ShortName { get { return "Pronto"; } }
        protected override string HeadingTitle { get { return "Tudo pronto"; } }

        protected override string SubSuffix
        {
            get { return "SenseMonitor " + ctx.Version + " instalado"; }
        }

        public override StepUi Ui()
        {
            StepUi u = base.Ui();
            u.PrimaryText = "Abrir o SenseMonitor";
            u.BVisible = true;
            u.BText = "Concluir sem abrir";
            return u;
        }

        // The primary button: the host hands the outcome to the finished handler, and the runner opens the app after it.
        public override bool Next()
        {
            ctx.OpenRequested = true;
            return false;
        }

        public override void ActionB()
        {
            ctx.OpenRequested = false;
            host.FinishNow();
        }
    }
}
