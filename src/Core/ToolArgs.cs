using System;

namespace Forja.Core
{
    // Command-line switches shared by the test tools (--teste-desempenho, --autoteste).
    public static class ToolArgs
    {
        // Unattended run: no dialog of any kind (confirm, result, errors); the reports are
        // written as usual and the exit code tells whether the tool ran to the end.
        public const string Auto = "--auto";
        // Opt-in of --autoteste: also run the RAM cleaner (it empties the working sets of the
        // whole system). Never implied by --auto.
        public const string RamClean = "--com-limpeza-ram";

        // Case-insensitive; args[0] is the program path and never counts.
        public static bool Has(string[] args, string name)
        {
            if (args == null || string.IsNullOrEmpty(name)) return false;
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool Unattended(string[] args)
        {
            return Has(args, Auto);
        }

        public static bool WithRamClean(string[] args)
        {
            return Has(args, RamClean);
        }
    }

    // Process exit codes of the test tools. 0 = ran to the end and left nothing wrong;
    // anything else = could not run, was stopped, or found a problem.
    public static class ToolExit
    {
        public const int Done = 0;
        public const int Failed = 1;
        public const int OtherInstance = 2;
        public const int Declined = 3;
        // The performance test ran but could not confirm Dia a dia + fans on Automático at the end.
        public const int RestoreFailed = 4;

        public static int ForPerf(PerfTestReport r)
        {
            if (r == null || r.Aborted)
            {
                // Aborted and the machine is not confirmed safe: say so with the more serious code.
                return r != null && !r.RestoreOk ? RestoreFailed : Failed;
            }
            return r.RestoreOk ? Done : RestoreFailed;
        }

        // 0 only when nothing FALHOU and the run was not cut short.
        public static int ForAuto(AutoTestReport r)
        {
            if (r == null || r.Aborted) return Failed;
            return r.Count(AutoStatus.Falhou) > 0 ? Failed : Done;
        }
    }
}
