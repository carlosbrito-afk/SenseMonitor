namespace Forja.Core
{
    // pt-BR texts of the safety features shown in the window and the tray.
    public static class SafetyText
    {
        // Top strip of every page while read-only mode is on.
        public const string ReadOnlyBanner = "Diagnóstico: o app só lê. Nada é enviado ao firmware.";

        // Reason shown next to a control that is disabled by read-only mode.
        public const string ReadOnlyReason = "Modo diagnóstico ligado: o app só lê e não envia comandos ao firmware.";

        // Short form for a tray item or a small note.
        public const string ReadOnlyShort = "Bloqueado pelo modo diagnóstico (somente leitura)";

        public const string SettingTitle = "Modo diagnóstico (somente leitura)";

        public static string SettingNote(bool effectiveNow, bool savedSetting, bool forcedBySwitch)
        {
            if (forcedBySwitch) return "Ligado nesta execução pela chave --somente-leitura.";
            if (effectiveNow && savedSetting) return "Ligado: o app só lê. Nenhum comando vai ao firmware e Limpar RAM fica bloqueado.";
            if (savedSetting) return "Vale a partir do próximo início do app.";
            if (effectiveNow) return "Desligado a partir do próximo início do app.";
            return "Desligado. Ligue para investigar quedas sem que o app envie comandos ao firmware.";
        }
    }
}
