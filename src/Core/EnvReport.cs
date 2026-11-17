using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Forja.Core
{
    // The "Exportar relatório do modelo" file: what a supporter needs to extend the model table without
    // owning the notebook. The facts are model, BIOS, the two firmware bitmaps, the test results and the
    // app version. Never written: serial numbers, UUIDs, the computer or user name, or any path with a user
    // name (every string is scrubbed of the profile path and the user name).
    public sealed class EnvReport
    {
        public string Text;
        public string Json;

        public static EnvReport Build(DetectionResult d, CapabilityProfile p, EnvTestResults t, List<ConflictItem> conflicts,
            string appVersion, string nowUtc, string userProfile, string userName)
        {
            EnvReport r = new EnvReport();
            Scrubber s = new Scrubber(userProfile, userName);
            r.Text = BuildText(d, p, t, conflicts, appVersion, nowUtc, s);
            r.Json = BuildJson(d, p, t, conflicts, appVersion, nowUtc, s);
            return r;
        }

        // relatorio-modelo-<model code>-<yyyyMMdd>: model code only (never a user or computer name).
        public static string SuggestedFileName(DetectionRaw raw, DateTime utc)
        {
            string code = raw == null ? null : ModelTable.ExtractCode(raw.Model);
            string part = code != null ? code.ToLowerInvariant() : "desconhecido";
            return "relatorio-modelo-" + part + "-" + utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        private sealed class Scrubber
        {
            private readonly string profile;
            private readonly string user;

            public Scrubber(string profile, string user)
            {
                this.profile = profile;
                this.user = user;
            }

            public string S(string v)
            {
                if (v == null) return null;
                return LogText.Scrub(v, profile, user);
            }
        }

        private static string Dash(string v)
        {
            return string.IsNullOrEmpty(v) ? "—" : v;
        }

        private static string Gib(long bytes)
        {
            if (bytes <= 0) return "—";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }

        private static string HexMask(int mask)
        {
            return "0x" + mask.ToString("x", CultureInfo.InvariantCulture);
        }

        private static string RawHex(ulong? v)
        {
            return v.HasValue ? "0x" + v.Value.ToString("X", CultureInfo.InvariantCulture) : "—";
        }

        private static string YesNo(bool b)
        {
            return b ? "sim" : "não";
        }

        private static string StepText(StepStatus s)
        {
            switch (s)
            {
                case StepStatus.Passed: return "passou";
                case StepStatus.Failed: return "falhou";
                case StepStatus.Skipped: return "pulado";
                default: return "não rodou";
            }
        }

        private static string SensorList(SensorBitmapInfo b)
        {
            if (!b.StatusOk) return "—";
            List<string> l = new List<string>();
            if (b.CpuTemp) l.Add("temperatura da CPU");
            if (b.CpuFan) l.Add("ventoinha da CPU");
            if (b.SystemTemp) l.Add("temperatura do sistema");
            if (b.GpuFan) l.Add("ventoinha da GPU");
            if (b.GpuTemp) l.Add("temperatura da GPU");
            return l.Count == 0 ? "nenhum dos conhecidos" : string.Join(", ", l.ToArray());
        }

        private static string ProfileList(ProfileBitmapInfo b)
        {
            if (!b.StatusOk) return "—";
            List<string> l = new List<string>();
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                bool on = m == PerfMode.Economico ? b.Eco : m == PerfMode.Silencioso ? b.Quiet : m == PerfMode.DiaADia ? b.Balanced
                    : m == PerfMode.Desempenho ? b.Performance : b.Turbo;
                if (on) l.Add(ModeCatalog.Name(m));
            }
            return l.Count == 0 ? "nenhum dos conhecidos" : string.Join(", ", l.ToArray());
        }

        // ---- text ----------------------------------------------------------------------------

        private static string BuildText(DetectionResult d, CapabilityProfile p, EnvTestResults t, List<ConflictItem> conflicts,
            string appVersion, string nowUtc, Scrubber s)
        {
            DetectionRaw raw = d.Raw;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Relatório do modelo — " + Display.AppName + " " + Dash(s.S(appVersion)));
            sb.AppendLine("Gerado em " + Dash(nowUtc) + " (UTC)");
            sb.AppendLine("Este relatório não contém número de série, nome do computador, nome de usuário nem caminhos de pastas pessoais.");
            sb.AppendLine();

            sb.AppendLine("NOTEBOOK");
            sb.AppendLine("  Fabricante: " + Dash(s.S(raw.Manufacturer)));
            sb.AppendLine("  Modelo: " + Dash(s.S(raw.Model)));
            sb.AppendLine("  Família: " + Dash(s.S(raw.SystemFamily)));
            sb.AppendLine("  BIOS: " + Dash(s.S(raw.Bios)));
            sb.AppendLine("  Identificação (modelo + BIOS): " + Dash(s.S(d.Fingerprint)));
            sb.AppendLine("  Processador: " + Dash(s.S(raw.Cpu)));
            sb.AppendLine("  Placa(s) de vídeo: " + (raw.Gpus.Count == 0 ? "—" : string.Join("; ", raw.Gpus.ConvertAll(delegate(string g) { return s.S(g); }).ToArray())));
            sb.AppendLine("  Memória: " + Gib(raw.RamBytes));
            if (d.Table != null)
                sb.AppendLine("  Tabela de modelos: " + s.S(d.Table.Family) + " (" + d.Code + "), confiança " + d.Table.Confidence);
            else
                sb.AppendLine("  Tabela de modelos: modelo " + (d.Code ?? "sem código") + " fora da tabela (não listado)");
            sb.AppendLine();

            sb.AppendLine("O QUE O FIRMWARE ANUNCIA");
            sb.AppendLine("  Interface gamer (AcerGamingFunction): classe " + YesNo(raw.FirmwareClass) + ", respondeu " + YesNo(raw.FirmwareAnswered));
            sb.AppendLine("  Lista de sensores (GetGamingSysInfo 0): " + (d.Sensors.StatusOk
                ? HexMask(d.Sensors.Mask) + " — " + SensorList(d.Sensors) + (d.Sensors.Plausible ? "" : " [não confiável: " + Dash(d.Sensors.Reason) + "]")
                : "sem resposta — " + Dash(d.Sensors.Reason)));
            sb.AppendLine("  Resposta bruta: " + RawHex(raw.SensorBitmapRaw));
            sb.AppendLine("  Lista de perfis (GetGamingMiscSetting 0x0A): " + (d.Profiles.StatusOk
                ? HexMask(d.Profiles.Mask) + " — " + ProfileList(d.Profiles) + (d.Profiles.Plausible ? "" : " [não confiável: " + Dash(d.Profiles.Reason) + "]")
                : "sem resposta — " + Dash(d.Profiles.Reason)));
            sb.AppendLine("  Resposta bruta: " + RawHex(raw.ProfileBitmapRaw));
            sb.AppendLine("  Perfil atual (código): " + (raw.ProfileCode.HasValue ? raw.ProfileCode.Value.ToString(CultureInfo.InvariantCulture) : "—"));
            sb.AppendLine("  Interface de bateria (BatteryControl): " + YesNo(raw.BatteryClass));
            sb.AppendLine("  APGeAction / APGeEvent: " + YesNo(raw.ActionClass) + " / " + YesNo(raw.EventClass));
            sb.AppendLine("  Comandos de iluminação do teclado: " + YesNo(raw.KeyboardLightMethod) + "; controlador ENE KB5130 (HID): " + YesNo(raw.EneKb5130));
            SensorReading live = raw.Live ?? new SensorReading();
            sb.AppendLine("  Leitura no momento: CPU " + Temp(live.CpuTempC) + ", GPU " + Temp(live.GpuTempC)
                + ", ventoinhas " + Rpm(live.CpuFanRpm) + " / " + Rpm(live.GpuFanRpm));
            sb.AppendLine();

            sb.AppendLine("RESULTADO DOS TESTES");
            sb.AppendLine("  Tecla NitroSense/PredatorSense: " + StepText(t.Key.Status)
                + (t.Key.UserHasNoKey ? " (o usuário informou que não tem a tecla)" : "")
                + (string.IsNullOrEmpty(t.Key.CandidateId) ? "" : " — código " + t.Key.CandidateId));
            sb.AppendLine("  Ventoinhas: " + StepText(t.Fan.Status)
                + (t.Fan.Status == StepStatus.Passed || t.Fan.Status == StepStatus.Failed
                    ? " (CPU " + YesNo(t.Fan.CpuOk) + ", GPU " + YesNo(t.Fan.GpuOk) + "; rotação no máximo CPU " + t.Fan.MaxRpmCpu.ToString(CultureInfo.InvariantCulture)
                        + " / GPU " + t.Fan.MaxRpmGpu.ToString(CultureInfo.InvariantCulture) + ")"
                    : "")
                + (string.IsNullOrEmpty(t.Fan.Reason) ? "" : " — " + s.S(t.Fan.Reason)));
            if (t.Modes.Count == 0) sb.AppendLine("  Modos: não rodou");
            foreach (ModeStepResult m in t.Modes)
            {
                sb.AppendLine("  Modo " + ModeCatalog.Name(m.Mode) + ": " + StepText(m.Status)
                    + (m.Status == StepStatus.Passed ? (m.Proven ? " (o estado mudou e foi confirmado)" : " (o estado já era esse: sem prova)") : ""));
            }
            if (t.ReadOnly) sb.AppendLine("  O modo somente leitura estava ligado: os testes que escrevem no firmware não rodaram.");
            sb.AppendLine();

            sb.AppendLine("CAPACIDADES");
            foreach (Cap c in CapNames.All)
            {
                sb.AppendLine("  " + CapNames.Name(c) + ": " + CapNames.State(p.State(c)) + " — " + Dash(s.S(p.Reason(c))));
            }
            sb.AppendLine("  Ventoinhas: " + p.FanCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();

            sb.AppendLine("SERVIÇOS DA ACER");
            if (conflicts == null || conflicts.Count == 0) sb.AppendLine("  Nenhum serviço da lista encontrado.");
            else
            {
                foreach (ConflictItem c in conflicts)
                {
                    sb.AppendLine("  " + c.Name + ": " + (c.Running ? "rodando" : "parado") + ", início " + Dash(c.StartMode));
                }
            }
            return sb.ToString();
        }

        private static string Temp(double? v)
        {
            return v.HasValue ? Math.Round(v.Value).ToString(CultureInfo.InvariantCulture) + " °C" : "—";
        }

        private static string Rpm(int? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) + " RPM" : "—";
        }

        // ---- json ----------------------------------------------------------------------------

        private static string BuildJson(DetectionResult d, CapabilityProfile p, EnvTestResults t, List<ConflictItem> conflicts,
            string appVersion, string nowUtc, Scrubber s)
        {
            DetectionRaw raw = d.Raw;
            JsonOut j = new JsonOut();
            j.Begin();
            j.Str("appVersion", s.S(appVersion));
            j.Str("generatedUtc", nowUtc);
            j.Str("note", "no serial numbers, computer name, user name or personal paths");
            j.Obj("machine");
            j.Str("manufacturer", s.S(raw.Manufacturer));
            j.Str("model", s.S(raw.Model));
            j.Str("family", s.S(raw.SystemFamily));
            j.Str("bios", s.S(raw.Bios));
            j.Str("fingerprint", s.S(d.Fingerprint));
            j.Str("cpu", s.S(raw.Cpu));
            j.StrArray("gpus", raw.Gpus.ConvertAll(delegate(string g) { return s.S(g); }));
            j.Num("ramBytes", raw.RamBytes);
            j.Str("tableFamily", d.Table == null ? null : s.S(d.Table.Family));
            j.Str("tableConfidence", d.Table == null ? null : d.Table.Confidence.ToString());
            j.End();
            j.Obj("firmware");
            j.Bool("gamingClass", raw.FirmwareClass);
            j.Bool("gamingAnswered", raw.FirmwareAnswered);
            j.Str("sensorBitmap", d.Sensors.StatusOk ? HexMask(d.Sensors.Mask) : null);
            j.Str("sensorBitmapRaw", raw.SensorBitmapRaw.HasValue ? RawHex(raw.SensorBitmapRaw) : null);
            j.Bool("sensorBitmapPlausible", d.Sensors.Plausible);
            j.Str("sensorBitmapReason", d.Sensors.Reason);
            j.Str("profileBitmap", d.Profiles.StatusOk ? HexMask(d.Profiles.Mask) : null);
            j.Str("profileBitmapRaw", raw.ProfileBitmapRaw.HasValue ? RawHex(raw.ProfileBitmapRaw) : null);
            j.Bool("profileBitmapPlausible", d.Profiles.Plausible);
            j.Str("profileBitmapReason", d.Profiles.Reason);
            j.Num("currentProfileCode", raw.ProfileCode.HasValue ? (long?)raw.ProfileCode.Value : null);
            j.Bool("batteryClass", raw.BatteryClass);
            j.Bool("apgeActionClass", raw.ActionClass);
            j.Bool("apgeEventClass", raw.EventClass);
            j.Bool("keyboardLightMethod", raw.KeyboardLightMethod);
            j.Bool("eneKb5130", raw.EneKb5130);
            j.End();
            j.Obj("tests");
            j.Str("key", t.Key.Status.ToString());
            j.Str("keyCandidate", t.Key.CandidateId);
            j.Bool("userHasNoKey", t.Key.UserHasNoKey);
            j.Str("fan", t.Fan.Status.ToString());
            j.Bool("fanCpuOk", t.Fan.CpuOk);
            j.Bool("fanGpuOk", t.Fan.GpuOk);
            j.Num("fanMaxRpmCpu", t.Fan.MaxRpmCpu);
            j.Num("fanMaxRpmGpu", t.Fan.MaxRpmGpu);
            j.Str("fanReason", s.S(t.Fan.Reason));
            j.Bool("readOnly", t.ReadOnly);
            j.ArrayOfObjects("modes");
            foreach (ModeStepResult m in t.Modes)
            {
                j.Item();
                j.Str("mode", m.Mode.ToString());
                j.Str("status", m.Status.ToString());
                j.Bool("proven", m.Proven);
                j.End();
            }
            j.EndArray();
            j.End();
            j.ArrayOfObjects("capabilities");
            foreach (Cap c in CapNames.All)
            {
                j.Item();
                j.Str("id", c.ToString());
                j.Str("state", p.State(c).ToString());
                j.Str("reason", s.S(p.Reason(c)));
                j.End();
            }
            j.EndArray();
            j.Num("fanCount", p.FanCount);
            j.ArrayOfObjects("services");
            if (conflicts != null)
            {
                foreach (ConflictItem c in conflicts)
                {
                    j.Item();
                    j.Str("name", c.Name);
                    j.Bool("running", c.Running);
                    j.Str("startMode", c.StartMode);
                    j.End();
                }
            }
            j.EndArray();
            j.EndRoot();
            return j.ToString();
        }

        // A very small indented JSON writer (members and arrays of objects only).
        private sealed class JsonOut
        {
            private readonly StringBuilder sb = new StringBuilder();
            private readonly Stack<bool> first = new Stack<bool>(); // true = nothing written yet at this level
            private int depth;

            private void Indent()
            {
                sb.Append(' ', depth * 2);
            }

            private void Member(string key)
            {
                if (first.Peek()) first.Pop(); else { first.Pop(); sb.Append(','); }
                first.Push(false);
                sb.Append('\n');
                Indent();
                if (key != null) sb.Append('"').Append(Esc(key)).Append("\": ");
            }

            public void Begin()
            {
                sb.Append('{');
                depth = 1;
                first.Push(true);
            }

            public void EndRoot()
            {
                first.Pop();
                depth = 0;
                sb.Append("\n}\n");
            }

            public void Obj(string key)
            {
                Member(key);
                sb.Append('{');
                depth++;
                first.Push(true);
            }

            public void Item()
            {
                Member(null);
                sb.Append('{');
                depth++;
                first.Push(true);
            }

            public void End()
            {
                bool empty = first.Pop();
                depth--;
                if (!empty) { sb.Append('\n'); Indent(); }
                sb.Append('}');
            }

            public void ArrayOfObjects(string key)
            {
                Member(key);
                sb.Append('[');
                depth++;
                first.Push(true);
            }

            public void EndArray()
            {
                bool empty = first.Pop();
                depth--;
                if (!empty) { sb.Append('\n'); Indent(); }
                sb.Append(']');
            }

            public void Str(string key, string v)
            {
                Member(key);
                if (v == null) sb.Append("null");
                else sb.Append('"').Append(Esc(v)).Append('"');
            }

            public void StrArray(string key, List<string> v)
            {
                Member(key);
                sb.Append('[');
                for (int i = 0; i < v.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('"').Append(Esc(v[i])).Append('"');
                }
                sb.Append(']');
            }

            public void Num(string key, long v)
            {
                Member(key);
                sb.Append(v.ToString(CultureInfo.InvariantCulture));
            }

            public void Num(string key, long? v)
            {
                Member(key);
                sb.Append(v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "null");
            }

            public void Bool(string key, bool v)
            {
                Member(key);
                sb.Append(v ? "true" : "false");
            }

            public override string ToString()
            {
                return sb.ToString();
            }

            private static string Esc(string s)
            {
                StringBuilder o = new StringBuilder(s.Length + 8);
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': o.Append("\\\""); break;
                        case '\\': o.Append("\\\\"); break;
                        case '\n': o.Append("\\n"); break;
                        case '\r': o.Append("\\r"); break;
                        case '\t': o.Append("\\t"); break;
                        default:
                            if (c < ' ') o.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else o.Append(c);
                            break;
                    }
                }
                return o.ToString();
            }
        }
    }
}
