using System;
using System.Collections.Generic;
using System.Globalization;

namespace Forja.Core
{
    // Combines the three sources into one profile:
    //   firmware bitmaps (what the notebook announces)  AND  model table (what the research expects)
    //   AND  the guided-test results (what was proved here).
    // Rules, in short:
    //  - Nothing is Confirmed without proof: a plausible reading (sensors) or a passed test (controls).
    //  - A bitmap that fails the sanity checks is ignored and the table decides what is worth testing.
    //  - What the firmware does not announce, or the table marks as dangerous, is Unavailable and is never tested.
    //  - A skipped or missing test leaves the capability Untested, which the app treats as locked.
    public static class CapabilityBuilder
    {
        public static CapabilityProfile Build(DetectionResult d, EnvTestResults t, string createdUtc)
        {
            if (d == null) d = DetectionResult.Interpret(null);
            if (t == null) t = new EnvTestResults();
            DetectionRaw raw = d.Raw;
            CapabilityProfile p = new CapabilityProfile();
            p.Fingerprint = d.Fingerprint;
            p.Model = raw.Model;
            p.Bios = raw.Bios;
            p.CreatedUtc = createdUtc;

            if (!d.IsAcer)
            {
                string why = "Este não é um notebook Acer: só monitor, saúde e manutenção.";
                FillAll(p, CapState.Unavailable, why);
                return p;
            }
            if (!raw.FirmwareClass)
            {
                string why = "Este Acer não tem a interface gamer do firmware (AcerGamingFunction): só monitor, saúde e manutenção.";
                FillAll(p, CapState.Unavailable, why);
                FillAuxiliary(p, d);
                return p;
            }
            if (!raw.FirmwareAnswered)
            {
                string why = "O firmware não respondeu (o app precisa estar como administrador).";
                FillAll(p, CapState.Unknown, why);
                FillAuxiliary(p, d);
                return p;
            }

            p.SensorBitmap = d.Sensors.StatusOk ? Hex(d.Sensors.Mask) : null;
            p.ProfileBitmap = d.Profiles.StatusOk ? Hex(d.Profiles.Mask) : null;
            p.ProfileBitmapTrusted = d.Profiles.Plausible;

            BuildSensorsAndFans(p, d, t);
            BuildModes(p, d, t);
            BuildKey(p, d, t);
            FillAuxiliary(p, d);
            Stamp(p, t, createdUtc);
            return p;
        }

        // When and how each guided capability was confirmed (what "Já testado neste notebook" shows later).
        private static void Stamp(CapabilityProfile p, EnvTestResults t, string createdUtc)
        {
            StampOne(p, Cap.Key, t.Key.Utc, t.Key.Method, createdUtc);
            StampOne(p, Cap.FanControl, t.Fan.Utc, t.Fan.Method, createdUtc);
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                ModeStepResult r = t.ModeResult(m);
                StampOne(p, ModeCatalog.CapOf(m), r == null ? null : r.Utc, r == null ? null : r.Method, createdUtc);
            }
        }

        private static void StampOne(CapabilityProfile p, Cap cap, string utc, string method, string createdUtc)
        {
            CapEntry e = p.Find(cap);
            if (e == null || e.StateValue != CapState.Confirmed) return;
            // Temperatures and fan reading are confirmed by reading, not by a guided test: only tests are stamped.
            e.Utc = string.IsNullOrEmpty(utc) ? createdUtc : utc;
            e.Method = method;
        }

        private static void FillAll(CapabilityProfile p, CapState s, string reason)
        {
            foreach (Cap c in CapNames.All) p.Set(c, s, reason);
        }

        // ---- sensors, fan reading, fan control ----------------------------------------------

