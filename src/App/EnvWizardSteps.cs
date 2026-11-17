using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Forja.App.Controls;
using Forja.Core;

namespace Forja.App
{
    // Drawing blocks shared by the steps. Every paragraph is measured before it is drawn and the next block
    // starts below it, so nothing collides at any DPI.
    internal static class WizUi
    {
        public static int S(int v) { return Theme.S(v); }

        public static string Dash(string v)
        {
            return string.IsNullOrEmpty(v) ? "—" : v;
        }

        public static KeyValuePair<string, string> KV(string k, string v)
        {
            return new KeyValuePair<string, string>(k, v);
        }

        // Title + one muted line. Returns the height used.
        public static int Heading(Graphics g, int x, int y, int w, string title, string sub)
        {
            int h = WizDraw.Para(g, title, Theme.Font(15f, true), Theme.Text, x, y, w);
            h += S(4);
            if (!string.IsNullOrEmpty(sub)) h += WizDraw.Para(g, sub, Theme.Font(10f, false), Theme.Muted, x, y + h, w);
            return h + S(8);
        }

        // A card with a small title and label/value rows. Returns the height used.
        public static int InfoCard(Graphics g, int x, int y, int w, string title, List<KeyValuePair<string, string>> rows)
        {
            int padIn = S(14);
            int labelW = S(150);
            int valueW = w - padIn * 2 - labelW;
            Font tf = Theme.Font(9f, true);
            Font lf = Theme.Font(9.5f, false);
            int h = padIn + tf.Height + S(8);
            int[] rh = new int[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                rh[i] = Math.Max(lf.Height, WizDraw.TextHeight(g, rows[i].Value, lf, valueW)) + S(5);
                h += rh[i];
            }
            h += padIn - S(5);
            WizDraw.Card(g, new Rectangle(x, y, w, h));
            int cy = y + padIn;
            Theme.Draw(g, title.ToUpperInvariant(), tf, Theme.Muted, new Rectangle(x + padIn, cy, w - padIn * 2, tf.Height + 2), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            cy += tf.Height + S(8);
            for (int i = 0; i < rows.Count; i++)
            {
                WizDraw.Line(g, rows[i].Key, lf, Theme.Muted, x + padIn, cy, labelW - S(8));
                WizDraw.Para(g, rows[i].Value, lf, Theme.Text, x + padIn + labelW, cy, valueW);
                cy += rh[i];
            }
            return h;
        }

        // A note with an icon; wraps below it. Returns the height used.
        public static int Note(Graphics g, WizIcon icon, string text, Color color, int x, int y, int w)
        {
            int isz = S(18);
            WizDraw.Icon(g, icon, new Rectangle(x, y, isz, isz));
            int h = WizDraw.Para(g, text, Theme.Font(9.5f, false), color, x + isz + S(8), y + S(1), w - isz - S(8));
            return Math.Max(isz, h + S(2)) + S(6);
        }
    }

    internal abstract class StepBase : IWizardStep
    {
        protected IWizardHost host;

        protected EnvSession Sess { get { return host.Session; } }

        public abstract string Key { get; }
        public abstract string ShortName { get; }
        public abstract string Title { get; }
        public virtual bool Applies(EnvSession s) { return true; }
        public virtual bool CanSkip { get { return false; } }

        public void Enter(IWizardHost h)
        {
            host = h;
            OnEnter();
        }

        protected virtual void OnEnter() { }
        public virtual void Leave() { }
        public virtual void Cancel() { }
        public virtual void Tick() { }
        public abstract void Paint(Graphics g, int x, int y, int w, int limit);
        public virtual StepUi Ui() { return new StepUi(); }
        public virtual void ActionA() { }
        public virtual void ActionB() { }
        public virtual void Skip() { }
        public virtual bool Next() { return true; }
    }

    // The six steps of the environment test, in order. Hosts may put their own steps in front.
    internal static class EnvWizardSteps
    {
        public static List<IWizardStep> Standard()
        {
            List<IWizardStep> l = new List<IWizardStep>();
            l.Add(new DetectStep());
            l.Add(new ConflictsStep());
            l.Add(new KeyStep());
            l.Add(new FanStep());
            l.Add(new ModeStep());
            l.Add(new SummaryStep());
            return l;
        }
    }

    // One of the six steps of WizardFlow: applies when the flow (decided from the detection) lists it.
    internal abstract class CoreStep : StepBase
    {
        protected abstract WizardStep Id { get; }

        public override bool Applies(EnvSession s)
        {
            return Id == WizardStep.Detect || (s.Detected && s.Flow.Contains(Id));
        }

        public override string Title { get { return WizardFlow.Title(Id); } }
    }

    // ---- 1. Reconhecendo ------------------------------------------------------------------------------------

    // Read-only detection. Works before the app is installed (running from the Downloads folder) and needs no
    // config: everything comes from WMI and the firmware's own announcements.
    internal sealed class DetectStep : CoreStep
    {
        protected override WizardStep Id { get { return WizardStep.Detect; } }
        public override string Key { get { return "detect"; } }
        public override string ShortName { get { return "Notebook"; } }

