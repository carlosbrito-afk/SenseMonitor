using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // In-memory world for the apply sequence: files hold a label ("old"/"new"), time only
    // moves when the applier sleeps, and the new app behaves as scripted.
    internal sealed class FakeApplyEnv : IUpdateApplyEnv
    {
        public Dictionary<string, string> Files = new Dictionary<string, string>();
        public List<string> Calls = new List<string>();
        public List<string> Logs = new List<string>();
        public List<string> Blocked = new List<string>();
        public bool BlockedPermanent;
        public long Now;
        public bool OldExits = true;
        public bool OthersGone = true;
        public bool StartOk = true;
        // After a start: running from UpMs, gone at DownMs (-1 = stays), flag at FlagMs (-1 = never).
        public int UpMs = 3000;
        public int DownMs = -1;
        public int FlagMs = 60000;
        public bool CloseWorks = true;
        public string FailReplaceInto;       // a Replace whose target is this path throws
        public string FailFirstReplaceInto;  // only the first Replace into this path throws
        public Action OnStart;
        public bool ThrowUnexpectedInReplace;
        public bool ReplaceFailsHalfway;   // the target is moved to the backup name, then the call throws (ReplaceFile mid-failure)
        public bool MoveFails;
        public List<string> Deferred = new List<string>();
        // A CLI tool (not the tray) that was already running: it must NOT count as the tray.
        public bool PreexistingTool;
        public int Starts;
        // An app instance that was already there before the helper ran (e.g. started by hand).
        public bool PreexistingApp;

        private long startedAt = -1;
        private bool closed;
        private bool flagCleared;

        public bool WaitForProcessExit(int pid, int timeoutMs) { Calls.Add("wait-old"); return OldExits; }
        public bool WaitNoOtherInstances(int timeoutMs) { Calls.Add("wait-others"); return OthersGone; }
        public bool Exists(string path) { return Files.ContainsKey(path); }
        public void Delete(string path) { Calls.Add("delete " + path); Files.Remove(path); }
        public void Replace(string source, string target, string backup)
        {
            Calls.Add("replace " + source + " -> " + target + " (backup " + backup + ")");
            if (ThrowUnexpectedInReplace) throw new InvalidOperationException("boom");
            if (ReplaceFailsHalfway)
            {
                Files[backup] = Files[target];
                Files.Remove(target);
                throw new System.IO.IOException("unable to move replacement");
            }
            if (target == FailReplaceInto) throw new System.IO.IOException("denied");
            if (target == FailFirstReplaceInto) { FailFirstReplaceInto = null; throw new System.IO.IOException("denied once"); }
            if (!Files.ContainsKey(source) || !Files.ContainsKey(target)) throw new System.IO.FileNotFoundException(source);
            Files[backup] = Files[target];
            Files[target] = Files[source];
            Files.Remove(source);
        }
        public void Move(string from, string to)
        {
            Calls.Add("move " + from + " -> " + to);
            if (MoveFails) throw new System.IO.IOException("move denied");
            if (!Files.ContainsKey(from)) throw new System.IO.FileNotFoundException(from);
            Files[to] = Files[from];
            Files.Remove(from);
        }
        // the tray only: a pre-existing tool (PreexistingTool) is deliberately not counted
        public bool IsTrayRunning() { return IsInstalledAppRunning(); }
        public void DeferApply(TimeSpan span, string reason) { Calls.Add("defer"); Deferred.Add(span.TotalHours + "h " + reason); }
        public void ClearOkFlag() { Calls.Add("clear-flag"); flagCleared = true; }
        public bool OkFlagMatches(string version)
        {
            if (!flagCleared || startedAt < 0 || FlagMs < 0) return false;
            return Now - startedAt >= FlagMs;
        }
        public bool StartInstalledApp()
        {
            Calls.Add("start");
            Starts++;
            if (OnStart != null) OnStart();
            if (!StartOk) return false;
            startedAt = Now;
            closed = false;
            return true;
        }
        public bool IsInstalledAppRunning()
        {
            if (startedAt < 0 || closed) return PreexistingApp;
            long age = Now - startedAt;
            if (age < UpMs) return PreexistingApp;
            return DownMs < 0 || age < DownMs || PreexistingApp;
        }
        public void RequestCloseInstalledApp() { Calls.Add("close"); if (CloseWorks) closed = true; }
        public bool WaitInstalledAppExit(int timeoutMs) { Calls.Add("wait-close"); return closed; }
        public void BlockVersion(string version, string reason, bool permanent) { Calls.Add("block " + version); Blocked.Add(version); BlockedPermanent = permanent; }
        public void Log(string line) { Logs.Add(line); }
        public void Sleep(int ms) { Now += ms; }
        public long NowMs() { return Now; }
    }

    public static class UpdateApplierTests
    {
        private const string Cur = @"P\SenseMonitor.exe";
        private const string Ant = @"P\SenseMonitor.anterior.exe";
        private const string Nov = @"P\atualizacao\SenseMonitor.novo.exe";
        private const string Fal = @"P\atualizacao\SenseMonitor.falhou.exe";

        private static ApplyPaths Paths()
        {
            ApplyPaths p = new ApplyPaths();
            p.Current = Cur; p.Anterior = Ant; p.Novo = Nov; p.Falhou = Fal;
            return p;
        }

        private static FakeApplyEnv Env()
        {
            FakeApplyEnv e = new FakeApplyEnv();
            e.Files[Cur] = "old";
            e.Files[Nov] = "new";
            return e;
        }

        private static ApplyResult Run(FakeApplyEnv e)
        {
            return new UpdateApplier(Paths(), new ApplyTimings()).Run(e, 1234, "0.2.1");
        }

        private static int Index(FakeApplyEnv e, string call)
        {
            return e.Calls.IndexOf(call);
        }

        private static string ReplaceCall(string src, string target, string backup)
        {
            return "replace " + src + " -> " + target + " (backup " + backup + ")";
        }

        [Test]
        public static void Success_SwapsStartsAndConfirms()
        {
            FakeApplyEnv e = Env();
            Assert.Equal(ApplyResult.Applied, Run(e));
            Assert.Equal("new", e.Files[Cur]);
            Assert.Equal("old", e.Files[Ant]);
            Assert.True(!e.Files.ContainsKey(Nov), "staged file moved in");
            Assert.Equal(1, e.Starts);
            Assert.Equal(0, e.Blocked.Count);
        }

        [Test]
        public static void Success_OrderIsWaitWaitOthersSwapClearFlagStart()
        {
            FakeApplyEnv e = Env();
            Run(e);
            int wait = Index(e, "wait-old");
            int others = Index(e, "wait-others");
            int swap = Index(e, ReplaceCall(Nov, Cur, Ant));
            int clear = Index(e, "clear-flag");
            int start = Index(e, "start");
            Assert.True(wait == 0 && wait < others && others < swap && swap < clear && clear < start, "order: " + string.Join(" | ", e.Calls.ToArray()));
        }

        [Test]
        public static void TheSwapIsASingleAtomicReplace_NotTwoSeparateMoves()
        {
            FakeApplyEnv e = Env();
            Run(e);
            int replaces = 0;
            foreach (string c in e.Calls) if (c.StartsWith("replace ")) replaces++;
            Assert.Equal(1, replaces);
        }

        [Test]
        public static void StaleAnterior_IsReplacedByTheBackupStep()
        {
            FakeApplyEnv e = Env();
            e.Files[Ant] = "ancient";
            Assert.Equal(ApplyResult.Applied, Run(e));
            Assert.Equal("old", e.Files[Ant]);
        }

        [Test]
        public static void OldProcessStillRunning_TouchesNothing_AndStartsNoSecondApp()
        {
            FakeApplyEnv e = Env();
            e.OldExits = false;
            e.PreexistingApp = true;   // the old app is still there (hung or closing)
            Assert.Equal(ApplyResult.OldStillRunning, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal("new", e.Files[Nov]);
            Assert.Equal(0, e.Starts);
        }

        [Test]
        public static void OldProcessReportedAlive_ButNoInstanceFound_StartsTheAppAgain()
        {
            FakeApplyEnv e = Env();
            e.OldExits = false;
            Assert.Equal(ApplyResult.OldStillRunning, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal(1, e.Starts);
        }

        [Test]
        public static void AnotherInstanceRunning_TouchesNothing()
        {
            FakeApplyEnv e = Env();
            e.OthersGone = false;
            e.PreexistingApp = true;
            Assert.Equal(ApplyResult.OtherInstanceRunning, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal("new", e.Files[Nov]);
            Assert.Equal(0, e.Starts);
            Assert.True(!e.Calls.Exists(delegate(string c) { return c.StartsWith("replace"); }), "no swap");
        }

        [Test]
        public static void MissingStagedFile_TouchesNothing_AndRestartsTheApp()
        {
            FakeApplyEnv e = Env();
            e.Files.Remove(Nov);
            Assert.Equal(ApplyResult.NotApplied, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal(1, e.Starts);
        }

        [Test]
        public static void MissingInstalledExe_TouchesNothing_AndTriesToRestart()
        {
            FakeApplyEnv e = Env();
            e.Files.Remove(Cur);
            Assert.Equal(ApplyResult.NotApplied, Run(e));
            Assert.Equal("new", e.Files[Nov]);
            Assert.Equal(1, e.Starts);
        }

        [Test]
        public static void ReplaceFails_NothingChanged_AppRestarted()
        {
            FakeApplyEnv e = Env();
            e.FailReplaceInto = Cur;
            Assert.Equal(ApplyResult.NotApplied, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal("new", e.Files[Nov]);
            Assert.True(!e.Files.ContainsKey(Ant), "no backup created");
            Assert.Equal(1, e.Starts);
            Assert.Equal(0, e.Blocked.Count);
        }

        [Test]
        public static void UnexpectedExceptionBeforeTheSwap_StillRestartsTheApp()
        {
            FakeApplyEnv e = Env();
            e.ThrowUnexpectedInReplace = true;
            ApplyResult r = Run(e);
            Assert.Equal(ApplyResult.NotApplied, r); // Replace failures of any type end as "not applied"
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal(1, e.Starts);
        }

        [Test]
        public static void StartFails_RollsBack_ButOnlyPausesTheVersion()
        {
            FakeApplyEnv e = Env();
            e.StartOk = false;
            Assert.Equal(ApplyResult.RolledBack, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal("new", e.Files[Fal]);
            Assert.Equal(1, e.Blocked.Count);
            Assert.Equal("0.2.1", e.Blocked[0]);
            Assert.True(!e.BlockedPermanent, "a failure to start a process is not the release's fault: 24 h pause only");
        }

        [Test]
        public static void NewAppCrashesAtStart_RollsBackAndBlocksForGood()
        {
            FakeApplyEnv e = Env();
            e.UpMs = 2000;
            e.DownMs = 8000;   // seen running, then gone, no flag
            e.FlagMs = -1;
            Assert.Equal(ApplyResult.RolledBack, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal("new", e.Files[Fal]);
            Assert.True(!e.Files.ContainsKey(Ant), "anterior consumed");
            Assert.Equal(2, e.Starts); // new, then the restored old one
            Assert.Equal("0.2.1", e.Blocked[0]);
            Assert.True(e.BlockedPermanent, "a release that ran and failed is blocked for good");
            Assert.True(!e.Calls.Contains("close"), "nothing to close: it already died");
        }

        [Test]
        public static void RollbackIsAnAtomicReplaceToo()
        {
            FakeApplyEnv e = Env();
            e.UpMs = 2000;
            e.DownMs = 8000;
            e.FlagMs = -1;
            Run(e);
            Assert.True(e.Calls.Contains(ReplaceCall(Ant, Cur, Fal)), "anterior -> current, failed one kept as falhou");
        }

        [Test]
        public static void NewAppNeverStarts_RollsBackAfterGrace()
        {
            FakeApplyEnv e = Env();
            e.UpMs = int.MaxValue;
            e.FlagMs = -1;
            Assert.Equal(ApplyResult.RolledBack, Run(e));
            Assert.Equal("old", e.Files[Cur]);
            Assert.True(e.Now >= 30000 && e.Now < 60000, "gave up after the 30 s grace, not the full 3 min (now=" + e.Now + ")");
        }

        [Test]
        public static void NewAppRunsButNeverConfirms_ClosesGracefullyThenRollsBack()
        {
            FakeApplyEnv e = Env();
            e.FlagMs = -1;   // runs forever, never writes the flag
            Assert.Equal(ApplyResult.RolledBack, Run(e));
            Assert.True(e.Now >= 180000, "waited the full 3 min");
            Assert.True(Index(e, "close") >= 0 && Index(e, "close") < Index(e, ReplaceCall(Ant, Cur, Fal)), "closed before swapping back");
            Assert.Equal("old", e.Files[Cur]);
            Assert.Equal("0.2.1", e.Blocked[0]);
        }

        [Test]
        public static void NewAppCannotBeClosed_IsNeverForced_AndStaysBlocked()
        {
            FakeApplyEnv e = Env();
            e.FlagMs = -1;
            e.CloseWorks = false;
            Assert.Equal(ApplyResult.RollbackFailed, Run(e));
            Assert.Equal("new", e.Files[Cur]);   // untouched: the running exe is not pulled from under it
            Assert.Equal("old", e.Files[Ant]);   // and the old one is kept for a manual restore
            Assert.Equal("0.2.1", e.Blocked[0]);
            Assert.True(!e.Calls.Exists(delegate(string c) { return c.StartsWith("kill"); }), "never a kill");
        }

        [Test]
        public static void RollbackWithoutAnterior_Fails()
        {
            FakeApplyEnv e = Env();
            e.StartOk = false;
            e.OnStart = delegate { e.Files.Remove(Ant); };
            Assert.Equal(ApplyResult.RollbackFailed, Run(e));
        }

        [Test]
        public static void RollbackReplaceFails_IsReportedLoudly()
        {
            FakeApplyEnv e = Env();
            e.UpMs = 2000;
            e.DownMs = 8000;
            e.FlagMs = -1;
            e.FailReplaceInto = null;
            e.FailFirstReplaceInto = null;
            // fail the second replace (the rollback): the first goes through
            e.OnStart = delegate { e.FailReplaceInto = Cur; };
            Assert.Equal(ApplyResult.RollbackFailed, Run(e));
            Assert.True(e.Logs.Exists(delegate(string l) { return l.Contains("ERRO GRAVE"); }), "loud log");
            Assert.Equal("new", e.Files[Cur]);
            Assert.Equal("old", e.Files[Ant]);
        }

        [Test]
        public static void StaleFlag_IsNotAccepted_FlagClearedBeforeStart()
        {
            FakeApplyEnv e = Env();
            e.FlagMs = 0;
            Assert.Equal(ApplyResult.Applied, Run(e));
            Assert.True(Index(e, "clear-flag") >= 0 && Index(e, "clear-flag") < Index(e, "start"), "flag cleared before start");
        }

        [Test]
        public static void Success_WhenFlagArrivesJustAtTheLimit()
        {
            FakeApplyEnv e = Env();
            e.FlagMs = 179000;
            Assert.Equal(ApplyResult.Applied, Run(e));
        }
    }
}
