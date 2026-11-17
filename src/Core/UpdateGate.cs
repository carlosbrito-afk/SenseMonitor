namespace Forja.Core
{
    // What the app looks like right now, as far as "is this a good moment to restart" goes.
    public sealed class IdleFacts
    {
        public bool Exiting;
        public bool WindowVisible;
        // How long the window has been hidden (ms); a window closed a moment ago is not idle yet.
        public long HiddenForMs;
        // Manual, Curva or Máximo is driving the fans.
        public bool FanCustomActive;
        public bool MaintenanceBusy;
        public bool Cleaning;
        // Fan validation, performance test or self-test (including their unattended forms).
        public bool ToolRunning;
        // A check, download or apply is already in flight.
        public bool UpdateBusy;
    }

    public enum GateResult
    {
        Ok,
        Exiting,
        UpdateBusy,
        WindowVisible,
        HiddenTooShort,
        FanCustomActive,
        MaintenanceBusy,
        Cleaning,
        ToolRunning
    }

    public static class UpdateGate
    {
        public const long MinHiddenMs = 60 * 1000;

        // First reason why NOT to apply now; Ok = the app is idle and the fans are on Automático.
        // The apply is only deferred, never forced: a user who keeps Curva on simply gets the
        // update after the next time the fans are back on Automático or the app restarts.
        public static GateResult Check(IdleFacts f)
        {
            if (f == null) return GateResult.UpdateBusy;
            if (f.Exiting) return GateResult.Exiting;
            if (f.UpdateBusy) return GateResult.UpdateBusy;
            if (f.ToolRunning) return GateResult.ToolRunning;
            if (f.MaintenanceBusy) return GateResult.MaintenanceBusy;
            if (f.Cleaning) return GateResult.Cleaning;
            if (f.FanCustomActive) return GateResult.FanCustomActive;
            if (f.WindowVisible) return GateResult.WindowVisible;
            if (f.HiddenForMs < MinHiddenMs) return GateResult.HiddenTooShort;
            return GateResult.Ok;
        }

        public static string Describe(GateResult r)
        {
            switch (r)
            {
                case GateResult.Ok: return "ocioso";
                case GateResult.Exiting: return "o app está saindo";
                case GateResult.UpdateBusy: return "outra etapa da atualização em andamento";
                case GateResult.WindowVisible: return "janela aberta";
                case GateResult.HiddenTooShort: return "janela fechada há pouco tempo";
                case GateResult.FanCustomActive: return "ventoinha em Manual/Curva/Máximo";
                case GateResult.MaintenanceBusy: return "manutenção em andamento";
                case GateResult.Cleaning: return "limpeza de RAM em andamento";
                default: return "teste ou validação em andamento";
            }
        }
    }
}