        protected override void OnEnter()
        {
            if (Sess.Detected || Sess.Detecting) return;
            Sess.Detecting = true;
            DetectionRaw r = null;
            List<ConflictItem> c = null;
            IWizardHost h = host;
            EnvSession s = Sess;
            Thread t = new Thread(delegate()
            {
                try
                {
                    r = s.Svc.Detect();
                    c = s.Svc.ScanConflicts();
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                }
                h.Post(delegate
                {
                    s.ApplyDetection(r, c);
                    h.Changed();
                });
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.EnvWizard.Detect";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.PrimaryEnabled = Sess.Detected;
            return u;
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            EnvSession s = Sess;
            if (!s.Detected)
            {
                y += WizUi.Heading(g, x, y, w, "Reconhecendo seu notebook…", "Só leitura: nada é alterado nesta etapa.");
                y += WizUi.S(10);
                WizDraw.Bar(g, new Rectangle(x, y, w, WizUi.S(8)), -1, host.Anim, Theme.Accent);
                return;
            }
            DetectionRaw raw = s.Raw;
            DetectionResult det = s.Det;
            y += WizUi.Heading(g, x, y, w, "Seu notebook", WizardText.ModelKnowledge(det));
            string gpus = raw.Gpus.Count == 0 ? "—" : string.Join(", ", raw.Gpus.ToArray());
            List<KeyValuePair<string, string>> a = new List<KeyValuePair<string, string>>();
            a.Add(WizUi.KV("Modelo", WizUi.Dash(raw.Model)));
            a.Add(WizUi.KV("BIOS", WizUi.Dash(raw.Bios)));
            a.Add(WizUi.KV("Processador", WizUi.Dash(raw.Cpu)));
            a.Add(WizUi.KV("Placa de vídeo", gpus));
            a.Add(WizUi.KV("Memória", raw.RamBytes > 0 ? Display.Gib(raw.RamBytes) + " GB" : "—"));
            y += WizUi.InfoCard(g, x, y, w, "O que encontrei", a) + WizUi.S(10);

            List<KeyValuePair<string, string>> b = new List<KeyValuePair<string, string>>();
            if (det.IsAcer && det.HasGamingInterface)
            {
                b.Add(WizUi.KV("Sensores", WizardText.AnnouncedSensors(det)));
                b.Add(WizUi.KV("Modos", WizardText.AnnouncedModes(det)));
                b.Add(WizUi.KV("Bateria", raw.BatteryClass ? "interface de limite de carga presente" : "interface de limite de carga ausente"));
                b.Add(WizUi.KV("Luz do teclado", "o SenseMonitor não controla a luz"));
            }
            else
            {
                b.Add(WizUi.KV("Controles da Acer", det.IsAcer ? (raw.FirmwareClass ? "o firmware não respondeu" : "este Acer não tem a interface gamer") : "não é um notebook Acer"));
            }
            y += WizUi.InfoCard(g, x, y, w, "O que o firmware anuncia", b) + WizUi.S(10);

            if (!det.IsAcer || !raw.FirmwareClass)
                y += WizUi.Note(g, WizIcon.Warn, "Este notebook não expõe os controles da Acer. O SenseMonitor vai funcionar só como monitor, saúde e manutenção.", Palette.Warn, x, y, w);
            else if (!raw.FirmwareAnswered)
                y += WizUi.Note(g, WizIcon.Warn, "O firmware não respondeu. O SenseMonitor precisa estar aberto como administrador.", Palette.Warn, x, y, w);
            if (s.Svc.ReadOnly)
                y += WizUi.Note(g, WizIcon.Warn, "Modo somente leitura ligado: as etapas de ventoinhas e modos serão puladas.", Palette.Warn, x, y, w);
            if (det.Table != null && det.Table.Quirks != null)
            {
                foreach (string q in det.Table.Quirks)
                {
                    if (y > limit - WizUi.S(30)) break;
                    y += WizUi.Note(g, WizIcon.Wait, q, Theme.Muted, x, y, w);
                }
            }
        }
    }

    // ---- 2. Conflitos -----------------------------------------------------------------------------------------

    internal sealed class ConflictsStep : CoreStep
    {
        private bool busy;
        private bool acted;

        protected override WizardStep Id { get { return WizardStep.Conflicts; } }
        public override string Key { get { return "conflicts"; } }
        public override string ShortName { get { return "Programas"; } }
        public override bool CanSkip { get { return true; } }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.SkipVisible = !acted;
            u.AVisible = true;
            u.AText = acted ? "Serviços tratados" : "Parar e desativar esses serviços";
            u.AEnabled = !acted;
            u.PrimaryEnabled = acted;
            return u;
        }

        public override void ActionA()
        {
            if (busy || acted) return;
            busy = true;
            DisableOutcome outcome = null;
            List<ConflictItem> after = null;
            EnvSession s = Sess;
            host.RunWorker("Services", delegate
            {
                outcome = s.Svc.DisableConflicts();
                after = s.Svc.ScanConflicts();
            }, delegate
            {
                busy = false;
                acted = true;
                s.DisableOutcome = outcome;
                if (after != null) s.Conflicts = after;
            });
        }

        private static string StartText(string mode)
        {
            if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase)) return "desativado";
            if (string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase)) return "manual";
            return "automático";
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            EnvSession s = Sess;
            y += WizUi.Heading(g, x, y, w, "Programas da Acer", "Eles podem disputar as ventoinhas e os modos com o SenseMonitor.");
            y += WizDraw.Para(g, ConflictServices.Explanation, Theme.Font(10f, false), Theme.Text, x, y, w) + WizUi.S(12);

