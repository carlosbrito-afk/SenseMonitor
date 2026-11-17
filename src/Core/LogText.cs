using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    // What goes into erro.log: exception type + message (and inner exceptions), never personal
    // data. The user-profile path becomes %USERPROFILE% and the user name %USERNAME%.
    public static class LogText
    {
        public static string Scrub(string text, string userProfile, string userName)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text;
            if (!string.IsNullOrEmpty(userProfile) && userProfile.Length > 3)
            {
                s = Regex.Replace(s, Regex.Escape(userProfile.TrimEnd('\\')), "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
            if (!string.IsNullOrEmpty(userName) && userName.Length > 1)
            {
                // Only as a path segment or after DOMAIN\ (never inside ordinary words).
                s = Regex.Replace(s, @"(?<=[\\/])" + Regex.Escape(userName) + @"(?=[\\/]|\b)", "%USERNAME%", RegexOptions.IgnoreCase);
            }
            return s;
        }

        public static string Describe(Exception ex, string userProfile, string userName)
        {
            if (ex == null) return "(sem detalhes)";
            StringBuilder sb = new StringBuilder();
            int depth = 0;
            for (Exception e = ex; e != null && depth < 5; e = e.InnerException, depth++)
            {
                if (depth > 0) sb.Append(" ---> ");
                sb.Append(e.GetType().FullName).Append(": ").Append(Scrub(e.Message, userProfile, userName));
            }
            return sb.ToString();
        }

        // Current user's values (for the app's log).
        public static string DescribeForCurrentUser(Exception ex)
        {
            return Describe(ex, SafeProfile(), SafeUser());
        }

        public static string ScrubForCurrentUser(string text)
        {
            return ScrubMachineName(Scrub(text, SafeProfile(), SafeUser()), SafeMachine());
        }

        // The computer name as a whole word (it identifies the person as much as the user name does).
        public static string ScrubMachineName(string text, string machine)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(machine) || machine.Length < 3) return text ?? "";
            return Regex.Replace(text, @"(?<![A-Za-z0-9_-])" + Regex.Escape(machine) + @"(?![A-Za-z0-9_-])", "%COMPUTERNAME%", RegexOptions.IgnoreCase);
        }

        private static string SafeMachine()
        {
            try { return Environment.MachineName; }
            catch (Exception) { return null; }
        }

        private static string SafeProfile()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch (Exception) { return null; }
        }

        private static string SafeUser()
        {
            try { return Environment.UserName; }
            catch (Exception) { return null; }
        }
    }

    // Pacing of WMI reconnection: after 5 consecutive failed calls, reconnect at most once every
    // 30 s (a missing class at logon recovers later; a broken connection is not hammered).
    public sealed class ReconnectPolicy
    {
        public const int FailuresBeforeReconnect = 5;
        public const int MinIntervalMs = 30000;

        private int failures;
        private bool tried;
        private int lastTry;

        public void Success()
        {
            failures = 0;
        }

        // Records a failed call; true = reconnect now.
        public bool Failure(int nowMs)
        {
            if (failures < int.MaxValue) failures++;
            if (failures < FailuresBeforeReconnect) return false;
            if (tried && unchecked(nowMs - lastTry) < MinIntervalMs) return false;
            tried = true;
            lastTry = nowMs;
            failures = 0;
            return true;
        }
    }
}
