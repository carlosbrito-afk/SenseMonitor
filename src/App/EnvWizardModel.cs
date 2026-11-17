using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // What a finished environment test hands to whoever hosts it: the standalone wizard saves it into the
    // config at once; the installer keeps it and saves it AFTER the app is installed (no config exists while
    // the test runs from the Downloads folder).
    internal sealed class EnvOutcome
    {
        public CapabilityProfile Profile;
        public EnvTestResults Results;
        public DetectionRaw Raw;
        public List<ConflictItem> Conflicts = new List<ConflictItem>();
        // Services stopped before a config existed (empty when the services layer wrote them into a config itself).
        public List<ServiceUndo> ServiceUndo = new List<ServiceUndo>();

        // Guided tests left for later ("Fazer depois"): the app reminds once on its first open.
        public bool AnyPending { get { return Profile != null && Profile.PendingTests().Count > 0; } }
    }

    // State shared by the steps of one run. Created by the host form; independent of any config file.
    internal sealed class EnvSession
    {
        public readonly IEnvServices Svc;
        public bool Detecting;
        public bool SaveFailed;
        public bool Detected;
        public DetectionRaw Raw = new DetectionRaw();
        public DetectionResult Det;
        public CapabilityProfile Pre;
        public List<ConflictItem> Conflicts = new List<ConflictItem>();
        // Which of the six environment steps apply to this notebook (WizardFlow).
        public List<WizardStep> Flow = new List<WizardStep>();
        public readonly EnvTestResults Results = new EnvTestResults();
        public List<PerfMode> ModeCandidates = new List<PerfMode>();
        public CapabilityProfile Final;
        public DisableOutcome DisableOutcome;

        public EnvSession(IEnvServices svc)
        {
            Svc = svc;
            Results.ReadOnly = svc.ReadOnly;
        }

        public static string NowUtc()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }

        public void ApplyDetection(DetectionRaw raw, List<ConflictItem> conflicts)
        {
            Raw = raw ?? new DetectionRaw();
            Conflicts = conflicts ?? new List<ConflictItem>();
            Det = DetectionResult.Interpret(Raw);
            Pre = CapabilityBuilder.Build(Det, new EnvTestResults(), NowUtc());
            Flow = WizardFlow.Steps(Det, Pre, Conflicts, Svc.ReadOnly);
            ModeCandidates = ModeTestPlan.Candidates(Pre);
            Detecting = false;
            Detected = true;
        }

        public CapabilityProfile BuildFinal()
        {
            Results.ReadOnly = Svc.ReadOnly;
            Final = CapabilityBuilder.Build(Det, Results, NowUtc());
            return Final;
        }

        public EnvOutcome ToOutcome()
        {
            if (Final == null) BuildFinal();
            EnvOutcome o = new EnvOutcome();
            o.Profile = Final;
            o.Results = Results;
            o.Raw = Raw;
            o.Conflicts = Conflicts;
            o.ServiceUndo = Svc.ServiceUndoRecords ?? new List<ServiceUndo>();
            return o;
        }
    }

    // What the host shows for the current step: the primary button, the "Fazer depois" button and two
    // step-specific actions. The host additionally disables everything but Cancelar while work is running.
    internal sealed class StepUi
    {
        public string PrimaryText = "Continuar";
        public bool PrimaryEnabled = true;
        public bool SkipVisible;
        public bool SkipEnabled = true;
        public string SkipText = "Fazer depois";
        public bool AVisible;
        public string AText = "";
        public bool AEnabled = true;
        public bool BVisible;
        public string BText = "";
        public bool BEnabled = true;
    }

    // The host form, as the steps see it.
    internal interface IWizardHost
    {
        EnvSession Session { get; }
        bool Busy { get; }
        bool Cancelling { get; }
        // 0..1 sweep for indeterminate bars.
        double Anim { get; }
        // Runs work on its own MTA thread; done runs on the UI thread.
        void RunWorker(string name, Action work, Action done);
        // Posts to the UI thread (safe after the form closed).
        void Post(Action a);
        // Buttons and drawing must be refreshed.
        void Changed();
        // A modal yes/no box from a worker thread (blocks it until answered).
        bool ConfirmOnUi(string text, string caption);
        IWin32Window DialogOwner { get; }
        // Closes the window without the cancel path (a host step that already handed over to another process).
        void CloseWindow();
        // Same as the Cancelar button: undoes what the run touched and closes.
        void RequestCancelNow();
        // Finishes the wizard now (the finished handler gets the outcome), as the last step's primary button does.
        void FinishNow();
    }

    // One step of the wizard. The same steps run in the standalone wizard (--teste-ambiente, first run,
    // Configurações) and inside an installer host that puts its own steps ("Bem-vindo", "Instalando") before them.
    // Everything is called on the UI thread unless stated.
    internal interface IWizardStep
    {
        // Short stable name ("detect", "conflicts", "key", "fan", "mode", "summary"; the installer adds its own).
        string Key { get; }
        // Label of the step indicator.
        string ShortName { get; }
        // Heading of the step.
        string Title { get; }
        // Does the step apply now (decided from the session: before the detection only the steps that do not need it).
        bool Applies(EnvSession s);
        // Offers "Fazer depois": the capability stays locked and the app reminds on its first open.
        bool CanSkip { get; }
        void Enter(IWizardHost host);
        void Leave();
        // Any thread: ask the running work to stop (it ends in Automático / Dia a dia by itself).
        void Cancel();
        // About every 60 ms while the step is shown.
        void Tick();
        void Paint(Graphics g, int x, int y, int w, int limit);
        StepUi Ui();
        void ActionA();
        void ActionB();
        // "Fazer depois": records the step as skipped.
        void Skip();
        // The primary button. true = go to the next step.
        bool Next();
    }
}