            int padIn = WizUi.S(14);
            int rowH = WizUi.S(26);
            int h = padIn * 2 + Math.Max(1, s.Conflicts.Count) * rowH - WizUi.S(4);
            WizDraw.Card(g, new Rectangle(x, y, w, h));
            int cy = y + padIn;
            if (s.Conflicts.Count == 0)
                WizDraw.Line(g, "Nenhum serviço da Acer encontrado.", Theme.Font(10f, false), Theme.Muted, x + padIn, cy, w - padIn * 2);
            foreach (ConflictItem c in s.Conflicts)
            {
                WizDraw.Icon(g, !c.Recognized ? WizIcon.Off : c.NeedsAction ? WizIcon.Warn : WizIcon.Ok, new Rectangle(x + padIn, cy, WizUi.S(18), WizUi.S(18)));
                string state = !c.Recognized ? "não reconhecido como da Acer · não vou mexer"
                    : (c.Running ? "rodando" : "parado") + " · início " + StartText(c.StartMode);
                WizDraw.Line(g, c.DisplayName, Theme.Font(10f, false), Theme.Text, x + padIn + WizUi.S(28), cy + WizUi.S(1), w / 2 - WizUi.S(40));
                WizDraw.Line(g, state, Theme.Font(9.5f, false), Theme.Muted, x + w / 2, cy + WizUi.S(1), w / 2 - padIn);
                cy += rowH;
            }
            y += h + WizUi.S(12);

