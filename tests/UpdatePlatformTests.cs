using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    internal sealed class TempFolder : IDisposable
    {
        public readonly string Path;

        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forja-plat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Sub(string name) { return System.IO.Path.Combine(Path, name); }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (Exception) { }
        }
    }

    public static class UpdateTaskTests
    {
        private const string Helper = @"C:\Program Files\SenseMonitor\atualizacao\SenseMonitor.aplicador.exe";

        [Test]
        public static void Xml_IsOnDemand_Parallel_Elevated_AndRunsTheHelperWithItsSwitch()
        {
            string xml = UpdateTask.BuildXml(Helper, "PC\\user");
            Assert.True(xml.IndexOf("<LogonTrigger", StringComparison.Ordinal) < 0 && xml.IndexOf("<Triggers", StringComparison.Ordinal) < 0, "no trigger: on demand only");
            Assert.True(xml.Contains("<MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>"), "parallel: a leftover running instance must not make /Run a no-op");
            Assert.True(xml.Contains("<RunLevel>HighestAvailable</RunLevel>"), "elevated");
            Assert.True(xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"), "no time limit (it would kill the app started inside the job)");
            Assert.True(xml.Contains("<Command>&quot;" + Helper + "&quot;</Command>"), "command (quotes are XML-escaped; Autostart.TaskPointsTo undoes that)");
            Assert.True(Autostart.TaskPointsTo(xml, Helper), "TaskPointsTo reads it back");
            Assert.True(xml.Contains("<Arguments>--aplicar-atualizacao</Arguments>"), "switch only: pid and version come from the instructions file");
            Assert.True(xml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"), "battery safe");
        }

        [Test]
        public static void Xml_EscapesTheUserName()
        {
            Assert.True(UpdateTask.BuildXml(Helper, "A&B<x>").Contains("A&amp;B&lt;x&gt;"), "escaped");
        }

        [Test]
        public static void IsCurrent_OnlyForTheSameExeAndSwitch()
        {
            string xml = UpdateTask.BuildXml(Helper, "PC\\user");
            Assert.True(UpdateTask.IsCurrent(xml, Helper), "same");
            Assert.True(!UpdateTask.IsCurrent(xml, @"C:\Other\x.exe"), "other exe");
            Assert.True(!UpdateTask.IsCurrent(xml.Replace("--aplicar-atualizacao", "--outra"), Helper), "other switch");
            Assert.True(!UpdateTask.IsCurrent(null, Helper), "null");
        }

        [Test]
        public static void TaskLists_CoverAutostart_Sentinel_AndHelper()
        {
            List<string> names = new List<string>(InstallTasks.TaskNames);
            Assert.True(names.Contains("SenseMonitor") && names.Contains("SenseMonitor-Sentinela") && names.Contains("SenseMonitor-Atualizar") && names.Contains("SenseMonitor-Desinstalar"), "the install owns all four tasks");
            Assert.Equal(3, InstallTasks.Registrars.Length); // Autostart.Enable (autostart + sentinel), the helper's task and the uninstall task
        }
    }

    public static class AdminDataFolderTests
    {
        [Test]
        public static void AnUnreadableAcl_IsNeverTreatedAsSafe()
        {
            // (the real ProgramData folder is never created by tests)
            string p = UpdateStore.AclProblem(Path.Combine(Path.GetTempPath(), "forja-does-not-exist-" + Guid.NewGuid().ToString("N")));
            Assert.True(p != null, "an unreadable ACL (missing folder) is a problem, never 'safe'");
        }

        [Test]
        public static void AUserOwnedFolder_IsNotAcceptedForTheProgramFilesRuleEither()
        {
            using (TempFolder t = new TempFolder())
            {
                Assert.True(UpdateStore.AclProblem(t.Path) != null, "a user-owned, user-writable folder is refused");
            }
        }
    }

    public static class StateBackupTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public static void DamagedStateFile_FallsBackToTheBackup_SoARollbackStaysBlocked()
        {
            using (TempFolder t = new TempFolder())
            {
                UpdateState s = new UpdateState();
                s.BlockPermanently(new UpdateVersion(0, 3, 0), "reversão");
                UpdateStore.SaveState(t.Path, s);
                UpdateStore.SaveState(t.Path, s);   // second save: the first copy becomes the .bak
                File.WriteAllText(Path.Combine(t.Path, "estado.json"), "{damaged");
                UpdateState back = UpdateStore.LoadState(t.Path);
                Assert.True(back.IsBlocked(new UpdateVersion(0, 3, 0), T0.AddDays(500)), "permanent block survived the damaged file");
            }
        }

        [Test]
        public static void MissingOrBothDamaged_IsAFreshState()
        {
            using (TempFolder t = new TempFolder())
            {
                Assert.True(!UpdateStore.LoadState(t.Path).LastCheckUtc.HasValue, "missing");
                File.WriteAllText(Path.Combine(t.Path, "estado.json"), "x");
                File.WriteAllText(Path.Combine(t.Path, "estado.json.bak"), "y");
                Assert.True(!UpdateStore.LoadState(t.Path).LastCheckUtc.HasValue, "both damaged");
            }
        }

        [Test]
        public static void TryParse_TellsDamagedFromEmpty()
        {
            UpdateState s;
            Assert.True(!UpdateState.TryParse(Encoding.UTF8.GetBytes("nope"), out s), "damaged");
            Assert.True(UpdateState.TryParse(Encoding.UTF8.GetBytes("{}"), out s), "empty object is a valid (empty) state");
            Assert.True(!UpdateState.TryParse(null, out s), "null");
        }
    }

    public static class HelperLockTests
    {
        [Test]
        public static void OnlyOneHolderAtATime_AndItIsFreeAfterDispose()
        {
            using (TempFolder t = new TempFolder())
            {
                string mx = @"Local\SenseMonitor.Test." + Guid.NewGuid().ToString("N");
                HelperLock a = HelperLock.TryAcquire(t.Path, mx);
                Assert.True(a != null, "first acquires");
                Assert.True(HelperLock.IsHeld(t.Path), "held while it runs");
                HelperLock b = HelperLock.TryAcquire(t.Path, mx + "x");
                Assert.True(b == null, "a second helper does not get in (file lock is the authority)");
                a.Dispose();
                Assert.True(!HelperLock.IsHeld(t.Path), "free after dispose");
                HelperLock c = HelperLock.TryAcquire(t.Path, mx + "y");
                Assert.True(c != null, "acquirable again");
                c.Dispose();
            }
        }

        [Test]
        public static void ASquattedMutexName_DoesNotStopTheHelper_ButIsReported()
        {
            using (TempFolder t = new TempFolder())
            {
                string mx = @"Local\SenseMonitor.Squat." + Guid.NewGuid().ToString("N");
                bool created;
                // another party creates the name first and holds it
                using (System.Threading.Mutex squatter = new System.Threading.Mutex(true, mx, out created))
                {
                    Assert.True(created, "squatter created it");
                    HelperLock h = null;
                    System.Threading.Thread th = new System.Threading.Thread(delegate() { h = HelperLock.TryAcquire(t.Path, mx); });
                    th.Start();
                    th.Join();
                    Assert.True(h != null, "the file lock decides: the helper still runs");
                    Assert.True(h.Note != null && h.Note.Contains("mutex"), "and says so");
                    h.Dispose();
                }
            }
        }

        [Test]
        public static void IsHeld_FalseWhenThereIsNoLockFile()
        {
            using (TempFolder t = new TempFolder())
            {
                Assert.True(!HelperLock.IsHeld(t.Path), "no file");
            }
        }
    }

    public static class ToolRunnerTests
    {
        [Test]
        public static void RunCapture_ReturnsStdoutAndExitCode()
        {
            int code;
            string outp;
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Assert.True(ToolRunner.RunCapture(cmd, "/d /c echo hello-forja & exit 3", 15000, out code, out outp), "ran");
            Assert.Equal(3, code);
            Assert.True(outp.Contains("hello-forja"), "stdout captured: " + outp);
        }

        [Test]
        public static void Run_TimeoutKillsTheToolAndReportsFailure()
        {
            int code;
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Stopwatch w = Stopwatch.StartNew();
            Assert.True(!ToolRunner.Run(cmd, "/d /c ping -n 30 127.0.0.1 >nul", 600, out code), "timed out");
            Assert.True(w.ElapsedMilliseconds < 8000, "did not wait for the tool");
        }
    }

    public static class RemoveKnownFilesTests
    {
        private static readonly string[] Names = { "config.json", "erro.log", "diario.log" };

        [Test]
        public static void DeletesOnlyTheKnownNames_NothingElse()
        {
            using (TempFolder t = new TempFolder())
            {
                File.WriteAllText(t.Sub("config.json"), "x");
                File.WriteAllText(t.Sub("erro.log"), "x");
                File.WriteAllText(t.Sub("minhas-notas.txt"), "keep me");
                string note = InstallSystem.RemoveKnownFiles(t.Path, Names, null);
                Assert.Null(note);
                Assert.True(!File.Exists(t.Sub("config.json")) && !File.Exists(t.Sub("erro.log")), "known files gone");
                Assert.True(File.Exists(t.Sub("minhas-notas.txt")), "an unknown file is never touched");
            }
        }

        [Test]
        public static void IsNotRecursive_AFolderWithAKnownNameIsLeftWithItsContent()
        {
            using (TempFolder t = new TempFolder())
            {
                Directory.CreateDirectory(t.Sub("config.json"));
                File.WriteAllText(Path.Combine(t.Sub("config.json"), "inner.txt"), "keep");
                string note = InstallSystem.RemoveKnownFiles(t.Path, Names, null);
                Assert.True(note != null, "reported");
                Assert.True(File.Exists(Path.Combine(t.Sub("config.json"), "inner.txt")), "nothing inside was touched");
            }
        }

        [Test]
        public static void AJunctionWithAKnownName_IsNotFollowedNorDeleted()
        {
            using (TempFolder t = new TempFolder())
            using (TempFolder target = new TempFolder())
            {
                File.WriteAllText(Path.Combine(target.Path, "precious.txt"), "do not delete");
                int code;
                string outp;
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                Assert.True(ToolRunner.RunCapture(cmd, "/d /c mklink /J \"" + t.Sub("erro.log") + "\" \"" + target.Path + "\"", 15000, out code, out outp) && code == 0, "junction created: " + outp);
                string note = InstallSystem.RemoveKnownFiles(t.Path, Names, null);
                Assert.True(note != null, "link reported");
                Assert.True(File.Exists(Path.Combine(target.Path, "precious.txt")), "the target of the junction is intact");
                Assert.True(Directory.Exists(t.Sub("erro.log")), "the junction itself was left");
            }
        }

        [Test]
        public static void AFolderThatIsItselfALink_IsLeftAlone()
        {
            using (TempFolder t = new TempFolder())
            using (TempFolder target = new TempFolder())
            {
                File.WriteAllText(Path.Combine(target.Path, "config.json"), "x");
                string link = t.Sub("data");
                int code;
                string outp;
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                Assert.True(ToolRunner.RunCapture(cmd, "/d /c mklink /J \"" + link + "\" \"" + target.Path + "\"", 15000, out code, out outp) && code == 0, "junction created");
                string note = InstallSystem.RemoveKnownFiles(link, Names, null);
                Assert.True(note != null, "reported");
                Assert.True(File.Exists(Path.Combine(target.Path, "config.json")), "nothing deleted through the link");
            }
        }

        [Test]
        public static void PredicateMode_OnlyDateNamedFiles()
        {
            using (TempFolder t = new TempFolder())
            {
                string good = TempLog.FileNameFor(new DateTime(2026, 11, 1));
                File.WriteAllText(t.Sub(good), "x");
                File.WriteAllText(t.Sub("other.csv"), "x");
                string note = InstallSystem.RemoveKnownFiles(t.Path, null, delegate(string n) { DateTime d; return TempLog.TryParseFileDay(n, out d); });
                Assert.Null(note);
                Assert.True(!File.Exists(t.Sub(good)), "date file removed");
                Assert.True(File.Exists(t.Sub("other.csv")), "other file kept");
            }
        }

        [Test]
        public static void TryRemoveIfEmpty_NeverDeletesAFolderWithContent()
        {
            using (TempFolder t = new TempFolder())
            {
                File.WriteAllText(t.Sub("keep.txt"), "x");
                InstallSystem.TryRemoveIfEmpty(t.Path);
                Assert.True(File.Exists(t.Sub("keep.txt")), "content survives");
                File.Delete(t.Sub("keep.txt"));
                InstallSystem.TryRemoveIfEmpty(t.Path);
                Assert.True(!Directory.Exists(t.Path), "empty folder removed");
            }
        }

        [Test]
        public static void MissingFolder_IsNotAnError()
        {
            Assert.Null(InstallSystem.RemoveKnownFiles(Path.Combine(Path.GetTempPath(), "forja-nope-" + Guid.NewGuid().ToString("N")), Names, null));
        }

        [Test]
        public static void TheKnownList_CoversWhatTheAppWrites()
        {
            List<string> k = new List<string>(InstallSystem.KnownDataFiles);
            string[] expected = { "config.json", "config.json.bak", "erro.log", "diario.log", "diario.log.1", "historico.json", "teste-desempenho.journal", "ventoinha-custom.flag", "sessao-aberta.flag" };
            foreach (string e in expected) Assert.True(k.Contains(e), "known: " + e);
        }
    }
}
