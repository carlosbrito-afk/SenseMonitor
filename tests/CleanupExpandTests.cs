using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // The cache and log targets. Everything happens in a sandbox under %TEMP% (CleanupSandbox);
    // processes, commands and the event log list are fakes, so nothing real is cleaned or run.
    public static class CleanupExpandTests
    {
        private static readonly string[] AllTargets =
        {
            "BrowserCaches", "ShaderCaches", "AppCaches", "DnsCache", "ErrorReports", "WindowsLogs", "EventLogs"
        };

        private static string Chrome(CleanupSandbox sb, string profile, string sub)
        {
            return Path.Combine(sb.LocalApp, @"Google\Chrome\User Data", profile, sub);
        }

        private static string Edge(CleanupSandbox sb, string profile, string sub)
        {
            return Path.Combine(sb.LocalApp, @"Microsoft\Edge\User Data", profile, sub);
        }

        private static Cleaner NewCleaner(CleanupSandbox sb, ChangeLog log)
        {
            return new Cleaner(sb.Env(), log);
        }

        private static void Only(CleanupPlan plan, params CleanupTargetId[] ids)
        {
            foreach (CleanupTarget t in plan.Targets) t.Selected = Array.IndexOf(ids, t.Id) >= 0;
        }

        // Analyze, select one target, clean.
        private static CleanupResult CleanOnly(CleanupSandbox sb, CleanupTargetId id)
        {
            Cleaner c = NewCleaner(sb, null);
            CleanupPlan plan = c.Analyze(null, CancellationToken.None);
            Only(plan, id);
            return c.Clean(plan, null, CancellationToken.None);
        }

        private static CleanupTarget AnalyzeOne(CleanupSandbox sb, CleanupTargetId id)
        {
            return NewCleaner(sb, null).Analyze(null, CancellationToken.None).Find(id);
        }

        // Every file under dir with its size and time, to prove something was not touched.
        private static string Snapshot(string dir)
        {
            StringBuilder sb = new StringBuilder();
            List<string> files = new List<string>(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                FileInfo fi = new FileInfo(f);
                sb.Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return sb.ToString();
        }

        private static bool HasRoot(CleanupTarget t, string path)
        {
            foreach (CleanupRoot r in t.Roots)
            {
                if (string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---- plan: groups, defaults, copy ----

        [Test]
        public static void Plan_NewTargets_GroupsAndDefaults()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.CreateDirectory(Chrome(sb, "Default", "Cache"));   // a target nobody can use is never selected
                CleanupPlan plan = CleanupPlan.Create(sb.Env());
                Assert.Equal(CleanupPlan.GroupCaches, plan.Find(CleanupTargetId.BrowserCaches).Group);
                Assert.Equal(CleanupPlan.GroupCaches, plan.Find(CleanupTargetId.ShaderCaches).Group);
                Assert.Equal(CleanupPlan.GroupCaches, plan.Find(CleanupTargetId.AppCaches).Group);
                Assert.Equal(CleanupPlan.GroupCaches, plan.Find(CleanupTargetId.DnsCache).Group);
                Assert.Equal(CleanupPlan.GroupLogs, plan.Find(CleanupTargetId.ErrorReports).Group);
                Assert.Equal(CleanupPlan.GroupLogs, plan.Find(CleanupTargetId.WindowsLogs).Group);
                Assert.Equal(CleanupPlan.GroupLogs, plan.Find(CleanupTargetId.EventLogs).Group);
                Assert.Equal(CleanupPlan.GroupBasics, plan.Find(CleanupTargetId.UserTemp).Group);
                Assert.Equal(CleanupPlan.GroupBasics, plan.Find(CleanupTargetId.Thumbnails).Group);
                // browser caches, DNS, WER and Windows logs on; shaders, app caches and event logs off
                Assert.True(plan.Find(CleanupTargetId.BrowserCaches).Selected, "browser caches default on");
                Assert.True(!plan.Find(CleanupTargetId.ShaderCaches).Selected, "shader caches default off");
                Assert.True(!plan.Find(CleanupTargetId.AppCaches).Selected, "app caches default off");
                Assert.True(plan.Find(CleanupTargetId.DnsCache).Selected, "dns default on");
                Assert.True(plan.Find(CleanupTargetId.ErrorReports).Selected, "WER default on");
                Assert.True(plan.Find(CleanupTargetId.WindowsLogs).Selected, "windows logs default on");
                Assert.True(!plan.Find(CleanupTargetId.EventLogs).Selected, "event logs default off");
                Assert.True(plan.Find(CleanupTargetId.DnsCache).Kind == CleanupKind.Action, "dns is an action");
                Assert.True(plan.Find(CleanupTargetId.EventLogs).Kind == CleanupKind.Action, "event logs are an action");
                Assert.True(plan.Find(CleanupTargetId.BrowserCaches).Kind == CleanupKind.Folders, "browser caches are folders");
            }
        }

        [Test]
        public static void Plan_GroupsAreContiguous_SoTheListCanShowOneHeaderEach()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupPlan plan = CleanupPlan.Create(sb.Env());
                List<string> order = new List<string>();
                foreach (CleanupTarget t in plan.Targets)
                {
                    Assert.True(!string.IsNullOrEmpty(t.Group), "every target has a group: " + t.Id);
                    if (order.Count == 0 || order[order.Count - 1] != t.Group) order.Add(t.Group);
                }
                Assert.Equal(3, order.Count);
            }
        }

        [Test]
        public static void Plan_NewTargets_HaveCopyInPortugueseAndNoBrandStrings()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupPlan plan = CleanupPlan.Create(sb.Env());
                foreach (CleanupTarget t in plan.Targets)
                {
                    Assert.True(!string.IsNullOrEmpty(t.Name) && !string.IsNullOrEmpty(t.Description), "copy for " + t.Id);
                    string all = (t.Name + " " + t.Description).ToLowerInvariant();
                    Assert.True(!all.Contains("acer") && !all.Contains("nitro"), "no brand strings in " + t.Id);
                }
                Assert.True(plan.Find(CleanupTargetId.BrowserCaches).Description.Contains("Senhas, cookies, histórico"), "says what stays");
                Assert.True(plan.Find(CleanupTargetId.ShaderCaches).Description.Contains("travar"), "warns games rebuild them");
                string ev = plan.Find(CleanupTargetId.EventLogs).Description;
                Assert.True(ev.Contains("System") && ev.Contains("Application") && ev.Contains("de propósito"), "says System/Application stay on purpose: " + ev);
                Assert.True(plan.Find(CleanupTargetId.WindowsLogs).Description.Contains("Panther"), "mentions Panther stays");
            }
        }

        [Test]
        public static void Plan_FolderTargets_StateTheMinimumAge()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupPlan plan = CleanupPlan.Create(sb.Env());
                foreach (CleanupTarget t in plan.Targets)
                {
                    if (t.Kind != CleanupKind.Folders) continue;
                    Assert.Equal(24, t.MinAgeHours);
                    Assert.True(t.Description.Contains("24 horas"), t.Id + " states the age rule");
                }
            }
        }

        // ---- locators ----

        [Test]
        public static void Locator_ChromiumProfileNames()
        {
            Assert.True(CleanupLocator.IsChromiumProfileDir("Default"), "Default");
            Assert.True(CleanupLocator.IsChromiumProfileDir("Profile 1"), "Profile 1");
            Assert.True(CleanupLocator.IsChromiumProfileDir("Profile 12"), "Profile 12");
            Assert.True(!CleanupLocator.IsChromiumProfileDir("Profile"), "no number");
            Assert.True(!CleanupLocator.IsChromiumProfileDir("Profile x"), "not a number");
            Assert.True(!CleanupLocator.IsChromiumProfileDir("System Profile"), "system profile");
            Assert.True(!CleanupLocator.IsChromiumProfileDir("Guest Profile"), "guest profile");
            Assert.True(!CleanupLocator.IsChromiumProfileDir("Crashpad"), "Crashpad");
            Assert.True(!CleanupLocator.IsChromiumProfileDir("Safe Browsing"), "Safe Browsing");
            Assert.True(!CleanupLocator.IsChromiumProfileDir(null), "null");
            Assert.True(CleanupLocator.IsTeamsProfileDir("WV2Profile_tfw"), "teams web view profile");
            Assert.True(!CleanupLocator.IsTeamsProfileDir("Crashpad"), "teams crashpad");
        }

        [Test]
        public static void Plan_BrowserRoots_AreOnlyTheCacheFoldersOfEachProfile()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.CreateDirectory(Chrome(sb, "Default", "Cache"));
                Directory.CreateDirectory(Chrome(sb, "Profile 2", "Cache"));
                Directory.CreateDirectory(Chrome(sb, "Crashpad", "Cache"));
                Directory.CreateDirectory(Chrome(sb, "System Profile", "Cache"));
                Directory.CreateDirectory(Edge(sb, "Default", "Cache"));
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Mozilla\Firefox\Profiles\abc.default-release\cache2"));
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"));
                CleanupTarget t = CleanupPlan.Create(sb.Env()).Find(CleanupTargetId.BrowserCaches);
                Assert.True(t.Available, "available");
                Assert.True(HasRoot(t, Chrome(sb, "Default", "Cache")), "chrome default cache");
                Assert.True(HasRoot(t, Chrome(sb, "Default", "Code Cache")), "chrome code cache");
                Assert.True(HasRoot(t, Chrome(sb, "Default", "GPUCache")), "chrome gpu cache");
                Assert.True(HasRoot(t, Chrome(sb, "Profile 2", "Cache")), "chrome second profile");
                Assert.True(!HasRoot(t, Chrome(sb, "Crashpad", "Cache")), "not a profile");
                Assert.True(!HasRoot(t, Chrome(sb, "System Profile", "Cache")), "not a user profile");
                Assert.True(HasRoot(t, Edge(sb, "Default", "Code Cache")), "edge");
                Assert.True(HasRoot(t, Path.Combine(sb.LocalApp, @"BraveSoftware\Brave-Browser\User Data\Default\GPUCache")), "brave");
                Assert.True(HasRoot(t, Path.Combine(sb.LocalApp, @"Mozilla\Firefox\Profiles\abc.default-release\cache2")), "firefox cache2");
                string[] allowedLeaf = { "Cache", "Code Cache", "GPUCache", "cache2" };
                foreach (CleanupRoot r in t.Roots)
                {
                    Assert.True(Array.IndexOf(allowedLeaf, Path.GetFileName(r.Path)) >= 0, "only cache folders, never a profile folder: " + r.Path);
                }
            }
        }

        [Test]
        public static void Plan_OperaRoots_CoverLocalAndRoamingProfiles()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Opera Software\Opera Stable"));
                Directory.CreateDirectory(Path.Combine(sb.RoamApp, @"Opera Software\Opera Stable"));
                Directory.CreateDirectory(Path.Combine(sb.RoamApp, @"Opera Software\Opera GX Stable"));
                CleanupTarget t = CleanupPlan.Create(sb.Env()).Find(CleanupTargetId.BrowserCaches);
                Assert.True(HasRoot(t, Path.Combine(sb.LocalApp, @"Opera Software\Opera Stable\Cache")), "opera local cache");
                Assert.True(HasRoot(t, Path.Combine(sb.RoamApp, @"Opera Software\Opera Stable\GPUCache")), "opera roaming gpu cache");
                Assert.True(HasRoot(t, Path.Combine(sb.RoamApp, @"Opera Software\Opera GX Stable\Code Cache")), "opera gx");
            }
        }

        [Test]
        public static void Plan_NoBrowserInstalled_BrowserTargetIsUnavailable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupTarget t = CleanupPlan.Create(sb.Env()).Find(CleanupTargetId.BrowserCaches);
                Assert.True(!t.Available && !t.Selected, "nothing to clean");
                Assert.True(!string.IsNullOrEmpty(t.Note), "says why");
            }
        }

        [Test]
        public static void Plan_MissingBaseFolders_DropTheTargetsBuiltOnThem()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupEnvironment env = sb.Env();
                env.LocalAppData = null;
                env.AppData = null;
                env.ProgramData = null;
                env.WindowsDir = null;
                CleanupPlan plan = CleanupPlan.Create(env);
                foreach (string name in new string[] { "BrowserCaches", "ShaderCaches", "AppCaches", "ErrorReports", "WindowsLogs" })
                {
                    CleanupTarget t = plan.Find((CleanupTargetId)Enum.Parse(typeof(CleanupTargetId), name));
                    Assert.True(!t.Available && !t.Selected, name + " is unavailable without its base folder");
                }
            }
        }

        // ---- browser caches ----

        [Test]
        public static void Browsers_CleanOnlyCacheFolders_KeepCookiesHistoryPasswordsSessionsExtensions()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string ud = Path.Combine(sb.LocalApp, @"Google\Chrome\User Data");
                string c1 = sb.Old(Path.Combine(ud, @"Default\Cache\Cache_Data"), "f_000001", 1000);
                string c2 = sb.Old(Path.Combine(ud, @"Default\Code Cache\js"), "a_0", 500);
                string c3 = sb.Old(Path.Combine(ud, @"Default\GPUCache"), "data_0", 200);
                string c4 = sb.Old(Path.Combine(ud, @"Profile 1\Cache\Cache_Data"), "f_000002", 300);
                string fresh = sb.Fresh(Path.Combine(ud, @"Default\Cache\Cache_Data"), "f_new", 50);
                string cookies = sb.Old(Path.Combine(ud, @"Default\Network"), "Cookies", 11);
                string history = sb.Old(Path.Combine(ud, "Default"), "History", 12);
                string logins = sb.Old(Path.Combine(ud, "Default"), "Login Data", 13);
                string sessions = sb.Old(Path.Combine(ud, @"Default\Sessions"), "Session_1", 14);
                string ext = sb.Old(Path.Combine(ud, @"Default\Extensions\abc\1.0"), "manifest.json", 15);
                string prefs = sb.Old(Path.Combine(ud, "Default"), "Preferences", 16);
                string localState = sb.Old(ud, "Local State", 17);
                string ffCache = sb.Old(Path.Combine(sb.LocalApp, @"Mozilla\Firefox\Profiles\x.default\cache2\entries"), "AB12", 400);
                string ffCookies = sb.Old(Path.Combine(sb.RoamApp, @"Mozilla\Firefox\Profiles\x.default"), "cookies.sqlite", 18);
                string ffPlaces = sb.Old(Path.Combine(sb.LocalApp, @"Mozilla\Firefox\Profiles\x.default"), "places.sqlite", 19);
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.BrowserCaches);
                Assert.Equal(1000L + 500 + 200 + 300 + 400, t.Bytes);
                Assert.Equal(5, t.Files);
                CleanupResult r = CleanOnly(sb, CleanupTargetId.BrowserCaches);
                Assert.True(!File.Exists(c1) && !File.Exists(c2) && !File.Exists(c3) && !File.Exists(c4) && !File.Exists(ffCache), "caches cleaned");
                Assert.True(File.Exists(fresh), "a cache file from the last 24 hours stays");
                foreach (string keep in new string[] { cookies, history, logins, sessions, ext, prefs, localState, ffCookies, ffPlaces })
                    Assert.True(File.Exists(keep), "kept: " + keep);
                Assert.Equal(5, r.DeletedFiles);
                Assert.Equal(2400L, r.FreedBytes);
                Assert.True(r.SkippedCount >= 1, "the recent file counts as kept");
            }
        }

        [Test]
        public static void Browsers_RunningBrowserIsSkipped_OthersStillCleaned()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string chrome = sb.Old(Chrome(sb, "Default", "Cache"), "c", 100);
                string edge = sb.Old(Edge(sb, "Default", "Cache"), "e", 100);
                sb.RunningProcesses.Add("chrome");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.BrowserCaches);
                Assert.True(t.Available, "edge is still cleanable");
                Assert.Equal(100L, t.Bytes);
                Assert.True(t.Note.Contains("Chrome") && t.Note.Contains("navegador"), "note names the open browser and says to close it: " + t.Note);
                CleanOnly(sb, CleanupTargetId.BrowserCaches);
                Assert.True(File.Exists(chrome), "open browser's cache untouched");
                Assert.True(!File.Exists(edge), "closed browser's cache cleaned");
            }
        }

        [Test]
        public static void Browsers_AllRunning_TargetUnavailableWithCloseHint()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string chrome = sb.Old(Chrome(sb, "Default", "Cache"), "c", 100);
                sb.RunningProcesses.Add("chrome");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.BrowserCaches);
                Assert.True(!t.Available && !t.Selected, "unavailable while the only browser runs");
                Assert.True(t.Note.Contains("Feche o navegador"), "note: " + t.Note);
                Assert.True(File.Exists(chrome), "analysis deletes nothing");
            }
        }

        [Test]
        public static void Browsers_ProcessProbeFails_CountsAsRunning()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string chrome = sb.Old(Chrome(sb, "Default", "Cache"), "c", 100);
                sb.ProcessProbeThrows = true;
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.BrowserCaches);
                Assert.True(!t.Available, "when it cannot be told, leave the browser alone");
                Assert.True(File.Exists(chrome), "untouched");
            }
        }

        [Test]
        public static void Browsers_OpenedBetweenPreviewAndClean_IsSkippedAtCleanTime()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string chrome = sb.Old(Chrome(sb, "Default", "Cache"), "c", 100);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.BrowserCaches);
                sb.RunningProcesses.Add("chrome");
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(chrome), "cache kept");
                Assert.Equal(0, r.DeletedFiles);
                Assert.True(r.Notes.Count >= 1, "result says it was skipped");
            }
        }

        [Test]
        public static void Browsers_EachBrowserUsesItsOwnProcessName()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string chrome = sb.Old(Chrome(sb, "Default", "Cache"), "c", 10);
                string edge = sb.Old(Edge(sb, "Default", "Cache"), "e", 10);
                string brave = sb.Old(Path.Combine(sb.LocalApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"), "b", 10);
                string opera = sb.Old(Path.Combine(sb.LocalApp, @"Opera Software\Opera Stable\Cache"), "o", 10);
                string ff = sb.Old(Path.Combine(sb.LocalApp, @"Mozilla\Firefox\Profiles\p\cache2"), "f", 10);
                sb.RunningProcesses.Add("msedge");
                sb.RunningProcesses.Add("brave");
                sb.RunningProcesses.Add("opera");
                sb.RunningProcesses.Add("firefox");
                CleanOnly(sb, CleanupTargetId.BrowserCaches);
                Assert.True(!File.Exists(chrome), "chrome was not running");
                Assert.True(File.Exists(edge) && File.Exists(brave) && File.Exists(opera) && File.Exists(ff), "the four running ones stay");
            }
        }

        // ---- shader caches ----

        [Test]
        public static void Shaders_CleansTheKnownCacheFolders_KeepsRecentAndLocked()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string d3d = sb.Old(Path.Combine(sb.LocalApp, "D3DSCache\\abc"), "x.idx", 100);
                string dx = sb.Old(Path.Combine(sb.LocalApp, @"NVIDIA\DXCache"), "a.bin", 100);
                string gl = sb.Old(Path.Combine(sb.LocalApp, @"NVIDIA\GLCache\q"), "b.bin", 100);
                string nv = sb.Old(Path.Combine(sb.ProgData, @"NVIDIA Corporation\NV_Cache"), "c.bin", 100);
                string intel = sb.Old(Path.Combine(sb.LocalApp, @"Intel\ShaderCache"), "d.bin", 100);
                string amd = sb.Old(Path.Combine(sb.LocalApp, @"AMD\DxCache"), "e.bin", 100);
                string fresh = sb.Fresh(Path.Combine(sb.LocalApp, "D3DSCache"), "new.idx", 100);
                string locked = sb.Old(Path.Combine(sb.LocalApp, @"AMD\DxCache"), "locked.bin", 100);
                string game = sb.Old(Path.Combine(sb.LocalApp, @"NVIDIA"), "NvBackend.exe", 100);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.ShaderCaches);
                CleanupResult r;
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    r = c.Clean(plan, null, CancellationToken.None);
                }
                Assert.True(!File.Exists(d3d) && !File.Exists(dx) && !File.Exists(gl) && !File.Exists(nv) && !File.Exists(intel) && !File.Exists(amd), "all six caches cleaned");
                Assert.True(File.Exists(fresh), "recent kept");
                Assert.True(File.Exists(locked), "locked file skipped");
                Assert.True(File.Exists(game), "files next to the cache folders stay");
                Assert.Equal(6, r.DeletedFiles);
                Assert.True(r.SkippedCount >= 2, "recent and locked counted as kept");
            }
        }

        [Test]
        public static void Shaders_NotAdmin_SkipsTheProgramDataCacheOnly()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string nv = sb.Old(Path.Combine(sb.ProgData, @"NVIDIA Corporation\NV_Cache"), "c.bin", 100);
                string d3d = sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "x.idx", 100);
                sb.Admin = false;
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.ShaderCaches);
                Assert.True(t.Available, "the per-user caches are still offered");
                Assert.Equal(100L, t.Bytes);
                CleanOnly(sb, CleanupTargetId.ShaderCaches);
                Assert.True(File.Exists(nv), "ProgramData cache needs admin");
                Assert.True(!File.Exists(d3d), "per-user cache cleaned");
            }
        }

        // ---- app caches ----

        [Test]
        public static void Apps_CleanCachesOnly_KeepAccountsAndLibraries()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string d1 = sb.Old(Path.Combine(sb.RoamApp, @"discord\Cache\Cache_Data"), "f", 100);
                string d2 = sb.Old(Path.Combine(sb.RoamApp, @"discord\Code Cache\js"), "f", 100);
                string d3 = sb.Old(Path.Combine(sb.RoamApp, @"discord\GPUCache"), "f", 100);
                string dKeep = sb.Old(Path.Combine(sb.RoamApp, @"discord\Local Storage\leveldb"), "000003.log", 100);
                string dSettings = sb.Old(Path.Combine(sb.RoamApp, "discord"), "settings.json", 100);
                string steam = sb.Old(Path.Combine(sb.LocalApp, @"Steam\htmlcache\Cache"), "f", 100);
                string steamKeep = sb.Old(Path.Combine(sb.LocalApp, @"Steam"), "steam.cfg", 100);
                string spot = sb.Old(Path.Combine(sb.LocalApp, @"Spotify\Data"), "a.file", 100);
                string spotKeep = sb.Old(Path.Combine(sb.LocalApp, @"Spotify"), "prefs", 100);
                string t1 = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\Cache"), "f", 100);
                string t2 = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\blob_storage\x"), "f", 100);
                string t3 = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\Application Cache\Cache"), "f", 100);
                string tDb = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\databases"), "db.sqlite", 100);
                string tIdx = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\IndexedDB"), "idx", 100);
                string tLs = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\Local Storage"), "ls", 100);
                string tCfg = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams"), "settings.json", 100);
                string nt = Path.Combine(sb.LocalApp, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView");
                string n1 = sb.Old(Path.Combine(nt, @"WV2Profile_tfw\Cache\Cache_Data"), "f", 100);
                string n2 = sb.Old(Path.Combine(nt, @"Default\Code Cache"), "f", 100);
                string nKeep = sb.Old(Path.Combine(nt, @"WV2Profile_tfw"), "Cookies", 100);
                string nCrash = sb.Old(Path.Combine(nt, @"Crashpad\Cache"), "f", 100);
                CleanupResult r = CleanOnly(sb, CleanupTargetId.AppCaches);
                foreach (string gone in new string[] { d1, d2, d3, steam, t1, t2, t3, n1, n2 })
                    Assert.True(!File.Exists(gone), "cache cleaned: " + gone);
                foreach (string keep in new string[] { dKeep, dSettings, steamKeep, spot, spotKeep, tDb, tIdx, tLs, tCfg, nKeep, nCrash })
                    Assert.True(File.Exists(keep), "kept: " + keep);
                Assert.Equal(9, r.DeletedFiles);
            }
        }

        [Test]
        public static void Apps_SpotifyIsNotATarget_NeitherPlanNorDescription()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Spotify\Data"));
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Spotify\Storage"));
                sb.Old(Path.Combine(sb.RoamApp, @"discord\Cache"), "f", 10);
                CleanupTarget t = CleanupPlan.Create(sb.Env()).Find(CleanupTargetId.AppCaches);
                foreach (CleanupRoot r in t.Roots)
                {
                    Assert.True(r.Path.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) < 0, "no Spotify folder: " + r.Path);
                    Assert.True(r.Owner != "Spotify", "no Spotify owner");
                }
                Assert.True(t.Description.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) < 0, "description does not promise Spotify: " + t.Description);
            }
        }

        [Test]
        public static void Apps_RunningAppIsSkipped()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string discord = sb.Old(Path.Combine(sb.RoamApp, @"discord\Cache"), "f", 100);
                string steam = sb.Old(Path.Combine(sb.LocalApp, @"Steam\htmlcache"), "f", 100);
                string spot = sb.Old(Path.Combine(sb.LocalApp, @"Spotify\Data"), "f", 100);
                string teams = sb.Old(Path.Combine(sb.RoamApp, @"Microsoft\Teams\Cache"), "f", 100);
                sb.RunningProcesses.Add("Discord");
                sb.RunningProcesses.Add("steamwebhelper");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.AppCaches);
                Assert.True(t.Available && t.Note.Contains("Discord") && t.Note.Contains("Steam"), "note names the open apps: " + t.Note);
                CleanOnly(sb, CleanupTargetId.AppCaches);
                Assert.True(File.Exists(discord) && File.Exists(steam), "open apps untouched");
                Assert.True(File.Exists(spot), "Spotify is never part of this target (offline downloads)");
                Assert.True(!File.Exists(teams), "Teams was closed, so it is cleaned");
            }
        }

        [Test]
        public static void Apps_AllOpen_TargetUnavailable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(Path.Combine(sb.RoamApp, @"discord\Cache"), "f", 100);
                sb.RunningProcesses.Add("Discord");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.AppCaches);
                Assert.True(!t.Available, "the only app is open");
                Assert.True(t.Note.Contains("Feche o aplicativo"), t.Note);
            }
        }

        // ---- Windows Error Reporting ----

        [Test]
        public static void ErrorReports_CleanQueuesAndArchives_KeepBlueScreenReports()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string wer = Path.Combine(sb.ProgData, @"Microsoft\Windows\WER");
                string a1 = sb.Old(Path.Combine(wer, @"ReportArchive\AppCrash_chrome.exe_1"), "Report.wer", 100);
                string a2 = sb.Old(Path.Combine(wer, @"ReportQueue\NonCritical_x"), "Report.wer", 100);
                string k1 = sb.Old(Path.Combine(wer, @"ReportArchive\Kernel_1a_abc"), "Report.wer", 100);
                string k2 = sb.Old(Path.Combine(wer, @"ReportQueue\LiveKernelEvent_141_x"), "Report.wer", 100);
                string k3 = sb.Old(Path.Combine(wer, @"ReportArchive\WHEA_x"), "Report.wer", 100);
                string fresh = sb.Fresh(Path.Combine(wer, @"ReportArchive\AppCrash_new"), "Report.wer", 100);
                string other = sb.Old(wer, "Settings.dat", 100);
                string local = sb.Old(Path.Combine(sb.LocalApp, @"Microsoft\Windows\WER\ReportArchive\AppHang_x"), "Report.wer", 100);
                string localQueue = sb.Old(Path.Combine(sb.LocalApp, @"Microsoft\Windows\WER\ReportQueue\y"), "Report.wer", 100);
                CleanupResult r = CleanOnly(sb, CleanupTargetId.ErrorReports);
                Assert.True(!File.Exists(a1) && !File.Exists(a2) && !File.Exists(local) && !File.Exists(localQueue), "old reports cleaned");
                Assert.True(File.Exists(k1) && File.Exists(k2) && File.Exists(k3), "blue screen reports stay for the investigation");
                Assert.True(File.Exists(fresh), "recent report stays");
                Assert.True(File.Exists(other), "files outside the queues stay");
                Assert.Equal(4, r.DeletedFiles);
            }
        }

        [Test]
        public static void ErrorReports_NotAdmin_StillCleansTheUserFolder()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string pd = sb.Old(Path.Combine(sb.ProgData, @"Microsoft\Windows\WER\ReportArchive\A_x"), "Report.wer", 100);
                string user = sb.Old(Path.Combine(sb.LocalApp, @"Microsoft\Windows\WER\ReportArchive\B_x"), "Report.wer", 100);
                sb.Admin = false;
                CleanOnly(sb, CleanupTargetId.ErrorReports);
                Assert.True(File.Exists(pd), "ProgramData needs admin");
                Assert.True(!File.Exists(user), "per-user reports cleaned");
            }
        }

        // ---- Windows logs ----

        [Test]
        public static void WindowsLogs_CleanOldLogsByPattern_KeepCurrentCbsLogAndEverythingElse()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string logs = Path.Combine(sb.WinDir, "Logs");
                string cbsCurrent = sb.Old(Path.Combine(logs, "CBS"), "CBS.log", 1000);
                string cbsOld = sb.Old(Path.Combine(logs, "CBS"), "CbsPersist_20260101.log", 100);
                string cbsCab = sb.Old(Path.Combine(logs, "CBS"), "CbsPersist_20260101.cab", 100);
                string cbsOther = sb.Old(Path.Combine(logs, "CBS"), "readme.txt", 100);
                string cbsQuirk = sb.Old(Path.Combine(logs, "CBS"), "x.logx", 100);
                string cbsFresh = sb.Fresh(Path.Combine(logs, "CBS"), "CbsPersist_new.log", 100);
                string cbsSub = sb.Old(Path.Combine(logs, @"CBS\sub"), "deep.log", 100);
                string dism = sb.Old(Path.Combine(logs, "DISM"), "dism.log", 100);
                string dismOther = sb.Old(Path.Combine(logs, "DISM"), "notes.txt", 100);
                string wu = sb.Old(Path.Combine(logs, "WindowsUpdate"), "WindowsUpdate.1.etl", 100);
                string wuLog = sb.Old(Path.Combine(logs, "WindowsUpdate"), "keep.log", 100);
                string panther = sb.Old(Path.Combine(sb.WinDir, "Panther"), "setupact.log", 100);
                string pantherLogs = sb.Old(Path.Combine(logs, "Panther"), "setuperr.log", 100);
                string sibling = sb.Old(logs, "top.log", 100);
                CleanupResult r = CleanOnly(sb, CleanupTargetId.WindowsLogs);
                Assert.True(!File.Exists(cbsOld) && !File.Exists(cbsCab) && !File.Exists(dism) && !File.Exists(wu), "old logs cleaned");
                Assert.True(File.Exists(cbsCurrent), "the current CBS.log stays (repair messages point to it)");
                foreach (string keep in new string[] { cbsOther, cbsQuirk, cbsFresh, cbsSub, dismOther, wuLog, panther, pantherLogs, sibling })
                    Assert.True(File.Exists(keep), "kept: " + keep);
                Assert.Equal(4, r.DeletedFiles);
            }
        }

        [Test]
        public static void WindowsLogs_DismLogInUse_Stays()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string dism = sb.Old(Path.Combine(sb.WinDir, @"Logs\DISM"), "dism.log", 100);
                string other = sb.Old(Path.Combine(sb.WinDir, @"Logs\DISM"), "old.log", 100);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.WindowsLogs);
                using (new FileStream(dism, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    c.Clean(plan, null, CancellationToken.None);
                }
                Assert.True(File.Exists(dism), "dism.log in use stays");
                Assert.True(!File.Exists(other), "other log cleaned");
            }
        }

        [Test]
        public static void WindowsLogs_NeedAdmin()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string f = sb.Old(Path.Combine(sb.WinDir, @"Logs\CBS"), "CbsPersist_1.log", 100);
                sb.Admin = false;
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.WindowsLogs);
                Assert.True(!t.Available, "needs admin");
                Assert.True(t.Note.ToLowerInvariant().Contains("administrador"), t.Note);
                Assert.True(File.Exists(f), "untouched");
            }
        }

        [Test]
        public static void WindowsLogs_RootsListNoPantherAndOnlyThreeFolders()
        {
            CleanupEnvironment env = new CleanupEnvironment();
            env.WindowsDir = @"C:\Windows";
            List<CleanupRoot> roots = CleanupLocator.WindowsLogRoots(env);
            Assert.Equal(3, roots.Count);
            Assert.Equal(@"C:\Windows\Logs\CBS", roots[0].Path);
            Assert.Equal(@"C:\Windows\Logs\DISM", roots[1].Path);
            Assert.Equal(@"C:\Windows\Logs\WindowsUpdate", roots[2].Path);
            foreach (CleanupRoot r in roots)
            {
                Assert.True(!r.Recursive, "top folder only: " + r.Path);
                Assert.True(r.Path.IndexOf("Panther", StringComparison.OrdinalIgnoreCase) < 0, "Panther is never listed");
            }
        }

        // ---- DNS flush ----

        [Test]
        public static void Dns_AnalysisRunsNothing_CleanRunsExactlyOneCommand()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.DnsCache);
                Assert.True(t.Available && t.Analyzed, "available after analysis");
                Assert.Equal(0, sb.CommandsRun.Count);
                Only(plan, CleanupTargetId.DnsCache);
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.Equal(1, sb.CommandsRun.Count);
                Assert.Equal(Path.Combine(sb.Sys32, "ipconfig.exe"), sb.CommandsRun[0].FileName);
                Assert.Equal("/flushdns", sb.CommandsRun[0].Arguments);
                Assert.Equal(1, r.ActionsDone);
                Assert.Equal(0, r.ActionsFailed);
                Assert.Equal(1, r.PerTarget.Count);
                Assert.True(r.PerTarget[0].IsAction && r.PerTarget[0].ActionOk, "reports OK");
                Assert.True(r.Done.Count == 1 && r.Notes.Count == 0, "done, nothing to warn about");
                Assert.Equal(0L, r.FreedBytes);
            }
        }

        [Test]
        public static void Dns_Failure_IsReportedNotHidden()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.CommandExitCode = 1;
                CleanupResult r = CleanOnly(sb, CleanupTargetId.DnsCache);
                Assert.Equal(1, r.ActionsFailed);
                Assert.Equal(0, r.ActionsDone);
                Assert.True(!r.PerTarget[0].ActionOk, "reports failure");
                Assert.True(r.Notes.Count == 1 && r.Notes[0].Contains("código 1"), "says the code: " + (r.Notes.Count > 0 ? r.Notes[0] : ""));
            }
        }

        [Test]
        public static void Dns_RunnerThrows_CountsAsFailure()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupEnvironment env = sb.Env();
                env.RunCommand = delegate { throw new InvalidOperationException("boom"); };
                Cleaner c = new Cleaner(env, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.DnsCache);
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.Equal(1, r.ActionsFailed);
            }
        }

        [Test]
        public static void Dns_NotAdmin_OrToolMissing_IsUnavailable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Admin = false;
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.DnsCache);
                Assert.True(!t.Available && !t.Selected, "needs admin");
            }
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                File.Delete(Path.Combine(sb.Sys32, "ipconfig.exe"));
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.DnsCache);
                Assert.True(!t.Available, "ipconfig missing from System32");
            }
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupEnvironment env = sb.Env();
                env.RunCommand = null;
                CleanupTarget t = new Cleaner(env, null).Analyze(null, CancellationToken.None).Find(CleanupTargetId.DnsCache);
                Assert.True(!t.Available, "no runner, no action");
            }
        }

        // ---- event logs ----

        private static void FillChannels(CleanupSandbox sb)
        {
            sb.Channel("System", EventChannelKind.Administrative, 9000);
            sb.Channel("Application", EventChannelKind.Administrative, 900);
            sb.Channel("Security", EventChannelKind.Administrative, 90);
            sb.Channel("Setup", EventChannelKind.Operational, 9);
            sb.Channel("Microsoft-Windows-Kernel-Power/Thermal-Operational", EventChannelKind.Operational, 20);
            sb.Channel("Microsoft-Windows-WHEA-Logger/Operational", EventChannelKind.Operational, 20);
            sb.Channel("Microsoft-Windows-WER-Diag/Operational", EventChannelKind.Operational, 20);
            sb.Channel("Microsoft-Windows-Diagnostics-Performance/Operational", EventChannelKind.Operational, 20);
            sb.Channel("Microsoft-Windows-WLAN-AutoConfig/Operational", EventChannelKind.Operational, 40);
            sb.Channel("Microsoft-Windows-PowerShell/Operational", EventChannelKind.Operational, 7);
            sb.Channel("Microsoft-Windows-NlaSvc/Operational", EventChannelKind.Operational, 0);
        }

        [Test]
        public static void EventLogs_AnalysisIsReadOnly_AndSummarizesTheChannelCount()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.EventLogs);
                Assert.True(t.Available, "available");
                Assert.True(!t.Selected, "off by default");
                Assert.Equal("2 canais", t.Summary);
                Assert.Equal(0, sb.CommandsRun.Count);
            }
        }

        [Test]
        public static void EventLogs_ClearOnlyTheFilteredChannels_WithTheFullWevtutilPath()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                CleanupResult r = CleanOnly(sb, CleanupTargetId.EventLogs);
                Assert.Equal(2, sb.CommandsRun.Count);
                foreach (CleanupCommand cmd in sb.CommandsRun)
                {
                    Assert.Equal(Path.Combine(sb.Sys32, "wevtutil.exe"), cmd.FileName);
                    Assert.True(cmd.Arguments.StartsWith("cl \""), "wevtutil cl: " + cmd.Arguments);
                    foreach (string never in new string[] { "\"System\"", "\"Application\"", "\"Security\"", "\"Setup\"", "Kernel-Power", "WHEA", "WER", "Diagnostics-Performance" })
                        Assert.True(!cmd.Arguments.Contains(never), "never cleared: " + cmd.Arguments);
                }
                Assert.Equal("cl \"Microsoft-Windows-PowerShell/Operational\"", sb.CommandsRun[0].Arguments);
                Assert.Equal("cl \"Microsoft-Windows-WLAN-AutoConfig/Operational\"", sb.CommandsRun[1].Arguments);
                Assert.Equal(1, r.ActionsDone);
                Assert.True(r.PerTarget[0].IsAction && r.PerTarget[0].ActionOk, "reports OK");
                Assert.True(r.Done[0].Contains("2 registros de eventos limpos"), r.Done[0]);
            }
        }

        [Test]
        public static void EventLogs_OneChannelFails_IsCountedAndTheRestStillRun()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                sb.FailingChannels.Add("Microsoft-Windows-PowerShell/Operational");
                CleanupResult r = CleanOnly(sb, CleanupTargetId.EventLogs);
                Assert.Equal(2, sb.CommandsRun.Count);
                Assert.Equal(1, r.PerTarget[0].SkippedCount);
                Assert.True(r.PerTarget[0].ActionOk, "one cleared is still a success");
                Assert.True(r.Done[0].Contains("1 não pôde ser limpo"), r.Done[0]);
            }
        }

        [Test]
        public static void EventLogs_AllFail_ReportsFailure()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                sb.CommandExitCode = 5;
                CleanupResult r = CleanOnly(sb, CleanupTargetId.EventLogs);
                Assert.Equal(1, r.ActionsFailed);
                Assert.True(!r.PerTarget[0].ActionOk, "failure");
            }
        }

        [Test]
        public static void EventLogs_NothingToClear_OrListUnreadable_IsUnavailable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Channel("System", EventChannelKind.Administrative, 10);
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.EventLogs);
                Assert.True(!t.Available && !t.Selected, "only protected channels: nothing to offer");
            }
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupEnvironment env = sb.Env();
                env.EventChannels = delegate { throw new InvalidOperationException("access denied"); };
                CleanupTarget t = new Cleaner(env, null).Analyze(null, CancellationToken.None).Find(CleanupTargetId.EventLogs);
                Assert.True(!t.Available, "unreadable list: not offered");
            }
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                File.Delete(Path.Combine(sb.Sys32, "wevtutil.exe"));
                Assert.True(!AnalyzeOne(sb, CleanupTargetId.EventLogs).Available, "wevtutil missing");
            }
        }

        [Test]
        public static void EventLogs_ChannelListIsFilteredAgainAtCleanTime()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.EventLogs);
                sb.Channels.Clear();                       // the list changes after the preview
                sb.Channel("System", EventChannelKind.Operational, 5);   // a (buggy) list that says System is operational
                sb.Channel("Microsoft-Windows-NCSI/Operational", EventChannelKind.Operational, 1);
                c.Clean(plan, null, CancellationToken.None);
                Assert.Equal(1, sb.CommandsRun.Count);
                Assert.Equal("cl \"Microsoft-Windows-NCSI/Operational\"", sb.CommandsRun[0].Arguments);
            }
        }

        [Test]
        public static void EventLogs_Cancelled_StopsBetweenChannels()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string[] five = { "Microsoft-Windows-Audio/Operational", "Microsoft-Windows-Bits-Client/Operational", "Microsoft-Windows-NCSI/Operational", "Microsoft-Windows-NetworkProfile/Operational", "Microsoft-Windows-Store/Operational" };
                foreach (string n in five) sb.Channel(n, EventChannelKind.Operational, 3);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.EventLogs);
                CancellationTokenSource cts = new CancellationTokenSource();
                int reports = 0;
                CleanupResult r = c.Clean(plan, delegate { if (++reports >= 2) cts.Cancel(); }, cts.Token);
                Assert.True(r.Cancelled, "cancelled");
                Assert.True(sb.CommandsRun.Count < 5, "stopped before the last channel");
            }
        }

        [Test]
        public static void Clean_WithDefaultSelection_RunsDnsOnly_NeverTheEventLogs()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                c.Clean(plan, null, CancellationToken.None);
                Assert.Equal(1, sb.CommandsRun.Count);
                Assert.Equal("/flushdns", sb.CommandsRun[0].Arguments);
            }
        }

        [Test]
        public static void Clean_ChangeLogMentionsTheActions()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
                Cleaner c = NewCleaner(sb, log);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.DnsCache);
                c.Clean(plan, null, CancellationToken.None);
                List<ChangeEntry> l = log.List();
                Assert.Equal(1, l.Count);
                Assert.True(!l[0].CanUndo, "not undoable");
                Assert.True(l[0].Description.Contains("1 ação executada"), l[0].Description);
            }
        }

        // ---- analysis stays read-only ----

        [Test]
        public static void Analyze_EverythingPopulated_ChangesNothingAndRunsNothing()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                sb.Old(Chrome(sb, "Default", "Cache"), "c", 100);
                sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "x", 100);
                sb.Old(Path.Combine(sb.RoamApp, @"discord\Cache"), "d", 100);
                sb.Old(Path.Combine(sb.ProgData, @"Microsoft\Windows\WER\ReportArchive\A_1"), "r", 100);
                sb.Old(Path.Combine(sb.WinDir, @"Logs\CBS"), "CbsPersist_1.log", 100);
                sb.Old(sb.UserTemp, "t.tmp", 100);
                string before = Snapshot(sb.Root);
                CleanupPlan plan = NewCleaner(sb, null).Analyze(null, CancellationToken.None);
                Assert.Equal(before, Snapshot(sb.Root));
                Assert.Equal(0, sb.CommandsRun.Count);
                Assert.True(plan.Find(CleanupTargetId.BrowserCaches).Bytes == 100 && plan.Find(CleanupTargetId.WindowsLogs).Bytes == 100, "but it did measure");
            }
        }

        [Test]
        public static void Clean_PerTargetResultsCarryIdBytesFilesAndSkipped()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "a", 100);
                sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "b", 200);
                sb.Fresh(Path.Combine(sb.LocalApp, "D3DSCache"), "c", 50);
                CleanupResult r = CleanOnly(sb, CleanupTargetId.ShaderCaches);
                Assert.Equal(1, r.PerTarget.Count);
                CleanupTargetResult tr = r.PerTarget[0];
                Assert.True(tr.Id == CleanupTargetId.ShaderCaches, "id");
                Assert.True(!tr.IsAction, "folder target");
                Assert.Equal(300L, tr.FreedBytes);
                Assert.Equal(2, tr.DeletedFiles);
                Assert.Equal(1, tr.SkippedCount);
            }
        }

        [Test]
        public static void Progress_OfAnActionIsMarked_SoTheUiDoesNotCallStepsFiles()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                FillChannels(sb);
                sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "x", 10);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Only(plan, CleanupTargetId.EventLogs, CleanupTargetId.ShaderCaches);
                bool sawAction = false, sawFolder = false;
                c.Clean(plan, delegate(CleanupProgress p)
                {
                    if (p.IsAction) sawAction = true; else sawFolder = true;
                }, CancellationToken.None);
                Assert.True(sawAction, "event log steps are reported as an action");
                Assert.True(sawFolder, "folder sweeps are not");
            }
        }

        // ---- links and forbidden paths ----

        [Test]
        public static void Safety_HasLink_LeafAndChain()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string real = Path.Combine(sb.Root, "real");
                Directory.CreateDirectory(Path.Combine(real, "inner"));
                string link = Path.Combine(sb.Root, "lnk");
                Assert.True(sb.MakeJunction(link, real), "mklink /J must work here");
                Assert.True(CleanupSafety.HasLink(null, link), "leaf is a junction");
                Assert.True(!CleanupSafety.HasLink(null, real), "a plain folder");
                Assert.True(CleanupSafety.HasLink(sb.Root, Path.Combine(link, "inner")), "a folder between base and path is a junction");
                Assert.True(!CleanupSafety.HasLink(sb.Root, Path.Combine(real, "inner")), "no link anywhere");
                Assert.True(CleanupSafety.HasLink(Path.Combine(sb.Root, "other"), real), "not under the base: refused");
                Assert.True(!CleanupSafety.HasLink(sb.Root, Path.Combine(real, "missing")), "a missing folder is not a link");
                Assert.True(CleanupSafety.HasLink(null, ""), "empty path");
            }
        }

        [Test]
        public static void Junctions_ProfileFolderThatIsALink_IsNeverFollowed()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                // Chrome's "Default" is a junction to a folder with a Cache folder inside: the root
                // looks like a plain folder but sits behind a link.
                string outside = Path.Combine(sb.Root, "outside");
                string precious = sb.Old(Path.Combine(outside, "Cache"), "precious.doc", 123);
                string ud = Path.Combine(sb.LocalApp, @"Google\Chrome\User Data");
                Directory.CreateDirectory(ud);
                Assert.True(sb.MakeJunction(Path.Combine(ud, "Default"), outside), "mklink /J must work here");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.BrowserCaches);
                Assert.True(!t.Available, "the only browser is behind a link");
                CleanupResult r = CleanOnly(sb, CleanupTargetId.BrowserCaches);
                Assert.True(File.Exists(precious), "nothing behind the link is touched");
                Assert.Equal(0, r.DeletedFiles);
            }
        }

        [Test]
        public static void Junctions_UserDataThatIsALink_IsNeverFollowed()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string outside = Path.Combine(sb.Root, "docs-like");
                string precious = sb.Old(Path.Combine(outside, @"Default\Cache"), "precious.doc", 123);
                string parent = Path.Combine(sb.LocalApp, @"Microsoft\Edge");
                Directory.CreateDirectory(parent);
                Assert.True(sb.MakeJunction(Path.Combine(parent, "User Data"), outside), "mklink /J must work here");
                CleanOnly(sb, CleanupTargetId.BrowserCaches);
                Assert.True(File.Exists(precious), "file behind the User Data junction survives");
            }
        }

        [Test]
        public static void Junctions_CacheFolderThatIsALink_ToMinidump_IsNeverFollowed()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string mini = Path.Combine(sb.Root, @"elsewhere\Minidump");
                string dmp = sb.Old(mini, "111426-1.dmp", 500);
                string profile = Path.Combine(sb.LocalApp, @"Google\Chrome\User Data\Default");
                Directory.CreateDirectory(profile);
                Assert.True(sb.MakeJunction(Path.Combine(profile, "Cache"), mini), "mklink /J must work here");
                string good = sb.Old(Path.Combine(profile, "GPUCache"), "g", 10);
                CleanOnly(sb, CleanupTargetId.BrowserCaches);
                Assert.True(File.Exists(dmp), "crash dump behind a junction survives");
                Assert.True(!File.Exists(good), "the plain cache folder next to it is still cleaned");
            }
        }

        [Test]
        public static void Junctions_NestedLinkInsideACache_IsSkippedNotDeleted()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string outside = Path.Combine(sb.Root, "outside");
                string precious = sb.Old(outside, "precious.doc", 123);
                string cache = Path.Combine(sb.LocalApp, "D3DSCache");
                string old = sb.Old(cache, "x", 10);
                string link = Path.Combine(cache, "evil");
                Assert.True(sb.MakeJunction(link, outside), "mklink /J must work here");
                CleanupResult r = CleanOnly(sb, CleanupTargetId.ShaderCaches);
                Assert.True(File.Exists(precious), "file behind the nested junction survives");
                Assert.True(Directory.Exists(link), "the junction itself stays");
                Assert.True(!File.Exists(old), "ordinary file cleaned");
                Assert.True(r.SkippedCount >= 1, "link counted as skipped");
            }
        }

        [Test]
        public static void Junctions_WindowsLogsFolderLinkedAway_IsRefused()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string outside = Path.Combine(sb.Root, "outside");
                string precious = sb.Old(Path.Combine(outside, "CBS"), "CbsPersist_1.log", 123);
                Assert.True(sb.MakeJunction(Path.Combine(sb.WinDir, "Logs"), outside), "mklink /J must work here");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.WindowsLogs);
                Assert.True(!t.Available, "Logs is a link: not offered");
                CleanOnly(sb, CleanupTargetId.WindowsLogs);
                Assert.True(File.Exists(precious), "nothing behind the link is touched");
            }
        }

        [Test]
        public static void Junctions_WerFolderLinkedToAForbiddenFolder_IsRefused()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string mini = Path.Combine(sb.Root, @"x\Minidump");
                string dmp = sb.Old(mini, "a.dmp", 10);
                string wer = Path.Combine(sb.ProgData, @"Microsoft\Windows\WER");
                Directory.CreateDirectory(wer);
                Assert.True(sb.MakeJunction(Path.Combine(wer, "ReportArchive"), mini), "mklink /J must work here");
                CleanOnly(sb, CleanupTargetId.ErrorReports);
                Assert.True(File.Exists(dmp), "dump survives");
            }
        }

        [Test]
        public static void Junctions_ShaderRootThatIsALink_IsRefused()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string outside = Path.Combine(sb.Root, "outside");
                string precious = sb.Old(outside, "precious.doc", 123);
                Assert.True(sb.MakeJunction(Path.Combine(sb.LocalApp, "D3DSCache"), outside), "mklink /J must work here");
                CleanupTarget t = AnalyzeOne(sb, CleanupTargetId.ShaderCaches);
                Assert.True(!t.Available, "the only shader cache is a link");
                CleanOnly(sb, CleanupTargetId.ShaderCaches);
                Assert.True(File.Exists(precious), "untouched");
            }
        }

        [Test]
        public static void Roots_ForcedIntoAForbiddenFolder_AreRefusedAtCleanTime()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string mini = Path.Combine(sb.Root, @"Windows\Minidump");
                string dmp = sb.Old(mini, "x.dmp", 100);
                Cleaner c = NewCleaner(sb, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.ShaderCaches);
                CleanupRoot evil = new CleanupRoot();
                evil.Path = mini;
                t.Roots.Add(evil);           // a buggy caller adds a forbidden root by hand
                t.Selected = true;
                t.Available = true;
                c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(dmp), "minidump survives");
            }
        }

        [Test]
        public static void Roots_InsideDocuments_AreNeverOffered()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                CleanupEnvironment env = sb.Env();
                env.LocalAppData = Path.Combine(docs, "forja-never-created");   // read-only: the folder does not exist
                env.AppData = env.LocalAppData;
                env.ProgramData = env.LocalAppData;
                CleanupPlan plan = CleanupPlan.Create(env);
                foreach (CleanupTargetId id in new CleanupTargetId[] { CleanupTargetId.ShaderCaches, CleanupTargetId.AppCaches, CleanupTargetId.ErrorReports })
                {
                    CleanupTarget t = plan.Find(id);
                    Assert.True(!t.Available && !t.Selected, id + " cannot resolve into Documents");
                    Assert.Equal(0, t.Roots.Count);
                }
            }
        }

        private static void AssertAllRootsSafe(CleanupPlan plan)
        {
            string[] banned = { "Minidump", "MEMORY.DMP", "Prefetch", "$Recycle.Bin", "Windows.old", "Panther", "Downloads", "Documents",
                "System Volume Information", "LiveKernelReports", "Recovery", "Desktop" };
            foreach (CleanupTarget t in plan.Targets)
            {
                foreach (CleanupRoot r in t.Roots)
                {
                    Assert.True(!CleanupSafety.IsUnsafeRoot(r.Path), t.Id + " root is on the safe list: " + r.Path);
                    Assert.True(!CleanupSafety.IsForbidden(r.Path), t.Id + " root is not forbidden: " + r.Path);
                    foreach (string seg in r.Path.Split('\\'))
                        Assert.True(Array.IndexOf(banned, seg) < 0, t.Id + " root has no protected folder name (" + seg + "): " + r.Path);
                }
            }
        }

        [Test]
        public static void Roots_OfTheRealMachineEnvironment_AreAllSafe_ReadOnly()
        {
            // Builds the plan with this machine's real folders. Plan.Create only lists folder names.
            CleanupPlan plan = CleanupPlan.Create(Cleaner.SystemEnvironment());
            AssertAllRootsSafe(plan);
            Assert.True(plan.Find(CleanupTargetId.WindowsLogs).Roots.Count == 3, "three log folders");
        }

        [Test]
        public static void Roots_OfAFullySandboxedInstall_AreAllSafe()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.CreateDirectory(Chrome(sb, "Default", "Cache"));
                Directory.CreateDirectory(Chrome(sb, "Profile 3", "Cache"));
                Directory.CreateDirectory(Edge(sb, "Default", "Cache"));
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Mozilla\Firefox\Profiles\a\cache2"));
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Opera Software\Opera Stable"));
                Directory.CreateDirectory(Path.Combine(sb.LocalApp, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_x"));
                AssertAllRootsSafe(CleanupPlan.Create(sb.Env()));
            }
        }

        [Test]
        public static void Roots_AllPlainPathsHaveABaseSoLinksAboveThemAreChecked()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                Directory.CreateDirectory(Chrome(sb, "Default", "Cache"));
                CleanupPlan plan = CleanupPlan.Create(sb.Env());
                foreach (CleanupTargetId id in new CleanupTargetId[] { CleanupTargetId.BrowserCaches, CleanupTargetId.ShaderCaches, CleanupTargetId.AppCaches, CleanupTargetId.ErrorReports, CleanupTargetId.WindowsLogs })
                {
                    foreach (CleanupRoot r in plan.Find(id).Roots)
                    {
                        Assert.True(!string.IsNullOrEmpty(r.Base), id + " root has a base: " + r.Path);
                        Assert.True(CleanupSafety.IsUnder(r.Base, r.Path), id + " root is under its base: " + r.Path);
                    }
                }
            }
        }
    }
}
