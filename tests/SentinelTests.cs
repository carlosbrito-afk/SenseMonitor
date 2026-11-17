using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Fake world for SentinelLoop: a clock that only moves when the loop sleeps.
    public class FakeSentinelHost : ISentinelHost
    {
        public int Now;
        public Func<int, bool> Alive = delegate { return true; };
        public MemFlag Marker = new MemFlag();
        public long? Age;
        public Func<int, bool> WriteOk = delegate { return true; };
        public Action<int> OnTime;
        public Action AfterWrite;
        public int Writes;
        public int Bumps;
        public int AliveThrows;
        public List<string> Logs = new List<string>();
        public int WriteTimes;
        public List<int> WriteAt = new List<int>();
        public int Limit = 20 * 60 * 1000;

        public bool AnyAppAlive()
        {
            if (AliveThrows > 0) { AliveThrows--; throw new InvalidOperationException("probe failed"); }
            return Alive(Now);
        }

        public bool MarkerSet { get { return Marker.Value; } }
        public long? HeartbeatAgeMs() { return Age; }

        // Optional: scripts the three outcomes instead of ok/failed (ok = Confirmed).
        public Func<int, AutoResult> Result;
        public int MarkerClears;

        public AutoResult TryWriteAuto()
        {
            Writes++;
            WriteAt.Add(Now);
            AutoResult r = Result != null ? Result(Writes) : (WriteOk(Writes) ? AutoResult.Confirmed : AutoResult.Failed);
            if (r == AutoResult.Confirmed) Marker.Value = false;
            if (AfterWrite != null) AfterWrite();
            return r;
        }

        public void ClearMarker()
        {
            MarkerClears++;
            Marker.Value = false;
        }

        public void BumpIntervention() { Bumps++; }
        public int NowMs() { return Now; }

        public void Sleep(int ms)
        {
            Now += ms;
            if (Now > Limit) throw new InvalidOperationException("test clock ran out");
            if (OnTime != null) OnTime(Now);
        }

        public void Log(string text) { Logs.Add(text); }

        public bool Logged(string part)
        {
            foreach (string l in Logs) { if (l.Contains(part)) return true; }
            return false;
        }
    }

    public static class SentinelTests
    {
        // ---- start-up recovery ---------------------------------------------------------------

        [Test]
        public static void Recovery_CleanPreviousRun_DoesNothing()
        {
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            GuardedFirmware g = SafetyTests.Guard(fw, j, new MemFlag(), false, null);
            RecoveryResult r = StartupRecovery.Run(g, new MemFlag(), new MemFlag(), j);
            Assert.True(!r.Needed, "not needed");
            Assert.Equal(0, fw.Inner.Calls.Count);
            Assert.Equal(0, j.Lines.Count);
        }

        [Test]
        public static void Recovery_MarkerLeft_WritesAutoWithoutTrustingTheReadBack()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.CpuRead = FanMode.Auto; // the read-back claims Auto; the marker says a Custom write was started
            ListJournal j = new ListJournal();
            MemFlag marker = new MemFlag();
            marker.Value = true;
            MemFlag session = new MemFlag();
            session.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, j, marker, false, null);
            RecoveryResult r = StartupRecovery.Run(g, marker, session, j);
            Assert.True(r.Needed && r.Wrote && r.AutoConfirmed, "wrote and confirmed");
            Assert.Equal(1, fw.Inner.Count("auto"));
            Assert.True(!marker.Value, "marker cleared");
            Assert.True(j.Has("RECUPERACAO", "não terminou normalmente"), "reason journaled");
        }

        [Test]
        public static void Recovery_OnlySessionFlag_VerifiedAuto_NoWrite()
        {
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            MemFlag session = new MemFlag();
            session.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, j, new MemFlag(), false, null);
            RecoveryResult r = StartupRecovery.Run(g, new MemFlag(), session, j);
            Assert.True(r.Needed && !r.Wrote && r.AutoConfirmed, "a verified Auto needs no write");
            Assert.Equal(0, fw.Inner.Calls.Count);
            Assert.True(j.Has("RECUPERACAO", "sem escrita"), "journaled");
        }

        [Test]
        public static void Recovery_OnlySessionFlag_FansOffAuto_WritesAuto()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.CpuRead = FanMode.Custom;
            fw.GpuRead = FanMode.Custom;
            ListJournal j = new ListJournal();
            MemFlag session = new MemFlag();
            session.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, j, new MemFlag(), false, null);
            RecoveryResult r = StartupRecovery.Run(g, new MemFlag(), session, j);
            Assert.True(r.Wrote, "wrote");
            Assert.Equal(1, fw.Inner.Count("auto"));
        }

        [Test]
        public static void Recovery_OnlySessionFlag_Unreadable_WritesAutoEvenInReadOnly()
        {
            // Auto is written unless the read-back is VERIFIED Auto; read-only mode does not change that.
            foreach (bool readOnly in new bool[] { false, true })
            {
                ExtFirmware fw = new ExtFirmware();
                fw.ReadOk = false;
                MemFlag session = new MemFlag();
                session.Value = true;
                GuardedFirmware g = SafetyTests.Guard(fw, new ListJournal(), new MemFlag(), readOnly, null);
                RecoveryResult r = StartupRecovery.Run(g, new MemFlag(), session, new ListJournal());
                Assert.True(r.Wrote && r.AutoConfirmed, "wrote (readOnly=" + readOnly + ")");
                Assert.Equal(1, fw.Inner.Count("auto"));
            }
        }

        [Test]
        public static void Recovery_MarkerLeft_ReadOnlyStillHandsTheFansBack()
        {
            ExtFirmware fw = new ExtFirmware();
            ListJournal j = new ListJournal();
            MemFlag marker = new MemFlag();
            marker.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, j, marker, true, null);
            RecoveryResult r = StartupRecovery.Run(g, marker, new MemFlag(), j);
            Assert.True(r.Wrote && r.AutoConfirmed, "the one write read-only mode allows");
            Assert.Equal(1, fw.Inner.Count("auto"));
        }

        [Test]
        public static void Recovery_NotConfirmed_LeavesTheMarkerSoEveryCallerKeepsRetrying()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.Inner.SetAutoResult = false;
            fw.ReadOk = false;
            MemFlag session = new MemFlag();
            session.Value = true;
            MemFlag marker = new MemFlag();
            GuardedFirmware g = SafetyTests.Guard(fw, new ListJournal(), marker, true, null);
            RecoveryResult r = StartupRecovery.Run(g, marker, session, new ListJournal());
            Assert.True(r.Wrote && !r.AutoConfirmed, "not confirmed");
            Assert.True(marker.Value, "marker set: SetFanAuto now keeps writing");
            // read-only mode: the controller's SetFanAuto is a real write while the marker is set
            fw.Inner.SetAutoResult = true;
            Assert.True(g.SetFanAuto(), "retry works");
            Assert.True(!marker.Value, "and clears the marker");
        }

        [Test]
        public static void Recovery_FirmwareUnavailable_IsJournaledAndKeepsTheMarker()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.AvailableValue = false;
            ListJournal j = new ListJournal();
            MemFlag marker = new MemFlag();
            marker.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, j, marker, false, null);
            RecoveryResult r = StartupRecovery.Run(g, marker, new MemFlag(), j);
            Assert.True(r.Needed && !r.Wrote, "nothing written");
            Assert.True(marker.Value, "marker kept for the sentinel / next try");
        }

        [Test]
        public static void Recovery_FirmwareUnavailable_SessionOnly_SetsTheMarker()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.AvailableValue = false;
            MemFlag marker = new MemFlag();
            MemFlag session = new MemFlag();
            session.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, new ListJournal(), marker, false, null);
            StartupRecovery.Run(g, marker, session, new ListJournal());
            Assert.True(marker.Value, "so the retries and the sentinel know");
        }

        // ---- read-only: the marker case and the "every write" sweep ----------------------------------

        [Test]
        public static void ReadOnly_MarkerSet_FanAutoIsARealWriteAndIsRetried()
        {
            ExtFirmware fw = new ExtFirmware();
            fw.Inner.SetAutoResult = false;
            ListJournal j = new ListJournal();
            MemFlag marker = new MemFlag();
            marker.Value = true;
            GuardedFirmware g = SafetyTests.Guard(fw, j, marker, true, null);
            Assert.True(!g.SetFanAuto(), "first try fails and says so");
            Assert.True(marker.Value, "still marked");
            fw.Inner.SetAutoResult = true;
            Assert.True(g.SetFanAuto(), "second try writes");
            Assert.Equal(2, fw.Inner.Count("auto"));
            Assert.True(!marker.Value, "cleared after the confirmed Auto");
            Assert.True(j.Has("ESCRITA-ANTES", "somente leitura ligado"), "journaled as the exception");
        }

        [Test]
        public static void ReadOnly_MarkerClear_StillReportsNothingToHandBackAndWritesNothing()
        {
            ExtFirmware fw = new ExtFirmware();
            GuardedFirmware g = SafetyTests.Guard(fw, new ListJournal(), new MemFlag(), true, null);
            Assert.True(g.SetFanAuto(), "nothing to hand back");
            Assert.Equal(0, fw.Inner.Calls.Count);
        }

        [Test]
        public static void ReadOnly_EverySetMethodOfTheFirmwareInterfacesIsBlocked()
        {
            // New write method on IFirmware/IFirmwareExt = it must be blocked in read-only mode too.
            List<MethodInfo> sets = new List<MethodInfo>();
            foreach (Type t in new Type[] { typeof(IFirmware), typeof(IFirmwareExt) })
            {
                foreach (MethodInfo m in t.GetMethods())
                {
                    if (m.Name.StartsWith("Set") && m.ReturnType == typeof(bool)) sets.Add(m);
                }
            }
            Assert.True(sets.Count >= 4, "found the known write methods: " + sets.Count);
            foreach (MethodInfo m in sets)
            {
                ExtFirmware fw = new ExtFirmware();
                GuardedFirmware g = SafetyTests.Guard(fw, new ListJournal(), new MemFlag(), true, null);
                List<object> args = new List<object>();
                foreach (ParameterInfo p in m.GetParameters())
                {
                    args.Add(p.ParameterType.IsEnum ? Enum.GetValues(p.ParameterType).GetValue(0) : (p.ParameterType == typeof(int) ? (object)50 : null));
                }
                m.Invoke(g, args.ToArray());
                Assert.Equal(0, fw.Inner.Calls.Count);
            }
        }

        [Test]
        public static void Sweep_NoFirmwareObjectIsCreatedOutsideSafetyWrap()
        {
            string root = FindRepoRoot();
            if (root == null) return; // run from somewhere without the sources: nothing to scan
            List<string> bad = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            {
                int n = 0;
                foreach (string line in File.ReadAllLines(file))
                {
                    n++;
                    string t = line.Trim();
                    if (t.StartsWith("//")) continue;
                    if (!t.Contains("new WmiFirmware(")) continue;
                    if (t.Contains("Safety.Wrap(new WmiFirmware()")) continue; // both overloads
                    bad.Add(Path.GetFileName(file) + ":" + n);
                }
            }
            Assert.True(bad.Count == 0, "unguarded new WmiFirmware(): " + string.Join(", ", bad.ToArray()));
        }

        private static string FindRepoRoot()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
            {
                if (File.Exists(Path.Combine(d, "build.cmd")) && Directory.Exists(Path.Combine(d, "src"))) return d;
                d = Path.GetDirectoryName(d.TrimEnd('\\'));
            }
            return null;
        }

        // ---- marker vs concurrent Custom writes (I3) --------------------------------------------------

        [Test]
        public static void Marker_AutoThatOverlapsACustomWrite_DoesNotClearIt()
        {
            // The UI thread is inside a Custom write that hangs; the isolated writer (its own guard,
            // same marker file) writes Auto meanwhile. The late Custom write lands afterwards.
            ExtFirmware ui = new ExtFirmware();
            ExtFirmware isolated = new ExtFirmware();
            MemFlag marker = new MemFlag();
            ListJournal j = new ListJournal();
            GuardedFirmware a = SafetyTests.Guard(ui, new ListJournal(), marker, false, null);
            GuardedFirmware b = SafetyTests.Guard(isolated, j, marker, false, null);
            bool isolatedOk = false;
            ui.OnWrite = delegate { isolatedOk = b.SetFanAuto(); };
            Assert.True(a.SetFanPercent(40, 40), "custom write ok");
            Assert.True(isolatedOk, "the Auto write itself was accepted");
            Assert.True(marker.Value, "the marker survived: Custom landed after the Auto");
            Assert.True(j.Has("ESCRITA-DEPOIS", "ao mesmo tempo"), "journaled why it was kept");
            // later, with nothing in flight, an Auto clears it as usual
            Assert.True(b.SetFanAuto(), "auto again");
            Assert.True(!marker.Value, "now cleared");
        }

        [Test]
        public static void Marker_ClearedBehindTheCustomWritesBack_IsReassertedAfterTheWrite()
        {
            ExtFirmware fw = new ExtFirmware();
            MemFlag marker = new MemFlag();
            fw.OnWrite = delegate { marker.Value = false; }; // something (a sentinel pass) cleared it mid-write
            GuardedFirmware g = SafetyTests.Guard(fw, new ListJournal(), marker, false, null);
            Assert.True(g.SetFanPercent(40, 40), "ok");
            Assert.True(marker.Value, "re-set after the write that is now on the firmware");
        }

        // ---- sentinel decisions ----------------------------------------------------------------

        [Test]
        public static void Brain_AppAlive_NoMarker_KeepsWatching()
        {
            SentinelBrain b = new SentinelBrain(0);
            Assert.Equal(SentinelStep.Wait, b.Next(true, false, 100, 1000));
            Assert.Equal(SentinelStep.Wait, b.Next(true, false, null, 500000));
        }

        [Test]
        public static void Brain_NoAppNoMarker_ExitsOnlyAfterTheStartupGrace()
        {
            SentinelBrain b = new SentinelBrain(1000);
            Assert.Equal(SentinelStep.Wait, b.Next(false, false, null, 1000 + SentinelBrain.StartupGraceMs - 1));
            Assert.Equal(SentinelStep.Exit, b.Next(false, false, null, 1000 + SentinelBrain.StartupGraceMs));
        }

        [Test]
        public static void Brain_NoApp_WithMarker_WritesAutoAtOnceThenExits()
        {
            SentinelBrain b = new SentinelBrain(0, 0);
            Assert.Equal(SentinelStep.WriteAuto, b.Next(false, true, null, 1000));
            Assert.Equal(SentinelReason.AppGone, b.Reason);
            b.Report(true, 1000);
            Assert.Equal(SentinelStep.Exit, b.Next(false, false, null, 2000));
        }

        [Test]
        public static void Brain_FailedWrites_NeverGiveUpWhileTheMarkerIsSet()
        {
            SentinelBrain b = new SentinelBrain(0, 0);
            int now = 1000;
            int writes = 0;
            for (int i = 0; i < 5000; i++)
            {
                SentinelStep s = b.Next(false, true, null, now);
                Assert.True(s != SentinelStep.Exit, "never exits with the marker set (iteration " + i + ")");
                if (s == SentinelStep.WriteAuto)
                {
                    writes++;
                    b.Report(false, now);
                }
                now += 500;
            }
            Assert.True(writes > 200, "kept trying every 5 s: " + writes);
        }

        [Test]
        public static void Brain_RetryWaitsFiveSecondsBetweenAttempts()
        {
            SentinelBrain b = new SentinelBrain(0, 0);
            Assert.Equal(SentinelStep.WriteAuto, b.Next(false, true, null, 1000));
            b.Report(false, 1000);
            Assert.Equal(SentinelStep.Wait, b.Next(false, true, null, 2000));
            Assert.Equal(SentinelStep.Wait, b.Next(false, true, null, 5999));
            Assert.Equal(SentinelStep.WriteAuto, b.Next(false, true, null, 6000));
        }

        [Test]
        public static void Brain_AppAliveWithFreshHeartbeat_NeverWrites_WhateverTheMarker()
        {
            SentinelBrain b = new SentinelBrain(0, 0);
            Assert.Equal(SentinelStep.Wait, b.Next(true, true, 2000, 100000));
            Assert.Equal(SentinelStep.Wait, b.Next(true, true, SentinelBrain.StaleHeartbeatMs - 1, 100000));
        }

        [Test]
        public static void Brain_AppFrozen_WithMarker_WritesAuto()
        {
            SentinelBrain b = new SentinelBrain(0, 0);
            Assert.Equal(SentinelStep.WriteAuto, b.Next(true, true, SentinelBrain.StaleHeartbeatMs + 1, 100000));
            Assert.Equal(SentinelReason.AppStalled, b.Reason);
            b.Report(true, 100000);
            Assert.Equal(SentinelStep.Wait, b.Next(true, false, SentinelBrain.StaleHeartbeatMs + 2000, 102000));
        }

        [Test]
        public static void Brain_UnknownHeartbeat_IsNotFrozen()
        {
            // The sentinel could not open the shared section (or the app never made one): that is
            // "unknown", never a reason to write Auto against a live app.
            SentinelBrain b = new SentinelBrain(1000, 0);
            Assert.Equal(SentinelStep.Wait, b.Next(true, true, null, 1000));
            Assert.Equal(SentinelStep.Wait, b.Next(true, true, null, 1000000));
        }

        [Test]
        public static void Brain_FrozenAppRecovers_RearmsTheRetries()
        {
            SentinelBrain b = new SentinelBrain(0, 0);
            b.Next(true, true, 40000, 100000);
            b.Report(false, 100000);
            Assert.Equal(1, b.Attempts);
            b.Next(true, true, 500, 110000); // beating again
            Assert.Equal(0, b.Attempts);
        }

        // ---- the sentinel loop with a fake world ------------------------------------------------------

        [Test]
        public static void Loop_AppKilledInManual_WritesAutoOnceAndLeaves()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Age = 1000;
            h.Alive = delegate(int t) { return t < 3000; };
            SentinelLoop.Run(h, 0);
            Assert.Equal(1, h.Writes);
            Assert.True(!h.Marker.Value, "marker cleared by the Auto write");
            Assert.True(h.WriteAt[0] >= 3000 && h.WriteAt[0] <= 4000, "right after the death: " + h.WriteAt[0]);
            Assert.True(h.Logged("encerrado: nenhum app aberto e a ventoinha está em automático"), "end line");
        }

        [Test]
        public static void Loop_WmiDownForThreeTries_ThenItWorks()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Alive = delegate { return false; };
            h.WriteOk = delegate(int n) { return n >= 4; };
            SentinelLoop.Run(h, 0);
            Assert.Equal(4, h.Writes);
            Assert.True(!h.Marker.Value, "done");
            Assert.True(h.WriteAt[3] - h.WriteAt[0] >= 3 * SentinelBrain.RetryMs, "retries are 5 s apart");
            Assert.True(h.Logged("confirmado após"), "recovery logged");
        }

        [Test]
        public static void Loop_NeverGivesUp_EvenAfterFiftyFailures()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Alive = delegate { return false; };
            h.WriteOk = delegate(int n) { return n >= 60; };
            h.Limit = 60 * 60 * 1000;
            SentinelLoop.Run(h, 0);
            Assert.Equal(60, h.Writes);
            Assert.True(!h.Marker.Value, "finally written");
            Assert.True(h.Logs.Count < 25, "the journal is not flooded: " + h.Logs.Count + " lines");
        }

        // ---- Auto accepted but never confirmed by the read-back ---------------------------------------

        [Test]
        public static void Loop_AutoAcceptedButNeverConfirmed_StopsAfterThreeAndLeaves()
        {
            // The firmware takes the command, the read-back never says Auto (decoding not proven on this
            // hardware): the sentinel must not write to the EC forever and must let the deploy finish.
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Alive = delegate { return false; };
            h.Result = delegate { return AutoResult.AcceptedUnconfirmed; };
            SentinelLoop.Run(h, 0);
            Assert.Equal(SentinelLoop.MaxUnconfirmed, h.Writes);
            Assert.Equal(1, h.MarkerClears);
            Assert.True(!h.Marker.Value, "marker dropped");
            Assert.True(h.Logged("aceitou o automático 3 vezes"), "said once why");
            Assert.True(h.Logged("encerrado"), "and it ended");
            Assert.True(h.Now < 60000, "within a minute, not forever: " + h.Now);
        }

        [Test]
        public static void Loop_UnconfirmedThenFailed_StartsCountingAgain()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Alive = delegate { return false; };
            h.Result = delegate(int n)
            {
                if (n == 3) return AutoResult.Failed;
                return AutoResult.AcceptedUnconfirmed;
            };
            SentinelLoop.Run(h, 0);
            // writes 1-2 unconfirmed, 3 failed (the count restarts), 4-6 unconfirmed: only then it stops
            Assert.Equal(6, h.Writes);
            Assert.Equal(1, h.MarkerClears);
        }

        [Test]
        public static void Loop_UnconfirmedThenConfirmed_EndsWithoutDroppingTheMarkerItself()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Alive = delegate { return false; };
            h.Result = delegate(int n) { return n == 1 ? AutoResult.AcceptedUnconfirmed : AutoResult.Confirmed; };
            SentinelLoop.Run(h, 0);
            Assert.Equal(2, h.Writes);
            Assert.Equal(0, h.MarkerClears);
        }

        // ---- the sentinel's journal under a long outage ------------------------------------------------

        [Test]
        public static void ThrottledJournal_SixtyFailingAttempts_WriteAFewLinesNotHundreds()
        {
            ListJournal j = new ListJournal();
            int now = 0;
            ThrottledJournal t = new ThrottledJournal(j, delegate { return now; });
            for (int i = 0; i < 60; i++)
            {
                now += 6000; // one attempt every 6 s: six minutes of outage
                t.Write(JournalKind.WriteBefore, "ventoinha -> automático (segurança)");
                t.Write(JournalKind.WriteAfter, "ventoinha -> automático (segurança): falhou");
            }
            Assert.True(j.Lines.Count <= 30, "not 120 lines: " + j.Lines.Count);
            Assert.True(j.Lines.Count >= 8, "but the series is still visible: " + j.Lines.Count);
            Assert.True(j.Has("ESCRITA-DEPOIS", "omitidas"), "a summary of what was left out");
        }

        [Test]
        public static void ThrottledJournal_PairsAreNeverCutInHalfAfterTheFirstLines()
        {
            ListJournal j = new ListJournal();
            int now = 0;
            ThrottledJournal t = new ThrottledJournal(j, delegate { return now; });
            for (int i = 0; i < 40; i++)
            {
                now += 6000;
                t.Write(JournalKind.WriteBefore, "x");
                t.Write(JournalKind.WriteAfter, "x: falhou");
            }
            int before = j.Count("ESCRITA-ANTES");
            int after = 0;
            foreach (string l in j.Lines) { if (l.StartsWith("ESCRITA-DEPOIS|x: falhou")) after++; }
            Assert.Equal(before, after);
        }

        [Test]
        public static void ThrottledJournal_RecoveryLineAlwaysPasses_AndResetsTheFreeLines()
        {
            ListJournal j = new ListJournal();
            int now = 0;
            ThrottledJournal t = new ThrottledJournal(j, delegate { return now; });
            for (int i = 0; i < 20; i++)
            {
                now += 6000;
                t.Write(JournalKind.WriteBefore, "a");
                t.Write(JournalKind.WriteAfter, "a: falhou");
            }
            now += 6000;
            t.Write(JournalKind.WriteBefore, "a");
            t.Write(JournalKind.WriteAfter, "a: ok (lido: CPU Auto / GPU Auto)");
            Assert.True(j.Lines[j.Lines.Count - 1].Contains(": ok"), "the recovery line is there");
            // a new outage starts with free lines again
            int n = j.Lines.Count;
            now += 6000;
            t.Write(JournalKind.WriteBefore, "b");
            t.Write(JournalKind.WriteAfter, "b: falhou");
            Assert.Equal(n + 2, j.Lines.Count);
        }

        [Test]
        public static void ThrottledJournal_AcceptedButKeptMarker_CountsAsFailing_AndOtherKindsPassThrough()
        {
            ListJournal j = new ListJournal();
            int now = 0;
            ThrottledJournal t = new ThrottledJournal(j, delegate { return now; });
            for (int i = 0; i < 30; i++)
            {
                now += 6000;
                t.Write(JournalKind.WriteBefore, "a");
                t.Write(JournalKind.WriteAfter, "a: ok (lido: CPU Custom / GPU Custom; marcador mantido: o firmware não confirmou automático)");
                t.Write(JournalKind.Sentinel, "linha do guardião " + i);
            }
            Assert.Equal(30, j.Count("SENTINELA"));
            Assert.True(j.Count("ESCRITA-ANTES") + j.Count("ESCRITA-DEPOIS") < 40, "write lines limited: " + (j.Count("ESCRITA-ANTES") + j.Count("ESCRITA-DEPOIS")));
        }

        // ---- the sentinel task must be current before it is run -------------------------------------

        private const string TaskXml =
            "<Task><Actions Context=\"Author\"><Exec><Command>\"C:\\Program Files\\SenseMonitor\\SenseMonitor.exe\"</Command>"
            + "<Arguments>--sentinela</Arguments></Exec></Actions></Task>";

        [Test]
        public static void SentinelTask_CurrentWhenItPointsAtTheInstalledExeWithTheSwitch()
        {
            Assert.True(Autostart.SentinelTaskIsCurrent(TaskXml, @"C:\Program Files\SenseMonitor\SenseMonitor.exe"), "current");
            Assert.True(Autostart.SentinelTaskIsCurrent(TaskXml.Replace("\"C:", "&quot;C:").Replace("exe\"", "exe&quot;"), @"c:\program files\sensemonitor\sensemonitor.exe"), "escaped quotes, case-insensitive");
        }

        [Test]
        public static void SentinelTask_NotCurrentForAnotherExeOrSwitchOrNothing()
        {
            Assert.True(!Autostart.SentinelTaskIsCurrent(TaskXml, @"C:\Dev\bin\SenseMonitor.exe"), "another exe");
            Assert.True(!Autostart.SentinelTaskIsCurrent(TaskXml.Replace("--sentinela", "--outra"), @"C:\Program Files\SenseMonitor\SenseMonitor.exe"), "another switch");
            Assert.True(!Autostart.SentinelTaskIsCurrent(TaskXml.Replace("<Arguments>--sentinela</Arguments>", ""), @"C:\Program Files\SenseMonitor\SenseMonitor.exe"), "no arguments");
            Assert.True(!Autostart.SentinelTaskIsCurrent(null, @"C:\x.exe"), "null");
            Assert.True(!Autostart.SentinelTaskIsCurrent("", @"C:\x.exe"), "empty");
        }

        [Test]
        public static void SentinelTask_TheXmlWeRegisterIsRecognizedAsCurrent()
        {
            string exe = @"C:\Program Files\SenseMonitor\SenseMonitor.exe";
            Assert.True(Autostart.SentinelTaskIsCurrent(Autostart.BuildSentinelTaskXml(exe, @"PC\User"), exe), "round trip");
        }

        // ---- the hung-thread cap still lets one attempt through a minute ----------------------------------

        [Test]
        public static void AutoWriter_AtTheHungCap_TriesAgainOnceAMinute()
        {
            ManualResetEvent gate = new ManualResetEvent(false);
            int clock = 0;
            int calls = 0;
            AutoWriter w = new AutoWriter(delegate
            {
                calls++;
                if (calls <= AutoWriter.MaxHung) gate.WaitOne(10000);
                return SafetyTests.Guard(new ExtFirmware(), new ListJournal(), new MemFlag(), false, null);
            }, 100, delegate { return clock; });
            for (int i = 0; i < AutoWriter.MaxHung; i++) Assert.True(!w.Try(), "hung " + i);
            Assert.Equal(AutoWriter.MaxHung, w.Hung);
            int c0 = calls;
            Assert.True(!w.Try(), "right at the cap: refused");
            clock += 30000;
            Assert.True(!w.Try(), "half a minute later: still refused");
            Assert.Equal(c0, calls);
            clock += 31000;
            Assert.True(w.Try(), "a minute later: a fresh connection is tried and works");
            Assert.Equal(c0 + 1, calls);
            gate.Set();
            for (int i = 0; i < 60 && w.Hung > 0; i++) Thread.Sleep(50);
        }

        [Test]
        public static void Loop_AppAliveWithFreshHeartbeat_NeverWritesAuto()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Age = 800;
            h.Alive = delegate(int t) { return t < 120000; };
            h.OnTime = delegate(int t) { if (t >= 119000) h.Marker.Value = false; }; // the app hands back and exits
            SentinelLoop.Run(h, 0);
            Assert.Equal(0, h.Writes);
        }

        [Test]
        public static void Loop_RestartedApp_IsCoveredAndNoFightHappens()
        {
            // App A dies cleanly (marker clear), app B starts and goes to Manual, B is killed: one Auto.
            FakeSentinelHost h = new FakeSentinelHost();
            h.Age = 500;
            h.Alive = delegate(int t) { return t < 2000 || (t >= 3000 && t < 40000); };
            h.OnTime = delegate(int t) { if (t >= 20000 && t < 40000 && !h.Marker.Value && h.Writes == 0) h.Marker.Value = true; };
            SentinelLoop.Run(h, 15000);
            Assert.Equal(1, h.Writes);
            Assert.True(h.WriteAt[0] >= 40000, "only after B died: " + h.WriteAt[0]);
            Assert.True(!h.Marker.Value, "fans safe");
        }

        [Test]
        public static void Loop_FrozenApp_GetsAutoAndTheAppIsToldOnce()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Age = SentinelBrain.StaleHeartbeatMs + 5000;
            h.AfterWrite = delegate { h.Alive = delegate { return false; }; }; // then the test ends the app
            SentinelLoop.Run(h, 0);
            Assert.Equal(1, h.Writes);
            Assert.Equal(1, h.Bumps);
        }

        [Test]
        public static void Loop_UnknownHeartbeat_NeverWritesAgainstALiveApp()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Marker.Value = true;
            h.Age = null;
            h.Alive = delegate(int t) { return t < 200000; };
            h.OnTime = delegate(int t) { if (t >= 199000) h.Marker.Value = false; };
            SentinelLoop.Run(h, 0);
            Assert.Equal(0, h.Writes);
        }

        [Test]
        public static void Loop_SurvivesExceptionsFromTheWorld()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.AliveThrows = 3;
            h.Marker.Value = true;
            h.Alive = delegate { return false; };
            SentinelLoop.Run(h, 0);
            Assert.Equal(1, h.Writes);
        }

        [Test]
        public static void Loop_NoAppNoMarker_WaitsTheGraceThenLeaves()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            h.Alive = delegate { return false; };
            SentinelLoop.Run(h, SentinelBrain.StartupGraceMs);
            Assert.True(h.Now >= SentinelBrain.StartupGraceMs, "waited the grace: " + h.Now);
            Assert.Equal(0, h.Writes);
        }

        [Test]
        public static void Loop_IdleWithALiveApp_LooksOnlyEveryFiveSeconds()
        {
            FakeSentinelHost h = new FakeSentinelHost();
            int looks = 0;
            h.Alive = delegate(int t) { looks++; return t < 60000; };
            SentinelLoop.Run(h, 0);
            Assert.True(looks <= 16, "about one look per 5 s while idle: " + looks);
        }

        // ---- one Auto attempt with a time limit and a fresh firmware each time -----------------------

        [Test]
        public static void AutoWriter_CreatesANewFirmwareEveryAttempt_SoALateWmiIsFound()
        {
            int created = 0;
            ExtFirmware late = new ExtFirmware();
            AutoWriter w = new AutoWriter(delegate
            {
                created++;
                ExtFirmware fw = new ExtFirmware();
                fw.AvailableValue = created >= 3;
                fw.Inner.SetAutoResult = created >= 3;
                if (created >= 3) late = fw;
                return SafetyTests.Guard(fw, new ListJournal(), new MemFlag(), false, null);
            }, 2000);
            Assert.True(!w.Try(), "1st: WMI not there");
            Assert.True(!w.Try(), "2nd: still not");
            Assert.True(w.Try(), "3rd: found, Auto written");
            Assert.Equal(1, late.Inner.Count("auto"));
            Assert.Equal(3, created);
        }

        [Test]
        public static void AutoWriter_AFirmwareThatThrowsWhileConnecting_IsAFailureNotACrash()
        {
            AutoWriter w = new AutoWriter(delegate { throw new InvalidOperationException("no WMI"); }, 2000);
            Assert.True(!w.Try(), "failed quietly");
        }

        [Test]
        public static void AutoWriter_AHungCall_TimesOutAndTheNextAttemptStillRuns()
        {
            ManualResetEvent gate = new ManualResetEvent(false);
            int calls = 0;
            AutoWriter w = new AutoWriter(delegate
            {
                calls++;
                if (calls == 1) gate.WaitOne(10000); // the first connection hangs
                ExtFirmware fw = new ExtFirmware();
                return SafetyTests.Guard(fw, new ListJournal(), new MemFlag(), false, null);
            }, 300);
            Assert.True(!w.Try(), "timed out");
            Assert.Equal(1, w.Hung);
            Assert.True(w.Try(), "the next attempt is independent and works");
            gate.Set();
            for (int i = 0; i < 40 && w.Hung > 0; i++) Thread.Sleep(50);
            Assert.Equal(0, w.Hung);
        }

        [Test]
        public static void AutoWriter_TooManyHungThreads_RefusesUntilOneComesBack()
        {
            ManualResetEvent gate = new ManualResetEvent(false);
            int calls = 0;
            AutoWriter w = new AutoWriter(delegate
            {
                calls++;
                gate.WaitOne(10000);
                return SafetyTests.Guard(new ExtFirmware(), new ListJournal(), new MemFlag(), false, null);
            }, 100);
            for (int i = 0; i < AutoWriter.MaxHung; i++) Assert.True(!w.Try(), "hung " + i);
            int before = calls;
            Assert.True(!w.Try(), "refused");
            Assert.Equal(before, calls);
            gate.Set();
            for (int i = 0; i < 60 && w.Hung > 0; i++) Thread.Sleep(50);
            Assert.Equal(0, w.Hung);
        }

        // ---- the sentinel process (real mutexes, but with names of their own) -------------------------

        private static string Unique(string what)
        {
            return "Local\\ForjaTest." + what + "." + Guid.NewGuid().ToString("N");
        }

        [Test]
        public static void SentinelRun_NoAppAndNoMarker_LeavesAndJournals_WithoutTheRealMutexes()
        {
            string dir = SafetyTests.TempDir();
            try
            {
                Safety.DirOverride = dir;
                int code = Sentinel.Run(Unique("sentinel"), Unique("app"), Unique("hb"), 0, 1000);
                Assert.Equal(0, code);
                List<JournalEntry> list = FileJournal.ReadRecent(Path.Combine(dir, "diario.log"), 10);
                Assert.True(list.Count >= 2, "start and end lines: " + list.Count);
                Assert.Equal("sentinela", list[0].Source);
                Assert.True(list[0].Text.Contains("iniciado"), list[0].Text);
                Assert.True(list[list.Count - 1].Text.Contains("encerrado"), list[list.Count - 1].Text);
                Assert.True(!File.Exists(Path.Combine(dir, "ventoinha-custom.flag")), "no marker created");
            }
            finally
            {
                Safety.DirOverride = null;
                Safety.SetSource(Safety.SourceApp);
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public static void SentinelRun_ASecondSentinel_LeavesWithoutDoingAnything()
        {
            string dir = SafetyTests.TempDir();
            string name = Unique("sentinel");
            Mutex held = new Mutex(true, name);
            try
            {
                Safety.DirOverride = dir;
                int code = Sentinel.Run(name, Unique("app"), Unique("hb"), 0, 300);
                Assert.Equal(0, code);
                Assert.True(!File.Exists(Path.Combine(dir, "diario.log")), "it never started guarding");
            }
            finally
            {
                held.ReleaseMutex();
                held.Close();
                Safety.DirOverride = null;
                Safety.SetSource(Safety.SourceApp);
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public static void SentinelExists_SeesAHeldMutex_AndNotAClosedOne()
        {
            string name = Unique("app");
            Assert.True(!Sentinel.Exists(name), "nothing yet");
            Mutex m = new Mutex(true, name);
            Assert.True(Sentinel.Exists(name), "held");
            m.ReleaseMutex();
            m.Close();
            Assert.True(!Sentinel.Exists(name), "gone with its last handle (the check itself keeps nothing alive)");
        }

        [Test]
        public static void SentinelEntry_NotASentinelCommandLine_IsLeftAlone()
        {
            int code;
            Assert.True(!Sentinel.TryDispatch(new string[] { "x.exe" }, out code), "no switch");
            Assert.True(!Sentinel.TryDispatch(new string[] { "x.exe", "--validar-ventoinha" }, out code), "other switch");
            Assert.True(!Sentinel.TryDispatch(null, out code), "null");
        }

        // ---- the sentinel's own scheduled task -------------------------------------------------------

        [Test]
        public static void SentinelTask_IsOnDemand_Elevated_AndRunsTheInstalledExeWithTheSwitch()
        {
            string xml = Autostart.BuildSentinelTaskXml(@"C:\Program Files\SenseMonitor\SenseMonitor.exe", @"PC\User");
            Assert.True(!xml.Contains("<Triggers"), "no trigger: only started on demand");
            Assert.True(xml.Contains("<RunLevel>HighestAvailable</RunLevel>"), "elevated like the app");
            Assert.True(xml.Contains("<LogonType>InteractiveToken</LogonType>"), "same logon type as the autostart task");
            Assert.True(xml.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"), "one at a time");
            Assert.True(xml.Contains("<AllowStartOnDemand>true</AllowStartOnDemand>"), "on demand");
            Assert.True(xml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"), "battery safe");
            Assert.True(xml.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"), "battery safe");
            Assert.True(xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"), "no 72 h limit");
            Assert.True(xml.Contains("<Command>&quot;C:\\Program Files\\SenseMonitor\\SenseMonitor.exe&quot;</Command>"), "command: " + xml);
            Assert.True(xml.Contains("<Arguments>--sentinela</Arguments>"), "arguments");
        }

        [Test]
        public static void SentinelTask_ArgumentsAndName()
        {
            Assert.Equal("SenseMonitor-Sentinela", Autostart.SentinelTaskName);
            Assert.Equal("/Run /TN SenseMonitor-Sentinela", Autostart.BuildRunSentinelArgs());
            Assert.Equal("/Create /TN SenseMonitor-Sentinela /XML \"C:\\x.xml\" /F", Autostart.BuildSentinelCreateArgs(@"C:\x.xml"));
            // the autostart task keeps its own name and arguments
            Assert.Equal("/Create /TN SenseMonitor /XML \"C:\\x.xml\" /F", Autostart.BuildCreateArgs(@"C:\x.xml"));
        }

        [Test]
        public static void SentinelTask_XmlEscapesTheUserAndPath()
        {
            string xml = Autostart.BuildSentinelTaskXml(@"C:\A & B\SenseMonitor.exe", @"PC\A<B>");
            Assert.True(xml.Contains("A &amp; B"), "path escaped");
            Assert.True(xml.Contains("A&lt;B&gt;"), "user escaped");
        }

        // ---- startup mode: read before write ---------------------------------------------------

        [Test]
        public static void StartupMode_AlreadyDiaADia_WritesNothingAndSaysSo()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.DiaADia;
            ListJournal j = new ListJournal();
            PerfMode? m = StartupMode.Apply(fw, null, j);
            Assert.Equal((PerfMode?)PerfMode.DiaADia, m);
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(j.Has("ACAO", "sem escrita (já estava)"), "journal line");
        }

        [Test]
        public static void StartupMode_Different_Writes()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Silencioso;
            ListJournal j = new ListJournal();
            PerfMode? m = StartupMode.Apply(fw, null, j);
            Assert.Equal((PerfMode?)PerfMode.DiaADia, m);
            Assert.Equal("mode:DiaADia", fw.Last());
            Assert.True(!j.Has("ACAO", "sem escrita"), "no 'already' line");
        }

        [Test]
        public static void StartupMode_UnreadableMode_StillTriesTheWrite()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = null;
            StartupMode.Apply(fw, null, new ListJournal());
            Assert.Equal(1, fw.Count("mode:DiaADia"));
        }

        // ---- heartbeat (real shared memory, a name of its own) ----------------------------------------

        [Test]
        public static void Heartbeat_AgeAndInterventions()
        {
            string name = Unique("heartbeat");
            Heartbeat app = Heartbeat.Create(name);
            Assert.True(app != null, "created");
            try
            {
                Heartbeat sentinel = Heartbeat.TryOpen(name);
                Assert.True(sentinel != null, "opened by the other side");
                try
                {
                    Assert.True(!sentinel.AgeMs().HasValue, "no beat yet: unknown, not old");
                    app.Beat();
                    long? age = sentinel.AgeMs();
                    Assert.True(age.HasValue && age.Value >= 0 && age.Value < 2000, "fresh beat: " + age);
                    int before = app.Interventions;
                    sentinel.BumpIntervention();
                    Assert.Equal(before + 1, app.Interventions);
                }
                finally { sentinel.Dispose(); }
            }
            finally { app.Dispose(); }
        }

        [Test]
        public static void Heartbeat_AgeIsMonotonicNotWallClock()
        {
            Assert.Equal(1500L, Heartbeat.AgeMsFrom(2500, 1000, 1000));
            Assert.Equal(0L, Heartbeat.AgeMsFrom(1000, 2500, 1000));  // never negative
            Assert.Equal(0L, Heartbeat.AgeMsFrom(5, 1, 0));
            Assert.Equal(30000L, Heartbeat.AgeMsFrom(30000 * 10000L, 0, 10000000));
        }

        [Test]
        public static void Heartbeat_TryOpenOfAMissingSection_IsNull()
        {
            Assert.True(Heartbeat.TryOpen(Unique("missing")) == null, "null, so the sentinel treats it as unknown");
        }
    }
}
