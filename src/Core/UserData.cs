using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Forja.Core
{
    // The one place that knows where the app keeps its files for this user and this machine:
    //   Roaming = %APPDATA%\SenseMonitor      (config.json, diario.log, erro.log, historico.json ...)
    //   Local   = %LOCALAPPDATA%\SenseMonitor (temperaturas)
    //   Common  = %ProgramData%\SenseMonitor  (estado, atualizacao)
    // A test host sets Override to a throw-away folder first thing: the three then live under it, so no test can touch the
    // real journal, config or state. A test host that still resolves a REAL folder is recorded in Violations (the runner
    // fails that test by name) instead of silently writing to the user's files.
    public static class UserData
    {
        private static readonly object sync = new object();
        private static readonly List<string> violations = new List<string>();
        private static string over;
        private static bool? testHost;

        // Test seam (the tests share the assembly). null = the real folders.
        internal static string Override
        {
            get { lock (sync) { return over; } }
            set { lock (sync) { over = string.IsNullOrEmpty(value) ? null : value; } }
        }

        public static string Roaming
        {
            get
            {
                string o = Override;
                if (o != null) return o;
                Real("Roaming");
                return RealRoaming;
            }
        }

        public static string Local
        {
            get
            {
                string o = Override;
                if (o != null) return Path.Combine(o, "local");
                Real("Local");
                return RealLocal;
            }
        }

        public static string Common
        {
            get
            {
                string o = Override;
                if (o != null) return Path.Combine(o, "common");
                Real("Common");
                return RealCommon;
            }
        }

        // The real folders, never redirected and never recorded (for the tests that check the default shape).
        public static string RealRoaming
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SenseMonitor"); }
        }

        public static string RealLocal
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SenseMonitor"); }
        }

        public static string RealCommon
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SenseMonitor"); }
        }

        // True when this process is the unit-test host (its exe is ForjaTests).
        public static bool IsTestHost
        {
            get
            {
                lock (sync)
                {
                    if (!testHost.HasValue)
                    {
                        try
                        {
                            using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                            {
                                testHost = p.ProcessName.StartsWith("ForjaTests", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        catch (Exception)
                        {
                            testHost = false;
                        }
                    }
                    return testHost.Value;
                }
            }
        }

        // What a test host that resolved a real folder looked like (which folder, from which test). Empty in a healthy run.
        internal static List<string> Violations
        {
            get { lock (sync) { return new List<string>(violations); } }
        }

        internal static int ViolationCount
        {
            get { lock (sync) { return violations.Count; } }
        }

        // Drops what the guard's own test provoked on purpose.
        internal static void ForgetViolationsFrom(int index)
        {
            lock (sync)
            {
                if (index >= 0 && index < violations.Count) violations.RemoveRange(index, violations.Count - index);
            }
        }

        private static void Real(string which)
        {
            if (!IsTestHost) return;
            StringBuilder sb = new StringBuilder("pasta real (" + which + ") usada pelo teste:");
            try
            {
                string[] frames = Environment.StackTrace.Split('\n');
                int n = 0;
                foreach (string f in frames)
                {
                    string t = f.Trim();
                    if (t.IndexOf("Forja.Tests.", StringComparison.Ordinal) < 0 || t.IndexOf("TestRunner", StringComparison.Ordinal) >= 0) continue;
                    sb.Append(' ').Append(t.Length > 90 ? t.Substring(0, 90) : t);
                    if (++n >= 2) break;
                }
            }
            catch (Exception)
            {
            }
            lock (sync) { violations.Add(sb.ToString()); }
        }
    }
}
