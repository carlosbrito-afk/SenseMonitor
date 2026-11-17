using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Final-review hardening: install paths and helper resolution, autostart target, undo
    // whitelist, DISM never killed.
    public static class HardeningTests
    {
        private const string ApprRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ApprRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
        private const string ApprFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        // ---- install location and helpers ----

        [Test]
        public static void InstallPaths_UnderProgramFiles()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Assert.Equal(Path.Combine(pf, "SenseMonitor"), InstallPaths.InstallDir);
            Assert.Equal(Path.Combine(pf, "SenseMonitor", "SenseMonitor.exe"), InstallPaths.InstalledExe);
            Assert.Equal(Path.Combine(pf, "SenseMonitor", "tmp"), InstallPaths.TempDirPath);
        }

        [Test]
        public static void InstallPaths_System32Helpers_AreFullPaths()
        {
            string p = InstallPaths.System32("schtasks.exe");
            Assert.True(Path.IsPathRooted(p), p);
            Assert.True(p.EndsWith(@"\System32\schtasks.exe", StringComparison.OrdinalIgnoreCase), p);
        }

        [Test]
        public static void NvidiaSmi_System32First_ThenNvidiaFolder_NeverBareName()
        {
            string sys = @"C:\Windows\System32\nvidia-smi.exe";
            string nv = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe";
            Assert.Equal(sys, InstallPaths.ResolveNvidiaSmi(@"C:\Windows", @"C:\Program Files", delegate(string f) { return true; }));
            Assert.Equal(nv, InstallPaths.ResolveNvidiaSmi(@"C:\Windows", @"C:\Program Files", delegate(string f) { return f == nv; }));
            Assert.Null(InstallPaths.ResolveNvidiaSmi(@"C:\Windows", @"C:\Program Files", delegate(string f) { return false; }));
        }

        // ---- autostart ----

        [Test]
        public static void Autostart_TaskPointsTo_ComparesTheCommand()
        {
            string exe = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";
            string xml = Autostart.BuildTaskXml(exe, "PC\\user");
            Assert.True(Autostart.TaskPointsTo(xml, exe), "same exe");
            Assert.True(Autostart.TaskPointsTo(xml.Replace("Program Files", "PROGRAM FILES"), exe), "case-insensitive");
            Assert.True(!Autostart.TaskPointsTo(Autostart.BuildTaskXml(@"C:\claude\forja\bin\SenseMonitor.exe", "PC\\user"), exe), "old dev path");
            Assert.True(!Autostart.TaskPointsTo(null, exe), "no task");
            Assert.True(!Autostart.TaskPointsTo("<Task></Task>", exe), "no command");
        }

        [Test]
        public static void Autostart_XmlFile_GoesToTheProtectedFolder()
        {
            string p = Autostart.XmlPathIn(@"C:\Program Files\SenseMonitor\tmp");
            Assert.True(p.StartsWith(@"C:\Program Files\SenseMonitor\tmp\", StringComparison.OrdinalIgnoreCase), p);
            Assert.True(p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase), p);
            Assert.True(p.IndexOf(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) < 0, "never %TEMP%");
        }

        // ---- Desfazer: registry whitelist ----

        [Test]
        public static void UndoWhitelist_OnlyStartupApprovedKeys()
        {
            Assert.True(StartupModel.IsAllowedUndoTarget(RegHive.CurrentUser, ApprRun), "HKCU Run");
            Assert.True(StartupModel.IsAllowedUndoTarget(RegHive.LocalMachine, ApprRun32), "HKLM Run32");
            Assert.True(StartupModel.IsAllowedUndoTarget(RegHive.LocalMachine, ApprFolder.ToUpperInvariant()), "case-insensitive");
            Assert.True(!StartupModel.IsAllowedUndoTarget(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Foo"), "services");
            Assert.True(!StartupModel.IsAllowedUndoTarget(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"), "the Run key itself");
            Assert.True(!StartupModel.IsAllowedUndoTarget(RegHive.CurrentUser, ApprRun + @"\..\..\Policies"), "no traversal");
            Assert.True(!StartupModel.IsAllowedUndoTarget(RegHive.CurrentUser, ApprRun + @"\Sub"), "no subkeys");
            Assert.True(!StartupModel.IsAllowedUndoTarget(RegHive.CurrentUser, null), "null");
        }

        [Test]
        public static void Undo_ForeignRegistryTarget_IsRefusedWithoutTouchingIt()
        {
            FakeRegistry reg = new FakeRegistry();
            string evil = @"SYSTEM\CurrentControlSet\Services\Foo";
            byte[] written = new byte[] { 3, 0, 0, 0 };
            reg.PutBinary(RegHive.LocalMachine, evil, "Start", written);
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            ChangeEntry e = log.Record(ChangeKind.Inicializacao, "forjado",
                StartupModel.EncodeUndo(RegHive.LocalMachine, evil, "Start", new byte[] { 2, 0, 0, 0 }, written));
            StartupManager m = new StartupManager(reg, null, null, log, null, null);
            StartupChangeResult r = m.Undo(e);
            Assert.True(!r.Ok, "refused");
            Assert.Equal(0, reg.Writes.Count + reg.Deletes.Count);
            Assert.Equal((byte)3, reg.GetBinary(RegHive.LocalMachine, evil, "Start")[0]);
            Assert.True(!StartupModel.UndoableEntryIds(log.List()).ContainsKey(e.Id), "no Desfazer button either");
        }

        // ---- repair: DISM is never killed ----

        private static RepairStep Step(RepairTool tool, string args)
        {
            RepairStep s = new RepairStep();
            s.Tool = tool;
            s.FileName = Environment.GetEnvironmentVariable("ComSpec");
            s.Arguments = args;
            return s;
        }

        [Test]
        public static void QuitPrompt_ExplainsThatDismKeepsRunning()
        {
            string dism = RepairParse.QuitPrompt(true);
            Assert.True(dism.Contains("DISM") && dism.Contains("continua"), dism);
            string other = RepairParse.QuitPrompt(false);
            Assert.True(other.Contains("cancela"), other);
            Assert.True(!RepairParse.Message(RepairOutcome.Cancelado, null).Contains("seguran"), "no 'safe to interrupt' claim");
        }

        [Test]
        public static void Repair_CancelDuringDism_DetachesInsteadOfKilling()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-rep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RepairRunner r = new RepairRunner(new List<RepairStep>(new RepairStep[]
                {
                    Step(RepairTool.Dism, "/c ping -n 41 127.0.0.1"),
                    Step(RepairTool.Sfc, "/c \"echo sfc should not run\"")
                }), Encoding.GetEncoding(850), null, false, null, dir);
                CancellationTokenSource cts = new CancellationTokenSource();
                Stopwatch sw = Stopwatch.StartNew();
                Thread t = new Thread(delegate() { Thread.Sleep(1500); cts.Cancel(); });
                t.Start();
                RepairResult res = r.Run(null, cts.Token);
                t.Join();
                Assert.True(sw.ElapsedMilliseconds < 15000, "returned without waiting for DISM");
                Assert.True(res.DismDetached, "DISM left running");
                Assert.True(!res.SfcRan, "SFC not started");
                Assert.True(res.Message.Contains("continua"), res.Message);
                Thread.Sleep(300);
                Assert.Equal(1, KillPings("-n 41 127.0.0.1"));
            }
            finally
            {
                KillPings("-n 41 127.0.0.1");
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void Repair_DismOutputThroughFile_IsParsedAndCleanedUp()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-rep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RepairRunner r = new RepairRunner(new List<RepairStep>(new RepairStep[]
                {
                    Step(RepairTool.Dism, "/c \"echo [=====25.0%=====]& echo The restore operation completed successfully.\"")
                }), Encoding.GetEncoding(850), null, false, null, dir);
                List<RepairProgress> seen = new List<RepairProgress>();
                RepairResult res = r.Run(delegate(RepairProgress p) { lock (seen) { seen.Add(p); } }, CancellationToken.None);
                Assert.Equal(RepairOutcome.SemProblemas, res.Outcome);
                Assert.Equal(0, res.DismExit);
                bool p25 = false;
                lock (seen) { foreach (RepairProgress p in seen) if (p.Percent == 25) p25 = true; }
                Assert.True(p25, "percent read from the file");
                Assert.Equal(0, Directory.GetFiles(dir).Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void Repair_SfcCancel_StillKills()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-rep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RepairRunner r = new RepairRunner(new List<RepairStep>(new RepairStep[]
                {
                    Step(RepairTool.Sfc, "/c ping -n 43 127.0.0.1")
                }), Encoding.GetEncoding(850), null, false, null, dir);
                CancellationTokenSource cts = new CancellationTokenSource();
                Thread t = new Thread(delegate() { Thread.Sleep(700); cts.Cancel(); });
                t.Start();
                RepairResult res = r.Run(null, cts.Token);
                t.Join();
                Assert.Equal(RepairOutcome.Cancelado, res.Outcome);
                Assert.True(!res.DismDetached, "nothing detached");
                Thread.Sleep(300);
                Assert.Equal(0, KillPings("-n 43 127.0.0.1"));
            }
            finally
            {
                KillPings("-n 43 127.0.0.1");
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void DismLogs_OlderThanOneDayAreDeletedOnStart()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-dlog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string old = Path.Combine(dir, "dism-old.log");
                string fresh = Path.Combine(dir, "dism-new.log");
                string other = Path.Combine(dir, "task-x.xml");
                File.WriteAllText(old, "x");
                File.WriteAllText(fresh, "x");
                File.WriteAllText(other, "x");
                DateTime now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(old, now.AddDays(-2));
                File.SetLastWriteTimeUtc(other, now.AddDays(-2));
                Assert.Equal(1, RepairRunner.CleanOldDismLogs(dir, now));
                Assert.True(!File.Exists(old), "old DISM log deleted");
                Assert.True(File.Exists(fresh), "recent one kept (DISM may still be writing)");
                Assert.True(File.Exists(other), "only dism-*.log files");
                Assert.Equal(0, RepairRunner.CleanOldDismLogs(null, now));
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }
        // Kills matching ping processes; returns how many were running.
        private static int KillPings(string fragment)
        {
            int n = 0;
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'PING.EXE'"))
            using (ManagementObjectCollection c = s.Get())
            {
                foreach (ManagementObject o in c)
                {
                    string cl = o["CommandLine"] as string;
                    if (cl != null && cl.Contains(fragment))
                    {
                        n++;
                        try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); }
                        catch (Exception) { }
                    }
                    o.Dispose();
                }
            }
            return n;
        }
    }
}
