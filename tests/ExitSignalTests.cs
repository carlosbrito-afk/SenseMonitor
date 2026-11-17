using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Forja.Core;

namespace Forja.Tests
{
    // The named exit event: who may signal it, the app's listener, the order every closer follows, the --sair switch.
    public static class ExitSignalTests
    {
        // ---- who may signal ----

        [Test]
        public static void Descriptor_AllowsOnlyAdministratorsAndSystem()
        {
            CommonSecurityDescriptor sd = new CommonSecurityDescriptor(false, false, ExitSignal.Sddl);
            Assert.True(sd.DiscretionaryAcl != null, "has a DACL");
            Assert.True((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "does not inherit anything");
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            List<SecurityIdentifier> seen = new List<SecurityIdentifier>();
            foreach (GenericAce ace in sd.DiscretionaryAcl)
            {
                CommonAce c = ace as CommonAce;
                Assert.True(c != null && c.AceType == AceType.AccessAllowed, "only plain allow entries");
                seen.Add(c.SecurityIdentifier);
            }
            Assert.Equal(2, seen.Count);
            Assert.True(seen.Contains(admins) && seen.Contains(system), "Administrators and SYSTEM");
        }

        [Test]
        public static void Descriptor_NeverNamesEveryoneUsersOrAuthenticatedUsers()
        {
            Assert.True(!ExitSignal.Sddl.Contains(";WD)") && !ExitSignal.Sddl.Contains(";BU)") && !ExitSignal.Sddl.Contains(";AU)")
                && !ExitSignal.Sddl.Contains(";IU)"), ExitSignal.Sddl);
        }

        [Test]
        public static void EventName_IsInTheGlobalNamespace()
        {
            Assert.Equal(@"Global\SenseMonitor.Sair", ExitSignal.EventName);
        }

        // ---- the --sair switch ----

        [Test]
        public static void Switch_OnlyExactlyOneArgumentIsAnExitRequest()
        {
            Assert.True(StartupRouter.IsExitRequest(new string[] { "SenseMonitor.exe", "--sair" }), "plain");
            Assert.True(StartupRouter.IsExitRequest(new string[] { "SenseMonitor.exe", "--SAIR" }), "case");
            Assert.True(!StartupRouter.IsExitRequest(new string[] { "SenseMonitor.exe" }), "no argument");
            Assert.True(!StartupRouter.IsExitRequest(new string[] { "SenseMonitor.exe", "--sair", "--instalar" }), "never mixed with another switch");
            Assert.True(!StartupRouter.IsExitRequest(new string[] { "SenseMonitor.exe", "--sair-agora" }), "exact match");
            Assert.True(!StartupRouter.IsExitRequest(null), "null");
        }

        // ---- the app's listener ----

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                Thread.Sleep(5);
            }
            return cond();
        }

