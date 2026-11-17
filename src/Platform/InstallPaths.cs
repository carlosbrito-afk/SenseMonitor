using System;
using System.IO;

namespace Forja.Platform
{
    // Where the elevated app lives and where it may write files that elevated helpers read.
    // %ProgramFiles%\SenseMonitor inherits the Program Files ACL (only administrators write), so
    // the autostart task never points at a user-writable folder and temporary files handed to
    // elevated tools (task XML, DISM output) cannot be swapped by a non-admin process.
    public static class InstallPaths
    {
        public const string AppFolder = "SenseMonitor";
        public const string ExeName = "SenseMonitor.exe";

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppFolder); }
        }

        public static string InstalledExe
        {
            get { return Path.Combine(InstallDir, ExeName); }
        }

        public static string TempDirPath
        {
            get { return Path.Combine(InstallDir, "tmp"); }
        }

        // The protected temp folder, created on demand; null when the app is not installed
        // under Program Files or the folder is a reparse point (junction/symlink).
        public static string SecureTempDir()
        {
            try
            {
                string install = InstallDir;
                if (!Directory.Exists(install) || IsReparse(install)) return null;
                string tmp = TempDirPath;
                if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp);
                if (IsReparse(tmp)) return null;
                return tmp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsReparse(string dir)
        {
            return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
        }

        // Windows tools by full path (never the app folder or the current directory).
        public static string System32(string exe)
        {
            return Path.Combine(Environment.SystemDirectory, exe);
        }

        // nvidia-smi: %SystemRoot%\System32 first (where the driver installs it), else the
        // NVIDIA folder under Program Files; null when neither exists.
        public static string ResolveNvidiaSmi(string systemRoot, string programFiles, Func<string, bool> exists)
        {
            if (exists == null) return null;
            if (!string.IsNullOrEmpty(systemRoot))
            {
                string a = Path.Combine(systemRoot, "System32", "nvidia-smi.exe");
                if (exists(a)) return a;
            }
            if (!string.IsNullOrEmpty(programFiles))
            {
                string b = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
                if (exists(b)) return b;
            }
            return null;
        }

        public static string NvidiaSmiPath()
        {
            try
            {
                return ResolveNvidiaSmi(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
