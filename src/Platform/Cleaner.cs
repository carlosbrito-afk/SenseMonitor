using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // Measures and clears the fixed list of cleanup targets. Rules, all enforced here:
    //  - never follows or deletes junctions and symbolic links (the folder itself, the folders
    //    above it up to its base, and everything inside);
    //  - keeps files newer than the target's minimum age (24 h);
    //  - keeps files that are in use (the delete simply fails and the file is counted);
    //  - keeps what a root says stays (the CBS.log in use, blue screen reports);
    //  - skips a program's cache while that program runs;
    //  - never touches Minidump or user document folders (CleanupSafety), even if asked;
    //  - leaves the root folders themselves in place;
    //  - actions (DNS flush, event logs) run fixed commands by their full System32 path.
    public class Cleaner : ICleanupService
    {
        private readonly CleanupEnvironment env;
        private readonly ChangeLog log;

        public Cleaner(CleanupEnvironment env, ChangeLog log)
        {
            if (env == null) throw new ArgumentNullException("env");
            this.env = env;
            this.log = log;
        }

        // The real folders of this machine.
        public static CleanupEnvironment SystemEnvironment()
        {
            CleanupEnvironment e = new CleanupEnvironment();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            e.UserTemp = Path.GetTempPath().TrimEnd('\\');
            e.WindowsTemp = JoinOrNull(win, "Temp");
            e.UpdateDownload = JoinOrNull(win, @"SoftwareDistribution\Download");
            e.DeliveryOptimization = JoinOrNull(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
            e.ThumbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
            e.LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            e.AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            e.ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            e.WindowsDir = win;
            e.System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            e.IsAdmin = IsElevated();
            e.UpdateServiceBusy = WindowsUpdateBusy;
            e.DeliveryOptimizationBusy = DeliveryOptimizationBusy;
            e.ProcessRunning = CleanupProbes.ProcessRunning;
            e.RunCommand = CleanupProbes.RunCommand;
            e.EventChannels = CleanupProbes.ReadEventChannels;
            return e;
        }

        // Base + relative, or null when the base is empty: Path.Combine("", "Temp") would be the
        // relative path "Temp", which means "the current folder", not a folder the app may clean.
        public static string JoinOrNull(string baseDir, string relative)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            return Path.Combine(baseDir, relative);
        }

        public static Cleaner CreateDefault(ChangeLog log)
        {
            return new Cleaner(SystemEnvironment(), log);
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Busy unless the service is stopped. When the state cannot be read, assume busy.
        public static bool WindowsUpdateBusy()
        {
            return ServiceBusy("wuauserv");
        }

        // Delivery Optimization (DoSvc) keeps its cache open while it shares or downloads.
        public static bool DeliveryOptimizationBusy()
        {
            return ServiceBusy("DoSvc");
        }

        private static bool ServiceBusy(string name)
        {
            try
            {
                using (ServiceController sc = new ServiceController(name))
                {
                    return sc.Status != ServiceControllerStatus.Stopped;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ---- availability ----

        private const string MsgUnsafe = "Pasta fora da lista segura. O app não mexe nela.";
        private const string MsgAdmin = "Precisa de administrador. Abra o app como administrador para incluir esta pasta.";
        private const string MsgLink = "A pasta é um atalho (junction ou link) para outro lugar. O app não limpa atalhos.";

        // What a target looks like right now: why it cannot run (Reason), or the parts that
        // can (Roots / Channels / Command) and a note about parts left out.
        private class Check
        {
            public string Reason;
            public string Note;
            public readonly List<CleanupRoot> Roots = new List<CleanupRoot>();
            public List<string> Channels;
            public CleanupCommand Command;
        }

        private Check Evaluate(CleanupTarget t, CancellationToken ct)
        {
            return t.Kind == CleanupKind.Action ? EvaluateAction(t, ct) : EvaluateFolders(t);
        }

        private static void AddOnce(List<string> l, string s)
        {
            if (s != null && !l.Contains(s)) l.Add(s);
        }

        private Check EvaluateFolders(CleanupTarget t)
        {
            Check c = new Check();
            bool anyUnsafe = false, anyAdmin = false, anyLink = false;
            List<string> prot = CleanupSafety.ProtectedSnapshot();
            List<string> running = new List<string>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (CleanupRoot r in t.Roots)
            {
                if (CleanupSafety.IsUnsafeRoot(r.Path, prot)) { anyUnsafe = true; continue; }
                if ((r.RequiresAdmin || t.RequiresAdmin) && !env.IsAdmin) { anyAdmin = true; continue; }
                if (!Directory.Exists(r.Path)) continue;
                if (CleanupSafety.HasLink(r.Base, r.Path)) { anyLink = true; continue; }
                if (OwnerRunning(r, seen))
                {
                    AddOnce(running, r.Owner);
                    continue;
                }
                c.Roots.Add(r);
            }
            string runningText = running.Count > 0 ? string.Join(", ", running.ToArray()) : null;
            string hint = string.IsNullOrEmpty(t.CloseHint) ? "Feche o programa" : t.CloseHint;
            if (c.Roots.Count == 0)
            {
                if (anyUnsafe) c.Reason = MsgUnsafe;
                else if (runningText != null) c.Reason = "Aberto agora: " + runningText + ". " + hint + " e analise de novo.";
                else if (anyAdmin) c.Reason = MsgAdmin;
                else if (anyLink) c.Reason = MsgLink;
                else if (t.Roots.Count > 1) c.Reason = "Nenhuma das pastas existe neste PC. Não há nada para limpar aqui.";
                else c.Reason = "A pasta não existe neste PC. Não há nada para limpar aqui.";
                return c;
            }
            if (t.Id == CleanupTargetId.UpdateDownload && IsBusy(env.UpdateServiceBusy))
            {
                c.Reason = "O Windows Update está baixando ou instalando agora. Esta pasta fica de fora; analise de novo depois.";
                return c;
            }
            if (t.Id == CleanupTargetId.DeliveryOptimization && IsBusy(env.DeliveryOptimizationBusy))
            {
                c.Reason = "A Otimização de Entrega do Windows está ativa agora (ou não deu para saber). Esta pasta fica de fora; analise de novo depois.";
                return c;
            }
            List<string> notes = new List<string>();
            if (runningText != null) notes.Add("Ficam de fora por estarem abertos: " + runningText + ". " + hint + " e analise de novo para incluir.");
            if (anyAdmin) notes.Add("Uma parte exige administrador e ficou de fora.");
            if (anyLink) notes.Add("Atalhos (junctions) foram ignorados.");
            if (notes.Count > 0) c.Note = string.Join(" ", notes.ToArray());
            return c;
        }

        // True while one of the root's owner processes runs. A probe that throws counts as
        // running, a missing probe as not running.
        private bool OwnerRunning(CleanupRoot r, Dictionary<string, bool> seen)
        {
            if (r.OwnerProcesses == null) return false;
            if (env.ProcessRunning == null) return true;   // cannot tell: leave the program's cache alone
            foreach (string name in r.OwnerProcesses)
            {
                bool on;
                if (!seen.TryGetValue(name, out on))
                {
                    try { on = env.ProcessRunning(name); }
                    catch (Exception) { on = true; }
                    seen[name] = on;
                }
                if (on) return true;
            }
            return false;
        }

        private Check EvaluateAction(CleanupTarget t, CancellationToken ct)
        {
            Check c = new Check();
            if (!env.IsAdmin)
            {
                c.Reason = "Precisa de administrador. Abra o app como administrador para usar esta limpeza.";
                return c;
            }
            if (env.RunCommand == null || string.IsNullOrEmpty(env.System32))
            {
                c.Reason = "Não deu para executar o comando do Windows neste PC.";
                return c;
            }
            if (t.Id == CleanupTargetId.DnsCache)
            {
                CleanupCommand cmd = CleanupCommands.FlushDns(env.System32);
                if (cmd == null || !File.Exists(cmd.FileName))
                {
                    c.Reason = "O ipconfig não foi encontrado em System32.";
                    return c;
                }
                c.Command = cmd;
                return c;
            }
            if (t.Id == CleanupTargetId.EventLogs)
            {
                string wevtutil = Path.Combine(env.System32, "wevtutil.exe");
                if (!File.Exists(wevtutil))
                {
                    c.Reason = "O wevtutil não foi encontrado em System32.";
                    return c;
                }
                if (env.EventChannels == null)
                {
                    c.Reason = "Não deu para ler a lista de registros de eventos.";
                    return c;
                }
                try { c.Channels = EventLogFilter.Select(env.EventChannels(ct)); }
                catch (Exception)
                {
                    // a read stopped by a cancel is not a failure: the caller sees the cancel
                    c.Reason = ct.IsCancellationRequested ? "Cancelado." : "Não deu para ler a lista de registros de eventos.";
                    return c;
                }
                if (c.Channels.Count == 0) c.Reason = "Nenhum registro operacional com conteúdo para limpar.";
                return c;
            }
            c.Reason = "Ação desconhecida. O app não faz nada com ela.";
            return c;
        }

        private static bool IsBusy(Func<bool> probe)
        {
            if (probe == null) return false;
            try { return probe(); }
            catch (Exception) { return true; }
        }

        private DateTime Now()
        {
            return env.UtcNow != null ? env.UtcNow() : DateTime.UtcNow;
        }

        private int RunSafe(CleanupCommand cmd)
        {
            if (cmd == null || env.RunCommand == null) return -1;
            try { return env.RunCommand(cmd); }
            catch (Exception) { return -1; }
        }

        private void Report(Action<CleanupProgress> progress, string phase, CleanupTarget t, int index, int count, long files)
        {
            if (progress == null) return;
            CleanupProgress p = new CleanupProgress();
            p.Phase = phase;
            p.TargetName = t.Name;
            p.TargetIndex = index;
            p.TargetCount = count;
            p.Files = files;
            p.IsAction = t.Kind == CleanupKind.Action;
            try { progress(p); }
            catch (Exception) { }
        }

        // ---- analyze ----

        public CleanupPlan Analyze(Action<CleanupProgress> progress, CancellationToken ct)
        {
            CleanupPlan plan = CleanupPlan.Create(env);
            int index = 0;
            foreach (CleanupTarget t in plan.Targets)
            {
                index++;
                if (!t.Available) continue;
                Check chk = Evaluate(t, ct);
                if (ct.IsCancellationRequested && chk.Reason != null)
                {
                    plan.Cancelled = true;
                    break;
                }
                if (chk.Reason != null)
                {
                    t.Available = false;
                    t.Selected = false;
                    t.Note = chk.Reason;
                    continue;
                }
                t.Note = chk.Note;
                if (ct.IsCancellationRequested)
                {
                    plan.Cancelled = true;
                    break;
                }
                if (t.Kind == CleanupKind.Action)
                {
                    // nothing to measure; the analysis only says what the action would do
                    t.Summary = chk.Channels != null
                        ? chk.Channels.Count + (chk.Channels.Count == 1 ? " canal" : " canais")
                        : "Ação";
                    t.Analyzed = true;
                    continue;
                }
                Sweep s = new Sweep(t, chk.Roots, false, Now(), progress, ct, "Analisando", index, plan.Targets.Count);
                s.Run();
                t.Bytes = s.Bytes;
                t.Files = s.Files;
                t.SkippedRecent = s.SkippedRecent;
                t.Analyzed = !s.Cancelled;
                if (s.Cancelled)
                {
                    plan.Cancelled = true;
                    break;
                }
            }
            return plan;
        }

        // ---- clean ----

        public CleanupResult Clean(CleanupPlan plan, Action<CleanupProgress> progress, CancellationToken ct)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            CleanupResult result = new CleanupResult();
            int index = 0;
            int attempted = 0;
            foreach (CleanupTarget t in plan.Targets)
            {
                index++;
                if (!t.Selected || !t.Available) continue;
                // Conditions may have changed since the preview (an update started, a program
                // was opened, a forced flag).
                Check chk = Evaluate(t, ct);
                if (ct.IsCancellationRequested && chk.Reason != null)
                {
                    result.Cancelled = true;
                    break;
                }
                if (chk.Reason != null)
                {
                    result.Notes.Add(t.Name + ": " + chk.Reason);
                    continue;
                }
                if (ct.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }
                attempted++;
                if (t.Kind == CleanupKind.Action)
                {
                    CleanupTargetResult ar = RunAction(t, chk, progress, ct, index, plan.Targets.Count, result);
                    if (ar.Cancelled && !ar.ActionOk)
                    {
                        // stopped before it did anything: not a failure, the result already says "cancelada"
                        break;
                    }
                    result.PerTarget.Add(ar);
                    if (ar.ActionOk) result.ActionsDone++; else result.ActionsFailed++;
                    if (ar.ActionOk) result.Done.Add(ar.Message); else result.Notes.Add(t.Name + ": " + ar.Message);
                    if (result.Cancelled) break;
                    continue;
                }
                Sweep s = new Sweep(t, chk.Roots, true, Now(), progress, ct, "Limpando", index, plan.Targets.Count);
                s.Run();
                CleanupTargetResult tr = new CleanupTargetResult();
                tr.Id = t.Id;
                tr.Name = t.Name;
                tr.FreedBytes = s.Bytes;
                tr.DeletedFiles = s.Deleted;
                tr.SkippedCount = s.SkippedTotal;
                result.PerTarget.Add(tr);
                result.FreedBytes += s.Bytes;
                result.DeletedFiles += s.Deleted;
                result.SkippedCount += s.SkippedTotal;
                if (chk.Note != null) result.Notes.Add(t.Name + ": " + chk.Note);
                if (s.Cancelled)
                {
                    result.Cancelled = true;
                    break;
                }
            }
            if (attempted > 0 && log != null)
            {
                int folders = 0;
                foreach (CleanupTargetResult ptr in result.PerTarget) if (!ptr.IsAction) folders++;
                string desc = "Limpeza: " + SizeFormat.Format(result.FreedBytes) + " liberados"
                    + (folders > 0 ? " em " + folders + (folders == 1 ? " local" : " locais") : "")
                    + " (" + result.SkippedCount + " itens mantidos por estarem em uso ou recentes)"
                    + (result.ActionsDone > 0 ? ", " + result.ActionsDone + (result.ActionsDone == 1 ? " ação executada" : " ações executadas") : "")
                    + (result.ActionsFailed > 0 ? ", " + result.ActionsFailed + (result.ActionsFailed == 1 ? " ação falhou" : " ações falharam") : "")
                    + (result.Cancelled ? ", cancelada" : "");
                try { log.Record(ChangeKind.Limpeza, desc, null); }
                catch (Exception) { }
            }
            return result;
        }

        private CleanupTargetResult RunAction(CleanupTarget t, Check chk, Action<CleanupProgress> progress, CancellationToken ct,
            int index, int count, CleanupResult result)
        {
            CleanupTargetResult r = new CleanupTargetResult();
            r.Id = t.Id;
            r.Name = t.Name;
            r.IsAction = true;
            Report(progress, "Limpando", t, index, count, 0);
            if (t.Id == CleanupTargetId.DnsCache)
            {
                int code = RunSafe(chk.Command);
                r.ActionOk = code == 0;
                r.Message = r.ActionOk ? "Cache de DNS esvaziado." : "Não deu para esvaziar o cache de DNS (ipconfig devolveu o código " + code + ").";
                if (!r.ActionOk) r.SkippedCount = 1;
                return r;
            }
            // event logs: one wevtutil call per channel
            int cleared = 0, failed = 0, done = 0;
            foreach (string channel in chk.Channels)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }
                done++;
                Report(progress, "Limpando", t, index, count, done);
                CleanupCommand cmd = CleanupCommands.ClearEventLog(env.System32, channel);
                if (cmd != null && RunSafe(cmd) == 0) cleared++;
                else failed++;
            }
            r.SkippedCount = failed;
            r.ActionOk = cleared > 0;
            r.Cancelled = result.Cancelled;
            string msg = cleared + (cleared == 1 ? " registro de eventos limpo" : " registros de eventos limpos");
            if (failed > 0) msg += ", " + failed + (failed == 1 ? " não pôde ser limpo (em uso ou protegido pelo Windows)" : " não puderam ser limpos (em uso ou protegidos pelo Windows)");
            r.Message = msg + ".";
            return r;
        }

        // One pass over the roots of a target, either counting (analysis) or deleting.
        private class Sweep
        {
            private readonly CleanupTarget target;
            private readonly List<CleanupRoot> roots;
            private CleanupRoot cur;
            private readonly bool delete;
            private readonly DateTime cutoffUtc;
            private readonly Action<CleanupProgress> progress;
            private readonly CancellationToken ct;
            private readonly string phase;
            private readonly int index;
            private readonly int count;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long lastReportMs = -1000;
            private int sinceReport;
            private long processed;
            private readonly List<string> protectedFolders = CleanupSafety.ProtectedSnapshot();

            public long Bytes;
            public int Files;
            public int Deleted;
            public int SkippedRecent;
            public int SkippedInUse;
            public int SkippedLinks;
            public int SkippedOther;
            public bool Cancelled;

            public Sweep(CleanupTarget target, List<CleanupRoot> roots, bool delete, DateTime nowUtc, Action<CleanupProgress> progress,
                CancellationToken ct, string phase, int index, int count)
            {
                this.target = target;
                this.roots = roots;
                this.delete = delete;
                this.cutoffUtc = target.MinAgeHours > 0 ? nowUtc.AddHours(-target.MinAgeHours) : DateTime.MaxValue;
                this.progress = progress;
                this.ct = ct;
                this.phase = phase;
                this.index = index;
                this.count = count;
            }

            public int SkippedTotal
            {
                get { return SkippedRecent + SkippedInUse + SkippedLinks + SkippedOther; }
            }

            public void Run()
            {
                Report(true);
                foreach (CleanupRoot r in roots)
                {
                    if (Stop()) break;
                    cur = r;
                    // the check at evaluation is old by now: look again right before the walk
                    if (CleanupSafety.HasLink(r.Base, r.Path))
                    {
                        SkippedLinks++;
                        continue;
                    }
                    Walk(r.Path);
                    if (Cancelled) break;
                }
                Report(true);
            }

            private bool Stop()
            {
                if (ct.IsCancellationRequested)
                {
                    Cancelled = true;
                    return true;
                }
                return false;
            }

            private void Report(bool force)
            {
                if (progress == null) return;
                sinceReport++;
                long ms = clock.ElapsedMilliseconds;
                if (!force && sinceReport < 200 && ms - lastReportMs < 150) return;
                sinceReport = 0;
                lastReportMs = ms;
                CleanupProgress p = new CleanupProgress();
                p.Phase = phase;
                p.TargetName = target.Name;
                p.TargetIndex = index;
                p.TargetCount = count;
                p.Files = processed;
                p.Bytes = Bytes;
                try { progress(p); }
                catch (Exception) { }
            }

            // Returns true when nothing was kept in this folder (so an empty folder can go).
            private bool Walk(string dir)
            {
                FileSystemInfo[] items;
                try
                {
                    items = new DirectoryInfo(dir).GetFileSystemInfos();
                }
                catch (Exception)
                {
                    SkippedOther++;
                    return false;
                }
                bool allGone = true;
                foreach (FileSystemInfo item in items)
                {
                    if (Stop()) return false;
                    FileAttributes attrs;
                    try { attrs = item.Attributes; }
                    catch (Exception) { SkippedOther++; allGone = false; continue; }

                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    if (!cur.Recursive)
                    {
                        // pattern roots: only the matching files directly in the folder
                        if (isDir || !CleanupNames.MatchesAny(item.Name, cur.Patterns)) continue;
                    }
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        // junction or symbolic link: not followed, not deleted
                        SkippedLinks++;
                        allGone = false;
                        continue;
                    }
                    if (CleanupNames.IsKept(item.Name, cur.KeepNames, cur.KeepPrefixes))
                    {
                        // the log in use, a blue screen report: stays
                        SkippedOther++;
                        allGone = false;
                        continue;
                    }
                    if (isDir)
                    {
                        if (!CleanupSafety.IsUnder(cur.Path, item.FullName) || CleanupSafety.IsForbidden(item.FullName, protectedFolders))
                        {
                            SkippedOther++;
                            allGone = false;
                            continue;
                        }
                        // A folder goes only when nothing is left in it and it is either old or
                        // was emptied by this very pass (a new, never-used empty folder stays).
                        if (CleanupSafety.HasLink(null, item.FullName))
                        {
                            // swapped for a link since it was listed
                            SkippedLinks++;
                            allGone = false;
                            continue;
                        }
                        DateTime born = Newest(item);
                        int deletedBefore = Deleted;
                        bool empty = Walk(item.FullName);
                        if (Cancelled) return false;
                        if (delete && empty && (born <= cutoffUtc || Deleted > deletedBefore))
                        {
                            try { Directory.Delete(item.FullName, false); }
                            catch (Exception) { allGone = false; }
                        }
                        else
                        {
                            allGone = false;
                        }
                        continue;
                    }
                    if (!HandleFile((FileInfo)item)) allGone = false;
                }
                return allGone;
            }

            private static DateTime Newest(FileSystemInfo i)
            {
                try
                {
                    DateTime c = i.CreationTimeUtc;
                    DateTime w = i.LastWriteTimeUtc;
                    return c > w ? c : w;
                }
                catch (Exception)
                {
                    return DateTime.MaxValue;
                }
            }

            // Returns true when the file is gone (or would go, in analysis).
            private bool HandleFile(FileInfo f)
            {
                processed++;
                Report(false);
                if (!CleanupSafety.IsUnder(cur.Path, f.FullName) || CleanupSafety.IsForbidden(f.FullName, protectedFolders))
                {
                    SkippedOther++;
                    return false;
                }
                if (Newest(f) > cutoffUtc)
                {
                    SkippedRecent++;
                    return false;
                }
                long len = 0;
                try { len = f.Length; }
                catch (Exception) { }
                if (!delete)
                {
                    Files++;
                    Bytes += len;
                    return true;
                }
                bool clearedReadOnly = false;
                try
                {
                    if (f.IsReadOnly)
                    {
                        f.IsReadOnly = false;
                        clearedReadOnly = true;
                    }
                    f.Delete();
                    Deleted++;
                    Files++;
                    Bytes += len;
                    return true;
                }
                catch (IOException)
                {
                    SkippedInUse++;
                }
                catch (UnauthorizedAccessException)
                {
                    SkippedInUse++;
                }
                catch (Exception)
                {
                    SkippedOther++;
                }
                if (clearedReadOnly)
                {
                    // the file stays, so it keeps the attribute it had
                    try { f.IsReadOnly = true; }
                    catch (Exception) { }
                }
                return false;
            }
        }
    }
}
