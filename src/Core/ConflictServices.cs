using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Forja.Core
{
    public sealed class ServiceInfo
    {
        public string Name;
        public string DisplayName;
        // As Windows reports it: "Running", "Stopped", ...
        public string State;
        // "Auto", "Manual", "Disabled" (Win32_Service.StartMode); anything else (or null) is "unreadable".
        public string StartMode;
        // Automatic (delayed start): StartMode says "Auto" for both.
        public bool Delayed;
        // The service executable (Win32_Service.PathName without arguments) and what its file version info says.
        public string PathName;
        public string Company;
        public string Product;
    }

    // The only operations the wizard may do to a service. There is deliberately no "delete" or
    // "uninstall": Acer's software is never removed, only stopped and set to Disabled (and put back).
    public interface IServiceControl
    {
        // null = the service is not installed. Never throws.
        ServiceInfo Query(string name);
        bool Stop(string name);
        bool Start(string name);
        // mode: "Automatic", "AutomaticDelayed", "Manual" or "Disabled".
        bool SetStartMode(string name, string mode);
    }

    // What a service was before the wizard disabled it, kept in config.json (or, before the app is installed, in
    // %ProgramData%) so Settings can undo it.
    [DataContract]
    public sealed class ServiceUndo
    {
        [DataMember] public string Name;
        [DataMember] public string PrevStartMode;
        [DataMember] public bool WasRunning;
        [DataMember] public bool PrevDelayed;
        [DataMember] public string Utc;
    }

    // The undo records as a file (the installer's run happens before a config exists).
    [DataContract]
    public sealed class ServiceUndoFile
    {
        [DataMember] public List<ServiceUndo> Records;

        public static string ToJson(List<ServiceUndo> records)
        {
            ServiceUndoFile f = new ServiceUndoFile();
            f.Records = records ?? new List<ServiceUndo>();
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ServiceUndoFile)).WriteObject(ms, f);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // Garbage or null reads as "no records".
        public static List<ServiceUndo> FromJson(string json)
        {
            List<ServiceUndo> none = new List<ServiceUndo>();
            if (string.IsNullOrEmpty(json)) return none;
            try
            {
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    ServiceUndoFile f = (ServiceUndoFile)new DataContractJsonSerializer(typeof(ServiceUndoFile)).ReadObject(ms);
                    return f == null || f.Records == null ? none : f.Records.FindAll(delegate(ServiceUndo u) { return ConflictServices.ValidRecord(u); });
                }
            }
            catch (Exception)
            {
                return none;
            }
        }
    }

    public sealed class ConflictItem
    {
        public string Name;
        public string DisplayName;
        public bool Running;
        public string StartMode;
        // The binary really is Acer's (path and file info); anything else is listed but never touched.
        public bool Recognized;
        // Recognized, and running or able to start again (not Disabled): it can still write to the same fans and profiles.
        public bool NeedsAction;
    }

    public sealed class DisableOutcome
    {
        public int Disabled;
        public int Failed;
        public readonly List<string> Messages = new List<string>();
    }

    public sealed class UndoOutcome
    {
        public int Restored;
        public int Failed;
        public bool PersistFailed;
        public readonly List<string> Messages = new List<string>();
    }

    // Detection and the user-triggered disable/undo of the Acer services that may fight this app
    // for the fans and profiles (docs\2026-11-17-pesquisa-modelos-acer.md, section 6).
    public static class ConflictServices
    {
        public static readonly string[] Names =
        {
            "ASMSvc", "AASSvc", "AcerServiceSvc", "AcerLightingService", "AcerDeviceEnablingServiceV2"
        };

        // The executable each of these services runs (from Win32_Service on an Acer notebook).
        private static string ExpectedFile(string name)
        {
            switch (name)
            {
                case "ASMSvc": return "acersystemcentralservice.exe";
                case "AASSvc": return "acercentralservice.exe";
                case "AcerServiceSvc": return "acerservicewrapper.exe";
                case "AcerLightingService": return "acerlightingservice.exe";
                case "AcerDeviceEnablingServiceV2": return "adesv2svc.exe";
                default: return "";
            }
        }

        public const string Explanation =
            "O NitroSense, o PredatorSense e os serviços da Acer também mexem nas ventoinhas e nos modos. "
            + "Se ficarem ligados junto com o SenseMonitor, os dois disputam o controle. "
            + "Você pode pedir para o SenseMonitor parar esses serviços e deixá-los desativados. "
            + "Nada é desinstalado, e dá para desfazer em Configurações a qualquer momento.";

        // The records of Settings "Desfazer" are trusted only for Acer's five services, and only to put back a mode
        // the wizard could have recorded (Auto or Manual). A record for any other service, or "Disabled", is a plant.
        public static bool ValidRecord(ServiceUndo u)
        {
            if (u == null || string.IsNullOrEmpty(u.Name)) return false;
            bool known = false;
            foreach (string n in Names)
            {
                if (string.Equals(n, u.Name, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            }
            if (!known) return false;
            return string.Equals(u.PrevStartMode, "Auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.PrevStartMode, "Manual", StringComparison.OrdinalIgnoreCase);
        }

        // "acer" as the start of a word (a path segment, a company, a driver package), never inside another word.
        private static bool HasAcerToken(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            int i = 0;
            while (i < lower.Length)
            {
                while (i < lower.Length && !char.IsLetterOrDigit(lower[i])) i++;
                int start = i;
                while (i < lower.Length && char.IsLetterOrDigit(lower[i])) i++;
                if (i > start && string.CompareOrdinal(lower, start, "acer", 0, 4) == 0) return true;
            }
            return false;
        }

        // Serialises disable/undo against each other (the Settings button runs on a pool thread).
        private static readonly object Sync = new object();

        // Is this really Acer's service? Short names like ASMSvc are generic, so the binary is checked:
        //  A. the file's CompanyName or ProductName says Acer AND it lives in an Acer folder or in the Windows driver
        //     store (signed driver packages);
        //  B. the exact executable of that service inside a driver-store package whose name says Acer (the
        //     acerservicecomponent package ships a generic service wrapper whose company is not Acer).
        // No path = not recognized.
        public static bool Recognize(string name, ServiceInfo i)
        {
            if (i == null || string.IsNullOrEmpty(i.PathName)) return false;
            string path = i.PathName.Replace('/', '\\').Trim().Trim('"');
            string lower = path.ToLowerInvariant();
            string[] seg = lower.Split('\\');
            bool acerFolder = false;
            for (int k = 0; k < seg.Length - 1; k++)
            {
                if (HasAcerToken(seg[k])) { acerFolder = true; break; }
            }
            string marker = "\\driverstore\\filerepository\\";
            int at = lower.IndexOf(marker, StringComparison.Ordinal);
            bool driverStore = at >= 0;
            string package = "";
            if (driverStore)
            {
                string rest = lower.Substring(at + marker.Length);
                int cut = rest.IndexOf('\\');
                package = cut > 0 ? rest.Substring(0, cut) : "";
            }
            string company = ((i.Company ?? "") + " " + (i.Product ?? "")).ToLowerInvariant();
            bool acerCompany = HasAcerToken(company);
            if (acerCompany && (acerFolder || driverStore)) return true;
            string file = seg[seg.Length - 1];
            return driverStore && HasAcerToken(package) && file == ExpectedFile(name);
        }

        public static List<ConflictItem> Scan(IServiceControl sc)
        {
            List<ConflictItem> l = new List<ConflictItem>();
            if (sc == null) return l;
            foreach (string name in Names)
            {
                ServiceInfo i = null;
                try { i = sc.Query(name); }
                catch (Exception) { i = null; }
                if (i == null) continue;
                ConflictItem c = new ConflictItem();
                c.Name = name;
                c.DisplayName = string.IsNullOrEmpty(i.DisplayName) ? name : i.DisplayName;
                c.Running = string.Equals(i.State, "Running", StringComparison.OrdinalIgnoreCase);
                c.StartMode = i.StartMode;
                c.Recognized = Recognize(name, i);
                c.NeedsAction = c.Recognized && (c.Running || !string.Equals(i.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase));
                l.Add(c);
            }
            return l;
        }

        public static bool AnyNeedsAction(List<ConflictItem> items)
        {
            if (items == null) return false;
            foreach (ConflictItem i in items)
            {
                if (i.NeedsAction) return true;
            }
            return false;
        }

        private static bool ReadableMode(string m)
        {
            return string.Equals(m, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(m, "Manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m, "Disabled", StringComparison.OrdinalIgnoreCase);
        }

        // persist saves the records (true = saved; the installer's run writes them to %ProgramData%). The previous
        // state of a service is saved BEFORE the service is touched; if it cannot be saved the service is left alone.
        // cfg.ServiceUndo is replaced by a new list at every change (never mutated in place, so a concurrent Save of
        // the config never sees a half-edited list).
        public static DisableOutcome DisableAll(IServiceControl sc, Config cfg, Func<bool> persist, IJournal journal, string nowUtc)
        {
            DisableOutcome r = new DisableOutcome();
            if (sc == null || cfg == null) return r;
            if (journal == null) journal = NullJournal.Instance;
            lock (Sync)
            {
                if (cfg.ServiceUndo == null) cfg.ServiceUndo = new List<ServiceUndo>();
                foreach (ConflictItem item in Scan(sc))
                {
                    if (!item.NeedsAction) continue;
                    ServiceInfo info = null;
                    try { info = sc.Query(item.Name); }
                    catch (Exception) { info = null; }
                    if (info == null || !ReadableMode(info.StartMode))
                    {
                        r.Failed++;
                        r.Messages.Add(item.DisplayName + ": não consegui ler como ele inicia; não vou mexer.");
                        journal.Write(JournalKind.Action, "serviço " + item.Name + ": modo de início ilegível; nada alterado");
                        continue;
                    }
                    ServiceUndo rec = FindRecord(cfg.ServiceUndo, item.Name);
                    bool added = false;
                    if (rec == null)
                    {
                        rec = new ServiceUndo();
                        rec.Name = item.Name;
                        rec.PrevStartMode = NormalizeMode(info.StartMode);
                        rec.WasRunning = item.Running;
                        rec.PrevDelayed = info.Delayed && rec.PrevStartMode == "Auto";
                        rec.Utc = nowUtc;
                        List<ServiceUndo> grown = new List<ServiceUndo>(cfg.ServiceUndo);
                        grown.Add(rec);
                        cfg.ServiceUndo = grown;
                        added = true;
                    }
                    if (!Save(persist))
                    {
                        if (added) cfg.ServiceUndo = Without(cfg.ServiceUndo, rec);
                        r.Failed++;
                        r.Messages.Add(item.DisplayName + ": não foi possível guardar o estado anterior; o serviço não foi alterado.");
                        journal.Write(JournalKind.Action, "serviço " + item.Name + ": estado anterior não pôde ser salvo; nada alterado");
                        continue;
                    }

                    // Disabled first, so that nothing starts it again while it is being stopped.
                    string name = item.Name;
                    bool modeOk = Safe(delegate { return sc.SetStartMode(name, "Disabled"); });
                    if (!modeOk)
                    {
                        if (added)
                        {
                            cfg.ServiceUndo = Without(cfg.ServiceUndo, rec);
                            Save(persist);
                        }
                        r.Failed++;
                        r.Messages.Add(item.DisplayName + ": o Windows não deixou desativar o serviço.");
                        journal.Write(JournalKind.Action, "serviço " + item.Name + ": não foi possível desativar");
                        continue;
                    }
                    bool stopOk = !item.Running || Safe(delegate { return sc.Stop(name); });
                    if (!stopOk)
                    {
                        r.Failed++;
                        r.Messages.Add(item.DisplayName + ": ficou desativado, mas continua rodando até reiniciar o notebook.");
                        journal.Write(JournalKind.Action, "serviço " + item.Name + ": desativado; parada falhou (continua rodando até reiniciar)");
                        continue;
                    }
                    r.Disabled++;
                    journal.Write(JournalKind.Action, "serviço " + item.Name + ": desativado e parado (antes: " + rec.PrevStartMode + (rec.PrevDelayed ? " atrasado" : "") + (rec.WasRunning ? ", rodando" : ", parado") + ")");
                }
            }
            return r;
        }

        // Puts every recorded service back to what it was. A record is dropped when the service was
        // restored, or is no longer installed; kept when something failed (it can be tried again).
        public static UndoOutcome UndoAll(IServiceControl sc, Config cfg, Func<bool> persist, IJournal journal)
        {
            UndoOutcome r = new UndoOutcome();
            if (sc == null || cfg == null) return r;
            if (journal == null) journal = NullJournal.Instance;
            lock (Sync)
            {
                if (cfg.ServiceUndo == null || cfg.ServiceUndo.Count == 0) return r;
                List<ServiceUndo> keep = new List<ServiceUndo>();
                foreach (ServiceUndo rec in new List<ServiceUndo>(cfg.ServiceUndo))
                {
                    if (rec == null) continue;
                    if (!ValidRecord(rec))
                    {
                        journal.Write(JournalKind.Action, "registro de desfazer ignorado (não é um serviço da Acer conhecido ou o modo não é válido): " + (rec.Name ?? "?"));
                        continue;
                    }
                    ServiceInfo i = null;
                    try { i = sc.Query(rec.Name); }
                    catch (Exception) { i = null; }
                    if (i == null)
                    {
                        journal.Write(JournalKind.Action, "serviço " + rec.Name + ": não existe mais; registro descartado");
                        continue;
                    }
                    if (!Recognize(rec.Name, i))
                    {
                        // The name now belongs to something that is not Acer's: never touched.
                        journal.Write(JournalKind.Action, "serviço " + rec.Name + ": o programa atual não é da Acer; registro descartado, nada alterado");
                        continue;
                    }
                    string mode = string.Equals(rec.PrevStartMode, "Disabled", StringComparison.OrdinalIgnoreCase) ? "Disabled"
                        : (string.Equals(rec.PrevStartMode, "Manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : (rec.PrevDelayed ? "AutomaticDelayed" : "Automatic"));
                    string name = rec.Name;
                    if (!Safe(delegate { return sc.SetStartMode(name, mode); }))
                    {
                        r.Failed++;
                        r.Messages.Add((string.IsNullOrEmpty(i.DisplayName) ? rec.Name : i.DisplayName) + ": o Windows não deixou restaurar o serviço.");
                        journal.Write(JournalKind.Action, "serviço " + rec.Name + ": não foi possível restaurar");
                        keep.Add(rec);
                        continue;
                    }
                    if (rec.WasRunning && !Safe(delegate { return sc.Start(name); }))
                    {
                        r.Messages.Add((string.IsNullOrEmpty(i.DisplayName) ? rec.Name : i.DisplayName) + ": restaurado, mas não iniciou agora (inicia no próximo boot ou pelo Windows).");
                    }
                    r.Restored++;
                    journal.Write(JournalKind.Action, "serviço " + rec.Name + ": restaurado para " + mode + (rec.WasRunning ? " e iniciado" : ""));
                }
                cfg.ServiceUndo = keep;
                if (!Save(persist)) r.PersistFailed = true;
            }
            return r;
        }

        private static ServiceUndo FindRecord(List<ServiceUndo> list, string name)
        {
            foreach (ServiceUndo u in list)
            {
                if (u != null && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)) return u;
            }
            return null;
        }

        private static List<ServiceUndo> Without(List<ServiceUndo> list, ServiceUndo rec)
        {
            List<ServiceUndo> l = new List<ServiceUndo>(list);
            l.Remove(rec);
            return l;
        }

        // WMI says "Auto"; ChangeStartMode wants "Automatic" (undo maps it back).
        private static string NormalizeMode(string m)
        {
            if (string.Equals(m, "Disabled", StringComparison.OrdinalIgnoreCase)) return "Disabled";
            if (string.Equals(m, "Manual", StringComparison.OrdinalIgnoreCase)) return "Manual";
            return "Auto";
        }

        private static bool Safe(Func<bool> f)
        {
            try { return f(); }
            catch (Exception) { return false; }
        }

        private static bool Save(Func<bool> persist)
        {
            if (persist == null) return true;
            try { return persist(); }
            catch (Exception) { return false; }
        }
    }
}
