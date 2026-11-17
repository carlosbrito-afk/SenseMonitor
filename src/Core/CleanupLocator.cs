using System;
using System.Collections.Generic;
using System.IO;

namespace Forja.Core
{
    // Where the cache and log folders of the new targets are. Only cache folders are ever
    // named here: a browser profile folder itself, cookies, history and the like never appear.
    // The folders are candidates; the cleaner checks each one (safe, present, not a link, not
    // in use) when it analyzes and again when it cleans.
    public static class CleanupLocator
    {
        private static readonly string[] ChromiumCacheNames = { "Cache", "Code Cache", "GPUCache" };

        // Chromium keeps one folder per profile: "Default", "Profile 1", "Profile 2"...
        public static bool IsChromiumProfileDir(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) return true;
            if (!name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = name.Substring("Profile ".Length);
            if (rest.Length == 0) return false;
            foreach (char c in rest)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        // The Teams web view keeps profiles named "Default", "Profile N" or "WV2Profile_xxx".
        public static bool IsTeamsProfileDir(string name)
        {
            return IsChromiumProfileDir(name)
                || (!string.IsNullOrEmpty(name) && name.StartsWith("WV2Profile", StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> ChildDirs(string dir)
        {
            List<string> l = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) return l;
                foreach (string d in Directory.GetDirectories(dir)) l.Add(Path.GetFileName(d));
            }
            catch (Exception)
            {
            }
            return l;
        }

        private static CleanupRoot Root(string path, string baseDir, string owner, string[] procs)
        {
            CleanupRoot r = new CleanupRoot();
            r.Path = path;
            r.Base = baseDir;
            r.Owner = owner;
            r.OwnerProcesses = procs;
            return r;
        }

        private static string Join(string a, string b)
        {
            return Path.Combine(a, b);
        }

        // Cache, Code Cache and GPUCache of one profile folder.
        private static void AddChromiumCaches(List<CleanupRoot> l, string profileDir, string baseDir, string owner, string[] procs)
        {
            foreach (string c in ChromiumCacheNames) l.Add(Root(Join(profileDir, c), baseDir, owner, procs));
        }

        private static void AddProfiles(List<CleanupRoot> l, string userData, string baseDir, string owner, string[] procs, bool includeSelf, bool teams)
        {
            if (includeSelf && Directory.Exists(userData)) AddChromiumCaches(l, userData, baseDir, owner, procs);
            foreach (string name in ChildDirs(userData))
            {
                if (teams ? IsTeamsProfileDir(name) : IsChromiumProfileDir(name))
                    AddChromiumCaches(l, Join(userData, name), baseDir, owner, procs);
            }
        }

        // Browser caches only. Chrome, Edge and Brave keep them in the profile folders under
        // "User Data"; Opera keeps its profile (and some of its cache) in the roaming folder too;
        // Firefox keeps cache2 beside its profiles in the local folder.
        public static List<CleanupRoot> BrowserRoots(CleanupEnvironment env)
        {
            List<CleanupRoot> l = new List<CleanupRoot>();
            string local = env.LocalAppData;
            string roam = env.AppData;
            if (!string.IsNullOrEmpty(local))
            {
                AddProfiles(l, Join(local, @"Google\Chrome\User Data"), local, "Chrome", new string[] { "chrome" }, false, false);
                AddProfiles(l, Join(local, @"Microsoft\Edge\User Data"), local, "Edge", new string[] { "msedge" }, false, false);
                AddProfiles(l, Join(local, @"BraveSoftware\Brave-Browser\User Data"), local, "Brave", new string[] { "brave" }, false, false);
                foreach (string v in new string[] { "Opera Stable", "Opera GX Stable" })
                    AddProfiles(l, Join(local, @"Opera Software\" + v), local, v.StartsWith("Opera GX") ? "Opera GX" : "Opera", new string[] { "opera" }, true, false);
                foreach (string profile in ChildDirs(Join(local, @"Mozilla\Firefox\Profiles")))
                    l.Add(Root(Join(local, @"Mozilla\Firefox\Profiles\" + profile + @"\cache2"), local, "Firefox", new string[] { "firefox" }));
            }
            if (!string.IsNullOrEmpty(roam))
            {
                foreach (string v in new string[] { "Opera Stable", "Opera GX Stable" })
                    AddProfiles(l, Join(roam, @"Opera Software\" + v), roam, v.StartsWith("Opera GX") ? "Opera GX" : "Opera", new string[] { "opera" }, true, false);
            }
            return l;
        }

        // DirectX, NVIDIA, Intel and AMD shader caches. Games rebuild them.
        public static List<CleanupRoot> ShaderRoots(CleanupEnvironment env)
        {
            List<CleanupRoot> l = new List<CleanupRoot>();
            string local = env.LocalAppData;
            if (!string.IsNullOrEmpty(local))
            {
                l.Add(Root(Join(local, "D3DSCache"), local, null, null));
                l.Add(Root(Join(local, @"NVIDIA\DXCache"), local, null, null));
                l.Add(Root(Join(local, @"NVIDIA\GLCache"), local, null, null));
                l.Add(Root(Join(local, @"Intel\ShaderCache"), local, null, null));
                l.Add(Root(Join(local, @"AMD\DxCache"), local, null, null));
            }
            if (!string.IsNullOrEmpty(env.ProgramData))
            {
                CleanupRoot nv = Root(Join(env.ProgramData, @"NVIDIA Corporation\NV_Cache"), env.ProgramData, null, null);
                nv.RequiresAdmin = true;
                l.Add(nv);
            }
            return l;
        }

        // Discord, Steam web cache and Teams (classic and new). Cache folders only:
        // Teams' databases, IndexedDB and Local Storage hold the sign-in and are not listed.
        public static List<CleanupRoot> AppRoots(CleanupEnvironment env)
        {
            List<CleanupRoot> l = new List<CleanupRoot>();
            string local = env.LocalAppData;
            string roam = env.AppData;
            if (!string.IsNullOrEmpty(roam))
            {
                string[] discord = new string[] { "Discord" };
                AddChromiumCaches(l, Join(roam, "discord"), roam, "Discord", discord);
                string[] teams = new string[] { "Teams", "ms-teams", "msteams" };
                string classic = Join(roam, @"Microsoft\Teams");
                AddChromiumCaches(l, classic, roam, "Teams", teams);
                l.Add(Root(Join(classic, "blob_storage"), roam, "Teams", teams));
                l.Add(Root(Join(classic, @"Application Cache\Cache"), roam, "Teams", teams));
            }
            if (!string.IsNullOrEmpty(local))
            {
                l.Add(Root(Join(local, @"Steam\htmlcache"), local, "Steam", new string[] { "steam", "steamwebhelper" }));
                string newTeams = Join(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView");
                AddProfiles(l, newTeams, local, "Teams", new string[] { "Teams", "ms-teams", "msteams" }, false, true);
            }
            return l;
        }

        // Windows Error Reporting queues and archives. Blue screen reports inside them stay.
        public static List<CleanupRoot> ErrorReportRoots(CleanupEnvironment env)
        {
            List<CleanupRoot> l = new List<CleanupRoot>();
            if (!string.IsNullOrEmpty(env.ProgramData))
            {
                foreach (string n in new string[] { "ReportArchive", "ReportQueue" })
                {
                    CleanupRoot r = Root(Join(env.ProgramData, @"Microsoft\Windows\WER\" + n), env.ProgramData, null, null);
                    r.RequiresAdmin = true;
                    r.KeepPrefixes = CleanupSafety.CrashReportPrefixes;
                    l.Add(r);
                }
            }
            if (!string.IsNullOrEmpty(env.LocalAppData))
            {
                CleanupRoot r = Root(Join(env.LocalAppData, @"Microsoft\Windows\WER"), env.LocalAppData, null, null);
                r.KeepPrefixes = CleanupSafety.CrashReportPrefixes;
                l.Add(r);
            }
            return l;
        }

        // Old Windows log files, by pattern, in the top of each folder. Panther is not listed.
        public static List<CleanupRoot> WindowsLogRoots(CleanupEnvironment env)
        {
            List<CleanupRoot> l = new List<CleanupRoot>();
            if (string.IsNullOrEmpty(env.WindowsDir)) return l;
            CleanupRoot cbs = Root(Join(env.WindowsDir, @"Logs\CBS"), env.WindowsDir, null, null);
            cbs.Patterns = new string[] { "*.log", "*.cab" };
            cbs.KeepNames = new string[] { "CBS.log" };   // the current log, which the repair messages point to
            CleanupRoot dism = Root(Join(env.WindowsDir, @"Logs\DISM"), env.WindowsDir, null, null);
            dism.Patterns = new string[] { "*.log" };
            CleanupRoot wu = Root(Join(env.WindowsDir, @"Logs\WindowsUpdate"), env.WindowsDir, null, null);
            wu.Patterns = new string[] { "*.etl" };
            foreach (CleanupRoot r in new CleanupRoot[] { cbs, dism, wu })
            {
                r.RequiresAdmin = true;
                l.Add(r);
            }
            return l;
        }
    }
}
