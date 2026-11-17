using System;
using System.Collections.Generic;

namespace Forja.Core
{
    public enum AppProcessKind
    {
        // The tray app or one of the tools (they hold the single-instance mutex while they run).
        App,
        // --sentinela / --sentinela-iniciar: the fan guard; it leaves on its own once the app is gone.
        Sentinel,
        // --aplicar-atualizacao: the update helper.
        Helper
    }

    // Tells the instances of SenseMonitor.exe apart by their command line (Win32_Process.CommandLine),
    // not by the image path: the sentinel runs the very same exe as the app.
    public static class ProcessKinds
    {
        // Splits a Windows command line with the CommandLineToArgvW rules (see CommandLine).
        public static List<string> Tokens(string commandLine)
        {
            return CommandLine.Split(commandLine);
        }

        // The tray app itself: an App-kind instance whose only arguments are the markers the start-up
        // stages add (--elevado, --executar-aqui). A CLI tool (--autoteste, --validar-ventoinha, ...) is not.
        public static bool IsTray(string commandLine)
        {
            if (Classify(commandLine) != AppProcessKind.App) return false;
            List<string> t = Tokens(commandLine);
            if (t.Count == 0) return false;
            for (int i = 1; i < t.Count; i++)
            {
                if (!Is(t[i], StartupRouter.ElevatedSwitch) && !Is(t[i], StartupRouter.ExecuteHereSwitch)) return false;
            }
            return true;
        }

        // An unreadable command line counts as App: when in doubt the instance is treated as one that
        // must be waited for (never swap an exe under something that might be the app).
        public static AppProcessKind Classify(string commandLine)
        {
            List<string> t = Tokens(commandLine);
            for (int i = 1; i < t.Count; i++)
            {
                if (Is(t[i], "--sentinela") || Is(t[i], "--sentinela-iniciar")) return AppProcessKind.Sentinel;
                if (Is(t[i], StartupRouter.ApplySwitch)) return AppProcessKind.Helper;
            }
            return AppProcessKind.App;
        }

        private static bool Is(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
