using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The real system behind the injectable pieces of CleanupEnvironment. Tests replace all
    // three, so none of this runs in them.
    public static class CleanupProbes
    {
        // Is a process with this name (no ".exe") running right now?
        public static bool ProcessRunning(string name)
        {
            Process[] list = Process.GetProcessesByName(name);
            try
            {
                return list.Length > 0;
            }
            finally
            {
                foreach (Process p in list) p.Dispose();
            }
        }

        // Runs one command without a window and returns its exit code; -1 when it could not
        // start or did not finish in 30 seconds.
        public static int RunCommand(CleanupCommand c)
        {
            if (c == null || string.IsNullOrEmpty(c.FileName)) return -1;
            ProcessStartInfo psi = new ProcessStartInfo(c.FileName, c.Arguments ?? "");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate { };
                p.ErrorDataReceived += delegate { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                    return -1;
                }
                return p.ExitCode;
            }
        }

        // Every event log channel that has records, with its kind. Read-only.
        public static List<EventChannelInfo> ReadEventChannels(CancellationToken ct)
        {
            List<EventChannelInfo> l = new List<EventChannelInfo>();
            using (EventLogSession session = new EventLogSession())
            {
                foreach (string name in session.GetLogNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        EventLogInformation info = session.GetLogInformation(name, PathType.LogName);
                        long records = info.RecordCount.HasValue ? info.RecordCount.Value : 0;
                        if (records <= 0) continue;
                        EventLogType type;
                        using (EventLogConfiguration cfg = new EventLogConfiguration(name, session))
                        {
                            type = cfg.LogType;
                        }
                        EventChannelInfo c = new EventChannelInfo();
                        c.Name = name;
                        c.Records = records;
                        c.Kind = Kind(type);
                        l.Add(c);
                    }
                    catch (Exception)
                    {
                        // a channel that cannot be read is simply not offered
                    }
                }
            }
            return l;
        }

        internal static EventChannelKind Kind(EventLogType t)
        {
            switch (t)
            {
                case EventLogType.Operational: return EventChannelKind.Operational;
                case EventLogType.Analytical: return EventChannelKind.Analytic;
                case EventLogType.Debug: return EventChannelKind.Debug;
                default: return EventChannelKind.Administrative;
            }
        }
    }
}
