namespace Forja.Core
{
    // One place for what each performance mode is called, what it does and which firmware profile
    // code it writes. Codes: docs\2026-11-17-pesquisa-modelos-acer.md, 1.4.
    public static class ModeCatalog
    {
        public const byte CodeQuiet = 0x00;
        public const byte CodeBalanced = 0x01;
        public const byte CodePerformance = 0x04;
        public const byte CodeTurbo = 0x05;
        public const byte CodeEco = 0x06;

        public static byte ProfileCode(PerfMode m)
        {
            switch (m)
            {
                case PerfMode.Silencioso: return CodeQuiet;
                case PerfMode.Desempenho: return CodePerformance;
                case PerfMode.Turbo: return CodeTurbo;
                case PerfMode.Economico: return CodeEco;
                default: return CodeBalanced;
            }
        }

        // null for a code the app does not know (2, 3, 7 ...).
        public static PerfMode? FromCode(byte code)
        {
            switch (code)
            {
                case CodeQuiet: return PerfMode.Silencioso;
                case CodeBalanced: return PerfMode.DiaADia;
                case CodePerformance: return PerfMode.Desempenho;
                case CodeTurbo: return PerfMode.Turbo;
                case CodeEco: return PerfMode.Economico;
                default: return null;
            }
        }

        public static string Name(PerfMode m)
        {
            switch (m)
            {
                case PerfMode.Silencioso: return "Silencioso";
                case PerfMode.Desempenho: return "Desempenho";
                case PerfMode.Turbo: return "Turbo";
                case PerfMode.Economico: return "Econômico";
                default: return "Dia a dia";
            }
        }

        // The Modes page card title.
        public static string CardName(PerfMode m)
        {
            return m == PerfMode.Desempenho ? "Desempenho (Gaming)" : Name(m);
        }

        public static string Description(PerfMode m)
        {
            switch (m)
            {
                case PerfMode.Silencioso: return "Ventoinhas baixas e menos ruído, para tarefas leves.";
                case PerfMode.Desempenho: return "Máximo desempenho, com mais ruído e calor.";
                case PerfMode.Turbo: return "Mais que o Desempenho: o máximo que o notebook entrega, com muito ruído e calor.";
                case PerfMode.Economico: return "Poupa energia e esquenta menos, para estudar ou assistir no modo bateria.";
                default: return "Equilíbrio entre desempenho e ruído para o uso normal.";
            }
        }

        public static Cap CapOf(PerfMode m)
        {
            switch (m)
            {
                case PerfMode.Silencioso: return Cap.ModeQuiet;
                case PerfMode.Desempenho: return Cap.ModePerformance;
                case PerfMode.Turbo: return Cap.ModeTurbo;
                case PerfMode.Economico: return Cap.ModeEco;
                default: return Cap.ModeBalanced;
            }
        }

        public static PerfMode? ModeOf(Cap c)
        {
            switch (c)
            {
                case Cap.ModeQuiet: return PerfMode.Silencioso;
                case Cap.ModeBalanced: return PerfMode.DiaADia;
                case Cap.ModePerformance: return PerfMode.Desempenho;
                case Cap.ModeTurbo: return PerfMode.Turbo;
                case Cap.ModeEco: return PerfMode.Economico;
                default: return null;
            }
        }

        // Modes in the order the cards, the tray menu and the report list them.
        public static readonly PerfMode[] DisplayOrder =
        {
            PerfMode.Economico, PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho, PerfMode.Turbo
        };
    }
}
