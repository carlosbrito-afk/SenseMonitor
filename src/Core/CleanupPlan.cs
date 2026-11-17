using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Forja.Core
{
    public enum CleanupTargetId
    {
        UserTemp,
        WindowsTemp,
        UpdateDownload,
        DeliveryOptimization,
        Thumbnails,
        BrowserCaches,
        ShaderCaches,
        AppCaches,
        DnsCache,
        ErrorReports,
        WindowsLogs,
        EventLogs
    }

    public enum CleanupKind
    {
        Folders,    // measured and deleted file by file
        Action      // a command, not a folder: nothing to measure, the result is OK or failure
    }

    // Where the fixed list of cleanup folders lives. The real values come from the system;
    // tests point them into a sandbox.
    public class CleanupEnvironment
    {
        public string UserTemp;
        public string WindowsTemp;
        public string UpdateDownload;
        public string DeliveryOptimization;
        public string ThumbFolder;
        // Base folders of the cache and log targets. A null one drops the targets built on it.
        public string LocalAppData;
        public string AppData;          // roaming
        public string ProgramData;
        public string WindowsDir;       // holds Logs
        public string System32;         // ipconfig.exe and wevtutil.exe are run from here, by full path
        public bool IsAdmin;            // false until the caller says otherwise
        public int MinAgeHours = 24;
        // True while Windows Update may be downloading or installing (or when that cannot be told).
        public Func<bool> UpdateServiceBusy;
        // Same for the Delivery Optimization service (DoSvc). A throw counts as busy.
        public Func<bool> DeliveryOptimizationBusy;
        // Is a process with this name (no ".exe") running? A throw counts as running. Null: not running.
        public Func<string, bool> ProcessRunning;
        // Runs a command and returns its exit code. A throw counts as a failure. Null: cannot run.
        public Func<CleanupCommand, int> RunCommand;
        // Every event log channel with its kind and record count (read-only).
        // The token lets a cancelled analysis or clean stop the read.
        public Func<CancellationToken, List<EventChannelInfo>> EventChannels;
        public Func<DateTime> UtcNow;
    }

    // One folder of a target, with what applies only to it.
    public class CleanupRoot
    {
        public string Path;
        // When set, no folder from here down to Path may be a junction or link.
        public string Base;
        // null: everything under Path. Otherwise only the matching files directly in Path.
        public string[] Patterns;
        // Names that stay (exact, e.g. the log in use) and name prefixes that stay (crash reports).
        public string[] KeepNames;
        public string[] KeepPrefixes;
        // Skipped while one of the processes runs ("Chrome": chrome).
        public string Owner;
        public string[] OwnerProcesses;
        public bool RequiresAdmin;

        public bool Recursive
        {
            get { return Patterns == null; }
        }
    }

    public class CleanupTarget
    {
        public CleanupTargetId Id;
        public string Group;
        public string Name;
        public string Description;
        public CleanupKind Kind = CleanupKind.Folders;
        public readonly List<CleanupRoot> Roots = new List<CleanupRoot>();
        public bool RequiresAdmin;
        public bool Optional;
        public int MinAgeHours;
        public bool Selected;
        public bool Available = true;
        public string Note;              // why it is not available, or something worth knowing
        public string CloseHint;         // "Feche o navegador": what to do when an owner is running
        public long Bytes;
        public int Files;
        public int SkippedRecent;
        public bool Analyzed;
        public string Summary;           // right-hand text for an action target, set by the analysis
    }

    public class CleanupPlan
    {
        public const string GroupBasics = "Temporários e atualizações";
        public const string GroupCaches = "Caches";
        public const string GroupLogs = "Logs e relatórios";

        public readonly List<CleanupTarget> Targets = new List<CleanupTarget>();
        public bool Cancelled;
        private List<string> prot;   // protected folders, read once per plan

        public CleanupTarget Find(CleanupTargetId id)
        {
            foreach (CleanupTarget t in Targets)
            {
                if (t.Id == id) return t;
            }
            return null;
        }

        public long SelectedBytes
        {
            get
            {
                long n = 0;
                foreach (CleanupTarget t in Targets) if (t.Selected && t.Available) n += t.Bytes;
                return n;
            }
        }

        public int SelectedFiles
        {
            get
            {
                int n = 0;
                foreach (CleanupTarget t in Targets) if (t.Selected && t.Available) n += t.Files;
                return n;
            }
        }

        // Selected, available action targets (DNS flush, event logs): they have no files to count.
        public int SelectedActions
        {
            get
            {
                int n = 0;
                foreach (CleanupTarget t in Targets) if (t.Selected && t.Available && t.Kind == CleanupKind.Action) n++;
                return n;
            }
        }

        public int SelectedFolders
        {
            get { return SelectedCount - SelectedActions; }
        }

        // Is there anything to run: files to delete, or an action ticked. False after a cancelled analysis.
        public bool CanClean
        {
            get { return !Cancelled && (SelectedFiles > 0 || SelectedActions > 0); }
        }

        // "Selecionado: 1,0 MB em 2 locais e 1 ação (estimativa; ...)": folders and actions counted apart.
        public string SelectionSummary()
        {
            int folders = SelectedFolders;
            int actions = SelectedActions;
            string text = "Selecionado: ";
            if (folders > 0) text += SizeFormat.Format(SelectedBytes) + " em " + folders + (folders == 1 ? " local" : " locais");
            if (actions > 0) text += (folders > 0 ? " e " : "") + actions + (actions == 1 ? " ação" : " ações");
            if (folders == 0 && actions == 0) text += "nada";
            if (folders > 0) text += " (estimativa; o que estiver em uso fica)";
            return text + ".";
        }

        public int SelectedCount
        {
            get
            {
                int n = 0;
                foreach (CleanupTarget t in Targets) if (t.Selected && t.Available) n++;
                return n;
            }
        }

        // The fixed list. A folder that is not on the safe list (Minidump, documents, drive
        // roots, folders that contain them) is never offered.
        public static CleanupPlan Create(CleanupEnvironment env)
        {
            if (env == null) throw new ArgumentNullException("env");
            CleanupPlan plan = new CleanupPlan();
            plan.prot = CleanupSafety.ProtectedSnapshot();
            string age = "Itens das últimas " + env.MinAgeHours + " horas e arquivos em uso são mantidos.";

            plan.AddOne(env, CleanupTargetId.UserTemp, GroupBasics, "Arquivos temporários do usuário",
                "Restos de instalações e programas. " + age,
                env.UserTemp, null, false, false, true);
            plan.AddOne(env, CleanupTargetId.WindowsTemp, GroupBasics, "Temporários do Windows",
                "Pasta Temp do sistema. " + age,
                env.WindowsTemp, null, true, false, true);
            plan.AddOne(env, CleanupTargetId.UpdateDownload, GroupBasics, "Downloads do Windows Update",
                "Pacotes já baixados; o Windows baixa de novo se precisar. " + age + " Fica de fora se o Windows Update estiver ativo.",
                env.UpdateDownload, null, true, false, true);
            plan.AddOne(env, CleanupTargetId.DeliveryOptimization, GroupBasics, "Cache de Otimização de Entrega",
                "Pedaços de atualizações guardados para compartilhar. " + age + " Fica de fora se a Otimização de Entrega estiver ativa.",
                env.DeliveryOptimization, null, true, false, true);
            plan.AddOne(env, CleanupTargetId.Thumbnails, GroupBasics, "Cache de miniaturas",
                "O Explorer recria as miniaturas aos poucos. " + age + " O ganho costuma ser pequeno.",
                env.ThumbFolder, new string[] { "thumbcache_*.db" }, false, true, false);

            plan.AddMany(env, CleanupTargetId.BrowserCaches, GroupCaches, "Cache dos navegadores",
                "Chrome, Edge, Brave, Opera e Firefox: só o cache de páginas e imagens. Senhas, cookies, histórico, sessões e extensões ficam. Os sites carregam um pouco mais devagar na primeira visita. " + age + " Navegador aberto fica de fora.",
                CleanupLocator.BrowserRoots(env), "Feche o navegador", false, false, true);
            plan.AddMany(env, CleanupTargetId.ShaderCaches, GroupCaches, "Cache de shaders da placa de vídeo",
                "DirectX, NVIDIA, Intel e AMD. Os jogos recriam esses arquivos; a primeira abertura de cada jogo pode travar um pouco. Jogos, saves e drivers não são tocados. " + age,
                CleanupLocator.ShaderRoots(env), null, false, true, false);
            plan.AddMany(env, CleanupTargetId.AppCaches, GroupCaches, "Cache de aplicativos",
                "Discord, Steam (cache web) e Teams: só o cache. Contas, mensagens e biblioteca de jogos ficam, mas as páginas web do cliente Steam (loja e comunidade) podem pedir login de novo; os apps recarregam imagens e dados na próxima abertura. " + age + " App aberto fica de fora.",
                CleanupLocator.AppRoots(env), "Feche o aplicativo", false, true, false);
            plan.AddAction(env, CleanupTargetId.DnsCache, GroupCaches, "Cache de DNS",
                "Esvazia a lista de endereços de sites que o Windows guardou (ipconfig /flushdns). Nenhum arquivo é apagado; o Windows refaz a lista sozinho.",
                true, false, true);

            plan.AddMany(env, CleanupTargetId.ErrorReports, GroupLogs, "Relatórios de erro do Windows",
                "Relatórios de falhas de programas já enviados ou na fila (WER). Relatórios de tela azul (Kernel_, LiveKernelEvent) e a pasta Minidump ficam. " + age,
                CleanupLocator.ErrorReportRoots(env), null, false, false, true);
            plan.AddMany(env, CleanupTargetId.WindowsLogs, GroupLogs, "Logs antigos do Windows",
                "Arquivos .log e .cab antigos do CBS, logs do DISM e arquivos .etl do Windows Update. O CBS.log atual fica e a pasta Panther não é tocada. " + age,
                CleanupLocator.WindowsLogRoots(env), null, true, false, true);
            plan.AddAction(env, CleanupTargetId.EventLogs, GroupLogs, "Registros do Visualizador de Eventos",
                "Limpa só uma lista fixa de registros de aplicativos, rede, Loja e telemetria do Windows (por exemplo PowerShell, Wi-Fi e Loja). O resto fica: System, Application, Security e Setup ficam de propósito, assim como tudo de energia (Kernel-Power), WHEA, drivers, disco e travamentos, que servem para investigar telas azuis e a aba Saúde. Não dá para desfazer.",
                true, true, false);
            return plan;
        }

        private CleanupTarget NewTarget(CleanupEnvironment env, CleanupTargetId id, string group, string name, string desc,
            bool admin, bool optional, bool selected)
        {
            CleanupTarget t = new CleanupTarget();
            t.Id = id;
            t.Group = group;
            t.Name = name;
            t.Description = desc;
            t.RequiresAdmin = admin;
            t.Optional = optional;
            t.MinAgeHours = env.MinAgeHours;
            t.Selected = selected;
            Targets.Add(t);
            return t;
        }

        private static void Unavailable(CleanupTarget t, string note)
        {
            t.Available = false;
            t.Selected = false;
            t.Note = note;
        }

        // A target with one folder.
        private void AddOne(CleanupEnvironment env, CleanupTargetId id, string group, string name, string desc, string path,
            string[] patterns, bool admin, bool optional, bool selected)
        {
            CleanupTarget t = NewTarget(env, id, group, name, desc, admin, optional, selected);
            CleanupRoot r = new CleanupRoot();
            r.Path = path;
            r.Patterns = patterns;
            r.RequiresAdmin = admin;
            t.Roots.Add(r);
            if (CleanupSafety.IsUnsafeRoot(path, prot)) Unavailable(t, "Pasta fora da lista segura. O app não mexe nela.");
        }

        // A target built from several folders. Folders that are never safe are left out.
        private void AddMany(CleanupEnvironment env, CleanupTargetId id, string group, string name, string desc,
            List<CleanupRoot> roots, string closeHint, bool admin, bool optional, bool selected)
        {
            CleanupTarget t = NewTarget(env, id, group, name, desc, admin, optional, selected);
            t.CloseHint = closeHint;
            bool dropped = false;
            foreach (CleanupRoot r in roots)
            {
                if (CleanupSafety.IsUnsafeRoot(r.Path, prot)) { dropped = true; continue; }
                t.Roots.Add(r);
            }
            if (t.Roots.Count == 0)
                Unavailable(t, dropped ? "Pasta fora da lista segura. O app não mexe nela." : "Não encontrado neste PC. Não há nada para limpar aqui.");
        }

        private void AddAction(CleanupEnvironment env, CleanupTargetId id, string group, string name, string desc,
            bool admin, bool optional, bool selected)
        {
            CleanupTarget t = NewTarget(env, id, group, name, desc, admin, optional, selected);
            t.Kind = CleanupKind.Action;
        }
    }

    public class CleanupProgress
    {
        public string Phase;        // "Analisando" or "Limpando"
        public string TargetName;
        public int TargetIndex;     // 1-based
        public int TargetCount;
        public long Files;          // processed in the current target
        public bool IsAction;       // a command, not a folder: Files counts its steps, not files
        public long Bytes;
    }

    public class CleanupTargetResult
    {
        public CleanupTargetId Id;
        public string Name;
        public long FreedBytes;
        public int DeletedFiles;
        public int SkippedCount;     // files kept (recent, in use, links, protected); for an action, items that failed
        public bool IsAction;
        public bool ActionOk;        // actions only: did it work
        public bool Cancelled;       // actions only: stopped by the user part-way
        public string Message;       // actions only: what happened, in plain words
    }

    public class CleanupResult
    {
        public long FreedBytes;
        public int DeletedFiles;
        public int SkippedCount;
        public int ActionsDone;
        public int ActionsFailed;
        public bool Cancelled;
        public readonly List<string> Notes = new List<string>();     // what did not happen
        public readonly List<string> Done = new List<string>();      // what an action did
        public readonly List<CleanupTargetResult> PerTarget = new List<CleanupTargetResult>();
    }

    public interface ICleanupService
    {
        CleanupPlan Analyze(Action<CleanupProgress> progress, CancellationToken ct);
        CleanupResult Clean(CleanupPlan plan, Action<CleanupProgress> progress, CancellationToken ct);
    }

    public static class SizeFormat
    {
        private static readonly CultureInfo Pt = new CultureInfo("pt-BR");

        public static string Format(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int i = -1;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }
            return v.ToString("0.0", Pt) + " " + units[i];
        }
    }

    // Hard limits that hold even when a caller (or a bug) selects something it should not.
    public static class CleanupSafety
    {
        private static string Full(string path)
        {
            try
            {
                string f = Path.GetFullPath(path);
                string root = Path.GetPathRoot(f);
                if (f.Length > root.Length) f = f.TrimEnd('\\', '/');
                return f;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Inside(string root, string path)
        {
            if (root.Length == 0 || path.Length <= root.Length) return false;
            string r = root.EndsWith("\\") ? root : root + "\\";
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ProtectedFolders()
        {
            System.Threading.Interlocked.Increment(ref protectedFoldersCalls);
            List<string> l = new List<string>();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Environment.SpecialFolder[] known =
            {
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos
            };
            foreach (Environment.SpecialFolder k in known)
            {
                string p = Environment.GetFolderPath(k);
                if (!string.IsNullOrEmpty(p)) l.Add(p);
            }
            if (!string.IsNullOrEmpty(profile))
            {
                l.Add(Path.Combine(profile, "Downloads"));
                l.Add(Path.Combine(profile, "Documents"));
            }
            string[] oneDrive = { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" };
            foreach (string n in oneDrive)
            {
                string v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrEmpty(v)) l.Add(v);
            }
            List<string> full = new List<string>();
            foreach (string p in l)
            {
                string f = Full(p);
                if (f != null) full.Add(f);
            }
            return full;
        }

        // How many times the protected-folder list was built (diagnostics for the tests: a
        // sweep builds it once, not once per file).
        private static int protectedFoldersCalls;

        public static int ProtectedFoldersCalls
        {
            get { return protectedFoldersCalls; }
        }

        // The protected folders as of now. Take it once per sweep and pass it to IsForbidden.
        public static List<string> ProtectedSnapshot()
        {
            return ProtectedFolders();
        }

        // Folder or file names that are never deletable, wherever they sit in a path: crash
        // dumps (the blue screen investigation), Prefetch (cleaning it slows the next boots),
        // the recycle bin, the previous Windows install, restore points and Panther.
        private static readonly string[] NeverTouchNames =
        {
            "Minidump", "LiveKernelReports", "MEMORY.DMP", "Prefetch", "$Recycle.Bin", "Recycler",
            "Windows.old", "System Volume Information", "Panther"
        };

        // Blue screen report folders inside the Windows Error Reporting queues: their
        // Report.wer holds the bugcheck code and parameters, so they stay.
        public static readonly string[] CrashReportPrefixes = { "Kernel_", "LiveKernelEvent", "BlueScreen", "WHEA" };

        // Crash dumps and the user's own files are never deletable.
        public static bool IsForbidden(string path)
        {
            return IsForbidden(path, null);
        }

        public static bool IsForbidden(string path, List<string> protectedFolders)
        {
            string f = string.IsNullOrEmpty(path) ? null : Full(path);
            if (f == null) return true;
            string root = Path.GetPathRoot(f);
            foreach (string seg in f.Substring(root.Length).Split('\\'))
            {
                foreach (string bad in NeverTouchNames)
                {
                    if (Same(seg, bad)) return true;
                }
            }
            foreach (string p in protectedFolders ?? ProtectedFolders())
            {
                if (Same(f, p) || Inside(p, f)) return true;
            }
            return false;
        }

        // A cleanup root must not be forbidden, a drive root, the Windows folder itself, or a
        // folder that contains a protected one (the user profile contains Documents).
        public static bool IsUnsafeRoot(string path)
        {
            return IsUnsafeRoot(path, ProtectedFolders());
        }

        // Same, with a protected-folder list taken once by the caller (a plan checks dozens
        // of roots; the list does not change in between).
        public static bool IsUnsafeRoot(string path, List<string> protectedFolders)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string f = Full(path);
            if (f == null) return true;
            if (Same(f, Path.GetPathRoot(f))) return true;
            if (IsForbidden(f, protectedFolders)) return true;
            string win = Full(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (win != null && (Same(f, win) || Inside(f, win))) return true;
            string sys = Full(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (sys != null && (Same(f, sys) || Inside(f, sys))) return true;
            foreach (string p in protectedFolders)
            {
                if (Inside(f, p)) return true;
            }
            string profile = Full(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (profile != null && Same(f, profile)) return true;
            return false;
        }

        // True when path, or any folder from baseDir (not included) down to it, is a junction or
        // symbolic link. A path that is not under baseDir, or that cannot be read, counts as a
        // link: refusing is the safe answer. With a null baseDir only path itself is looked at.
        // A folder that does not exist is not a link.
        public static bool HasLink(string baseDir, string path)
        {
            string p = string.IsNullOrEmpty(path) ? null : Full(path);
            if (p == null) return true;
            string b = string.IsNullOrEmpty(baseDir) ? null : Full(baseDir);
            if (b != null && !Same(b, p) && !Inside(b, p)) return true;
            try
            {
                DirectoryInfo d = new DirectoryInfo(p);
                while (d != null)
                {
                    string cur = Full(d.FullName);
                    if (cur == null) return true;
                    if (b != null && Same(cur, b) && !Same(b, p)) break;
                    if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                    if (b == null || Same(b, p)) break;
                    d = d.Parent;
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // True when path is strictly inside root after normalization (no ".." escapes, no
        // sibling folders sharing a prefix).
        public static bool IsUnder(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            string r = Full(root);
            string p = Full(path);
            if (r == null || p == null) return false;
            return Inside(r, p);
        }
    }
}