        [Test]
        public static void Listener_CallsBackOncePerSignal()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            int hits = 0;
            using (ExitSignalListener l = new ExitSignalListener(ev, delegate { Interlocked.Increment(ref hits); }, null))
            {
                l.Start();
                Thread.Sleep(30);
                Assert.Equal(0, hits);
                ev.Set();
                Assert.True(WaitFor(delegate { return hits == 1; }, 2000), "first signal");
                Thread.Sleep(50);
                Assert.Equal(1, hits);
                ev.Set();
                Assert.True(WaitFor(delegate { return hits == 2; }, 2000), "second signal");
            }
            ev.Close();
        }

        [Test]
        public static void Listener_DisposeStopsItWithoutACallback()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            int hits = 0;
            ExitSignalListener l = new ExitSignalListener(ev, delegate { Interlocked.Increment(ref hits); }, null);
            l.Start();
            l.Dispose();
            ev.Set();
            Thread.Sleep(80);
            Assert.Equal(0, hits);
            l.Dispose(); // twice is fine
            ev.Close();
        }

        [Test]
        public static void Listener_ACallbackThatThrowsDoesNotKillTheWait()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            int hits = 0;
            int errors = 0;
            using (ExitSignalListener l = new ExitSignalListener(ev, delegate { Interlocked.Increment(ref hits); throw new InvalidOperationException("x"); },
                delegate(Exception e) { Interlocked.Increment(ref errors); }))
            {
                l.Start();
                ev.Set();
                Assert.True(WaitFor(delegate { return hits == 1; }, 2000), "first");
                ev.Set();
                Assert.True(WaitFor(delegate { return hits == 2; }, 2000), "still listening after the error");
                Assert.True(WaitFor(delegate { return Volatile.Read(ref errors) >= 2; }, 2000), "both errors reported");
            }
            ev.Close();
        }

        // ---- the real event (a throw-away name; the access list of the test is the current user's, not the production one) ----

        private static string TestName()
        {
            return @"Local\SenseMonitor.Sair.test." + Guid.NewGuid().ToString("N");
        }

        private static string Me()
        {
            return "D:P(A;;GA;;;" + WindowsIdentity.GetCurrent().User.Value + ")";
        }

        [Test]
        public static void RealEvent_SignalReachesTheListener()
        {
            string name = TestName();
            string err;
            EventWaitHandle ev = Forja.Platform.ExitEvent.Create(name, Me(), out err);
            Assert.True(ev != null, err);
            int hits = 0;
            using (ExitSignalListener l = new ExitSignalListener(ev, delegate { Interlocked.Increment(ref hits); }, null))
            {
                l.Start();
                Assert.True(Forja.Platform.ExitEvent.Signal(name), "the closer found it");
                Assert.True(WaitFor(delegate { return hits == 1; }, 3000), "the app side heard it");
            }
            ev.Close();
        }

        [Test]
        public static void RealEvent_NoEvent_SignalSaysSoAndNothingThrows()
        {
            Assert.True(!Forja.Platform.ExitEvent.Signal(TestName()), "no app, no event: false");
        }

        [Test]
        public static void RealEvent_ANameAnotherProcessAlreadyMade_IsNotTrusted()
        {
            string name = TestName();
            string err;
            EventWaitHandle first = Forja.Platform.ExitEvent.Create(name, Me(), out err);
            Assert.True(first != null, err);
            string err2;
            EventWaitHandle second = Forja.Platform.ExitEvent.Create(name, Me(), out err2);
            Assert.True(second == null, "its access list is unknown: not used");
            Assert.True(err2 != null && err2.Contains("já existe"), err2);
            first.Close();
        }

        // ---- the order every closer follows ----

        private sealed class FakeEnv : ICloseEnv
        {
            public long Now;
            public bool Running = true;
            public bool SentinelRunning;
            public bool WaitForSentinel;
            public bool EventExists = true;
            // when (ms) the app leaves after the event / after the window close; -1 = never
            public long LeavesAfterEventMs = -1;
            public long LeavesAfterWindowCloseMs = -1;
            public long EventAt = -1;
            public long WindowCloseAt = -1;
            public readonly List<string> Calls = new List<string>();

            public bool AppRunning() { Update(); return Running; }
            public bool Gone() { Update(); return !Running && !(WaitForSentinel && SentinelRunning); }
            public bool SignalExit()
            {
                Calls.Add("event");
                if (!EventExists) return false;
                EventAt = Now;
                return true;
            }
            public void RequestWindowClose() { Calls.Add("taskkill"); if (WindowCloseAt < 0) WindowCloseAt = Now; }
            public void Sleep(int ms) { Now += ms; }
            public long NowMs() { return Now; }

            private void Update()
            {
                if (EventAt >= 0 && LeavesAfterEventMs >= 0 && Now - EventAt >= LeavesAfterEventMs) Running = false;
                if (WindowCloseAt >= 0 && LeavesAfterWindowCloseMs >= 0 && Now - WindowCloseAt >= LeavesAfterWindowCloseMs) Running = false;
            }
        }

        [Test]
        public static void Close_TheEventComesFirst_AndWhenItWorksNoTaskkillIsSent()
        {
            FakeEnv env = new FakeEnv();
            env.LeavesAfterEventMs = 3000;
            Assert.True(GracefulClose.Run(env, 30000), "gone");
            Assert.Equal("event", string.Join("|", env.Calls.ToArray()));
        }

        [Test]
        public static void Close_NoEvent_FallsBackToTaskkillAtOnce()
        {
            FakeEnv env = new FakeEnv();
            env.EventExists = false; // an older app, or the event is not there
            env.LeavesAfterWindowCloseMs = 2000;
            Assert.True(GracefulClose.Run(env, 30000), "gone");
            Assert.Equal("event|taskkill", string.Join("|", env.Calls.ToArray()));
            Assert.True(env.WindowCloseAt <= GracefulClose.PollMs, "no grace wait when nobody can be listening: " + env.WindowCloseAt);
        }

        [Test]
        public static void Close_EventIgnored_TaskkillFollowsAfterTheGrace_NeverBefore()
        {
            FakeEnv env = new FakeEnv();
            env.LeavesAfterWindowCloseMs = 1000; // the event does nothing (stale), the window close works
            Assert.True(GracefulClose.Run(env, 30000), "gone");
            Assert.Equal("event|taskkill", string.Join("|", env.Calls.ToArray()));
            Assert.True(env.WindowCloseAt >= ExitSignal.SignalGraceMs, "waited for the event first: " + env.WindowCloseAt);
            Assert.True(env.WindowCloseAt < ExitSignal.SignalGraceMs + 2 * GracefulClose.PollMs, "then no longer than a poll: " + env.WindowCloseAt);
        }

        [Test]
        public static void Close_NothingWorks_GivesUpAtTheLimitWithoutForcing()
        {
            FakeEnv env = new FakeEnv();
            Assert.True(!GracefulClose.Run(env, 30000), "still running");
            Assert.True(env.Now >= 30000 && env.Now < 30000 + 2 * GracefulClose.PollMs, "waited the limit: " + env.Now);
            Assert.Equal("event|taskkill", string.Join("|", env.Calls.ToArray()));
        }

        [Test]
        public static void Close_NothingRunning_SendsNothing_OnlyWaitsForTheSentinelWhenAsked()
        {
            FakeEnv env = new FakeEnv();
            env.Running = false;
            Assert.True(GracefulClose.Run(env, 30000), "already gone");
            Assert.Equal(0, env.Calls.Count);

            FakeEnv s = new FakeEnv();
            s.Running = false;
            s.SentinelRunning = true;
            s.WaitForSentinel = true;
            Assert.True(!GracefulClose.Run(s, 2000), "the sentinel never leaves");
            Assert.Equal(0, s.Calls.Count);
        }

        [Test]
        public static void Close_TheAppGoneBeforeTheGraceEnds_NoWindowCloseIsSent()
        {
            FakeEnv env = new FakeEnv();
            env.LeavesAfterEventMs = ExitSignal.SignalGraceMs - 1000;
            Assert.True(GracefulClose.Run(env, 30000), "gone");
            Assert.True(!env.Calls.Contains("taskkill"), "no taskkill");
        }

        [Test]
        public static void Close_ASignalThatThrows_CountsAsNoEvent()
        {
            ThrowingEnv env = new ThrowingEnv();
            env.LeavesAfterWindowCloseMs = 500;
            Assert.True(GracefulClose.Run(env, 30000), "still closes");
            Assert.True(env.Asked, "fell back to the window close");
        }

        private sealed class ThrowingEnv : ICloseEnv
        {
            public long Now;
            public bool Asked;
            public long LeavesAfterWindowCloseMs;
            private long closeAt = -1;
            public bool AppRunning() { return !Gone(); }
            public bool Gone() { return closeAt >= 0 && Now - closeAt >= LeavesAfterWindowCloseMs; }
            public bool SignalExit() { throw new UnauthorizedAccessException(); }
            public void RequestWindowClose() { Asked = true; closeAt = Now; }
            public void Sleep(int ms) { Now += ms; }
            public long NowMs() { return Now; }
        }
    }
}
