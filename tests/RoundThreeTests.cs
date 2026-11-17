using System;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Fix round 3 (re-review 2): the file fan marker widens its recorded mask, the data-dir variable cannot redirect the
    // markers of an elevated app, a refused state folder is not retried (and renamed) over and over, Eco and Turbo have
    // names in the telemetry texts. (The N4 / m10 / m11 recovery cases live in RecoveryRoundTwoTests.)
    public static class RoundThreeTests
    {
        [Test]
        public static void FanMarkerFile_WidensTheRecordedMask()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sm-fm-" + Guid.NewGuid().ToString("N"));
            try
            {
                FanMarker m = new FanMarker(Path.Combine(dir, "v.flag"));
                Assert.True(m.Set(FanLayout.CpuOnly, "x|y"), "raised for the cpu fan");
                Assert.Equal("cpu", m.Read().Layout);
                Assert.True(m.Set(FanLayout.CpuOnly, "x|y"), "same fan again");
                Assert.Equal("cpu", m.Read().Layout);
                Assert.True(m.Set(FanLayout.GpuOnly, "x|y"), "and the other one");
                Assert.Equal("both", m.Read().Layout);
                Assert.True(m.Set(FanLayout.CpuOnly, "x|y"), "never narrowed again");
                Assert.Equal("both", m.Read().Layout);
                Assert.Equal("x|y", m.Read().Fingerprint);
                Assert.True(m.Clear(), "cleared");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void DataDirVariable_IgnoredByAnElevatedApp_ExceptInATestHost()
        {
            Assert.True(!Safety.HonourDataDirVariable(true, false), "elevated app: the markers always use the verified folder");
            Assert.True(Safety.HonourDataDirVariable(true, true), "a test host may use it even elevated");
            Assert.True(Safety.HonourDataDirVariable(false, false), "not elevated: nothing to protect");
            Assert.True(Safety.HonourDataDirVariable(false, true), "not elevated");
        }

        private sealed class CountingFolder : IAdminFolder
        {
            public int Calls;
            public string Result;
            public string Prepare(string path, IJournal journal) { Calls++; return Result; }
        }

        [Test]
        public static void StateFolder_RefusalIsRemembered_ForTheProcess_SuccessIsVerifiedEveryTime()
        {
            IAdminFolder saved = StateFolder.Impl;
            CountingFolder f = new CountingFolder();
            try
            {
                StateFolder.Impl = f;
                string bad = @"C:\ProgramData\SenseMonitor\teste-recusa-" + Guid.NewGuid().ToString("N");
                Assert.Null(StateFolder.Prepare(bad, null));
                Assert.Null(StateFolder.Prepare(bad, null));
                Assert.Equal(1, f.Calls);
                string ok = @"C:\ProgramData\SenseMonitor\teste-ok-" + Guid.NewGuid().ToString("N");
                f.Result = ok;
                Assert.Equal(ok, StateFolder.Prepare(ok, null));
                Assert.Equal(ok, StateFolder.Prepare(ok, null));
                Assert.Equal(3, f.Calls);
            }
            finally
            {
                StateFolder.Impl = saved;
            }
        }

        // ---- the adapter over the updater's AdminFolder ----

        private sealed class ListJournal : IJournal
        {
            public readonly System.Collections.Generic.List<string> Lines = new System.Collections.Generic.List<string>();
            public void Write(string kind, string text) { Lines.Add(kind + " " + text); }
        }

        [Test]
        public static void Adapter_MissingFolder_NotElevated_IsRefused_NothingCreated()
        {
            // the test host is not elevated: the adapter verifies, it does not create
            string parent = Path.Combine(Path.GetTempPath(), "sm-ad-" + Guid.NewGuid().ToString("N"));
            string state = Path.Combine(parent, "estado");
            ListJournal j = new ListJournal();
            Assert.Null(new AdminFolderAdapter().Prepare(state, j));
            Assert.True(!Directory.Exists(parent), "nothing created");
            Assert.True(j.Lines.Exists(delegate(string l) { return l.Contains("recusado"); }), "the refusal is journaled");
        }

        [Test]
        public static void Adapter_FolderOwnedByAStandardUser_IsRefused_AndLeftAlone()
        {
            string parent = Path.Combine(Path.GetTempPath(), "sm-ad-" + Guid.NewGuid().ToString("N"));
            string state = Path.Combine(parent, "estado");
            try
            {
                Directory.CreateDirectory(state);
                File.WriteAllText(Path.Combine(state, "marca.flag"), "x");
                ListJournal j = new ListJournal();
                Assert.Null(new AdminFolderAdapter().Prepare(state, j));
                Assert.True(File.Exists(Path.Combine(state, "marca.flag")), "contents untouched");
                Assert.True(Directory.Exists(parent), "not renamed (verify only, not elevated)");
            }
            finally
            {
                try { Directory.Delete(parent, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void RemoveStateFolder_FolderThatDoesNotVerify_IsLeftAlone()
        {
            string state = Path.Combine(Path.GetTempPath(), "sm-rs-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(state);
                File.WriteAllText(Path.Combine(state, "marca.flag"), "x");
                string err = InstallSystem.RemoveStateFolder(state);
                Assert.True(err != null, "reported");
                Assert.True(File.Exists(Path.Combine(state, "marca.flag")), "nothing deleted from a folder that is not ours");
            }
            finally
            {
                try { Directory.Delete(state, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void RemoveStateFolder_MissingFolder_IsFine()
        {
            Assert.Null(InstallSystem.RemoveStateFolder(Path.Combine(Path.GetTempPath(), "sm-rs-nao-existe-" + Guid.NewGuid().ToString("N"))));
        }

        [Test]
        public static void PerfVerdict_ModeName_CoversEcoAndTurbo()
        {
            Assert.Equal("Econômico", PerfVerdict.ModeName(PerfMode.Economico));
            Assert.Equal("Turbo", PerfVerdict.ModeName(PerfMode.Turbo));
            Assert.Equal("Silencioso", PerfVerdict.ModeName(PerfMode.Silencioso));
            Assert.Equal("Desempenho", PerfVerdict.ModeName(PerfMode.Desempenho));
            Assert.Equal("Dia a dia", PerfVerdict.ModeName(PerfMode.DiaADia));
        }
    }
}
