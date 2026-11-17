using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    public enum Tri { Unknown, Yes, No }

    // How the keyboard lighting is driven (only the table knows: the firmware accepts RGB writes without
    // error and nothing lights up on the I2C models, so there is no safe probe).
    public enum RgbKind { Unknown, None, Backlight, FourZone, HidI2c }

    // How the NitroSense / PredatorSense button reaches the system.
    public enum KeyKind { Unknown, KeyboardKey, TurboButtonWmi, HidButton, NoChannel }

    // One family of notebooks and what the research expects of it. Data only: the capability logic
    // (CapabilityBuilder) never trusts this over a reading or a test, it only fills the gaps and
    // refuses what is known to be dangerous.
    public sealed class ModelEntry
    {
        public string Family;
        public string[] ModelCodes;
        // A = measured here or code + hardware from 2+ sources; M = one source or kernel code untested; B = inference.
        public char Confidence = 'B';
        public Tri Sensors = Tri.Unknown;
        // -1 = unknown.
        public int Fans = -1;
        public Tri FanControl = Tri.Unknown;
        // When true, Profiles lists the profiles the model is expected to have (empty = none at all).
        public bool ProfilesKnown;
        public PerfMode[] Profiles = new PerfMode[0];
        // The performance profile through WMI breaks with some BIOS versions (ANV15-52).
        public bool ProfileBiosDependent;
        // The Turbo of this model is an overclock path, not a profile: never test or offer it.
        public bool TurboDanger;
        // The sensor ids differ from the usual ones (PHN14-51): the standard readings cannot be trusted.
        public bool SensorIdsDiffer;
        public RgbKind Rgb = RgbKind.Unknown;
        public Tri Battery = Tri.Unknown;
        public KeyKind Key = KeyKind.Unknown;
        public string[] Quirks = new string[0];

        public ModelEntry Conf(char c) { Confidence = c; return this; }
        public ModelEntry Sens(Tri t) { Sensors = t; return this; }
        public ModelEntry FanCount(int n) { Fans = n; return this; }
        public ModelEntry FanCtl(Tri t) { FanControl = t; return this; }
        public ModelEntry Prof(params PerfMode[] p) { ProfilesKnown = true; Profiles = p; return this; }
        public ModelEntry BiosDependent() { ProfileBiosDependent = true; return this; }
        public ModelEntry TurboRisk() { TurboDanger = true; return this; }
        public ModelEntry OtherSensorIds() { SensorIdsDiffer = true; return this; }
        public ModelEntry Light(RgbKind k) { Rgb = k; return this; }
        public ModelEntry Bat(Tri t) { Battery = t; return this; }
        public ModelEntry KeyIs(KeyKind k) { Key = k; return this; }
        public ModelEntry Note(params string[] q) { Quirks = q; return this; }
    }

    // Section 7 of docs\2026-11-17-pesquisa-modelos-acer.md as data.
    public static class ModelTable
    {
        private static readonly Regex CodePattern = new Regex(@"\b([A-Z]{1,5}\d{1,3}[A-Z]{0,2}-\d{2,3}[A-Z]{0,2})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static ModelEntry E(string family, params string[] codes)
        {
            ModelEntry e = new ModelEntry();
            e.Family = family;
            e.ModelCodes = codes;
            return e;
        }

        private const PerfMode Q = PerfMode.Silencioso;
        private const PerfMode B = PerfMode.DiaADia;
        private const PerfMode P = PerfMode.Desempenho;
        private const PerfMode T = PerfMode.Turbo;
        private const PerfMode G = PerfMode.Economico;

        private const string TurboRejected = "O Turbo é recusado pelo firmware deste modelo (confirmado); só Silencioso, Dia a dia, Desempenho e Econômico existem.";
        private const string PowerNote = "Rode o teste com o carregador ligado.";

        public static readonly List<ModelEntry> All = Build();

        private static List<ModelEntry> Build()
        {
            List<ModelEntry> l = new List<ModelEntry>();

            // ---- Nitro V 15 ----
            l.Add(E("Nitro V 15", "ANV15-51").Conf('A').Sens(Tri.Yes).FanCount(2).FanCtl(Tri.Yes).Prof(Q, B, P, G)
                .Light(RgbKind.Backlight).Bat(Tri.Yes).KeyIs(KeyKind.KeyboardKey)
                .Note(TurboRejected, "O backlight do teclado é de uma zona só (branco)."));
            l.Add(E("Nitro V 15", "ANV15-41").Conf('M').Sens(Tri.Yes).Prof(Q, B, P, G).KeyIs(KeyKind.KeyboardKey)
                .Note("Perfis e ventoinhas vêm do mesmo caminho do ANV15-51, ainda sem teste em hardware."));
            l.Add(E("Nitro V 15", "ANV15-52").Conf('M').Sens(Tri.Yes).Prof(Q, B, P, G).BiosDependent()
                .Bat(Tri.Yes).KeyIs(KeyKind.KeyboardKey)
                .Note("O perfil de desempenho por WMI falha em algumas versões de BIOS (relatado na V1.53) e funcionava na V1.31.",
                    "Depois de atualizar a BIOS, refaça o teste de ambiente."));

            // ---- Nitro V 16 / Nitro 16 / Nitro 17 ----
            l.Add(E("Nitro V 16 / Nitro 16 / Nitro 17", "ANV16-41", "AN16-41", "AN16-42", "AN17-51").Conf('M').Prof(Q, B, P, G)
                .Light(RgbKind.FourZone).KeyIs(KeyKind.KeyboardKey));
            l.Add(E("Nitro 16", "AN16-43").Conf('M').Prof(Q, B, P, G).Light(RgbKind.FourZone).Bat(Tri.Yes).KeyIs(KeyKind.KeyboardKey));
            l.Add(E("Nitro V 16", "ANV16-42").Conf('M').Prof(Q, B, P, G).Light(RgbKind.FourZone).Bat(Tri.Yes).KeyIs(KeyKind.NoChannel)
                .Note("A tecla do Nitro não chega ao Windows por nenhum canal conhecido neste modelo; o teste da tecla pode não achar nada."));
            l.Add(E("Nitro V 16", "ANV16-71").Conf('M').Prof(Q, B, P, G).Light(RgbKind.FourZone).TurboRisk().KeyIs(KeyKind.KeyboardKey)
                .Note("Este modelo tem um Turbo por overclock (não é um perfil); o app não o usa."));

            // ---- Nitro V 16S / 16S AI ----
            l.Add(E("Nitro V 16S / 16S AI", "ANV16S-41", "AN16S-61").Conf('M').Prof(Q, B, P, G)
                .Light(RgbKind.HidI2c).KeyIs(KeyKind.HidButton)
                .Note("O teclado RGB fica num controlador I2C: o firmware aceita os comandos de luz e nada acende. O app não controla essa luz.",
                    "O botão do Nitro chega por HID, não como tecla comum."));

            // ---- Nitro 5 recente ----
            l.Add(E("Nitro 5", "AN515-58").Conf('M').Sens(Tri.Yes).FanCtl(Tri.Yes).Prof(Q, B, P, G)
                .Light(RgbKind.FourZone).Bat(Tri.Yes).KeyIs(KeyKind.KeyboardKey));
            l.Add(E("Nitro 5", "AN515-57").Conf('M').Prof(Q, B, P, G).Light(RgbKind.Backlight).Bat(Tri.Yes).KeyIs(KeyKind.KeyboardKey)
                .Note("O teclado tem uma cor fixa (vermelho)."));
            l.Add(E("Nitro 5 / Nitro 17", "AN517-54").Conf('M').Prof(Q, B, P, G).Bat(Tri.Yes).KeyIs(KeyKind.KeyboardKey));
            l.Add(E("Nitro 17", "AN517-41").Conf('M').Prof(Q, B, P, G).Light(RgbKind.FourZone).KeyIs(KeyKind.KeyboardKey));

            // ---- Nitro 5 antigo: sem perfil de desempenho ----
            l.Add(E("Nitro 5 (antigo)", "AN515-43", "AN515-44").Conf('M').Sens(Tri.Yes).FanCtl(Tri.Yes).Prof()
                .Light(RgbKind.FourZone).KeyIs(KeyKind.KeyboardKey)
                .Note("Este modelo não tem perfis de desempenho por plataforma; só ventoinhas e sensores.",
                    "O modo Custom não fica mais silencioso que o automático (piso de rotação mais alto)."));
            l.Add(E("Nitro 5 (antigo)", "AN515-45", "AN515-54", "AN515-55", "AN515-56").Conf('B').Prof()
                .Light(RgbKind.FourZone)
                .Note("Este modelo não tem perfis de desempenho por plataforma."));
            l.Add(E("Nitro 5 (antigo)", "AN515-47").Conf('M').Bat(Tri.No).KeyIs(KeyKind.KeyboardKey));

            // ---- Predator Helios 300 ----
            l.Add(E("Predator Helios 300", "PH315-53", "PH315-54", "PH315-55").Conf('M').Sens(Tri.Yes).FanCount(2).FanCtl(Tri.Yes)
                .Prof().TurboRisk().Light(RgbKind.FourZone).KeyIs(KeyKind.TurboButtonWmi)
                .Note("O perfil de desempenho dá erro neste modelo; o Turbo dele é um botão com LED, ventoinha e overclock, e o app não o usa.",
                    "Nunca use o Turbo com a bateria removida: o notebook pode desligar sob carga."));
            l.Add(E("Predator Helios 300", "PH315-52").Conf('M').Sens(Tri.Yes).FanCount(2).FanCtl(Tri.Yes)
                .Prof().TurboRisk().Light(RgbKind.FourZone).Bat(Tri.No).KeyIs(KeyKind.TurboButtonWmi)
                .Note("O perfil de desempenho não existe; o limite de carga da bateria não é suportado."));

            // ---- Predator Helios Neo / Helios 16 / 18 ----
            l.Add(E("Predator Helios Neo 16", "PHN16-71").Conf('M').Prof(Q, B, P, T, G).Light(RgbKind.FourZone).Bat(Tri.Yes)
                .KeyIs(KeyKind.KeyboardKey)
                .Note("O teclado por tecla (per-key) pode falhar.", PowerNote));
            l.Add(E("Predator Helios Neo 16", "PHN16-72", "PH16-72").Conf('M').FanCtl(Tri.Yes).Prof(Q, B, P, T, G)
                .Light(RgbKind.FourZone)
                .Note("Há relatos de o sistema travar ao trocar para Desempenho com o software da Acer no Windows 11 24H2; use a tomada e o Dia a dia se acontecer.", PowerNote));
            l.Add(E("Predator Helios 16 / 18", "PHN16-73", "PH16-71", "PH18-71", "PH18-73", "PHN18-71").Conf('M').Prof(Q, B, P, T, G)
                .Light(RgbKind.FourZone)
                .Note(PowerNote));
            l.Add(E("Predator Helios Neo 16S AI", "PHN16S-71").Conf('M').Prof(Q, B, P, T, G).Light(RgbKind.HidI2c).KeyIs(KeyKind.HidButton)
                .Note("O teclado RGB fica num controlador I2C: o firmware aceita os comandos de luz e nada acende. O app não controla essa luz.",
                    "O botão do Predator chega por HID, não como tecla comum."));

            // ---- Predator Triton 14 ----
            l.Add(E("Predator Triton 14", "PT14-51").Conf('M').Sens(Tri.Yes).FanCtl(Tri.Yes).Prof(Q, B, P, T, G)
                .Note(PowerNote));
            l.Add(E("Predator Triton 14", "PHN14-51").Conf('B').OtherSensorIds().Light(RgbKind.FourZone)
                .Note("Este modelo usa outros números de sensor e lê a rotação das ventoinhas por outro caminho: as leituras padrão não valem aqui."));

            // ---- Triton 300 / 500, Helios 500 / 700 ----
            l.Add(E("Predator Triton / Helios (antigos)", "PT315-51", "PT315-52", "PT316-51", "PT316-51S", "PT515-51", "PT515-52",
                "PT516-52S", "PT314-51", "PT314-52S", "PT917-71", "PH517-51", "PH517-52", "PH717-71", "PH717-72").Conf('B')
                .TurboRisk().KeyIs(KeyKind.TurboButtonWmi)
                .Note("O Turbo destes modelos é um botão com overclock, sem teste em hardware; o app não o usa."));

            // ---- Aspire (not gaming) ----
            l.Add(E("Aspire (não gamer)", "A715-42G").Conf('M').Bat(Tri.Yes)
                .Note("Modelo não gamer: sem prova de que a interface gamer exista."));
            l.Add(E("Aspire (não gamer)", "A515-57").Conf('B').FanCtl(Tri.Unknown)
                .Note("Modelo não gamer: o firmware responde ao Turbo escondido, mas o modo volta sozinho em poucos segundos."));
            l.Add(E("Aspire (não gamer)", "A315-58G").Conf('M').Sens(Tri.Yes));
            return l;
        }

        // The model code inside a DMI product name ("Nitro ANV15-51" -> "ANV15-51"), upper case; null when none.
        public static string ExtractCode(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            Match m = CodePattern.Match(model);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        public static ModelEntry Find(string model)
        {
            string code = ExtractCode(model);
            if (code == null) return null;
            foreach (ModelEntry e in All)
            {
                foreach (string c in e.ModelCodes)
                {
                    if (c == code) return e;
                }
            }
            return null;
        }

        // Overclock families (the Turbo of these is a button + overclock, not a profile): blocked by prefix even
        // for a code the table does not list.
        private static readonly string[] TurboRiskPrefixes =
        {
            "PH315-", "PH317-", "PT315-", "PT316-", "PT515-", "PT516-", "PT314-", "PT917-", "PH517-", "PH717-"
        };

        public static bool IsTurboRiskFamily(string model)
        {
            string code = ExtractCode(model);
            if (code == null) return false;
            foreach (string p in TurboRiskPrefixes)
            {
                if (code.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // A gaming line by name only (for a model the table does not list: the report says so).
        public static bool LooksLikeAcerGaming(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = model.ToLowerInvariant();
            return m.Contains("nitro") || m.Contains("predator") || m.Contains("helios") || m.Contains("triton");
        }
    }
}