            if (busy)
            {
                WizDraw.Para(g, "Parando e desativando os serviços…", Theme.Font(10f, true), Theme.Accent, x, y, w);
                WizDraw.Bar(g, new Rectangle(x, y + WizUi.S(24), w, WizUi.S(8)), -1, host.Anim, Theme.Accent);
            }
            else if (acted && s.DisableOutcome != null)
            {
                DisableOutcome o = s.DisableOutcome;
                string t = o.Disabled.ToString(CultureInfo.InvariantCulture) + " serviço(s) parado(s) e desativado(s)."
                    + (o.Failed > 0 ? " " + o.Failed.ToString(CultureInfo.InvariantCulture) + " não deu certo." : "");
                y += WizUi.Note(g, o.Failed > 0 ? WizIcon.Warn : WizIcon.Ok, t, o.Failed > 0 ? Palette.Warn : Theme.Text, x, y, w);
                foreach (string m in o.Messages)
                {
                    if (y > limit - WizUi.S(24)) break;
                    y += WizUi.Note(g, WizIcon.Warn, m, Theme.Muted, x, y, w);
                }
                if (y <= limit - WizUi.S(24))
                    WizUi.Note(g, WizIcon.Ok, "Para desfazer, abra Configurações e use “Reativar serviços da Acer”.", Theme.Muted, x, y, w);
            }
            else
            {
                WizUi.Note(g, WizIcon.Wait, "Só mexo nisso se você clicar no botão abaixo. Nada é desinstalado. “Fazer depois” deixa tudo como está.", Theme.Muted, x, y, w);
            }
        }
    }

    // ---- 3. Tecla ---------------------------------------------------------------------------------------------

    internal sealed class KeyStep : CoreStep
    {
        private enum KeyPhase { Waiting, Done, NoKey, Timeout, Unavailable }
        private const int TimeoutMs = 45000;

        private KeyPhase phase = KeyPhase.Waiting;
        private readonly KeyTestState state = new KeyTestState();
        private IDisposable listen;
        private int startTick;

        protected override WizardStep Id { get { return WizardStep.Key; } }
        public override string Key { get { return "key"; } }
        public override string ShortName { get { return "Tecla"; } }
        public override bool CanSkip { get { return true; } }

        protected override void OnEnter()
        {
            Start();
        }

        private void Start()
        {
            Stop();
            state.Reset();
            phase = KeyPhase.Waiting;
            startTick = Environment.TickCount;
            Sess.Results.Key = new KeyTestResult();
            listen = Sess.Svc.ListenKey(KeyCandidates.All, OnPressed);
            if (listen == null) phase = KeyPhase.Unavailable;
        }

        private void Stop()
        {
            IDisposable k = listen;
            listen = null;
            if (k != null)
            {
                try { k.Dispose(); }
                catch (Exception) { }
            }
        }

        public override void Leave() { Stop(); }
        public override void Cancel() { }

        // UI thread (the hook posts here). Only the candidate codes ever reach this point.
        private void OnPressed(KeyCandidate c)
        {
            if (phase != KeyPhase.Waiting) return;
            state.Pressed(c);
            if (state.Complete)
            {
                Stop();
                EnvResultMap.KeyPassed(Sess.Results, state.Chosen);
                phase = KeyPhase.Done;
            }
            host.Changed();
        }

        public override void Tick()
        {
            if (phase == KeyPhase.Waiting && unchecked(Environment.TickCount - startTick) >= TimeoutMs)
            {
                Stop();
                EnvResultMap.KeyTimeout(Sess.Results);
                phase = KeyPhase.Timeout;
                host.Changed();
            }
        }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.SkipVisible = true;
            u.BVisible = true;
            u.BText = "Meu notebook não tem essa tecla";
            u.BEnabled = phase != KeyPhase.NoKey && phase != KeyPhase.Done;
            u.AVisible = phase == KeyPhase.Timeout || phase == KeyPhase.Unavailable;
            u.AText = "Tentar de novo";
            u.PrimaryEnabled = phase != KeyPhase.Waiting;
            return u;
        }

        public override void ActionA()
        {
            Start();
        }

        public override void ActionB()
        {
            Stop();
            EnvResultMap.KeyNone(Sess.Results);
            phase = KeyPhase.NoKey;
        }

        public override void Skip()
        {
            Stop();
            Sess.Results.Key.Status = StepStatus.Skipped;
            Sess.Results.Key.CandidateId = null;
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            y += WizUi.Heading(g, x, y, w, "Tecla do teclado", "Para o SenseMonitor abrir quando você apertar o botão do Nitro ou do Predator.");
            y += WizDraw.Para(g, "Aperte a tecla NitroSense (ou PredatorSense) 3 vezes.", Theme.Font(13f, true), Theme.Text, x, y, w) + WizUi.S(14);

            int d = WizUi.S(44);
            int gap = WizUi.S(18);
            int cx = x;
            for (int i = 0; i < KeyTestState.Target; i++)
            {
                Rectangle c = new Rectangle(cx, y, d, d);
                bool on = state.Presses > i || phase == KeyPhase.Done;
                if (on)
                {
                    Theme.FillRound(g, Palette.Ok, c, d / 2);
                    using (Pen p = new Pen(Theme.OnAccent, Math.Max(2f, WizUi.S(3))))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.DrawLines(p, new Point[] { new Point(c.X + d * 30 / 100, c.Y + d / 2), new Point(c.X + d * 45 / 100, c.Y + d * 66 / 100), new Point(c.X + d * 70 / 100, c.Y + d * 35 / 100) });
                        g.SmoothingMode = old;
                    }
                }
                else
                {
                    Theme.DrawRound(g, phase == KeyPhase.Waiting ? Theme.Accent : Palette.Line2, c, d / 2);
                    Theme.Draw(g, (i + 1).ToString(CultureInfo.InvariantCulture), Theme.Font(13f, true), Theme.Muted, c,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
                cx += d + gap;
            }
            y += d + WizUi.S(14);

            string status;
            Color sc = Theme.Muted;
            switch (phase)
            {
                case KeyPhase.Done:
                    status = "Pronto: a tecla respondeu" + (state.Chosen != null ? " (" + state.Chosen.Description + ")." : ".");
                    sc = Palette.Ok;
                    break;
                case KeyPhase.NoKey:
                    status = "Anotado: este notebook não tem essa tecla.";
                    break;
                case KeyPhase.Timeout:
                    status = "Nenhuma tecla conhecida chegou em " + (TimeoutMs / 1000).ToString(CultureInfo.InvariantCulture) + " segundos. Você pode tentar de novo, deixar para depois ou dizer que não tem a tecla.";
                    sc = Palette.Warn;
                    break;
                case KeyPhase.Unavailable:
                    status = "Não consegui ouvir o teclado agora. Tente de novo ou deixe para depois.";
                    sc = Palette.Warn;
                    break;
                default:
                    int left = Math.Max(0, (TimeoutMs - unchecked(Environment.TickCount - startTick)) / 1000);
                    status = "Esperando… recebi " + state.Presses.ToString(CultureInfo.InvariantCulture) + " de " + KeyTestState.Target.ToString(CultureInfo.InvariantCulture)
                        + ". Tempo restante: " + left.ToString(CultureInfo.InvariantCulture) + " s.";
                    sc = Theme.Text;
                    break;
            }
            y += WizDraw.Para(g, status, Theme.Font(10.5f, false), sc, x, y, w) + WizUi.S(10);

            string advice = Sess.Det == null ? "" : WizardText.KeyAdvice(Sess.Det);
            if (advice.Length > 0 && y < limit - WizUi.S(30)) y += WizUi.Note(g, WizIcon.Wait, advice, Theme.Muted, x, y, w);
            if (y < limit - WizUi.S(24))
                WizUi.Note(g, WizIcon.Ok, "Só o código dessa tecla é observado. Nenhuma outra tecla é lida, registrada ou guardada.", Theme.Muted, x, Math.Min(y, limit - WizUi.S(26)), w);
        }
    }

    // ---- 4. Ventoinhas ----------------------------------------------------------------------------------------

    internal sealed class FanStep : CoreStep
    {
        private enum Phase { Intro, Running, Done }

        private Phase phase = Phase.Intro;
        private FanProgress progress;
        private FanRun run;
        private FanTestResult result;

        protected override WizardStep Id { get { return WizardStep.Fan; } }
        public override string Key { get { return "fan"; } }
        public override string ShortName { get { return "Ventoinhas"; } }
        public override bool CanSkip { get { return true; } }

        public override void Cancel()
        {
            if (host != null) Sess.Svc.CancelFanTest();
        }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.SkipVisible = true;
            u.AVisible = phase == Phase.Intro || (phase == Phase.Done && result != null && result.Status != StepStatus.Passed);
            u.AText = phase == Phase.Intro ? "Começar o teste das ventoinhas" : "Repetir o teste";
            u.PrimaryEnabled = phase == Phase.Done;
            return u;
        }

        public override void ActionA()
        {
            FanLayout layout = Sess.Pre == null ? null : Sess.Pre.Layout;
            if (layout == null) return;
            phase = Phase.Running;
            progress = null;
            run = null;
            FanRun r = null;
            EnvSession s = Sess;
            IWizardHost h = host;
            s.Svc.AcquireFirmware(); // the tray's own writers pause until this step is over
            host.RunWorker("Fans", delegate
            {
                r = s.Svc.RunFanTest(layout, delegate(FanProgress p) { h.Post(delegate { progress = p; h.Changed(); }); });
            }, delegate
            {
                run = r ?? new FanRun();
                result = run.Report != null ? EnvResultMap.Fan(run.Report) : EnvResultMap.FanError(run.Error ?? "o teste não rodou");
                s.Results.Fan = result;
                phase = Phase.Done;
                s.Svc.ReleaseFirmware();
            });
        }

        public override void Skip()
        {
            Sess.Results.Fan.Status = StepStatus.Skipped;
        }

        private static string RpmText(int? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " RPM" : "    — RPM";
        }

        private static string FanLine(FanCheck c)
        {
            string s = "automático " + c.Baseline.ToString(CultureInfo.InvariantCulture) + " · 50% " + c.Pct.ToString(CultureInfo.InvariantCulture)
                + " · máximo " + c.Max.ToString(CultureInfo.InvariantCulture) + " RPM";
            return c.Passed ? s + " — ok" : s + " — " + (c.Reason ?? "falhou");
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            FanLayout l = Sess.Pre == null ? null : Sess.Pre.Layout;
            string what = l == null ? "" : (l.Count == 2 ? "Vou testar as 2 ventoinhas (CPU e GPU)." : "Vou testar a ventoinha do notebook (" + (l.Cpu ? "CPU" : "GPU") + ").");
            y += WizUi.Heading(g, x, y, w, "Ventoinhas", what);

            if (phase == Phase.Intro)
            {
                y += WizDraw.Para(g, "As ventoinhas vão trocar de velocidade e ficar no máximo por alguns segundos. Fazer barulho é normal. Escute: você vai notar o som mudando.",
                    Theme.Font(10.5f, false), Theme.Text, x, y, w) + WizUi.S(12);
                List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
                rows.Add(WizUi.KV("1. Automático", "mede o ruído de referência"));
                rows.Add(WizUi.KV("2. 50%", "o som deve mudar um pouco"));
                rows.Add(WizUi.KV("3. Máximo", "o barulho deve subir bastante"));
                rows.Add(WizUi.KV("4. Automático", "o barulho vai baixando aos poucos"));
                y += WizUi.InfoCard(g, x, y, w, "Como vai ser (cerca de 40 segundos)", rows) + WizUi.S(12);
                y += WizUi.Note(g, WizIcon.Warn, "Se a temperatura chegar a 90 °C ou algo falhar, as ventoinhas voltam ao automático na hora. O teste sempre termina no automático.", Palette.Warn, x, y, w);
                if (y < limit - WizUi.S(24)) WizUi.Note(g, WizIcon.Wait, "Deixe o carregador ligado e o notebook em uma superfície firme. “Fazer depois” mantém o controle manual bloqueado.", Theme.Muted, x, y, w);
                return;
            }
            if (phase == Phase.Running)
            {
                if (progress == null)
                {
                    y += WizDraw.Para(g, "Preparando o teste…", Theme.Font(12f, true), Theme.Accent, x, y, w) + WizUi.S(10);
                    WizDraw.Bar(g, new Rectangle(x, y, w, WizUi.S(10)), -1, host.Anim, Theme.Accent);
                    return;
                }
                y += WizDraw.Para(g, progress.Text, Theme.Font(12f, true), Theme.Text, x, y, w) + WizUi.S(14);
                WizDraw.Bar(g, new Rectangle(x, y, w, WizUi.S(10)), progress.Percent / 100.0, 0, Theme.Accent);
                y += WizUi.S(18);
                WizDraw.Line(g, progress.Percent.ToString(CultureInfo.InvariantCulture) + "%  ·  " + (progress.ElapsedMs / 1000).ToString(CultureInfo.InvariantCulture)
                    + " s de " + (progress.TotalMs / 1000).ToString(CultureInfo.InvariantCulture) + " s", Theme.Font(9f, false), Theme.Muted, x, y, w);
                y += WizUi.S(26);
                string rpm = (l != null && l.Cpu ? "CPU  " + RpmText(progress.CpuRpm) : "") + (l != null && l.Cpu && l.Gpu ? "      " : "")
                    + (l != null && l.Gpu ? "GPU  " + RpmText(progress.GpuRpm) : "");
                WizDraw.Line(g, rpm, WizDraw.Mono(12f), Theme.Text, x, y, w);
                y += WizUi.S(34);
                WizUi.Note(g, WizIcon.Warn, "Não feche nem suspenda o computador. Se algo passar de 90 °C, volto ao automático na hora.", Palette.Warn, x, y, w);
                return;
            }

            FanTestResult r = result ?? new FanTestResult();
            if (r.Status == StepStatus.Passed)
                y += WizUi.Note(g, WizIcon.Ok, "As ventoinhas seguiram os comandos. O controle manual será liberado quando você concluir.", Theme.Text, x, y, w);
            else if (r.Status == StepStatus.Failed)
                y += WizUi.Note(g, WizIcon.Fail, "O teste das ventoinhas falhou: " + (r.Reason ?? "sem detalhes") + ". O controle manual continua bloqueado.", Palette.Hot, x, y, w);
            else
                y += WizUi.Note(g, WizIcon.Warn, "O teste não terminou: " + (r.Reason ?? "sem detalhes") + ". O controle manual continua bloqueado.", Palette.Warn, x, y, w);
            y += WizUi.S(6);
            if (run != null && run.Report != null)
            {
                List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
                if (run.Report.Cpu != null) rows.Add(WizUi.KV("CPU", FanLine(run.Report.Cpu)));
                if (run.Report.Gpu != null) rows.Add(WizUi.KV("GPU", FanLine(run.Report.Gpu)));
                if (rows.Count > 0 && y < limit - WizUi.S(80)) y += WizUi.InfoCard(g, x, y, w, "Rotação medida", rows) + WizUi.S(10);
            }
            if (y < limit - WizUi.S(24)) WizUi.Note(g, WizIcon.Ok, "As ventoinhas ficam no automático ao fim do teste.", Theme.Muted, x, y, w);
        }
    }

    // ---- 5. Modos ---------------------------------------------------------------------------------------------

    internal sealed class ModeStep : CoreStep
    {
        private enum Phase { Intro, Running, Done }

        private Phase phase = Phase.Intro;
        private ModeTestProgress progress;
        private ModeTestOutcome outcome;

        protected override WizardStep Id { get { return WizardStep.Mode; } }
        public override string Key { get { return "mode"; } }
        public override string ShortName { get { return "Modos"; } }
        public override bool CanSkip { get { return true; } }

        public override void Cancel()
        {
            if (host != null) Sess.Svc.CancelModeTest();
        }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.SkipVisible = true;
            u.AVisible = phase == Phase.Intro;
            u.AText = "Começar o teste dos modos";
            u.PrimaryEnabled = phase == Phase.Done;
            return u;
        }

        public override void ActionA()
        {
            phase = Phase.Running;
            progress = null;
            outcome = null;
            ModeTestOutcome o = null;
            EnvSession s = Sess;
            IWizardHost h = host;
            s.Svc.AcquireFirmware(); // the tray's own writers pause until this step is over
            host.RunWorker("Modes", delegate
            {
                o = s.Svc.RunModeTest(s.ModeCandidates, delegate(PerfMode m) { return Confirm(m); },
                    delegate(ModeTestProgress p) { h.Post(delegate { progress = p; h.Changed(); }); });
            }, delegate
            {
                outcome = o ?? new ModeTestOutcome();
                EnvResultMap.Modes(outcome, s.Results);
                phase = Phase.Done;
                s.Svc.ReleaseFirmware();
            });
        }

        // Worker thread: Turbo needs a second, explicit yes before it is tried.
        private bool Confirm(PerfMode m)
        {
            if (m != PerfMode.Turbo) return true;
            return host.ConfirmOnUi(
                "O modo Turbo faz o notebook trabalhar no limite: muito mais barulho e calor.\n\n"
                + "• Deixe o carregador ligado e a bateria instalada.\n"
                + "• Se o notebook esquentar demais ou ficar instável, o teste volta ao Dia a dia sozinho.\n\n"
                + "Quer testar o Turbo agora?", "Confirmar o teste do Turbo");
        }

        public override void Skip()
        {
            foreach (PerfMode m in Sess.ModeCandidates) Sess.Results.SetMode(m, StepStatus.Skipped, false);
            Sess.Results.SetMode(PerfMode.DiaADia, StepStatus.Skipped, false);
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            y += WizUi.Heading(g, x, y, w, "Modos", "Vou trocar para cada modo que o notebook anuncia e conferir se o firmware confirmou.");

            if (phase == Phase.Intro)
            {
                // the real order of the steps (the first one moves aside when it is the mode in use now)
                PerfMode? now = Sess.Raw.ProfileCode.HasValue ? ModeCatalog.FromCode(Sess.Raw.ProfileCode.Value) : null;
                List<string> names = new List<string>();
                foreach (PerfMode m in ModeTestPlan.Order(now, Sess.ModeCandidates)) names.Add(ModeCatalog.Name(m));
                y += WizDraw.Para(g, "Ordem do teste: " + string.Join(" → ", names.ToArray()) + ". Cada troca é lida de volta no firmware. Termino sempre no Dia a dia.",
                    Theme.Font(10.5f, false), Theme.Text, x, y, w) + WizUi.S(12);
                y += WizUi.Note(g, WizIcon.Wait, "Cada troca leva alguns segundos. Escute: o ruído das ventoinhas pode mudar.", Theme.Muted, x, y, w);
                if (Sess.ModeCandidates.Contains(PerfMode.Turbo))
                    y += WizUi.Note(g, WizIcon.Warn, "O Turbo vai pedir uma confirmação extra antes de ser testado. Use o carregador, com a bateria instalada.", Palette.Warn, x, y, w);
                else
                    y += WizUi.Note(g, WizIcon.Wait, "Deixe o carregador ligado durante o teste.", Theme.Muted, x, y, w);
                CapabilityProfile pre = Sess.Pre;
                if (pre != null && pre.State(Cap.ModeTurbo) == CapState.Unavailable && y < limit - WizUi.S(24))
                    y += WizUi.Note(g, WizIcon.Off, "O Turbo não será testado: " + WizardText.ShortReason(Cap.ModeTurbo, CapState.Unavailable, pre.Reason(Cap.ModeTurbo)).TrimEnd('.') + ".", Theme.Muted, x, y, w);
                if (y < limit - WizUi.S(24)) WizUi.Note(g, WizIcon.Wait, "“Fazer depois” deixa os modos bloqueados até você refazer o teste.", Theme.Muted, x, y, w);
                return;
            }
            if (phase == Phase.Running)
            {
                if (progress == null)
                {
                    y += WizDraw.Para(g, "Preparando o teste…", Theme.Font(12f, true), Theme.Accent, x, y, w) + WizUi.S(10);
                    WizDraw.Bar(g, new Rectangle(x, y, w, WizUi.S(10)), -1, host.Anim, Theme.Accent);
                    return;
                }
                y += WizDraw.Para(g, progress.Text, Theme.Font(12f, true), Theme.Text, x, y, w) + WizUi.S(14);
                WizDraw.Bar(g, new Rectangle(x, y, w, WizUi.S(10)), (progress.Index + 0.5) / Math.Max(1, progress.Total), 0, Theme.Accent);
                y += WizUi.S(24);
                WizUi.Note(g, WizIcon.Warn, "Não feche nem suspenda o computador. Se você cancelar, volto ao Dia a dia.", Palette.Warn, x, y, w);
                return;
            }

            ModeTestOutcome o = outcome ?? new ModeTestOutcome();
            int padIn = WizUi.S(14);
            int rowH = WizUi.S(26);
            int rows = Math.Max(1, o.Steps.Count);
            int h = padIn * 2 + rows * rowH - WizUi.S(4);
            WizDraw.Card(g, new Rectangle(x, y, w, h));
            int cy = y + padIn;
            foreach (ModeStepResult s in o.Steps)
            {
                WizIcon ic = s.Status == StepStatus.Passed && s.Proven ? WizIcon.Ok : s.Status == StepStatus.Failed ? WizIcon.Fail : s.Status == StepStatus.Skipped ? WizIcon.Off : WizIcon.Warn;
                string text = s.Status == StepStatus.Passed ? (s.Proven ? "o firmware trocou e confirmou" : "aceito, mas o modo já era esse: sem prova")
                    : s.Status == StepStatus.Failed ? "o firmware não confirmou na leitura" : "pulado";
                WizDraw.Icon(g, ic, new Rectangle(x + padIn, cy, WizUi.S(18), WizUi.S(18)));
                WizDraw.Line(g, ModeCatalog.Name(s.Mode), Theme.Font(10f, true), Theme.Text, x + padIn + WizUi.S(28), cy + WizUi.S(1), WizUi.S(150));
                WizDraw.Line(g, text, Theme.Font(9.5f, false), Theme.Muted, x + padIn + WizUi.S(190), cy + WizUi.S(1), w - padIn * 2 - WizUi.S(190));
                cy += rowH;
            }
            y += h + WizUi.S(12);
            if (o.EndedBalanced) y += WizUi.Note(g, WizIcon.Ok, "Terminei no modo Dia a dia.", Theme.Text, x, y, w);
            else y += WizUi.Note(g, WizIcon.Warn, "Não consegui confirmar o Dia a dia no fim. Confira o modo atual na página Modos.", Palette.Warn, x, y, w);
            if (o.Cancelled) y += WizUi.Note(g, WizIcon.Warn, "O teste foi interrompido: os modos que não rodaram continuam bloqueados.", Palette.Warn, x, y, w);
        }
    }

    // ---- 6. Resumo --------------------------------------------------------------------------------------------

    internal class SummaryStep : CoreStep
    {
        private string exportNote;

        protected override WizardStep Id { get { return WizardStep.Summary; } }
        public override string Key { get { return "summary"; } }
        public override string ShortName { get { return "Resumo"; } }
        // The installer's last screen reuses this one with its own heading and closing note.
        protected virtual string HeadingTitle { get { return "Resumo"; } }
        protected virtual string SubSuffix { get { return null; } }

        protected override void OnEnter()
        {
            Sess.BuildFinal();
        }

        public override StepUi Ui()
        {
            StepUi u = new StepUi();
            u.PrimaryText = "Concluir";
            u.AVisible = true;
            u.AText = "Exportar relatório do modelo";
            return u;
        }

        // The host hands the outcome to its finished handler (it closes the window when that worked).
        public override bool Next() { return false; }

        public override void ActionA()
        {
            EnvSession s = Sess;
            if (s.Final == null || s.Det == null) return;
            string name = EnvReport.SuggestedFileName(s.Raw, DateTime.UtcNow);
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Exportar relatório do modelo";
                dlg.Filter = "Relatório de texto (*.txt)|*.txt|Relatório em JSON (*.json)|*.json";
                dlg.FileName = name;
                dlg.DefaultExt = "txt";
                dlg.OverwritePrompt = true;
                if (dlg.ShowDialog(host.DialogOwner) != DialogResult.OK) return;
                try
                {
                    string profile = null;
                    string user = null;
                    try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); user = Environment.UserName; }
                    catch (Exception) { }
                    EnvReport rep = EnvReport.Build(s.Det, s.Final, s.Results, s.Conflicts, s.Svc.AppVersion, EnvSession.NowUtc(), profile, user);
                    bool json = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(dlg.FileName, json ? rep.Json : rep.Text, new UTF8Encoding(true));
                    exportNote = "Relatório salvo. Ele não tem número de série, nome de usuário nem pastas pessoais.";
                    s.Svc.Journal("relatório do modelo exportado");
                }
                catch (Exception)
                {
                    exportNote = "Não foi possível salvar o relatório nesse local.";
                }
            }
        }

        public override void Paint(Graphics g, int x, int y, int w, int limit)
        {
            EnvSession s = Sess;
            CapabilityProfile p = s.Final;
            if (p == null) return;
            DetectionRaw raw = s.Raw;
            string sub = WizUi.Dash(raw.Model) + "  ·  BIOS " + WizUi.Dash(raw.Bios)
                + (p.FanCount > 0 ? "  ·  " + p.FanCount.ToString(CultureInfo.InvariantCulture) + (p.FanCount == 1 ? " ventoinha" : " ventoinhas") : "");
            y += WizUi.Heading(g, x, y, w, HeadingTitle, SubSuffix == null ? sub : sub + "  ·  " + SubSuffix);

            List<SummaryRow> rows = WizardText.Summary(p);
            List<SummaryRow> on = rows.FindAll(delegate(SummaryRow r) { return r.Enabled; });
            List<SummaryRow> off = rows.FindAll(delegate(SummaryRow r) { return !r.Enabled; });
            int rowH = WizUi.S(23);
            int nameW = WizUi.S(250);
            Font hf = Theme.Font(9f, true);
            Font nf = Theme.Font(10f, false);
            Font sf = Theme.Font(9.5f, false);

            WizDraw.Line(g, "LIBERADO", hf, Palette.Ok, x, y, w);
            y += hf.Height + WizUi.S(6);
            if (on.Count == 0)
            {
                WizDraw.Line(g, "Nada foi liberado neste notebook.", sf, Theme.Muted, x + WizUi.S(28), y, w - WizUi.S(28));
                y += rowH;
            }
            foreach (SummaryRow r in on)
            {
                WizDraw.Icon(g, WizIcon.Ok, new Rectangle(x, y, WizUi.S(18), WizUi.S(18)));
                WizDraw.Line(g, r.Name, nf, Theme.Text, x + WizUi.S(28), y + WizUi.S(1), nameW - WizUi.S(28));
                WizDraw.Line(g, r.Short, sf, Theme.Muted, x + nameW, y + WizUi.S(1), w - nameW);
                y += rowH;
            }
            y += WizUi.S(8);
            WizDraw.Line(g, "BLOQUEADO OU INDISPONÍVEL", hf, Palette.Warn, x, y, w);
            y += hf.Height + WizUi.S(6);
            foreach (SummaryRow r in off)
            {
                WizIcon ic = r.State == CapState.Unavailable ? WizIcon.Off : WizIcon.Warn;
                WizDraw.Icon(g, ic, new Rectangle(x, y, WizUi.S(18), WizUi.S(18)));
                WizDraw.Line(g, r.Name, nf, Theme.Text, x + WizUi.S(28), y + WizUi.S(1), nameW - WizUi.S(28));
                WizDraw.Line(g, r.Short, sf, Theme.Muted, x + nameW, y + WizUi.S(1), w - nameW);
                y += rowH;
            }
            y += WizUi.S(6);
            if ((!s.Det.IsAcer || !s.Det.HasGamingInterface) && y < limit - WizUi.S(20))
                y += WizUi.Note(g, WizIcon.Ok, "Monitor, saúde e manutenção continuam funcionando normalmente.", Theme.Muted, x, y, w);
            if (p.PendingTests().Count > 0 && y < limit - WizUi.S(20))
                y += WizUi.Note(g, WizIcon.Wait, "Alguns testes ficaram para depois: o SenseMonitor avisa na primeira vez que abrir.", Theme.Muted, x, y, w);
            if (s.DisableOutcome != null && s.DisableOutcome.Disabled > 0 && y < limit - WizUi.S(20))
                y += WizUi.Note(g, WizIcon.Ok, s.DisableOutcome.Disabled.ToString(CultureInfo.InvariantCulture) + " serviço(s) da Acer desativado(s); dá para desfazer em Configurações.", Theme.Muted, x, y, w);
            if (s.SaveFailed && y < limit - WizUi.S(20))
                y += WizUi.Note(g, WizIcon.Fail, "Não foi possível salvar o resultado. Tente concluir de novo.", Palette.Hot, x, y, w);
            if (exportNote != null && y < limit - WizUi.S(20))
                WizUi.Note(g, WizIcon.Ok, exportNote, Theme.Muted, x, y, w);
        }
    }
}
