using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The fan sentinel: a second, tiny process (SenseMonitor.exe --sentinela) that hands the fans
    // back to the firmware when no SenseMonitor process is left to do it (killed, crashed) or the app
    // froze with the fans fixed. Decisions live in SentinelBrain / SentinelLoop (Core, unit-tested);
    // this class gathers the facts and starts the process.
    //
    // Starting it. The app (and the tools that write to the fans) call EnsureRunning before the first
    // Custom/Máximo write. First choice: the on-demand scheduled task SenseMonitor-Sentinela
    // (schtasks /Run), registered by --registrar-inicio next to the autostart task. The task runs
    // outside the app task's job object, so ending the app's task does not end the sentinel. Only when
    // that task is missing or does not come up (a development run) the launcher is used: the app starts
    // "--sentinela-iniciar", which starts the sentinel and exits, leaving it an orphan outside the app's
    // process tree (but still in its job: that is why the task is the real answer). The journal says
    // which path was used.
    //
    // What it watches: ANY SenseMonitor instance, through the single-instance mutex that the app and
    // every fan-writing tool hold for as long as they run, plus the shared heartbeat. It never writes
    // Auto while an instance is alive unless the heartbeat is known and older than 30 s. So a restarted
    // app is covered by the same sentinel and the two never fight.
    //
    // One at a time: a named mutex. A second sentinel waits up to 15 s (the first one is finishing)
    // and then leaves.
    public static class Sentinel
    {
        public const string MutexName = @"Global\SenseMonitor.Sentinel";
        public const string SwitchRun = "--sentinela";
        public const string SwitchLaunch = "--sentinela-iniciar";
        private const int AutoTimeoutMs = 4000;
        private const int MutexWaitMs = 15000;

        // ---- process entry (Program.Main, before any UI or handler is set up) -------------------

        // --sentinela (the guard) or --sentinela-iniciar (the launcher). Returns false for any
        // other command line, which is then handled as usual.
        public static bool TryDispatch(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null) return false;
            for (int i = 1; i < args.Length; i++)
            {
                bool run = string.Equals(args[i], SwitchRun, StringComparison.OrdinalIgnoreCase);
                bool launch = string.Equals(args[i], SwitchLaunch, StringComparison.OrdinalIgnoreCase);
                if (!run && !launch) continue;
                try { exitCode = run ? Run() : RunLauncher(); }
                catch (Exception) { exitCode = 1; }
                return true;
            }
            return false;
        }

        // ---- app side ----------------------------------------------------------------------

        // Is a sentinel process alive (or waiting for the mutex)?
        public static bool IsRunning()
        {
            return Exists(MutexName);
        }

        // Is any SenseMonitor instance (app or tool) alive: the single-instance mutex exists. The
        // handle is closed at once, so the check itself never keeps the mutex alive.
        public static bool IsAppAlive()
        {
            return Exists(SingleInstance.MutexName);
        }

        internal static bool Exists(string mutexName)
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(mutexName, MutexRights.Synchronize))
                {
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // exists, owned by a differently elevated process
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Called before a Custom/Máximo write. true = a sentinel is running (confirmed). false = it
        // could not be started or confirmed: the write is refused (fail closed).
        public static bool EnsureRunning()
        {
            if (IsRunning()) return true;
            IJournal journal = Safety.Journal;
            // The task must point at the installed exe before it is run (schtasks /Run accepts a task whose
            // exe is gone and the failure would only show later, as the launcher fallback).
            bool refreshed;
            string taskProblem = Autostart.EnsureSentinelTaskCurrent(out refreshed);
            if (refreshed) journal.Write(JournalKind.Sentinel, "a tarefa " + Autostart.SentinelTaskName + " não apontava para o exe instalado; registrada de novo");
            else if (taskProblem != null) journal.Write(JournalKind.Sentinel, "a tarefa " + Autostart.SentinelTaskName + " não pôde ser verificada ou atualizada (" + taskProblem + ")");
            bool task = taskProblem == null && Autostart.RunSentinelTask();
            if (task)
            {
                if (WaitRunning(3000))
                {
                    journal.Write(JournalKind.Sentinel, "guardião iniciado pela tarefa agendada " + Autostart.SentinelTaskName);
                    return true;
                }
                journal.Write(JournalKind.Sentinel, "a tarefa " + Autostart.SentinelTaskName + " foi aceita mas o guardião não apareceu em 3 s; usando o lançador");
            }
            else
            {
                journal.Write(JournalKind.Sentinel, "tarefa " + Autostart.SentinelTaskName + " ausente (execução fora da instalação?); usando o lançador desanexado");
            }
            if (!Launch())
            {
                journal.Write(JournalKind.Sentinel, "o lançador do guardião não pôde ser iniciado");
                return false;
            }
            if (WaitRunning(3000))
            {
                journal.Write(JournalKind.Sentinel, "guardião iniciado pelo lançador (fora do Agendador de Tarefas)");
                return true;
            }
            journal.Write(JournalKind.Sentinel, "o guardião não foi confirmado em 3 s");
            return false;
        }

        private static bool WaitRunning(int ms)
        {
            for (int waited = 0; waited < ms; waited += 100)
            {
                if (IsRunning()) return true;
                Thread.Sleep(100);
            }
            return IsRunning();
        }

        // Starts the launcher process, which starts the real sentinel and exits.
        public static bool Launch()
        {
            return Start(SwitchLaunch);
        }

        // ---- launcher process (--sentinela-iniciar) -------------------------------------------

        public static int RunLauncher()
        {
            return Start(SwitchRun) ? 0 : 1;
        }

        // Starts this same exe (inherits the elevation) with the given argument and does not wait.
        private static bool Start(string args)
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                // Elevated re-spawn of the running image by path: from outside Program Files it obeys the same
                // companion rule as every other elevation (fails closed: the Custom/Max write is then refused).
                if (!ElevationGuard.RespawnAllowed(exe)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    return p != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- the sentinel itself (--sentinela) ------------------------------------------------

        // Blocks until no SenseMonitor is running and the fans are safe.
        public static int Run()
        {
            return Run(MutexName, SingleInstance.MutexName, Heartbeat.Name, SentinelBrain.StartupGraceMs, MutexWaitMs);
        }

        // Names, grace and mutex wait are parameters so tests never touch the real mutexes.
        internal static int Run(string sentinelMutex, string appMutex, string heartbeatName, int graceMs, int mutexWaitMs)
        {
            int code = 0;
            Thread t = new Thread(delegate() { code = Loop(sentinelMutex, appMutex, heartbeatName, graceMs, mutexWaitMs); });
            t.IsBackground = false;
            t.Name = "SenseMonitor.Sentinel";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            return code;
        }

        private static int Loop(string sentinelMutex, string appMutex, string heartbeatName, int graceMs, int mutexWaitMs)
        {
            Safety.SetSource(Safety.SourceSentinel);
            IJournal journal = Safety.Journal;
            Mutex mutex = null;
            bool owned = false;
            try
            {
                try
                {
                    mutex = new Mutex(false, sentinelMutex);
                    try { owned = mutex.WaitOne(mutexWaitMs); }
                    catch (AbandonedMutexException) { owned = true; } // a previous sentinel was killed
                }
                catch (Exception)
                {
                    owned = false;
                }
                if (!owned) return 0; // another sentinel is already guarding

                journal.Write(JournalKind.Sentinel, "guardião iniciado");
                RealHost host = new RealHost(appMutex, heartbeatName);
                try
                {
                    SentinelLoop.Run(host, graceMs);
                }
                finally
                {
                    host.Dispose();
                }
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
            finally
            {
                if (mutex != null)
                {
                    if (owned)
                    {
                        try { mutex.ReleaseMutex(); }
                        catch (Exception) { }
                    }
                    mutex.Close();
                }
            }
        }

        private sealed class RealHost : ISentinelHost, IDisposable
        {
            private readonly string appMutex;
            private readonly string heartbeatName;
            private readonly AutoWriter writer;
            private Heartbeat heartbeat;

            public RealHost(string appMutex, string heartbeatName)
            {
                this.appMutex = appMutex;
                this.heartbeatName = heartbeatName;
                // One throttled journal for every attempt: each attempt has a firmware (and a guard) of its own,
                // so without it a long WMI outage would write two lines every few seconds.
                IJournal throttled = new ThrottledJournal(Safety.Journal, null);
                writer = new AutoWriter(delegate
                {
                    GuardedFirmware g = Safety.Wrap(new WmiFirmware(), throttled);
                    // the hand-back addresses the fans the marker says were written (never a both-fan mask by default)
                    FanLayout fromMarker = Safety.MarkerLayout();
                    if (fromMarker != null) g.SetLayout(fromMarker);
                    return g;
                }, AutoTimeoutMs);
            }

            public bool AnyAppAlive() { return Exists(appMutex); }

            public bool MarkerSet { get { return Safety.Marker.IsSet; } }

            public long? HeartbeatAgeMs()
            {
                if (heartbeat == null) heartbeat = Heartbeat.TryOpen(heartbeatName);
                return heartbeat == null ? null : heartbeat.AgeMs();
            }

            public AutoResult TryWriteAuto()
            {
                if (!writer.Try()) return AutoResult.Failed;
                // accepted: the guarded write cleared the marker if the read-back agreed
                return Safety.Marker.IsSet ? AutoResult.AcceptedUnconfirmed : AutoResult.Confirmed;
            }

            public void ClearMarker() { Safety.Marker.Clear(); }

            public void BumpIntervention()
            {
                if (heartbeat != null) heartbeat.BumpIntervention();
            }

            public int NowMs() { return Environment.TickCount; }

            public void Sleep(int ms) { Thread.Sleep(ms); }

            public void Log(string text) { Safety.Journal.Write(JournalKind.Sentinel, text); }

            public void Dispose()
            {
                if (heartbeat != null) heartbeat.Dispose();
            }
        }
    }
}
