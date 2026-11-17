using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    public class RepairStep
    {
        public RepairTool Tool;
        public string FileName;
        public string Arguments;
    }

    // Runs DISM /RestoreHealth and then sfc /scannow, one after the other, in hidden
    // processes. Output is read as raw bytes (OEM code page, or UTF-16 for SFC), split on
    // '\r' as well as '\n', and turned into percent and phase. Never started automatically;
    // the page asks for confirmation first.
    //
    // DISM is never killed: interrupting it mid-servicing can damage the component store. A
    // cancel (the page disables it during DISM; app exit still cancels) leaves DISM running
    // on its own and the run returns at once (RepairResult.DismDetached); SFC is not started.
    // When a protected folder is available (the install folder's tmp), DISM writes to a log
    // file there through cmd.exe instead of a pipe owned by this process, so it keeps going
    // after the app exits; the file is tailed for progress and deleted when DISM ends here.
    // SFC can be cancelled: its whole process tree is killed.
    public class RepairRunner : IRepairService
    {
        private const int MaxTail = 300;

        private readonly List<RepairStep> steps;
        private readonly Encoding oem;
        private readonly ChangeLog log;
        private readonly bool requireElevation;
        private readonly Func<bool> isElevated;
        private readonly string dismLogDir;
        private readonly bool resolveLogDir;

        public RepairRunner(ChangeLog log)
            : this(DefaultSteps(), OemEncoding(), log, true, null, null)
        {
            resolveLogDir = true;
        }

        public RepairRunner(List<RepairStep> steps, Encoding oem, ChangeLog log, bool requireElevation, Func<bool> isElevated)
            : this(steps, oem, log, requireElevation, isElevated, null)
        {
        }

        // dismLogDir: protected folder for DISM's output file (null = read DISM through a pipe).
        public RepairRunner(List<RepairStep> steps, Encoding oem, ChangeLog log, bool requireElevation, Func<bool> isElevated,
            string dismLogDir)
        {
            this.dismLogDir = dismLogDir;
            if (steps == null) throw new ArgumentNullException("steps");
            this.steps = steps;
            this.oem = oem ?? OemEncoding();
            this.log = log;
            this.requireElevation = requireElevation;
            this.isElevated = isElevated ?? Cleaner.IsElevated;
        }

        // At startup: DISM output files left by a detached run (dism-*.log) older than one day are
        // deleted; newer ones may still be written by a DISM that outlived the app. Returns the count.
        public static int CleanOldDismLogs(string dir, DateTime nowUtc)
        {
            int n = 0;
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
                foreach (string f in Directory.GetFiles(dir, "dism-*.log"))
                {
                    try
                    {
                        if (nowUtc - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1))
                        {
                            File.Delete(f);
                            n++;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return n;
        }

        public static Encoding OemEncoding()
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch (Exception) { return Encoding.GetEncoding(850); }
        }

        public static List<RepairStep> DefaultSteps()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            List<RepairStep> l = new List<RepairStep>();
            RepairStep d = new RepairStep();
            d.Tool = RepairTool.Dism;
            d.FileName = Path.Combine(sys, "Dism.exe");
            d.Arguments = "/Online /Cleanup-Image /RestoreHealth";
            l.Add(d);
            RepairStep s = new RepairStep();
            s.Tool = RepairTool.Sfc;
            s.FileName = Path.Combine(sys, "sfc.exe");
            s.Arguments = "/scannow";
            l.Add(s);
            return l;
        }

        public RepairResult Run(Action<RepairProgress> progress, CancellationToken ct)
        {
            Stopwatch total = Stopwatch.StartNew();
            RepairResult result = new RepairResult();
            if (requireElevation && !SafeElevated())
            {
                result.Outcome = RepairOutcome.Erro;
                result.Title = RepairParse.Title(RepairOutcome.Erro);
                result.Message = "O reparo precisa de permissão de administrador e o app está sem ela. Abra o app como administrador e tente de novo. Nada foi executado.";
                return result;
            }

            ToolOutcome dism = null;
            ToolOutcome sfc = null;
            bool cancelled = false;
            for (int i = 0; i < steps.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                RepairStep step = steps[i];
                StepRun run = RunStep(step, i + 1, steps.Count, progress, ct, total, result.Tail);
                ToolOutcome o;
                if (step.Tool == RepairTool.Dism)
                {
                    result.DismRan = true;
                    result.DismExit = run.ExitCode;
                    o = RepairParse.ClassifyDism(run.ExitCode, run.Lines);
                    dism = o;
                }
                else
                {
                    result.SfcRan = true;
                    result.SfcExit = run.ExitCode;
                    o = RepairParse.ClassifySfc(run.ExitCode, run.Lines);
                    sfc = o;
                }
                if (run.Detached) result.DismDetached = true;
                if (run.Cancelled)
                {
                    cancelled = true;
                    break;
                }
                if (o.NeedsElevation) break; // no point in running the next tool without admin rights
            }

            result.Outcome = RepairParse.Combine(dism, sfc, cancelled);
            string detail = DetailFor(dism, sfc);
            result.Title = RepairParse.Title(result.Outcome);
            result.Message = RepairParse.Message(result.Outcome, detail);
            if (result.DismDetached) result.Message += " " + RepairParse.DismDetachedNote;
            result.Duration = total.Elapsed;
            if (log != null)
            {
                try
                {
                    log.Record(ChangeKind.Reparo,
                        "Reparo do Windows (DISM + SFC): " + result.Title + " em " + FormatDuration(result.Duration)
                        + (result.DismDetached ? " (o DISM continuou em segundo plano)" : ""), null);
                }
                catch (Exception) { }
            }
            return result;
        }

        private bool SafeElevated()
        {
            try { return isElevated(); }
            catch (Exception) { return false; }
        }

        private static string DetailFor(ToolOutcome dism, ToolOutcome sfc)
        {
            StringBuilder sb = new StringBuilder();
            if (dism != null && dism.Ran && !string.IsNullOrEmpty(dism.Detail)) sb.Append(dism.Detail);
            if (sfc != null && sfc.Ran && !string.IsNullOrEmpty(sfc.Detail))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(sfc.Detail);
            }
            return sb.ToString();
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
            if (t.TotalMinutes >= 1) return t.Minutes + " min " + t.Seconds + " s";
            return t.Seconds + " s";
        }

        private class StepRun
        {
            public int ExitCode = -1;
            public bool Cancelled;
            public bool Detached;
            public readonly List<string> Lines = new List<string>();
        }

        private string LogDir()
        {
            if (!resolveLogDir) return dismLogDir;
            return InstallPaths.SecureTempDir();
        }

        private StepRun RunStep(RepairStep step, int index, int count, Action<RepairProgress> progress,
            CancellationToken ct, Stopwatch total, List<string> tail)
        {
            StepRun run = new StepRun();
            object sync = new object();
            RepairProgress state = new RepairProgress();
            state.Tool = step.Tool;
            state.Step = index;
            state.StepCount = count;
            state.Phase = RepairParse.DefaultPhase(step.Tool);

            Action<string> onLine = delegate(string line)
            {
                int pct = RepairParse.ParsePercent(line);
                string phase = RepairParse.PhaseOf(step.Tool, line);
                bool changed = false;
                lock (sync)
                {
                    if (phase != null && phase != state.Phase) { state.Phase = phase; changed = true; }
                    if (pct >= 0 && pct != state.Percent) { state.Percent = pct; changed = true; }
                    if (pct < 0)
                    {
                        run.Lines.Add(line);
                        tail.Add(line);
                        while (tail.Count > MaxTail) tail.RemoveAt(0);
                    }
                    state.LastLine = line;
                    state.Elapsed = total.Elapsed;
                }
                if (changed) Raise(progress, state, sync);
            };

            Raise(progress, state, sync);
            bool detachable = step.Tool == RepairTool.Dism;
            string dir = detachable ? LogDir() : null;
            string logFile = dir != null ? Path.Combine(dir, "dism-" + Guid.NewGuid().ToString("N") + ".log") : null;
            ProcessStartInfo psi;
            if (logFile != null)
            {
                psi = new ProcessStartInfo(InstallPaths.System32("cmd.exe"),
                    "/d /s /c \"\"" + step.FileName + "\" " + step.Arguments + " > \"" + logFile + "\" 2>&1\"");
            }
            else
            {
                psi = new ProcessStartInfo(step.FileName, step.Arguments);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                onLine("Não foi possível iniciar " + Path.GetFileName(step.FileName) + ": " + ex.Message);
                return run;
            }
            Thread outReader = null;
            Thread errReader = null;
            FileTail fileTail = null;
            try
            {
                using (p)
                {
                    if (logFile == null)
                    {
                        try { p.StandardInput.Close(); }
                        catch (Exception) { }
                        outReader = StartReader(p.StandardOutput.BaseStream, step.Tool, onLine);
                        errReader = StartReader(p.StandardError.BaseStream, step.Tool, onLine);
                    }
                    else
                    {
                        fileTail = new FileTail(logFile, new OutputDecoder(oem, false), onLine);
                    }
                    while (!p.WaitForExit(200))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            run.Cancelled = true;
                            if (detachable) run.Detached = true; // never kill DISM: leave it running
                            else KillTree(p);
                            break;
                        }
                    }
                    if (run.Detached)
                    {
                        if (fileTail != null) fileTail.Abandon();
                        return run;
                    }
                    if (run.Cancelled) p.WaitForExit(5000);
                    if (fileTail != null) fileTail.Finish(10000);
                    if (outReader != null) outReader.Join(5000);
                    if (errReader != null) errReader.Join(5000);
                    try { run.ExitCode = p.HasExited ? p.ExitCode : -1; }
                    catch (Exception) { run.ExitCode = -1; }
                }
            }
            finally
            {
                if (logFile != null && !run.Detached)
                {
                    try { if (File.Exists(logFile)) File.Delete(logFile); }
                    catch (Exception) { }
                }
            }
            return run;
        }

        // Follows a file another process is writing (DISM's redirected output).
        private sealed class FileTail
        {
            private readonly string path;
            private readonly OutputDecoder dec;
            private readonly Action<string> onLine;
            private readonly LineSplitter split = new LineSplitter();
            private readonly ManualResetEvent exited = new ManualResetEvent(false);
            private readonly Thread thread;
            private volatile bool abandoned;

            public FileTail(string path, OutputDecoder dec, Action<string> onLine)
            {
                this.path = path;
                this.dec = dec;
                this.onLine = onLine;
                thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Name = "SenseMonitor.RepairTail";
                thread.Start();
            }

            // The writer exited: read what is left, then stop.
            public void Finish(int timeoutMs)
            {
                exited.Set();
                thread.Join(timeoutMs);
            }

            // The writer keeps running without us: stop reading, leave the file alone.
            public void Abandon()
            {
                abandoned = true;
                exited.Set();
            }

            private void Loop()
            {
                FileStream fs = null;
                byte[] buf = new byte[4096];
                try
                {
                    while (!abandoned)
                    {
                        if (fs == null)
                        {
                            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                            catch (Exception) { fs = null; }
                        }
                        int n = 0;
                        if (fs != null)
                        {
                            n = fs.Read(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                foreach (string line in split.Feed(dec.Decode(buf, n))) onLine(line);
                                continue;
                            }
                        }
                        // Nothing new: done once the writer has exited and the file is drained.
                        if (exited.WaitOne(150) && (fs != null || !File.Exists(path)))
                        {
                            if (fs != null)
                            {
                                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                                {
                                    foreach (string line in split.Feed(dec.Decode(buf, n))) onLine(line);
                                }
                            }
                            break;
                        }
                    }
                    if (!abandoned)
                    {
                        string rest = split.Flush();
                        if (rest != null) onLine(rest);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (fs != null) fs.Dispose();
                }
            }
        }

        private static void Raise(Action<RepairProgress> progress, RepairProgress state, object sync)
        {
            if (progress == null) return;
            RepairProgress copy = new RepairProgress();
            lock (sync)
            {
                copy.Tool = state.Tool;
                copy.Step = state.Step;
                copy.StepCount = state.StepCount;
                copy.Phase = state.Phase;
                copy.Percent = state.Percent;
                copy.LastLine = state.LastLine;
                copy.Elapsed = state.Elapsed;
            }
            try { progress(copy); }
            catch (Exception) { }
        }

        private Thread StartReader(Stream stream, RepairTool tool, Action<string> onLine)
        {
            Thread t = new Thread(delegate()
            {
                OutputDecoder dec = new OutputDecoder(oem, tool == RepairTool.Sfc);
                LineSplitter split = new LineSplitter();
                byte[] buf = new byte[4096];
                try
                {
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        foreach (string line in split.Feed(dec.Decode(buf, n))) onLine(line);
                    }
                }
                catch (Exception)
                {
                }
                string rest = split.Flush();
                if (rest != null) onLine(rest);
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        // taskkill /T takes the children down too (DISM starts DismHost).
        private static void KillTree(Process p)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(InstallPaths.System32("taskkill.exe"), "/PID " + p.Id + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process k = Process.Start(psi))
                {
                    k.StandardOutput.ReadToEnd();
                    k.StandardError.ReadToEnd();
                    k.WaitForExit(10000);
                }
            }
            catch (Exception)
            {
            }
            try { if (!p.HasExited) p.Kill(); }
            catch (Exception) { }
        }
    }
}
