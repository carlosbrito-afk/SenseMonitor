using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Forja.Core;

namespace Forja.Platform
{
    // The same companion rule as the first stage, for code that re-spawns the RUNNING image elevated on its own
    // (the fan sentinel's launcher). From the installed copy: always fine. From anywhere else (a "just run it
    // here" start, a tool run from Downloads): refused while a *.config, *.dll, *.manifest, *.local or a culture
    // folder is next to the exe, except this exe's own .config when its bytes equal the config embedded in the
    // exe itself (that is what the build puts in bin\; it equals what the installer writes).
    public static class ElevationGuard
    {
        public static bool RespawnAllowed(string exePath)
        {
            try
            {
                if (UpdatePolicy.IsInstalledCopy(exePath, InstallPaths.InstalledExe)) return true;
                string dir = Path.GetDirectoryName(exePath);
                List<string> names = new List<string>();
                foreach (string e in Directory.GetFileSystemEntries(dir)) names.Add(Path.GetFileName(e));
                string ownName = Path.GetFileName(exePath) + ".config";
                return FirstStage.RespawnAllowed(false, names, ownName, OwnConfigMatchesEmbedded(exePath + ".config"));
            }
            catch (Exception)
            {
                return false; // cannot tell: do not elevate
            }
        }

        private static bool OwnConfigMatchesEmbedded(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return false;
                Assembly a = Assembly.GetEntryAssembly();
                if (a == null) return false;
                byte[] embedded;
                using (Stream s = a.GetManifestResourceStream("SenseMonitor.app.config"))
                {
                    if (s == null) return false;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        embedded = ms.ToArray();
                    }
                }
                byte[] have = File.ReadAllBytes(configPath);
                return have.Length == embedded.Length && UpdateManifestFile.Sha256Hex(have) == UpdateManifestFile.Sha256Hex(embedded);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