        private static void BuildSensorsAndFans(CapabilityProfile p, DetectionResult d, EnvTestResults t)
        {
            SensorReading live = d.Raw.Live ?? new SensorReading();
            bool cpuTempOk = ValidTemp(live.CpuTempC);
            bool gpuTempOk = ValidTemp(live.GpuTempC);
            bool anyTemp = cpuTempOk || gpuTempOk;
            SensorBitmapInfo s = d.Sensors;
            // The temperatures guard the fan test (90 C): over about five seconds they must look like a real sensor.
            PlausibilityResult plaus = new PlausibilityResult();
            plaus.Ok = true;
            // One or two readings are too few to judge (zero = none were taken, only readings given directly).
            if (d.Raw.LiveSamples != null && d.Raw.LiveSamples.Count >= 1) plaus = SensorPlausibility.Check(d.Raw.LiveSamples);

            // Sensors
            if (d.Table != null && d.Table.SensorIdsDiffer)
            {
                p.Set(Cap.Sensors, CapState.Unknown, "Este modelo usa outros números de sensor: as leituras padrão não são confiáveis aqui.");
            }
            else if (anyTemp && !plaus.Ok)
            {
                p.Set(Cap.Sensors, CapState.Untested, "Temperaturas lidas, mas " + plaus.Reason + ": não confirmadas.");
            }
            else if (anyTemp)
            {
                p.Set(Cap.Sensors, CapState.Confirmed, s.Plausible
                    ? "Temperaturas lidas do firmware (" + TempText(live) + ")."
                    : "Temperaturas lidas (" + TempText(live) + "), apesar da lista de sensores do firmware não ser confiável.");
            }
            else if (s.Plausible)
            {
                p.Set(Cap.Sensors, CapState.Untested, "O firmware anuncia os sensores, mas nenhuma temperatura válida foi lida agora.");
            }
            else if (d.Table != null && d.Table.Sensors == Tri.Yes)
            {
                p.Set(Cap.Sensors, CapState.Untested, "A tabela do modelo espera sensores, mas nenhuma leitura válida veio. " + Nz(s.Reason));
            }
            else
            {
                p.Set(Cap.Sensors, CapState.Unknown, "Nenhuma temperatura legível. " + Nz(s.Reason));
            }

            // Which fans exist
            bool cpuFan;
            bool gpuFan;
            string source;
            if (s.Plausible)
            {
                cpuFan = s.CpuFan;
                gpuFan = s.GpuFan;
                source = "anunciada pelo firmware";
            }
            else
            {
                cpuFan = Rpm(live.CpuFanRpm);
                gpuFan = Rpm(live.GpuFanRpm);
                source = "vista na leitura";
                if (!cpuFan && !gpuFan && d.Table != null && d.Table.Fans > 0)
                {
                    cpuFan = true;
                    gpuFan = d.Table.Fans >= 2;
                    source = "esperada pela tabela do modelo";
                }
            }
            p.CpuFan = cpuFan;
            p.GpuFan = gpuFan;

            // Fan reading
            if (!cpuFan && !gpuFan)
            {
                p.Set(Cap.FanRead, CapState.Unavailable, "Nenhuma ventoinha foi anunciada nem lida.");
            }
            else
            {
                bool cpuRead = !cpuFan || Rpm(live.CpuFanRpm);
                bool gpuRead = !gpuFan || Rpm(live.GpuFanRpm);
                string count = FanCountText(cpuFan, gpuFan);
                if (cpuRead && gpuRead)
                {
                    p.Set(Cap.FanRead, CapState.Confirmed, count + " (" + source + "), com rotação lida: " + RpmText(live, cpuFan, gpuFan) + ".");
                }
                else
                {
                    p.Set(Cap.FanRead, CapState.Untested, count + " (" + source + "), mas a rotação veio zero ou ausente: pode ser uma ventoinha parada em repouso.");
                }
            }

            // Fan control
            if (!cpuFan && !gpuFan)
            {
                p.Set(Cap.FanControl, CapState.Unavailable, "Sem ventoinha para controlar.");
            }
            else if (d.Table != null && d.Table.FanControl == Tri.No)
            {
                p.Set(Cap.FanControl, CapState.Unavailable, "A tabela do modelo diz que ele não aceita controle de ventoinha.");
            }
            else if (p.State(Cap.Sensors) != CapState.Confirmed)
            {
                // Max and 50 % are only tried with a trusted temperature watching them (90 C stops the test).
                p.Set(Cap.FanControl, CapState.Unknown, "A temperatura não está confirmada neste notebook: o teste das ventoinhas (que usa 90 °C como limite) não roda.");
            }
            else if (t.ReadOnly)
            {
                p.Set(Cap.FanControl, CapState.Untested, "Modo somente leitura ligado: o teste das ventoinhas escreve no firmware e não rodou.");
            }
            else if (t.Fan.Status == StepStatus.Passed)
            {
                bool ok = (!cpuFan || t.Fan.CpuOk) && (!gpuFan || t.Fan.GpuOk);
                p.Set(Cap.FanControl, ok ? CapState.Confirmed : CapState.Unavailable,
                    ok ? "As ventoinhas seguiram os comandos no teste." : (t.Fan.Reason ?? "Uma ventoinha não seguiu o comando no teste."));
            }
            else if (t.Fan.Status == StepStatus.Failed)
            {
                p.Set(Cap.FanControl, CapState.Unavailable, "No teste: " + (t.Fan.Reason ?? "a ventoinha não seguiu o comando") + ".");
            }
            else if (t.Fan.Status == StepStatus.Skipped)
            {
                p.Set(Cap.FanControl, CapState.Untested, "Teste pulado: o controle manual continua bloqueado.");
            }
            else
            {
                p.Set(Cap.FanControl, CapState.Untested, "Ainda não testado: o controle manual fica bloqueado até o teste.");
            }
        }

