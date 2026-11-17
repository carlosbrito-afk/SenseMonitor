using System;
using System.IO;
using System.Management;
using System.Text;

namespace Forja.Probe
{
    // Read-only probe: calls ONLY Get* methods of AcerGamingFunction. Needs admin.
    public static class Program
    {
        private static StringBuilder sb = new StringBuilder();
        private static ManagementObject inst;

        private static void Say(string s)
        {
            Console.WriteLine(s);
            sb.AppendLine(s);
        }

        private static void Get(string method, uint input, bool hasInput)
        {
            string label = hasInput ? string.Format("{0}(0x{1:X})", method, input) : method + "()";
            try
            {
                ManagementBaseObject p = null;
                if (hasInput)
                {
                    p = inst.GetMethodParameters(method);
                    p["gmInput"] = input;
                }
                ManagementBaseObject o = inst.InvokeMethod(method, p, null);
                object v = o == null ? null : o["gmOutput"];
                if (v == null) Say(label + " -> (null)");
                else Say(string.Format("{0} -> 0x{1:X16}", label, Convert.ToUInt64(v)));
            }
            catch (Exception e)
            {
                Say(label + " -> ERRO " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static ulong? Read(string method, uint input)
        {
            ManagementBaseObject p = inst.GetMethodParameters(method);
            p["gmInput"] = input;
            ManagementBaseObject o = inst.InvokeMethod(method, p, null);
            return o == null || o["gmOutput"] == null ? (ulong?)null : Convert.ToUInt64(o["gmOutput"]);
        }

        private static void Write(string method, ulong input)
        {
            ManagementBaseObject p = inst.GetMethodParameters(method);
            p["gmInput"] = input;
            ManagementBaseObject o = inst.InvokeMethod(method, p, null);
            StringBuilder r = new StringBuilder();
            if (o != null) foreach (PropertyData pd in o.Properties) r.Append(" " + pd.Name + "=" + pd.Value);
            Say(string.Format("{0}(0x{1:X}) -> retorno:{2}", method, input, r));
        }

        // Only the two safe-state writes: profile balanced, fans auto. Each followed by a Get.
        private static void SafeWrite()
        {
            Say("== ESCRITA SEGURA ==");
            Say("antes: misc 0xB = " + Fmt(Read("GetGamingMiscSetting", 0x0B)) + ", fan behavior(0x9) = " + Fmt(Read("GetGamingFanBehavior", 0x9)));
            Write("SetGamingMiscSetting", 0x0B | (0x01UL << 8));
            Say("depois: misc 0xB = " + Fmt(Read("GetGamingMiscSetting", 0x0B)));
            Write("SetGamingFanBehavior", 0x1UL | 0x8UL | (1UL << 16) | (1UL << 22));
            Say("depois: fan behavior(0x9) = " + Fmt(Read("GetGamingFanBehavior", 0x9)));
        }

        private static string Fmt(ulong? v)
        {
            return v.HasValue ? string.Format("0x{0:X16}", v.Value) : "(null)";
        }

        public static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : null;
            Say("Forja probe (somente Get*) " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                using (ManagementClass mc = new ManagementClass(@"root\WMI", "AcerGamingFunction", null))
                {
                    foreach (ManagementObject mo in mc.GetInstances())
                    {
                        inst = mo;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Say("ERRO ao abrir a classe: " + e.GetType().Name + ": " + e.Message);
            }
            if (inst == null)
            {
                Say("Nenhuma instancia de AcerGamingFunction (precisa de admin?)");
            }
            else if (args.Length > 1 && args[1] == "--safe-write")
            {
                try { SafeWrite(); }
                catch (Exception e) { Say("ERRO na escrita segura: " + e.GetType().Name + ": " + e.Message); }
            }
            else
            {
                Say("== GetGamingSysInfo, consulta candidata 0x02|(id<<8) ==");
                for (uint id = 0x01; id <= 0x0F; id++) Get("GetGamingSysInfo", 0x02 | (id << 8), true);
                Say("== GetGamingSysInfo, consulta alternativa cmd|(id<<8), cmd 0x01 e 0x03..0x05 ==");
                uint[] cmds = new uint[] { 0x01, 0x03, 0x04, 0x05 };
                foreach (uint c in cmds) for (uint id = 0x01; id <= 0x0F; id++) Get("GetGamingSysInfo", c | (id << 8), true);
                Say("== GetGamingSysInfo, entrada crua = id ==");
                for (uint id = 0x00; id <= 0x0F; id++) Get("GetGamingSysInfo", id, true);
                Say("== GetGamingMiscSetting 0x00..0x10 ==");
                for (uint s = 0x00; s <= 0x10; s++) Get("GetGamingMiscSetting", s, true);
                Say("== GetGamingProfileSetting ==");
                for (uint s = 0x00; s <= 0x0F; s++) Get("GetGamingProfileSetting", s, true);
                Say("== GetGamingProfile ==");
                for (uint s = 0x00; s <= 0x03; s++) Get("GetGamingProfile", s, true);
                Say("== GetGamingFanBehavior ==");
                uint[] fb = new uint[] { 0x00, 0x01, 0x08, 0x09, 0xFFFF };
                foreach (uint s in fb) Get("GetGamingFanBehavior", s, true);
                Say("== GetGamingFanSpeed ==");
                uint[] fs = new uint[] { 0x00, 0x01, 0x02, 0x04, 0x08 };
                foreach (uint s in fs) Get("GetGamingFanSpeed", s, true);
                Say("== GetGamingFanTable ==");
                Get("GetGamingFanTable", 0, false);
            }
            if (outPath != null)
            {
                try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false)); }
                catch (Exception e) { Console.WriteLine("Nao gravou " + outPath + ": " + e.Message); }
            }
            return 0;
        }
    }
}
