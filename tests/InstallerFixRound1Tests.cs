using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Fix round 1 of the installer review: I1 (the exe is held and copied from the handle), I2 (the uninstaller
    // starts outside the app's job), m2 (invariant UI culture), m3 (no both-fan fallback in a fresh process),
    // m4 (the hand-back decision fits the caller's total timeout), m1, m5.
    public static class InstallerFixRound1Tests
    {
        private static string Temp()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-fix1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static void Gone(string d)
        {
            try { Directory.Delete(d, true); }
            catch (Exception) { }
        }

        private const string Hex64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- I1: the token for the first stage's hash -----------------------------------------------------

        private static StartupRoute R(params string[] a)
        {
            List<string> l = new List<string>();
            l.Add(@"C:\Users\x\Downloads\SenseMonitor.exe");
            l.AddRange(a);
            return StartupRouter.Decide(l.ToArray(), @"C:\Users\x\Downloads\SenseMonitor.exe", @"C:\Program Files\SenseMonitor\SenseMonitor.exe", false);
        }

        [Test]
        public static void Sha_WithInstall_IsCarried_Lowercase()
        {
            StartupRoute r = R("--instalar", "--passo=reconhecer", "--sha=" + Hex64.ToUpperInvariant(), "--elevado");
            Assert.Equal(StartupAction.Install, r.Action);
            Assert.Equal(InstallerStage.Detect, r.Stage);
            Assert.Equal(Hex64, r.ExpectedSha256);
            Assert.Null(R("--instalar", "--passo=reconhecer").ExpectedSha256);
            Assert.Equal(Hex64, R("--instalar", "--sha=" + Hex64).ExpectedSha256);
        }

        [Test]
        public static void Sha_Malformed_DuplicateOrAlone_IsRefused()
        {
            string[] bad = { "--sha=", "--sha=abc", "--sha=" + Hex64 + "0", "--sha=" + Hex64.Substring(1) + "g", "--sha=" + Hex64 + " ", "--sha=..\\x" };
            foreach (string b in bad) Assert.Equal(StartupAction.BadArguments, R("--instalar", b).Action);
            Assert.Equal(StartupAction.BadArguments, R("--instalar", "--sha=" + Hex64, "--sha=" + Hex64).Action);
            Assert.Equal(StartupAction.BadArguments, R("--sha=" + Hex64).Action);
            Assert.Equal(StartupAction.BadArguments, StartupRouter.Decide(new string[] { "x.exe", "--desinstalar", "--sha=" + Hex64 }, "x.exe", "y.exe", true).Action);
        }

        [Test]
        public static void ShaSwitch_RoundTripsThroughTheRouter()
        {
            Assert.Equal("--sha=" + Hex64, StartupRouter.ShaSwitch(Hex64.ToUpperInvariant()));
            Assert.Equal(Hex64, R("--instalar", StartupRouter.ShaSwitch(Hex64)).ExpectedSha256);
            Assert.Null(StartupRouter.ShaSwitch("nope"));
        }

        // ---- I1: the held source ---------------------------------------------------------------------------

        private static string MakeFile(string dir, string name, string text)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, text);
            return p;
        }

        [Test]
        public static void SourceHold_DeniesRenameDeleteAndWrite_WhileHeld()
        {
            string d = Temp();
            try
            {
                string src = MakeFile(d, "SenseMonitor.exe", "original bytes");
                string err;
                SourceHold h = SourceHold.Open(src, out err);
                Assert.Null(err);
                try
                {
                    Assert.True(h != null, "opened");
                    Assert.Throws<IOException>(delegate { File.Move(src, Path.Combine(d, "elsewhere.exe")); });
                    Assert.Throws<IOException>(delegate { File.Delete(src); });
                    Assert.Throws<IOException>(delegate { File.WriteAllText(src, "malware"); });
                    Assert.Equal("original bytes", File.ReadAllText(src));
                }
                finally
                {
                    h.Dispose();
                }
                // released: now it is just a file again
                File.Move(src, Path.Combine(d, "elsewhere.exe"));
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void SourceHold_Open_FailsWhileSomebodyHasTheFileOpenForWriting()
        {
            string d = Temp();
            try
            {
                string src = MakeFile(d, "SenseMonitor.exe", "x");
                using (FileStream w = new FileStream(src, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    string err;
                    SourceHold h = SourceHold.Open(src, out err);
                    Assert.True(h == null && err != null, "an open writer means the bytes can still change: refused");
                }
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void SourceHold_CopiesFromTheHandle_AndSaysWhatItHashed()
        {
            string d = Temp();
            try
            {
                string src = MakeFile(d, "SenseMonitor.exe", new string('A', 200000));
                string err;
                using (SourceHold h = SourceHold.Open(src, out err))
                {
                    string expected = UpdateManifestFile.Sha256Hex(File.ReadAllBytes(src));
                    Assert.Equal(expected, h.Sha256);
                    string dest = Path.Combine(d, "copy.bin");
                    string copied;
                    using (FileStream o = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        copied = h.CopyTo(o);
                    }
                    Assert.Equal(expected, copied);
                    Assert.Equal(expected, UpdateManifestFile.Sha256Hex(File.ReadAllBytes(dest)));
                    // a second copy starts from the beginning again
                    string again;
                    using (MemoryStream ms = new MemoryStream()) { again = h.CopyTo(ms); Assert.Equal(200000, (int)ms.Length); }
                    Assert.Equal(expected, again);
                }
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void SourceHold_Verify_TheFirstStagesHash()
        {
            string d = Temp();
            try
            {
                string src = MakeFile(d, "SenseMonitor.exe", "bytes");
                string err;
                using (SourceHold h = SourceHold.Open(src, out err))
                {
                    Assert.Null(h.Verify(null));                    // not given: nothing to compare
                    Assert.Null(h.Verify(h.Sha256.ToUpperInvariant()));
                    Assert.True(h.Verify(Hex64) != null, "another file than the one the first stage saw");
                }
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void CopyExe_FromTheHeldHandle_PutsTheHeldBytesInPlace_KeepingThePreviousOne()
        {
            string d = Temp();
            try
            {
                string src = MakeFile(d, "downloaded.exe", "NEW");
                string dir = Path.Combine(d, "pf");
                Directory.CreateDirectory(dir);
                string target = MakeFile(dir, "SenseMonitor.exe", "OLD");
                string tmp = Path.Combine(dir, "SenseMonitor.instalando.tmp");
                string prev = Path.Combine(dir, "SenseMonitor.anterior.exe");
                string err;
                using (SourceHold h = SourceHold.Open(src, out err))
                {
                    Assert.Null(InstallSystem.CopyExe(h, target, tmp, prev));
                    Assert.Equal("NEW", File.ReadAllText(target));
                    Assert.Equal("OLD", File.ReadAllText(prev));
                    Assert.True(!File.Exists(tmp), "temporary copy gone");
                    // the source is still held and still the same bytes
                    Assert.Throws<IOException>(delegate { File.WriteAllText(src, "x"); });
                }
            }
            finally
            {
                Gone(d);
            }
        }

        [Test]
        public static void CopyExe_FreshInstall_And_AFailedSwapPutsTheOldOneBack()
        {
            string d = Temp();
            try
            {
                string src = MakeFile(d, "downloaded.exe", "NEW");
                string dir = Path.Combine(d, "pf");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "SenseMonitor.exe");
                string tmp = Path.Combine(dir, "SenseMonitor.instalando.tmp");
                string prev = Path.Combine(dir, "SenseMonitor.anterior.exe");
                string err;
                using (SourceHold h = SourceHold.Open(src, out err))
                {
                    Assert.Null(InstallSystem.CopyExe(h, target, tmp, prev));
                    Assert.Equal("NEW", File.ReadAllText(target));
                    Assert.True(!File.Exists(prev), "nothing to keep on a fresh install");
                    // a target that cannot be moved aside (somebody runs it): the old one stays, no temp left
                    using (FileStream run = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        string e2 = InstallSystem.CopyExe(h, target, tmp, prev);
                        Assert.True(e2 != null, "refused");
                        Assert.True(!File.Exists(tmp), "no temp left");
                    }
                    Assert.Equal("NEW", File.ReadAllText(target));
                }
            }
            finally
            {
                Gone(d);
            }
        }

        // ---- I2: the uninstall task, its instructions and the delayed cleanup -------------------------------

        [Test]
        public static void UninstallTask_IsOnDemand_Elevated_AndRunsTheExeWithTheUninstallSwitchOnly()
        {
            string xml = UninstallTask.BuildXml(@"C:\Program Files\SenseMonitor\SenseMonitor.exe", @"PC\user");
            Assert.True(xml.IndexOf("<Command>&quot;C:\\Program Files\\SenseMonitor\\SenseMonitor.exe&quot;</Command>", StringComparison.Ordinal) >= 0, "the installed exe");
            Assert.True(xml.IndexOf("<Arguments>--desinstalar</Arguments>", StringComparison.Ordinal) >= 0, "the fixed argument");
            Assert.True(xml.IndexOf("HighestAvailable", StringComparison.Ordinal) >= 0, "elevated like the others");
            Assert.True(xml.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) < 0, "no trigger: only on demand");
            Assert.True(xml.IndexOf("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.Ordinal) >= 0, "no time limit that would kill a half-done uninstall");
            Assert.Equal("SenseMonitor-Desinstalar", UninstallTask.TaskName);
        }

        [Test]
        public static void UninstallTask_IsRegisteredAndRemovedWithTheOthers_ButNotByTheRunningUninstaller()
        {
            Assert.True(new List<string>(InstallTasks.TaskNames).Contains(UninstallTask.TaskName), "deleted by the install's undo");
            Assert.True(!new List<string>(InstallTasks.TaskNamesForUninstaller).Contains(UninstallTask.TaskName), "the uninstaller does not delete the task that hosts it");
            Assert.True(new List<string>(InstallTasks.TaskNamesForUninstaller).Contains(Autostart.TaskName), "the rest still goes");
            Assert.Equal(InstallTasks.TaskNames.Length - 1, InstallTasks.TaskNamesForUninstaller.Length);
        }

        [Test]
        public static void DelayedRemove_CanAlsoDeleteTheUninstallTask_AsItsLastWord()
        {
            string args = PathSafety.DelayedRemoveArgs(@"C:\Program Files\SenseMonitor", @"C:\Program Files", @"C:\WINDOWS\system32", UninstallTask.TaskName);
            int rm = args.IndexOf("rmdir", StringComparison.Ordinal);
            int del = args.IndexOf("schtasks.exe /Delete /TN SenseMonitor-Desinstalar /F", StringComparison.Ordinal);
            Assert.True(rm > 0 && del > rm, "the folder first, the task last: " + args);
            Assert.True(args.StartsWith("/d /c ", StringComparison.Ordinal), "same shape");
            // only the one known task name is ever allowed into the line
            Assert.Throws<ArgumentException>(delegate { PathSafety.DelayedRemoveArgs(@"C:\Program Files\SenseMonitor", @"C:\Program Files", @"C:\WINDOWS\system32", "x & del /s /q C:\\"); });
            // the old form is unchanged
            Assert.True(PathSafety.DelayedRemoveArgs(@"C:\Program Files\SenseMonitor", @"C:\Program Files", @"C:\WINDOWS\system32").IndexOf("schtasks", StringComparison.Ordinal) < 0, "no task in the old form");
        }

        [Test]
        public static void UninstallInstructions_AreStrict_FreshAndOneShape()
        {
            DateTime now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
            string nonce = ApplyInstructionsFile.NewNonce();
            byte[] ok = UninstallInstructionsFile.Build(true, now, nonce);
            UninstallInstructions i;
            string err;
            Assert.True(UninstallInstructionsFile.TryParse(ok, now.AddMinutes(2), out i, out err), "fresh: " + err);
            Assert.True(i.KeepData, "keep");
            Assert.True(UninstallInstructionsFile.TryParse(UninstallInstructionsFile.Build(false, now, nonce), now, out i, out err) && !i.KeepData, "erase");
            Assert.True(!UninstallInstructionsFile.TryParse(ok, now.AddMinutes(11), out i, out err), "old");
            Assert.True(!UninstallInstructionsFile.TryParse(ok, now.AddMinutes(-5), out i, out err), "from the future");
            Assert.True(!UninstallInstructionsFile.TryParse(UninstallInstructionsFile.Build(true, now, "short"), now, out i, out err), "bad nonce");
            Assert.True(!UninstallInstructionsFile.TryParse(new byte[0], now, out i, out err), "empty");
            Assert.True(!UninstallInstructionsFile.TryParse(System.Text.Encoding.UTF8.GetBytes("{\"nada\":1}"), now, out i, out err), "not ours");
            Assert.True(!UninstallInstructionsFile.TryParse(System.Text.Encoding.UTF8.GetBytes("lixo"), now, out i, out err), "garbage");
            Assert.True(!UninstallInstructionsFile.TryParse(new byte[5000], now, out i, out err), "too big");
        }

        [Test]
        public static void Breakaway_BuildsAQuotedLine_AndStartingNeverThrows()
        {
            Assert.Equal("\"C:\\Program Files\\SenseMonitor\\SenseMonitor.exe\" --desinstalar", JobBreakaway.CommandLine(@"C:\Program Files\SenseMonitor\SenseMonitor.exe", "--desinstalar"));
            Assert.Equal("\"C:\\x.exe\"", JobBreakaway.CommandLine(@"C:\x.exe", null));
            // a harmless real start: either it breaks away (true) or the job refuses (false + the Win32 error), never an exception
            int error;
            bool started = JobBreakaway.Start(Environment.SystemDirectory + "\\cmd.exe", "/d /c exit 0", out error);
            Assert.True(started || error != 0, "answers with a reason when it cannot");
        }

        // ---- m2 ----

        [Test]
        public static void InvariantCulture_OnlyForAnElevatedProcessOutsideTheInstall()
        {
            Assert.True(CulturePolicy.UseInvariantUi(true, false), "elevated, downloaded exe");
            Assert.True(!CulturePolicy.UseInvariantUi(true, true), "the installed copy has nothing planted");
            Assert.True(!CulturePolicy.UseInvariantUi(false, false), "not elevated: nothing to protect");
        }

        // ---- m3 ----

        [Test]
        public static void FanLayoutStore_CurrentIfSet_IsNullUntilThisProcessSetsIt()
        {
            FanLayoutStore.Reset();
            Assert.Null(FanLayoutStore.CurrentIfSet);
            FanLayout read = FanLayoutStore.Current; // loading from disk is not "set by this process"
            Assert.True(read != null, "Current still answers (both by default)");
            Assert.Null(FanLayoutStore.CurrentIfSet);
            FanLayoutStore.Set(FanLayout.CpuOnly);
            Assert.Equal(FanLayout.CpuOnly, FanLayoutStore.CurrentIfSet);
            FanLayoutStore.Unset();
            Assert.Null(FanLayoutStore.CurrentIfSet);
            FanLayoutStore.Reset();
        }

        // ---- m4 ----

        [Test]
        public static void HandBackBudget_NeverExceedsTheCallersTotal()
        {
            foreach (int total in new int[] { 600, 1500, 2500, 3000, 5000 })
            {
                int decision, write;
                FanSafety.SplitBudget(total, out decision, out write);
                Assert.Equal(total, decision + write);
                Assert.True(decision <= 1000 && decision >= 1, "decision is capped: " + decision);
                Assert.True(write >= total / 2, "the write keeps at least half: " + write);
            }
            int d2, w2;
            FanSafety.SplitBudget(2500, out d2, out w2);
            Assert.True(d2 + w2 <= 3000, "exit, crash and watchdog stay within 3 s");
        }
    }
}