        // ---- modes --------------------------------------------------------------------------

        private static void BuildModes(CapabilityProfile p, DetectionResult d, EnvTestResults t)
        {
            bool trusted = d.Profiles.Plausible;
            ModelEntry table = d.Table;
            List<PerfMode> fromFirmware = d.Profiles.Modes();
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                Cap cap = ModeCatalog.CapOf(m);
                string name = ModeCatalog.Name(m);
                bool announced;
                string source;
                if (trusted)
                {
                    announced = fromFirmware.Contains(m);
                    source = "firmware";
                }
                else if (table != null && table.ProfilesKnown)
                {
                    announced = new List<PerfMode>(table.Profiles).Contains(m);
                    source = "tabela";
                }
                else
                {
                    p.Set(cap, CapState.Unknown, "A lista de perfis do firmware não é confiável (" + Nz(d.Profiles.Reason).TrimEnd('.') + ") e este modelo não está na tabela.");
                    continue;
                }

                if (!announced)
                {
                    p.Set(cap, CapState.Unavailable, source == "firmware"
                        ? "O firmware não anuncia o perfil " + name + " neste notebook."
                        : "A tabela do modelo não lista o perfil " + name + " (a lista do firmware não é confiável: " + Nz(d.Profiles.Reason).TrimEnd('.') + ").");
                    continue;
                }
                if (m == PerfMode.Turbo && table == null)
                {
                    p.Set(cap, CapState.Unavailable, "O Turbo só é testado em modelos da tabela do SenseMonitor: este modelo está fora da tabela.");
                    continue;
                }
                if (m == PerfMode.Turbo && (table.TurboDanger || ModelTable.IsTurboRiskFamily(d.Raw.Model)))
                {
                    p.Set(cap, CapState.Unavailable, "O Turbo deste modelo é um overclock com risco conhecido (tabela do modelo): bloqueado por segurança.");
                    continue;
                }

                string basis = source == "firmware" ? "anunciado pelo firmware" : "esperado pela tabela do modelo";
                if (source == "tabela") basis += " (a lista do firmware foi descartada: " + Nz(d.Profiles.Reason).TrimEnd('.') + ")";
                if (source == "firmware" && table != null && table.ProfilesKnown && table.Profiles.Length == 0)
                    basis += ", embora a tabela diga que este modelo não tem perfis: o teste decide";
                if (table != null && table.ProfileBiosDependent)
                    basis += "; neste modelo o perfil depende da versão da BIOS";

                ModeStepResult r = t.ModeResult(m);
                if (t.ReadOnly)
                {
                    p.Set(cap, CapState.Untested, "Modo " + name + " " + basis + ". Modo somente leitura ligado: não foi testado.");
                }
                else if (r != null && r.Status == StepStatus.Passed && r.Proven)
                {
                    p.Set(cap, CapState.Confirmed, "O firmware trocou para " + name + " e confirmou na leitura de volta.");
                }
                else if (r != null && r.Status == StepStatus.Passed)
                {
                    p.Set(cap, CapState.Untested, "O firmware aceitou " + name + ", mas o modo já era esse: a leitura de volta não prova nada.");
                }
                else if (r != null && r.Status == StepStatus.Failed)
                {
                    p.Set(cap, CapState.Unavailable, "O firmware não confirmou o perfil " + name + " na leitura de volta.");
                }
                else if (r != null && r.Status == StepStatus.Skipped)
                {
                    p.Set(cap, CapState.Untested, "Modo " + name + " " + basis + ". Teste pulado: continua bloqueado.");
                }
                else
                {
                    p.Set(cap, CapState.Untested, "Modo " + name + " " + basis + ". Ainda não testado.");
                }
            }
        }

        // ---- key ----------------------------------------------------------------------------

        private static void BuildKey(CapabilityProfile p, DetectionResult d, EnvTestResults t)
        {
            KeyKind kind = d.Table == null ? KeyKind.Unknown : d.Table.Key;
            KeyTestResult k = t.Key;
            if (k.Status == StepStatus.Passed && k.UserHasNoKey)
            {
                p.Set(Cap.Key, CapState.Unavailable, "Você informou que este notebook não tem essa tecla.");
            }
            else if (k.Status == StepStatus.Passed && KeyCandidates.ById(k.CandidateId) != null)
            {
                p.KeyCandidateId = k.CandidateId;
                p.Set(Cap.Key, CapState.Confirmed, "A tecla respondeu com o " + KeyCandidates.ById(k.CandidateId).Description + ".");
            }
            else if (k.Status == StepStatus.Failed)
            {
                p.Set(Cap.Key, CapState.Unknown, "Nenhuma das teclas conhecidas respondeu. " + KeyChannelNote(kind));
            }
            else if (k.Status == StepStatus.Skipped)
            {
                p.Set(Cap.Key, CapState.Untested, "Teste pulado: o app continua ouvindo só o código que sempre usou. " + KeyChannelNote(kind));
            }
            else
            {
                p.Set(Cap.Key, CapState.Untested, "Ainda não testado. " + KeyChannelNote(kind));
            }
        }

        private static string KeyChannelNote(KeyKind kind)
        {
            switch (kind)
            {
                case KeyKind.TurboButtonWmi: return "Neste modelo o botão Turbo avisa por um evento do firmware (WMI), que o teste de tecla não vê.";
                case KeyKind.HidButton: return "Neste modelo o botão chega por HID, não como tecla comum.";
                case KeyKind.NoChannel: return "Neste modelo a tabela não conhece nenhum canal pelo qual a tecla chegue ao Windows.";
                default: return "";
            }
        }

        // ---- battery and keyboard light (information only: the app does not use them) ---------

        private static void FillAuxiliary(CapabilityProfile p, DetectionResult d)
        {
            DetectionRaw raw = d.Raw;
            ModelEntry table = d.Table;
            if (!raw.BatteryClass)
            {
                p.Set(Cap.Battery, CapState.Unavailable, "A interface de bateria da Acer (BatteryControl) não existe neste notebook.");
            }
            else if (table != null && table.Battery == Tri.No)
            {
                p.Set(Cap.Battery, CapState.Unavailable, "A tabela do modelo diz que o limite de carga não é suportado.");
            }
            else
            {
                p.Set(Cap.Battery, CapState.Untested, "A interface de bateria existe; o app ainda não usa o limite de carga e nada foi lido ou escrito nela.");
            }

            RgbKind rgb = table == null ? RgbKind.Unknown : table.Rgb;
            if (rgb == RgbKind.HidI2c || raw.EneKb5130)
            {
                p.Set(Cap.KeyboardLight, CapState.Unavailable, "O teclado RGB deste modelo fica num controlador I2C: o firmware aceita os comandos de luz e nada acende. O app não controla essa luz.");
            }
            else if (!raw.FirmwareClass || !raw.KeyboardLightMethod)
            {
                p.Set(Cap.KeyboardLight, CapState.Unavailable, "O firmware não expõe comandos de iluminação do teclado.");
            }
            else
            {
                p.Set(Cap.KeyboardLight, CapState.Unknown, "O firmware aceita comandos de luz sem provar efeito: não há leitura confiável, e o app não controla a luz.");
            }
        }

        // ---- helpers -------------------------------------------------------------------------

        private static string Hex(int v)
        {
            return "0x" + v.ToString("x", CultureInfo.InvariantCulture);
        }

        private static string Nz(string s)
        {
            return s ?? "";
        }

        private static bool ValidTemp(double? t)
        {
            return t.HasValue && !double.IsNaN(t.Value) && !double.IsInfinity(t.Value) && t.Value > 0 && t.Value < 130;
        }

        private static bool Rpm(int? v)
        {
            return v.HasValue && v.Value > 0;
        }

        private static string TempText(SensorReading r)
        {
            List<string> l = new List<string>();
            if (ValidTemp(r.CpuTempC)) l.Add("CPU " + Math.Round(r.CpuTempC.Value).ToString(CultureInfo.InvariantCulture) + " °C");
            if (ValidTemp(r.GpuTempC)) l.Add("GPU " + Math.Round(r.GpuTempC.Value).ToString(CultureInfo.InvariantCulture) + " °C");
            return string.Join(", ", l.ToArray());
        }

        private static string RpmText(SensorReading r, bool cpu, bool gpu)
        {
            List<string> l = new List<string>();
            if (cpu) l.Add("CPU " + r.CpuFanRpm.Value.ToString(CultureInfo.InvariantCulture) + " RPM");
            if (gpu) l.Add("GPU " + r.GpuFanRpm.Value.ToString(CultureInfo.InvariantCulture) + " RPM");
            return string.Join(", ", l.ToArray());
        }

        private static string FanCountText(bool cpu, bool gpu)
        {
            if (cpu && gpu) return "2 ventoinhas (CPU e GPU)";
            return cpu ? "1 ventoinha (CPU)" : "1 ventoinha (GPU)";
        }
    }
}
