using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Every cleanup test works inside a throwaway sandbox under %TEMP%; nothing else is touched.
    public class CleanupSandbox : IDisposable
    {
        public readonly string Root;
        public readonly string UserTemp;
        public readonly string WinTemp;
        public readonly string Update;
        public readonly string DeliveryOpt;
        public readonly string Thumbs;
        public readonly List<string> Junctions = new List<string>();
        public bool UpdateBusy;
        public bool DeliveryBusy;
        public bool DeliveryCannotTell;
        public bool Admin = true;

        // Base folders for the cache and log targets, all inside the sandbox.
        public readonly string LocalApp;
        public readonly string RoamApp;
        public readonly string ProgData;
        public readonly string WinDir;
        public readonly string Sys32;
        // Fakes for the injectable system pieces: nothing here starts a process or reads a log.
        public readonly List<string> RunningProcesses = new List<string>();
        public bool ProcessProbeThrows;
        public readonly List<CleanupCommand> CommandsRun = new List<CleanupCommand>();
        public int CommandExitCode;
        public readonly List<string> FailingChannels = new List<string>();
        public readonly List<EventChannelInfo> Channels = new List<EventChannelInfo>();

        public CleanupSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            UserTemp = Path.Combine(Root, "usertemp");
            WinTemp = Path.Combine(Root, "wintemp");
            Update = Path.Combine(Root, "update");
            DeliveryOpt = Path.Combine(Root, "dopt");
            Thumbs = Path.Combine(Root, "thumbs");
            Directory.CreateDirectory(UserTemp);
            Directory.CreateDirectory(WinTemp);
            Directory.CreateDirectory(Update);
            Directory.CreateDirectory(DeliveryOpt);
            Directory.CreateDirectory(Thumbs);
            LocalApp = Path.Combine(Root, "Local");
            RoamApp = Path.Combine(Root, "Roaming");
            ProgData = Path.Combine(Root, "ProgramData");
            WinDir = Path.Combine(Root, "Windows");
            Sys32 = Path.Combine(WinDir, "System32");
            Directory.CreateDirectory(LocalApp);
            Directory.CreateDirectory(RoamApp);
            Directory.CreateDirectory(ProgData);
            Directory.CreateDirectory(Sys32);
            File.WriteAllBytes(Path.Combine(Sys32, "ipconfig.exe"), new byte[1]);
            File.WriteAllBytes(Path.Combine(Sys32, "wevtutil.exe"), new byte[1]);
        }

        public void Channel(string name, EventChannelKind kind, long records)
        {
            EventChannelInfo c = new EventChannelInfo();
            c.Name = name;
            c.Kind = kind;
            c.Records = records;
            Channels.Add(c);
        }

        public CleanupEnvironment Env()
        {
            CleanupEnvironment e = new CleanupEnvironment();
            e.UserTemp = UserTemp;
            e.WindowsTemp = WinTemp;
            e.UpdateDownload = Update;
            e.DeliveryOptimization = DeliveryOpt;
            e.ThumbFolder = Thumbs;
            e.IsAdmin = Admin;
            e.LocalAppData = LocalApp;
            e.AppData = RoamApp;
            e.ProgramData = ProgData;
            e.WindowsDir = WinDir;
            e.System32 = Sys32;
            e.ProcessRunning = delegate(string name)
            {
                if (ProcessProbeThrows) throw new InvalidOperationException("cannot list processes");
                foreach (string p in RunningProcesses)
                {
                    if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            };
            e.RunCommand = delegate(CleanupCommand c)
            {
                CommandsRun.Add(c);
                foreach (string ch in FailingChannels)
                {
                    if (c.Arguments.Contains("\"" + ch + "\"")) return 5;
                }
                return CommandExitCode;
            };
            e.EventChannels = delegate { return new List<EventChannelInfo>(Channels); };
            e.UpdateServiceBusy = delegate { return UpdateBusy; };
            e.DeliveryOptimizationBusy = delegate
            {
                if (DeliveryCannotTell) throw new InvalidOperationException("cannot read service");
                return DeliveryBusy;
            };
            return e;
        }

        public string Old(string dir, string name, int bytes)
        {
            string p = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, new byte[bytes]);
            DateTime t = DateTime.UtcNow.AddDays(-3);
            File.SetCreationTimeUtc(p, t);
            File.SetLastWriteTimeUtc(p, t);
            return p;
        }

        public string Fresh(string dir, string name, int bytes)
        {
            string p = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, new byte[bytes]);
            return p;
        }

        // Directory junction (no admin needed). Returns false when it cannot be created here.
        public bool MakeJunction(string link, string target)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    if (p.ExitCode != 0) return false;
                }
                Junctions.Add(link);
                return Directory.Exists(link);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            foreach (string j in Junctions)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c rmdir \"" + j + "\"");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    using (Process p = Process.Start(psi)) { p.WaitForExit(10000); }
                }
                catch (Exception) { }
            }
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (Exception) { }
        }
    }

    public static class CleanupTests
    {
        private static Cleaner NewCleaner(CleanupSandbox sb, ChangeLog log)
        {
            return new Cleaner(sb.Env(), log);
        }

        private static void Only(CleanupPlan plan, params CleanupTargetId[] ids)
        {
            foreach (CleanupTarget t in plan.Targets)
            {
                t.Selected = Array.IndexOf(ids, t.Id) >= 0;
            }
        }

        // ---- pure rules ----

        [Test]
        public static void SizeFormat_UsesPortugueseDecimalComma()
        {
            Assert.Equal("0 B", SizeFormat.Format(0));
            Assert.Equal("512 B", SizeFormat.Format(512));
            Assert.Equal("1,5 KB", SizeFormat.Format(1536));
            Assert.Equal("1,9 GB", SizeFormat.Format(2040109465L));
            Assert.Equal("151,6 MB", SizeFormat.Format((long)(151.6 * 1024 * 1024)));
        }

        [Test]
        public static void Safety_MinidumpIsAlwaysForbidden()
        {
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\Minidump"), "folder");
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\Minidump\111426-1.dmp"), "file inside");
            Assert.True(CleanupSafety.IsForbidden(@"c:\windows\MINIDUMP\x.dmp"), "case-insensitive");
            Assert.True(CleanupSafety.IsForbidden(@"C:\Windows\MEMORY.DMP"), "full dump");
            Assert.True(!CleanupSafety.IsForbidden(@"C:\Windows\Temp\a.tmp"), "temp is fine");
        }

        [Test]
        public static void Safety_UserDocumentFoldersAreForbidden()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            Assert.True(CleanupSafety.IsForbidden(docs), "documents");
            Assert.True(CleanupSafety.IsForbidden(Path.Combine(docs, "a", "b.docx")), "inside documents");
            Assert.True(CleanupSafety.IsForbidden(desk), "desktop");
            Assert.True(CleanupSafety.IsForbidden(pics), "pictures");
            string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Assert.True(CleanupSafety.IsForbidden(dl), "downloads");
        }

        [Test]
        public static void Safety_UnsafeRoots()
        {
            Assert.True(CleanupSafety.IsUnsafeRoot(@"C:\"), "drive root");
            Assert.True(CleanupSafety.IsUnsafeRoot(@"C:\Windows"), "windows dir");
            Assert.True(CleanupSafety.IsUnsafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "profile contains documents");
            Assert.True(CleanupSafety.IsUnsafeRoot(@"C:\Windows\Minidump"), "minidump");
            Assert.True(CleanupSafety.IsUnsafeRoot(""), "empty");
            Assert.True(!CleanupSafety.IsUnsafeRoot(@"C:\Windows\Temp"), "windows temp ok");
            Assert.True(!CleanupSafety.IsUnsafeRoot(Path.GetTempPath().TrimEnd('\\')), "user temp ok");
        }

        [Test]
        public static void Safety_IsUnder_RejectsSiblingPrefixAndTraversal()
        {
            Assert.True(CleanupSafety.IsUnder(@"C:\a\temp", @"C:\a\temp\x\y.tmp"), "inside");
            Assert.True(!CleanupSafety.IsUnder(@"C:\a\temp", @"C:\a\temp2\y.tmp"), "sibling with same prefix");
            Assert.True(!CleanupSafety.IsUnder(@"C:\a\temp", @"C:\a\temp\..\other\y.tmp"), "dot dot traversal");
            Assert.True(!CleanupSafety.IsUnder(@"C:\a\temp", @"C:\a\temp"), "the root itself is not 'under'");
        }

        // ---- plan building ----

        [Test]
        public static void Plan_Defaults()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupPlan plan = CleanupPlan.Create(sb.Env());
                Assert.Equal(12, plan.Targets.Count);
                Assert.True(plan.Find(CleanupTargetId.UserTemp).Selected, "user temp on by default");
                Assert.True(plan.Find(CleanupTargetId.WindowsTemp).Selected, "windows temp on by default");
                Assert.True(!plan.Find(CleanupTargetId.Thumbnails).Selected, "thumbnails optional, off by default");
                Assert.True(plan.Find(CleanupTargetId.Thumbnails).Optional, "thumbnails optional");
                Assert.True(plan.Find(CleanupTargetId.WindowsTemp).RequiresAdmin, "windows temp needs admin");
                Assert.True(!plan.Find(CleanupTargetId.UserTemp).RequiresAdmin, "user temp does not");
            }
        }

        [Test]
        public static void Plan_UnsafeRoot_MarksTargetUnavailable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupEnvironment env = sb.Env();
                env.WindowsTemp = @"C:\Windows\Minidump";
                CleanupPlan plan = CleanupPlan.Create(env);
                CleanupTarget t = plan.Find(CleanupTargetId.WindowsTemp);
                Assert.True(!t.Available, "minidump never offered");
                Assert.True(!t.Selected, "and never selected");
                Assert.True(t.Note != null && t.Note.Length > 0, "says why");
            }
        }

        // ---- analyze ----

        [Test]
        public static void Analyze_CountsOnlyEligibleFiles_AndNotesRecentOnes()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(sb.UserTemp, "a.tmp", 1000);
                sb.Old(sb.UserTemp, Path.Combine("sub", "deep", "b.tmp"), 2000);
                sb.Fresh(sb.UserTemp, "new.tmp", 5000);
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.UserTemp);
                Assert.True(t.Available, "available");
                Assert.Equal(3000L, t.Bytes);
                Assert.Equal(2, t.Files);
                Assert.Equal(1, t.SkippedRecent);
                Assert.True(File.Exists(Path.Combine(sb.UserTemp, "a.tmp")), "analysis deletes nothing");
            }
        }

        [Test]
        public static void Analyze_MissingFolder_IsUnavailableWithNote()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.Delete(sb.DeliveryOpt);
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.DeliveryOptimization);
                Assert.True(!t.Available, "missing folder");
                Assert.True(t.Note != null && t.Note.Length > 0, "note");
            }
        }

        [Test]
        public static void Analyze_NotAdmin_MarksAdminTargetsAndDoesNotTouchThem()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Admin = false;
                sb.Old(sb.WinTemp, "w.tmp", 100);
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.WindowsTemp);
                Assert.True(!t.Available, "needs admin");
                Assert.True(t.Note.ToLowerInvariant().Contains("administrador"), "says what to do");
            }
        }

        [Test]
        public static void Analyze_UpdateServiceBusy_SkipsDownloadFolderWithNote()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(sb.Update, "pkg.cab", 4000);
                sb.UpdateBusy = true;
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.UpdateDownload);
                Assert.True(!t.Available, "skipped while updating");
                Assert.True(t.Note.Contains("Windows Update"), "note names the service");
                sb.UpdateBusy = false;
                plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                Assert.True(plan.Find(CleanupTargetId.UpdateDownload).Available, "available when idle");
                Assert.Equal(4000L, plan.Find(CleanupTargetId.UpdateDownload).Bytes);
            }
        }

        [Test]
        public static void Analyze_ReportsProgressAndHonorsCancellation()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                for (int i = 0; i < 450; i++) sb.Old(sb.UserTemp, "f" + i + ".tmp", 10);
                int reports = 0;
                CancellationTokenSource cts = new CancellationTokenSource();
                CleanupPlan plan = NewCleaner(sb, null).Analyze(delegate(CleanupProgress p)
                {
                    reports++;
                    cts.Cancel();
                }, cts.Token);
                Assert.True(reports >= 1, "progress reported");
                Assert.True(plan.Cancelled, "plan flagged as cancelled");
            }
        }

        // ---- clean ----

        [Test]
        public static void Clean_DeletesOldKeepsRecent_RemovesEmptiedSubfolders_KeepsRoot()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string a = sb.Old(sb.UserTemp, "a.tmp", 1000);
                string b = sb.Old(sb.UserTemp, Path.Combine("sub", "deep", "b.tmp"), 2000);
                string keep = sb.Fresh(sb.UserTemp, "new.tmp", 5000);
                string keepSub = sb.Fresh(sb.UserTemp, Path.Combine("mix", "new2.tmp"), 10);
                sb.Old(sb.UserTemp, Path.Combine("mix", "old2.tmp"), 10);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UserTemp);
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(!File.Exists(a) && !File.Exists(b), "old files deleted");
                Assert.True(File.Exists(keep) && File.Exists(keepSub), "recent files kept");
                Assert.True(!Directory.Exists(Path.Combine(sb.UserTemp, "sub")), "emptied folders removed");
                Assert.True(Directory.Exists(Path.Combine(sb.UserTemp, "mix")), "folder with recent file kept");
                Assert.True(Directory.Exists(sb.UserTemp), "root stays");
                Assert.Equal(3, r.DeletedFiles);
                Assert.Equal(3010L, r.FreedBytes);
                Assert.True(r.SkippedCount >= 2, "recent files counted as skipped");
                Assert.True(!r.Cancelled, "not cancelled");
            }
        }

        [Test]
        public static void Clean_OnlyTouchesSelectedTargets()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string u = sb.Old(sb.UserTemp, "u.tmp", 10);
                string w = sb.Old(sb.WinTemp, "w.tmp", 10);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.WindowsTemp);
                c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(u), "user temp untouched");
                Assert.True(!File.Exists(w), "windows temp cleaned");
            }
        }

        [Test]
        public static void Clean_NeverFollowsJunctions()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string outside = Path.Combine(sb.Root, "outside");
                Directory.CreateDirectory(outside);
                string precious = sb.Old(outside, "precious.doc", 123);
                string link = Path.Combine(sb.UserTemp, "link");
                Assert.True(sb.MakeJunction(link, outside), "mklink /J must work here: without it this test proves nothing");
                string gone = sb.Old(sb.UserTemp, "gone.tmp", 10);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Assert.Equal(10L, plan.Find(CleanupTargetId.UserTemp).Bytes);
                Only(plan, CleanupTargetId.UserTemp);
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(precious), "file behind the junction survives");
                Assert.True(Directory.Exists(link), "junction itself is left alone");
                Assert.True(!File.Exists(gone), "normal file deleted");
                Assert.True(r.SkippedCount >= 1, "junction counted as skipped");
            }
        }

        [Test]
        public static void Clean_SkipsFilesInUse()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string locked = sb.Old(sb.UserTemp, "locked.tmp", 50);
                string free = sb.Old(sb.UserTemp, "free.tmp", 60);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UserTemp);
                CleanupResult r;
                using (FileStream fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    r = c.Clean(plan, null, CancellationToken.None);
                }
                Assert.True(File.Exists(locked), "in-use file stays");
                Assert.True(!File.Exists(free), "other file deleted");
                Assert.Equal(1, r.DeletedFiles);
                Assert.Equal(60L, r.FreedBytes);
                Assert.True(r.SkippedCount >= 1, "in-use counted");
            }
        }

        [Test]
        public static void Clean_DeletesReadOnlyFiles()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string ro = sb.Old(sb.UserTemp, "ro.tmp", 20);
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UserTemp);
                c.Clean(plan, null, CancellationToken.None);
                Assert.True(!File.Exists(ro), "read-only temp file deleted");
            }
        }

        [Test]
        public static void Clean_UpdateServiceBecomesBusy_SkipsAtCleanTime()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string pkg = sb.Old(sb.Update, "pkg.cab", 4000);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UpdateDownload);
                sb.UpdateBusy = true; // an update starts between the preview and the confirmation
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(pkg), "download folder untouched while updating");
                Assert.Equal(0, r.DeletedFiles);
                Assert.True(r.Notes.Count >= 1, "result says it was skipped");
            }
        }

        [Test]
        public static void Clean_AllTargets_KeepFilesFromTheLast24Hours()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string pkg = sb.Fresh(sb.Update, "pkg.cab", 100);
                string pkgOld = sb.Old(sb.Update, "old.cab", 100);
                string dopt = sb.Fresh(sb.DeliveryOpt, "chunk.bin", 100);
                string doptOld = sb.Old(sb.DeliveryOpt, "old.bin", 100);
                string thumb = sb.Fresh(sb.Thumbs, "thumbcache_32.db", 100);
                string thumbOld = sb.Old(sb.Thumbs, "thumbcache_96.db", 100);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                foreach (CleanupTarget t in plan.Targets) t.Selected = true;
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(pkg) && File.Exists(dopt) && File.Exists(thumb), "recent files stay in every target");
                Assert.True(!File.Exists(pkgOld) && !File.Exists(doptOld) && !File.Exists(thumbOld), "old files go");
                Assert.Equal(3, r.DeletedFiles);
            }
        }

        [Test]
        public static void Plan_EveryTargetUsesTheSameMinimumAge_AndSaysSo()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupEnvironment env = sb.Env();
                env.MinAgeHours = 48;
                CleanupPlan plan = CleanupPlan.Create(env);
                foreach (CleanupTarget t in plan.Targets)
                {
                    Assert.Equal(48, t.MinAgeHours);
                    if (t.Kind != CleanupKind.Folders) continue;   // actions delete no files, so no age rule
                    Assert.True(t.Description.Contains("48 horas"), "description of " + t.Id + " states the age rule: " + t.Description);
                }
            }
        }

        [Test]
        public static void Analyze_DeliveryOptimizationBusy_SkipsWithNote()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(sb.DeliveryOpt, "chunk.bin", 4000);
                sb.DeliveryBusy = true;
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.DeliveryOptimization);
                Assert.True(!t.Available && !t.Selected, "skipped while the service is active");
                Assert.True(t.Note.Contains("Otimização de Entrega"), "note names the service: " + t.Note);
                sb.DeliveryBusy = false;
                plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                Assert.True(plan.Find(CleanupTargetId.DeliveryOptimization).Available, "available when idle");
                Assert.Equal(4000L, plan.Find(CleanupTargetId.DeliveryOptimization).Bytes);
            }
        }

        [Test]
        public static void Analyze_DeliveryOptimizationCannotBeRead_CountsAsBusy()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(sb.DeliveryOpt, "chunk.bin", 4000);
                sb.DeliveryCannotTell = true;
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                Assert.True(!plan.Find(CleanupTargetId.DeliveryOptimization).Available, "unknown state means skip");
            }
        }

        [Test]
        public static void Clean_DeliveryOptimizationBecomesBusy_SkipsAtCleanTime()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string chunk = sb.Old(sb.DeliveryOpt, "chunk.bin", 4000);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.DeliveryOptimization);
                sb.DeliveryBusy = true;
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(chunk), "cache untouched while the service works");
                Assert.True(r.Notes.Count >= 1, "result says it was skipped");
            }
        }

        [Test]
        public static void Analyze_DeliveryOptimizationFolderMissing_SaysMissingEvenIfServiceStateUnknown()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.Delete(sb.DeliveryOpt);
                sb.DeliveryCannotTell = true;
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.DeliveryOptimization);
                Assert.True(!t.Available, "missing folder");
                Assert.True(t.Note.Contains("não existe"), "says the folder is missing, not that the service is busy: " + t.Note);
            }
        }

        [Test]
        public static void Analyze_RootThatIsAJunction_IsRefused()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string real = Path.Combine(sb.Root, "real");
                string victim = sb.Old(real, "important.dat", 10);
                string link = Path.Combine(sb.Root, "linkroot");
                Assert.True(sb.MakeJunction(link, real), "mklink /J must work here: without it this test proves nothing");
                CleanupEnvironment env = sb.Env();
                env.UserTemp = link;
                Cleaner c = new Cleaner(env, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.UserTemp);
                Assert.True(!t.Available && !t.Selected, "a root that is a link is not offered");
                Assert.True(t.Note.Contains("atalho"), "says why: " + t.Note);
                t.Selected = true; t.Available = true; // forced by a buggy caller
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(victim), "nothing behind the link is touched, even when forced");
                Assert.True(r.Notes.Count >= 1, "clean says it skipped the target");
            }
        }

        [Test]
        public static void Clean_FailedDelete_PutsReadOnlyAttributeBack()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string ro = sb.Old(sb.UserTemp, "locked-ro.tmp", 20);
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UserTemp);
                using (FileStream fs = new FileStream(ro, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    c.Clean(plan, null, CancellationToken.None);
                }
                Assert.True(File.Exists(ro), "in-use file stays");
                Assert.True((File.GetAttributes(ro) & FileAttributes.ReadOnly) != 0, "read-only flag is restored when the delete fails");
                File.SetAttributes(ro, FileAttributes.Normal);
            }
        }

        [Test]
        public static void Sweep_ReadsTheProtectedFolderListOncePerTarget_NotPerFile()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                for (int i = 0; i < 120; i++) sb.Old(sb.UserTemp, "f" + i + ".tmp", 5);
                Cleaner c = NewCleaner(sb, null);
                int before = CleanupSafety.ProtectedFoldersCalls;
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                int analyzeCalls = CleanupSafety.ProtectedFoldersCalls - before;
                Assert.True(analyzeCalls < 40, "analysis of 120 files made " + analyzeCalls + " protected-list reads");
                Only(plan, CleanupTargetId.UserTemp);
                before = CleanupSafety.ProtectedFoldersCalls;
                c.Clean(plan, null, CancellationToken.None);
                int cleanCalls = CleanupSafety.ProtectedFoldersCalls - before;
                Assert.True(cleanCalls < 40, "cleaning 120 files made " + cleanCalls + " protected-list reads");
            }
        }

        [Test]
        public static void Clean_Thumbnails_OnlyThumbcacheFilesInTopFolder()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string t1 = sb.Old(sb.Thumbs, "thumbcache_256.db", 100);
                string t2 = sb.Old(sb.Thumbs, "thumbcache_idx.db", 10);
                string icon = sb.Old(sb.Thumbs, "iconcache_256.db", 100);
                string other = sb.Old(sb.Thumbs, "something.dat", 100);
                string nested = sb.Old(sb.Thumbs, Path.Combine("sub", "thumbcache_1.db"), 100);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Assert.Equal(110L, plan.Find(CleanupTargetId.Thumbnails).Bytes);
                Only(plan, CleanupTargetId.Thumbnails);
                c.Clean(plan, null, CancellationToken.None);
                Assert.True(!File.Exists(t1) && !File.Exists(t2), "thumbcache removed");
                Assert.True(File.Exists(icon) && File.Exists(other) && File.Exists(nested), "everything else stays");
            }
        }

        [Test]
        public static void Clean_Cancelled_StopsEarlyAndSaysSo()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                for (int i = 0; i < 700; i++) sb.Old(sb.UserTemp, "f" + i + ".tmp", 10);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UserTemp);
                CancellationTokenSource cts = new CancellationTokenSource();
                CleanupResult r = c.Clean(plan, delegate { cts.Cancel(); }, cts.Token);
                Assert.True(r.Cancelled, "cancelled");
                Assert.True(r.DeletedFiles < 700, "stopped before finishing");
                Assert.True(Directory.GetFiles(sb.UserTemp).Length > 0, "some files remain");
            }
        }

        [Test]
        public static void Clean_RecordsInChangeLog_NotUndoable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(sb.UserTemp, "a.tmp", 1048576);
                ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
                Cleaner c = NewCleaner(sb, log);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.UserTemp);
                c.Clean(plan, null, CancellationToken.None);
                List<ChangeEntry> l = log.List();
                Assert.Equal(1, l.Count);
                Assert.Equal(ChangeKind.Limpeza, l[0].Kind);
                Assert.True(!l[0].CanUndo, "cleanup cannot be undone");
                Assert.True(l[0].Description.Contains("1,0 MB"), "description has the freed size: " + l[0].Description);
            }
        }

        [Test]
        public static void Clean_RefusesForbiddenRootsEvenIfSelectedByHand()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string mini = Path.Combine(sb.Root, "Windows", "Minidump");
                string dmp = sb.Old(mini, "x.dmp", 100);
                CleanupEnvironment env = sb.Env();
                env.WindowsTemp = mini;
                Cleaner c = new Cleaner(env, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.WindowsTemp);
                t.Selected = true;       // forced by a buggy caller
                t.Available = true;
                c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(dmp), "minidump survives even when forced");
            }
        }

        [Test]
        public static void Plan_SelectedTotals_OnlyCountSelectedAvailableTargets()
        {
            CleanupPlan plan = new CleanupPlan();
            CleanupTarget a = new CleanupTarget(); a.Id = CleanupTargetId.UserTemp; a.Available = true; a.Selected = true; a.Bytes = 100; a.Files = 2;
            CleanupTarget b = new CleanupTarget(); b.Id = CleanupTargetId.WindowsTemp; b.Available = true; b.Selected = false; b.Bytes = 900; b.Files = 9;
            CleanupTarget c = new CleanupTarget(); c.Id = CleanupTargetId.Thumbnails; c.Available = false; c.Selected = true; c.Bytes = 50; c.Files = 1;
            plan.Targets.Add(a); plan.Targets.Add(b); plan.Targets.Add(c);
            Assert.Equal(100L, plan.SelectedBytes);
            Assert.Equal(2, plan.SelectedFiles);
            Assert.Equal(1, plan.SelectedCount);
        }
    }
}
