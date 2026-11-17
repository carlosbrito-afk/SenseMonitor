using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // File name rules shared by every folder target.
    public static class CleanupNames
    {
        // '*' (any run) and '?' (one character), case-insensitive, and exact: the Windows
        // directory search quirk where "*.log" also finds "x.logx" does not apply here.
        public static bool Matches(string name, string pattern)
        {
            if (name == null || pattern == null) return false;
            int n = 0, p = 0, star = -1, mark = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])) && pattern[p] != '*')
                {
                    n++;
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = n;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    n = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        // No filter (null) means everything matches. An empty list is a mistake and matches
        // nothing, so it can never turn a pattern root into "delete everything on top".
        public static bool MatchesAny(string name, string[] patterns)
        {
            if (patterns == null) return true;
            foreach (string p in patterns)
            {
                if (Matches(name, p)) return true;
            }
            return false;
        }

        // Names that stay even inside a target: an exact name (the log in use right now) or
        // a prefix (blue screen reports).
        public static bool IsKept(string name, string[] keepNames, string[] keepPrefixes)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (keepNames != null)
            {
                foreach (string k in keepNames)
                {
                    if (string.Equals(name, k, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            if (keepPrefixes != null)
            {
                foreach (string k in keepPrefixes)
                {
                    if (name.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }

    // A command the cleanup would run. Built in Core so the text can be tested; the runner
    // lives in Platform and is injected, so tests never start a process.
    public class CleanupCommand
    {
        public string FileName;
        public string Arguments;
    }

    public static class CleanupCommands
    {
        private static string Tool(string system32, string exe)
        {
            if (string.IsNullOrEmpty(system32)) return null;
            return system32.TrimEnd('\\', '/') + "\\" + exe;
        }

        // ipconfig /flushdns, by its full System32 path (no PATH lookup).
        public static CleanupCommand FlushDns(string system32)
        {
            string f = Tool(system32, "ipconfig.exe");
            if (f == null) return null;
            CleanupCommand c = new CleanupCommand();
            c.FileName = f;
            c.Arguments = "/flushdns";
            return c;
        }

        // wevtutil cl "<channel>". Null for a name that is not allowed, is excluded or could break the
        // command line, so a bad channel can never reach the runner.
        public static CleanupCommand ClearEventLog(string system32, string channel)
        {
            string f = Tool(system32, "wevtutil.exe");
            if (f == null) return null;
            if (!EventLogFilter.IsSafeName(channel) || !EventLogFilter.IsAllowed(channel) || EventLogFilter.IsExcluded(channel)) return null;
            CleanupCommand c = new CleanupCommand();
            c.FileName = f;
            c.Arguments = "cl \"" + channel + "\"";
            return c;
        }
    }

    public enum EventChannelKind
    {
        Administrative,
        Operational,
        Analytic,
        Debug
    }

    public class EventChannelInfo
    {
        public string Name;
        public EventChannelKind Kind;
        public long Records;
    }

    // Which event log channels the cleanup may clear. The Health page and the blue screen
    // investigation read the System log (Kernel-Power 41, 1001, 6008) and WHEA, so those and
    // the logs around them are never cleared.
    public static class EventLogFilter
    {
        private static readonly string[] ExactNames = { "System", "Application", "Security", "Setup" };

        // The only channels the cleanup may clear: application, network status, store, telemetry
        // and shell noise. The other Windows families (kernel, hardware, storage, security, audit,
        // crash) are not here, and a channel Windows adds later is kept until someone adds it on
        // purpose. CleanupEventGoldenTests pins the result on real channel names.
        //
        // Single channels, matched by their full name.
        private static readonly string[] AllowedChannels =
        {
            "Microsoft-Windows-PowerShell/Operational",           // script history; only this channel of PowerShell
            "Microsoft-Windows-Known Folders API Service"
        };

        // Families: a name matches an entry when it is "<entry>/<channel>".
        private static readonly string[] AllowedFamilies =
        {
            "Microsoft-Windows-WLAN-AutoConfig",                  // Wi-Fi connect and disconnect noise
            "Microsoft-Windows-NetworkProfile",
            "Microsoft-Windows-NCSI",
            "Microsoft-Windows-NlaSvc",
            "Microsoft-Windows-Wcmsvc",
            "Microsoft-Windows-WinINet-Config",
            "Microsoft-Windows-Bits-Client",                      // background transfer jobs
            "Microsoft-Windows-Store",                            // Microsoft Store and app packages
            "Microsoft-Windows-AppXDeployment",
            "Microsoft-Windows-AppXDeployment-Server",
            "Microsoft-Windows-AppXDeploymentServer",
            "Microsoft-Windows-AppxPackaging",
            "Microsoft-Windows-AppReadiness",
            "Microsoft-Windows-StateRepository",
            "Microsoft-Windows-Application-Experience",           // compatibility assistant and program telemetry
            "Microsoft-Windows-UniversalTelemetryClient",
            "Microsoft-Windows-Diagnosis-DPS",                    // troubleshooters
            "Microsoft-Windows-Diagnosis-PCW",
            "Microsoft-Windows-Diagnosis-Scheduled",
            "Microsoft-Windows-Diagnosis-Scripted",
            "Microsoft-Windows-WindowsSystemAssessmentTool",
            "Microsoft-Windows-PushNotification-Platform",
            "Microsoft-Windows-BackgroundTaskInfrastructure",
            "Microsoft-Windows-Shell-Core",
            "Microsoft-Windows-ShellCommon-StartLayoutPopulation",
            "Microsoft-Windows-TWinUI",
            "Microsoft-Windows-CloudStore",
            "Microsoft-Windows-CloudRestoreLauncher",
            "Microsoft-Windows-UserSettingsBackup-Orchestrator",
            "Microsoft-Windows-LanguagePackSetup",
            "Microsoft-Windows-MUI",
            "Microsoft-Windows-TZSync",
            "Microsoft-Windows-Perflib",
            "Microsoft-Windows-MF-FrameServer",                   // camera pipeline
            "Microsoft-Windows-Audio"
        };

        public static bool IsAllowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string ch in AllowedChannels)
            {
                if (string.Equals(name, ch, StringComparison.OrdinalIgnoreCase)) return true;
            }
            foreach (string fam in AllowedFamilies)
            {
                if (name.Length > fam.Length + 1 && name[fam.Length] == '/'
                    && name.StartsWith(fam, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Case-insensitive parts of a channel name that are never cleared, a second guard on top of
        // the allowlist. Kernel power, WHEA and boot performance are the ones the Health page and
        // the blue screen investigation read; the rest are crash, hardware, storage, audit and
        // security-tool channels.
        private static readonly string[] NameParts =
        {
            "Kernel-Power", "WHEA", "Diagnostics-Performance",
            "-Kernel-",
            "Windows Defender", "Sysmon", "Firewall", "BitLocker", "CodeIntegrity", "TPM", "Security-",
            "Storage", "Ntfs", "Disk",
            "Crashdump", "Resource-Exhaustion", "Fault-Tolerant-Heap", "PCI", "USB", "PnP", "Partition",
            "Volume", "Storsvc", "DriverFrameworks", "Dxgkrnl", "AppLocker"
        };

        public static bool IsExcluded(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (string e in ExactNames)
            {
                if (string.Equals(name, e, StringComparison.OrdinalIgnoreCase)) return true;
            }
            // "WER" / "Wer" with their case: "Power" and "PowerShell" must not match.
            if (name.IndexOf("WER", StringComparison.Ordinal) >= 0 || name.IndexOf("Wer", StringComparison.Ordinal) >= 0) return true;
            foreach (string part in NameParts)
            {
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // Letters, digits and ordinary punctuation only: nothing that could close the quote
        // or start another command.
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 250) return false;
            if (!char.IsLetterOrDigit(name[0])) return false;   // a leading "-" or "/" could be read as an option
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) continue;
                if (c == ' ' || c == '-' || c == '_' || c == '.' || c == '/' || c == '(' || c == ')' || c == '+' || c == ',') continue;
                return false;
            }
            return true;
        }

        // Operational and analytic channels that have records, minus the exclusions.
        // Sorted, no duplicates.
        public static List<string> Select(IList<EventChannelInfo> channels)
        {
            List<string> result = new List<string>();
            if (channels == null) return result;
            foreach (EventChannelInfo c in channels)
            {
                if (c == null || c.Records <= 0) continue;
                if (c.Kind != EventChannelKind.Operational && c.Kind != EventChannelKind.Analytic) continue;
                if (!IsSafeName(c.Name) || !IsAllowed(c.Name) || IsExcluded(c.Name)) continue;
                bool dup = false;
                foreach (string r in result)
                {
                    if (string.Equals(r, c.Name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (!dup) result.Add(c.Name);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
