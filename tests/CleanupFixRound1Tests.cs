using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Fix round 1 after the cleanup review: the action-only clean button (I2), safer defaults
    // (M1, M2, M3), cancellation and counting (M4, M5), copy (M8), a link swapped in during a
    // run (M9), the event type mapping (M10) and an empty Windows folder (M11).
    public static class CleanupFixRound1Tests
    {
        private static CleanupTarget Target(CleanupTargetId id, CleanupKind kind, bool selected, bool available, int files)
        {
            CleanupTarget t = new CleanupTarget();
            t.Id = id;
            t.Kind = kind;
            t.Selected = selected;
            t.Available = available;
            t.Files = files;
            return t;
        }

        // ---- I2: the clean button ----

        [Test]
        public static void CanClean_OnlyActionsSelected_IsTrue()
        {
            CleanupPlan plan = new CleanupPlan();
            plan.Targets.Add(Target(CleanupTargetId.UserTemp, CleanupKind.Folders, false, true, 5));
            plan.Targets.Add(Target(CleanupTargetId.DnsCache, CleanupKind.Action, true, true, 0));
            Assert.True(plan.CanClean, "DNS flush alone can be run");
            Assert.Equal(1, plan.SelectedActions);
            Assert.Equal(0, plan.SelectedFolders);
        }

        [Test]
        public static void CanClean_FolderSelectedButEmpty_IsFalse_AndFalseWhenNothingOrCancelled()
        {
            CleanupPlan plan = new CleanupPlan();
            plan.Targets.Add(Target(CleanupTargetId.UserTemp, CleanupKind.Folders, true, true, 0));
            Assert.True(!plan.CanClean, "a selected folder with nothing to delete is not a reason to enable the button");
            plan.Targets.Add(Target(CleanupTargetId.WindowsTemp, CleanupKind.Folders, true, true, 3));
            Assert.True(plan.CanClean, "a folder with files");
            plan.Cancelled = true;
            Assert.True(!plan.CanClean, "an analysis that was cancelled never allows a clean");
            Assert.True(!new CleanupPlan().CanClean, "empty plan");
        }

        [Test]
        public static void CanClean_UnavailableOrUnselectedActionsDoNotCount()
        {
            CleanupPlan plan = new CleanupPlan();
            plan.Targets.Add(Target(CleanupTargetId.DnsCache, CleanupKind.Action, true, false, 0));
            plan.Targets.Add(Target(CleanupTargetId.EventLogs, CleanupKind.Action, false, true, 0));
            Assert.True(!plan.CanClean, "unavailable or unticked actions");
            Assert.Equal(0, plan.SelectedActions);
        }

        [Test]
        public static void CanClean_OnAFreshAnalysisWhereEveryFolderIsEmpty_DnsIsStillRunnable()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CleanupPlan plan = new Cleaner(sb.Env(), null).Analyze(null, CancellationToken.None);
                Assert.Equal(0, plan.SelectedFiles);
                Assert.True(plan.Find(CleanupTargetId.DnsCache).Selected, "DNS is ticked by default");
                Assert.True(plan.CanClean, "so the button works on a PC with nothing to delete");
            }
        }

        [Test]
        public static void SelectionSummary_CountsFoldersAndActionsApart()
        {
            CleanupPlan plan = new CleanupPlan();
            CleanupTarget a = Target(CleanupTargetId.UserTemp, CleanupKind.Folders, true, true, 2);
            a.Bytes = 1048576;
            plan.Targets.Add(a);
            plan.Targets.Add(Target(CleanupTargetId.DnsCache, CleanupKind.Action, true, true, 0));
            Assert.Equal("Selecionado: 1,0 MB em 1 local e 1 ação (estimativa; o que estiver em uso fica).", plan.SelectionSummary());
            a.Selected = false;
            Assert.Equal("Selecionado: 1 ação.", plan.SelectionSummary());
            plan.Targets[1].Selected = false;
            Assert.Equal("Selecionado: nada.", plan.SelectionSummary());
        }

        // ---- M1: defaults fail closed ----

        [Test]
        public static void Defaults_AdminIsFalseUntilTheCallerSaysOtherwise()
        {
            Assert.True(!new CleanupEnvironment().IsAdmin, "no admin unless set");
        }

        [Test]
        public static void ProcessProbeMissing_ForARootWithOwners_CountsAsRunning()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string cache = sb.Old(Path.Combine(sb.LocalApp, @"Google\Chrome\User Data\Default\Cache"), "c", 100);
                CleanupEnvironment env = sb.Env();
                env.ProcessRunning = null;
                Cleaner c = new Cleaner(env, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Assert.True(!plan.Find(CleanupTargetId.BrowserCaches).Available, "without a probe the browser is assumed open");
                foreach (CleanupTarget t in plan.Targets) t.Selected = true;
                c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(cache), "untouched");
            }
        }

        [Test]
        public static void ProcessProbeMissing_DoesNotBlockTargetsWithoutOwners()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string f = sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "x", 10);
                CleanupEnvironment env = sb.Env();
                env.ProcessRunning = null;
                Cleaner c = new Cleaner(env, null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                Assert.True(plan.Find(CleanupTargetId.ShaderCaches).Available, "shader caches have no owner process");
            }
        }

        // ---- M2 / M3 ----

        [Test]
        public static void EmptyPatternList_MatchesNothing_NullMatchesEverything()
        {
            Assert.True(!CleanupNames.MatchesAny("a.log", new string[0]), "an empty list is a mistake, not 'everything'");
            Assert.True(CleanupNames.MatchesAny("a.log", null), "null still means no filter");
        }

        [Test]
        public static void RootWithAnEmptyPatternArray_DeletesNothing()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string f = sb.Old(Path.Combine(sb.LocalApp, "D3DSCache"), "x", 10);
                Cleaner c = new Cleaner(sb.Env(), null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                CleanupTarget t = plan.Find(CleanupTargetId.ShaderCaches);
                t.Roots[0].Patterns = new string[0];
                foreach (CleanupTarget x in plan.Targets) x.Selected = x == t;
                CleanupResult r = c.Clean(plan, null, CancellationToken.None);
                Assert.True(File.Exists(f), "nothing matches an empty pattern list");
                Assert.Equal(0, r.DeletedFiles);
            }
        }

        [Test]
        public static void SafeName_MustStartWithALetterOrDigit()
        {
            Assert.True(!EventLogFilter.IsSafeName("-x"), "could be read as an option");
            Assert.True(!EventLogFilter.IsSafeName("/x"), "could be read as an option");
            Assert.True(!EventLogFilter.IsSafeName(" x"), "leading space");
            Assert.True(!EventLogFilter.IsSafeName("(x)"), "leading parenthesis");
            Assert.True(EventLogFilter.IsSafeName("Microsoft-Windows-NCSI/Operational"), "real name");
            Assert.True(EventLogFilter.IsSafeName("7zip/Operational"), "digit first");
            Assert.Null(CleanupCommands.ClearEventLog(@"C:\Windows\System32", "-Microsoft-Windows-NCSI/Operational"));
        }

        // ---- M4: cancellation and counting ----

        [Test]
        public static void EventLogs_CancelledBeforeTheFirstChannel_IsNotAFailure()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Channel("Microsoft-Windows-NCSI/Operational", EventChannelKind.Operational, 3);
                sb.Channel("Microsoft-Windows-Audio/Operational", EventChannelKind.Operational, 3);
                Cleaner c = new Cleaner(sb.Env(), null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                foreach (CleanupTarget t in plan.Targets) t.Selected = t.Id == CleanupTargetId.EventLogs;
                CancellationTokenSource cts = new CancellationTokenSource();
                CleanupResult r = c.Clean(plan, delegate { cts.Cancel(); }, cts.Token);
                Assert.True(r.Cancelled, "cancelled");
                Assert.Equal(0, sb.CommandsRun.Count);
                Assert.Equal(0, r.ActionsFailed);
                Assert.Equal(0, r.Notes.Count);
            }
        }

        [Test]
        public static void ChangeLog_CountsFoldersAndActionsSeparately()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
                Cleaner c = new Cleaner(sb.Env(), log);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                foreach (CleanupTarget t in plan.Targets) t.Selected = t.Id == CleanupTargetId.DnsCache;
                c.Clean(plan, null, CancellationToken.None);
                string d = log.List()[0].Description;
                Assert.True(!d.Contains(" local"), "an action is not a 'local': " + d);
                Assert.True(d.Contains("1 ação executada"), d);
            }
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                sb.Old(sb.UserTemp, "a.tmp", 10);
                ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
                Cleaner c = new Cleaner(sb.Env(), log);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                foreach (CleanupTarget t in plan.Targets) t.Selected = t.Id == CleanupTargetId.UserTemp || t.Id == CleanupTargetId.DnsCache;
                c.Clean(plan, null, CancellationToken.None);
                string d = log.List()[0].Description;
                Assert.True(d.Contains("em 1 local"), "one folder target: " + d);
                Assert.True(d.Contains("1 ação executada"), d);
            }
        }

        // ---- M5: the channel list read can be cancelled ----

        [Test]
        public static void EventChannelRead_ReceivesTheCancellationToken()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                CancellationToken seen = CancellationToken.None;
                CleanupEnvironment env = sb.Env();
                env.EventChannels = delegate(CancellationToken t) { seen = t; return new List<EventChannelInfo>(); };
                new Cleaner(env, null).Analyze(null, cts.Token);
                Assert.True(seen == cts.Token, "the same token reaches the reader");
            }
        }

        [Test]
        public static void EventChannelRead_CancelledDuringTheRead_CancelsTheAnalysisNotTheTarget()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                CleanupEnvironment env = sb.Env();
                env.EventChannels = delegate(CancellationToken t)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(t);
                };
                CleanupPlan plan = new Cleaner(env, null).Analyze(null, cts.Token);
                Assert.True(plan.Cancelled, "analysis says it was cancelled");
                CleanupTarget ev = plan.Find(CleanupTargetId.EventLogs);
                Assert.True(ev.Note == null || !ev.Note.Contains("Não deu para ler"), "not reported as a read failure");
            }
        }

        // ---- M8: copy ----

        [Test]
        public static void AppCaches_CopyWarnsThatSteamWebPagesMayAskToSignInAgain()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string d = CleanupPlan.Create(sb.Env()).Find(CleanupTargetId.AppCaches).Description;
                Assert.True(d.Contains("login"), d);
            }
        }

        [Test]
        public static void EventLogs_CopyDescribesTheFixedListNotAGeneralClear()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string d = CleanupPlan.Create(sb.Env()).Find(CleanupTargetId.EventLogs).Description;
                Assert.True(d.Contains("lista fixa"), "says it is a fixed list: " + d);
                Assert.True(d.Contains("System") && d.Contains("Application") && d.Contains("de propósito"), d);
            }
        }

        // ---- M9: a link swapped in after the check ----

        [Test]
        public static void Sweep_RootSwappedForAJunctionAfterTheCheck_IsNotFollowed()
        {
            using (CleanupSandbox sb = new CleanupSandbox())
            {
                string outside = Path.Combine(sb.Root, "outside");
                string precious = sb.Old(outside, "precious.doc", 123);
                string cache = Path.Combine(sb.LocalApp, "D3DSCache");
                sb.Old(cache, "x", 10);
                Cleaner c = new Cleaner(sb.Env(), null);
                CleanupPlan plan = c.Analyze(null, CancellationToken.None);
                foreach (CleanupTarget t in plan.Targets) t.Selected = t.Id == CleanupTargetId.ShaderCaches;
                bool swapped = false;
                CleanupResult r = c.Clean(plan, delegate(CleanupProgress p)
                {
                    if (swapped) return;
                    swapped = true;
                    // another program replaces the folder with a link after the safety check
                    Directory.Delete(cache, true);
                    Assert.True(sb.MakeJunction(cache, outside), "mklink /J must work here");
                }, CancellationToken.None);
                Assert.True(swapped, "the swap happened");
                Assert.True(File.Exists(precious), "nothing behind the new junction is touched");
            }
        }

        // ---- M10 / M11 ----

        [Test]
        public static void EventTypeMapping_CoversEveryKind()
        {
            Assert.True(CleanupProbes.Kind(EventLogType.Operational) == EventChannelKind.Operational, "operational");
            Assert.True(CleanupProbes.Kind(EventLogType.Analytical) == EventChannelKind.Analytic, "analytic");
            Assert.True(CleanupProbes.Kind(EventLogType.Debug) == EventChannelKind.Debug, "debug");
            Assert.True(CleanupProbes.Kind(EventLogType.Administrative) == EventChannelKind.Administrative, "administrative");
        }

        [Test]
        public static void JoinOrNull_RefusesAnEmptyBase_SoNoRelativePathIsBuilt()
        {
            Assert.Null(Cleaner.JoinOrNull("", "Temp"));
            Assert.Null(Cleaner.JoinOrNull(null, "Temp"));
            Assert.Equal(@"C:\Windows\Temp", Cleaner.JoinOrNull(@"C:\Windows", "Temp"));
        }
    }
}
