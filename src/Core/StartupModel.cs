using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Forja.Core
{
    public enum RegHive
    {
        CurrentUser,
        LocalMachine
    }

    // The slice of the registry the startup manager needs. The real one lives in Platform;
    // tests use an in-memory fake so nothing touches this machine.
    public interface IRegistryAccess
    {
        // Empty when the key does not exist.
        string[] GetValueNames(RegHive hive, string subKey);
        // Raw text, environment variables not expanded; null when missing or not text.
        string GetString(RegHive hive, string subKey, string name);
        // Null when missing or not binary.
        byte[] GetBinary(RegHive hive, string subKey, string name);
        void SetBinary(RegHive hive, string subKey, string name, byte[] data);
        void DeleteValue(RegHive hive, string subKey, string name);
    }

    public enum StartupSource
    {
        UserRun,
        MachineRun,
        MachineRun32,
        UserFolder,
        CommonFolder
    }

    public class StartupItem
    {
        public string Id;
        public string Name;          // registry value name or file name (the StartupApproved key)
        public string DisplayName;   // what the list shows
        public string Command;
        public string ExePath;
        public string Publisher;
        public StartupSource Source;
        public bool Enabled;
        public byte[] Approved;      // current StartupApproved bytes, null when there is no entry
        public bool RequiresAdmin;
        public bool IsVendor;        // Microsoft or a hardware driver: disabling may remove features
    }

    public class LogonTask
    {
        public string Name;
        public string Command;
        public bool Enabled;
        public bool IsSystem;
    }

    public class StartupChangeResult
    {
        public readonly bool Ok;
        public readonly string Message;

        public StartupChangeResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }
    }

    public class StartupUndo
    {
        public RegHive Hive;
        public string SubKey;
        public string Name;
        public byte[] Previous; // null: there was no StartupApproved value before
        public byte[] Written;  // what the change wrote; null in old (v1) entries, which cannot be undone safely
    }

    public interface IStartupService
    {
        List<StartupItem> List();
        List<LogonTask> ListLogonTasks();
        StartupChangeResult SetEnabled(StartupItem item, bool enable);
        StartupChangeResult Undo(ChangeEntry entry);
    }

    public static class StartupModel
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        public const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static RegHive HiveOf(StartupSource s)
        {
            return (s == StartupSource.UserRun || s == StartupSource.UserFolder) ? RegHive.CurrentUser : RegHive.LocalMachine;
        }

        public static bool RequiresAdmin(StartupSource s)
        {
            return HiveOf(s) == RegHive.LocalMachine;
        }

        public static string RunSubKey(StartupSource s)
        {
            if (s == StartupSource.UserRun || s == StartupSource.MachineRun) return RunKey;
            if (s == StartupSource.MachineRun32) return RunKey32;
            return null;
        }

        public static string ApprovedSubKey(StartupSource s)
        {
            switch (s)
            {
                case StartupSource.UserRun:
                case StartupSource.MachineRun:
                    return ApprovedBase + "Run";
                case StartupSource.MachineRun32:
                    return ApprovedBase + "Run32";
                default:
                    return ApprovedBase + "StartupFolder";
            }
        }

        public static string DescribeSource(StartupSource s)
        {
            switch (s)
            {
                case StartupSource.UserRun: return "Registro do usuário";
                case StartupSource.MachineRun: return "Registro do computador";
                case StartupSource.MachineRun32: return "Registro do computador (32 bits)";
                case StartupSource.UserFolder: return "Pasta Inicializar do usuário";
                default: return "Pasta Inicializar de todos os usuários";
            }
        }

        // No StartupApproved value means enabled. The Windows rule: an odd first byte (03)
        // is disabled; 02 and 06 are enabled.
        public static bool IsEnabled(byte[] approved)
        {
            if (approved == null || approved.Length == 0) return true;
            return (approved[0] & 1) == 0;
        }

        // Disable: 03 + FILETIME of now in bytes 4..11. Enable: 02 and zeros, which is what
        // the Task Manager writes.
        public static byte[] BuildApproved(bool enable, long fileTime)
        {
            byte[] b = new byte[12];
            if (enable)
            {
                b[0] = 0x02;
                return b;
            }
            b[0] = 0x03;
            byte[] t = BitConverter.GetBytes(fileTime);
            Array.Copy(t, 0, b, 4, 8);
            return b;
        }

        // The program part of a Run command: quoted path, or everything up to the first ".exe".
        public static string ExtractExePath(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string c = Environment.ExpandEnvironmentVariables(command).Trim();
            if (c.Length == 0) return null;
            if (c[0] == '"')
            {
                int close = c.IndexOf('"', 1);
                string q = close > 1 ? c.Substring(1, close - 1) : c.Substring(1);
                return q.Trim().Length == 0 ? null : q.Trim();
            }
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0) return c.Substring(0, exe + 4);
            int sp = c.IndexOf(' ');
            return sp > 0 ? c.Substring(0, sp) : c;
        }

        public static bool IsVendorPublisher(string publisher)
        {
            if (string.IsNullOrEmpty(publisher)) return false;
            string p = publisher.ToLowerInvariant();
            string[] marks = { "microsoft", "nvidia", "intel", "advanced micro devices", "realtek" };
            foreach (string m in marks)
            {
                if (p.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            }
            return Regex.IsMatch(p, @"\bamd\b");
        }

        // ---- undo data: "v2;HKCU|HKLM;<key>;<name>;<hex of previous bytes | ->;<hex of written bytes>" ----
        // v1 (no written bytes) still decodes, but Undo refuses it: without the bytes this change wrote
        // there is no way to tell whether the item was changed again afterwards.

        public static string EncodeUndo(RegHive hive, string subKey, string name, byte[] previous, byte[] written)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("v2;");
            sb.Append(hive == RegHive.CurrentUser ? "HKCU" : "HKLM");
            sb.Append(';').Append(Uri.EscapeDataString(subKey ?? ""));
            sb.Append(';').Append(Uri.EscapeDataString(name ?? ""));
            sb.Append(';');
            if (previous == null) sb.Append('-');
            else
            {
                foreach (byte b in previous) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            sb.Append(';');
            if (written == null) sb.Append('-');
            else
            {
                foreach (byte b in written) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static bool ParseHex(string text, out byte[] bytes)
        {
            bytes = null;
            if (text.Length == 0 || text.Length % 2 != 0) return false;
            byte[] b = new byte[text.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                if (!byte.TryParse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return false;
            }
            bytes = b;
            return true;
        }

        // Desfazer writes only to the StartupApproved Run / Run32 / StartupFolder keys (HKCU or
        // HKLM), the only places this app changes. Anything else read from historico.json (a
        // file a user-level process could edit) is refused.
        public static bool IsAllowedUndoTarget(RegHive hive, string subKey)
        {
            if (hive != RegHive.CurrentUser && hive != RegHive.LocalMachine) return false;
            if (string.IsNullOrEmpty(subKey)) return false;
            string[] allowed = { ApprovedBase + "Run", ApprovedBase + "Run32", ApprovedBase + "StartupFolder" };
            foreach (string a in allowed)
            {
                if (string.Equals(subKey, a, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        // Which history entries may offer "Desfazer": the newest pending (not undone) entry of each
        // startup item. An older entry of an item that changed again would undo the wrong thing.
        // The list is newest first, as ChangeLog.List returns it.
        public static Dictionary<string, bool> UndoableEntryIds(List<ChangeEntry> newestFirst)
        {
            Dictionary<string, bool> ids = new Dictionary<string, bool>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            if (newestFirst == null) return ids;
            foreach (ChangeEntry e in newestFirst)
            {
                if (e.Kind != ChangeKind.Inicializacao || !e.CanUndo) continue;
                StartupUndo u = DecodeUndo(e.UndoData);
                if (u == null || !IsAllowedUndoTarget(u.Hive, u.SubKey)) continue;
                string key = (u.Hive + "|" + u.SubKey + "|" + u.Name).ToLowerInvariant();
                if (seen.ContainsKey(key)) continue;
                seen[key] = true;
                ids[e.Id] = true;
            }
            return ids;
        }

        public static StartupUndo DecodeUndo(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            string[] p = data.Split(';');
            bool v2 = p.Length == 6 && p[0] == "v2";
            if (!v2 && !(p.Length == 5 && p[0] == "v1")) return null;
            StartupUndo u = new StartupUndo();
            if (p[1] == "HKCU") u.Hive = RegHive.CurrentUser;
            else if (p[1] == "HKLM") u.Hive = RegHive.LocalMachine;
            else return null;
            try
            {
                u.SubKey = Uri.UnescapeDataString(p[2]);
                u.Name = Uri.UnescapeDataString(p[3]);
            }
            catch (UriFormatException)
            {
                return null;
            }
            if (u.SubKey.Length == 0 || u.Name.Length == 0) return null;
            if (p[4] == "-")
            {
                u.Previous = null;
            }
            else
            {
                byte[] b;
                if (!ParseHex(p[4], out b)) return null;
                u.Previous = b;
            }
            if (v2 && p[5] != "-")
            {
                byte[] w;
                if (!ParseHex(p[5], out w)) return null;
                u.Written = w;
            }
            return u;
        }

        // ---- scheduled tasks (read only) ----

        // Parses the output of "schtasks /Query /XML ONE": a <Tasks> root with one <Task> per
        // task, each preceded by a comment with its path. Keeps tasks with a logon trigger.
        public static List<LogonTask> ParseTasksXml(string xml)
        {
            List<LogonTask> result = new List<LogonTask>();
            if (string.IsNullOrEmpty(xml)) return result;
            XmlDocument doc = new XmlDocument();
            try
            {
                doc.XmlResolver = null;
                doc.LoadXml(xml);
            }
            catch (XmlException)
            {
                return ParseTasksLoosely(xml);
            }
            if (doc.DocumentElement == null) return result;
            string comment = null;
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
            {
                if (n.NodeType == XmlNodeType.Comment)
                {
                    comment = n.Value == null ? null : n.Value.Trim();
                }
                else if (n.NodeType == XmlNodeType.Element && n.LocalName == "Task")
                {
                    LogonTask t = ReadTask(n, comment);
                    if (t != null) result.Add(t);
                    comment = null;
                }
            }
            return result;
        }

        // The whole output is not valid XML when one task has odd characters: parse task by task.
        private static List<LogonTask> ParseTasksLoosely(string text)
        {
            List<LogonTask> result = new List<LogonTask>();
            MatchCollection ms = Regex.Matches(text, @"(?:<!--\s*(?<name>.*?)\s*-->\s*)?(?<task><Task\b.*?</Task>)", RegexOptions.Singleline);
            foreach (Match m in ms)
            {
                try
                {
                    XmlDocument d = new XmlDocument();
                    d.XmlResolver = null;
                    d.LoadXml(m.Groups["task"].Value);
                    LogonTask t = ReadTask(d.DocumentElement, m.Groups["name"].Success ? m.Groups["name"].Value : null);
                    if (t != null) result.Add(t);
                }
                catch (XmlException)
                {
                }
            }
            return result;
        }

        private static LogonTask ReadTask(XmlNode task, string commentName)
        {
            XmlNode logon = FindFirst(task, "LogonTrigger");
            if (logon == null) return null;
            string name = commentName;
            if (string.IsNullOrEmpty(name))
            {
                XmlNode uri = FindFirst(task, "URI");
                name = uri != null ? uri.InnerText.Trim() : "(sem nome)";
            }
            bool taskEnabled = TextOf(FindFirst(FindFirst(task, "Settings"), "Enabled")) != "false";
            bool triggerEnabled = TextOf(FindFirst(logon, "Enabled")) != "false";
            XmlNode cmd = FindFirst(FindFirst(task, "Exec"), "Command");
            LogonTask t = new LogonTask();
            t.Name = name;
            t.Command = cmd != null ? cmd.InnerText.Trim() : "";
            t.Enabled = taskEnabled && triggerEnabled;
            t.IsSystem = name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
            return t;
        }

        private static string TextOf(XmlNode n)
        {
            return n == null ? null : n.InnerText.Trim().ToLowerInvariant();
        }

        private static XmlNode FindFirst(XmlNode root, string localName)
        {
            if (root == null) return null;
            foreach (XmlNode c in root.ChildNodes)
            {
                if (c.NodeType != XmlNodeType.Element) continue;
                if (c.LocalName == localName) return c;
                XmlNode deeper = FindFirst(c, localName);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
