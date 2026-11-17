using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // Review leftovers of the environment test (ambiente-review.md, re-review 4): m19, m20, m21.
    public static class InstallerLeftoversTests
    {
        private static FanMarkerData Marker(string layout)
        {
            FanMarkerData d = new FanMarkerData();
            d.Layout = layout;
            d.Fingerprint = "x|y";
            return d;
        }

        // ---- m19: an unreadable marker is read once more, then the fans this process addresses are used ----

        [Test]
        public static void M19_ReadWithRetry_FirstReadHasALayout_ReadOnce()
        {
            int reads = 0;
            FanMarkerData d = FanMarkerReads.ReadWithRetry(delegate { reads++; return Marker("cpu"); }, null);
            Assert.Equal(1, reads);
            Assert.Equal("cpu", d.Layout);
        }

        [Test]
        public static void M19_ReadWithRetry_EmptyThenLayout_SecondReadWins_AfterAPause()
        {
            int reads = 0;
            int pauses = 0;
            FanMarkerData d = FanMarkerReads.ReadWithRetry(delegate { reads++; return reads == 1 ? new FanMarkerData() : Marker("gpu"); }, delegate { pauses++; });
            Assert.Equal(2, reads);
            Assert.Equal(1, pauses);
            Assert.Equal("gpu", d.Layout);
        }

        [Test]
        public static void M19_ReadWithRetry_NeverMoreThanTwoReads_AndNeverNull()
        {
            int reads = 0;
            FanMarkerData d = FanMarkerReads.ReadWithRetry(delegate { reads++; return null; }, null);
            Assert.Equal(2, reads);
            Assert.True(d != null, "an empty record, never null");
            Assert.True(d.GetLayout() == null, "no layout");
        }

        [Test]
        public static void M19_Decide_MarkerWithoutLayout_FallsBackToTheFansThisProcessAddresses()
        {
            HandBackDecision h = AutoHandBackRule.Decide(true, Marker(null), false, null, "x|y", FanLayout.CpuOnly);
            Assert.True(h.Write, "a marker is there: Auto is handed back");
            Assert.Equal(FanLayout.CpuOnly, h.Layout);
            Assert.True(!AutoHandBackRule.Decide(true, Marker(null), false, null, "x|y", null).Write, "no process layout: still no safe mask");
            Assert.True(!AutoHandBackRule.Decide(true, Marker(null), false, null, "x|y").Write, "the five-argument form is unchanged");
        }

        [Test]
        public static void M19_Decide_TheRecordedLayoutBeatsTheFallback_AndAForeignMarkerNeverWrites()
        {
            Assert.Equal(FanLayout.GpuOnly, AutoHandBackRule.Decide(true, Marker("gpu"), false, null, "x|y", FanLayout.Both).Layout);
            FanMarkerData foreign = Marker(null);
            foreign.Fingerprint = "predator phn16-72|v1.17";
            Assert.True(!AutoHandBackRule.Decide(true, foreign, false, null, "nitro anv15-51|v1.60", FanLayout.Both).Write, "another machine");
        }

        [Test]
        public static void M19_FanMarkerRead_WhileAWriterHoldsTheFile_DoesNotFail()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-m19-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "ventoinha-custom.flag");
                FanMarker m = new FanMarker(path);
                Assert.True(m.Set(FanLayout.CpuOnly, "x|y"), "raised");
                // The marker's own writer opens it for writing and shares read/write/delete: a reader must get in.
                using (FileStream w = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    FanMarkerData d = m.Read();
                    Assert.Equal("cpu", d.Layout);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        // ---- m20: the decision has its own budget and the timeout text says what really timed out ----

        [Test]
        public static void M20_SlowDecision_DoesNotEatTheWritesTime()
        {
            bool skipped;
            string reason;
            HandBackDecision go = new HandBackDecision();
            go.Kind = HandBackKind.Write;
            go.Layout = FanLayout.Both;
            bool ok = FanSafety.RunAutoIsolated(
                delegate { Thread.Sleep(400); return go; },
                delegate(HandBackDecision d) { Thread.Sleep(20); return null; },
                2000, 150, out skipped, out reason);
            Assert.True(ok, "400 ms of deciding is outside the 150 ms write window");
            Assert.True(!skipped, "it wrote");
            Assert.Null(reason);
        }

        [Test]
        public static void M20_DecisionThatNeverFinishes_SaysTheDecisionTimedOut_NotTheFirmware()
        {
            bool skipped;
            string reason;
            ManualResetEvent release = new ManualResetEvent(false);
            bool ok = FanSafety.RunAutoIsolated(
                delegate { release.WaitOne(3000); return new HandBackDecision(); },
                delegate(HandBackDecision d) { return null; },
                120, 150, out skipped, out reason);
            release.Set();
            Assert.True(!ok, "no confirmation");
            Assert.True(!skipped, "not a skip");
            Assert.True(reason != null && reason.IndexOf("decis", StringComparison.OrdinalIgnoreCase) >= 0, "the text blames the decision: " + reason);
            Assert.True(reason.IndexOf("sem interface de firmware", StringComparison.OrdinalIgnoreCase) < 0, "never the wrong claim");
        }

        [Test]
        public static void M20_WriteThatNeverFinishes_SaysTheWriteTimedOut()
        {
            bool skipped;
            string reason;
            ManualResetEvent release = new ManualResetEvent(false);
            HandBackDecision go = new HandBackDecision();
            go.Kind = HandBackKind.Write;
            go.Layout = FanLayout.Both;
            bool ok = FanSafety.RunAutoIsolated(
                delegate { return go; },
                delegate(HandBackDecision d) { release.WaitOne(3000); return null; },
                1000, 120, out skipped, out reason);
            release.Set();
            Assert.True(!ok, "timed out");
            Assert.True(reason != null && reason.IndexOf("escrita", StringComparison.OrdinalIgnoreCase) >= 0, "the text blames the write: " + reason);
        }

        [Test]
        public static void M20_NothingToHandBack_IsASkipAndOk()
        {
            bool skipped;
            string reason;
            HandBackDecision none = new HandBackDecision();
            none.Kind = HandBackKind.Unconfirmed;
            bool wrote = false;
            bool ok = FanSafety.RunAutoIsolated(delegate { return none; }, delegate(HandBackDecision d) { wrote = true; return null; }, 1000, 1000, out skipped, out reason);
            Assert.True(ok && skipped, "nothing to hand back");
            Assert.True(!wrote, "no write");
        }

        [Test]
        public static void M20_AWriteThatFails_CarriesItsOwnReason()
        {
            bool skipped;
            string reason;
            HandBackDecision go = new HandBackDecision();
            go.Kind = HandBackKind.Write;
            go.Layout = FanLayout.Both;
            bool ok = FanSafety.RunAutoIsolated(delegate { return go; }, delegate(HandBackDecision d) { return "sem interface de firmware (o app precisa ser administrador)"; }, 1000, 1000, out skipped, out reason);
            Assert.True(!ok, "failed");
            Assert.Equal("sem interface de firmware (o app precisa ser administrador)", reason);
        }

        // ---- m21: an unreadable config.json is never overwritten ----

        private sealed class Svc : IServiceControl
        {
            public readonly Dictionary<string, ServiceInfo> Items = new Dictionary<string, ServiceInfo>();
            public readonly List<string> Log = new List<string>();

            public void Add(string name)
            {
                ServiceInfo i = new ServiceInfo();
                i.Name = name; i.DisplayName = name; i.State = "Stopped"; i.StartMode = "Disabled";
                i.PathName = @"C:\WINDOWS\System32\DriverStore\FileRepository\predatorservice.inf_amd64_c634eb8e856fb962\" + name + ".exe";
                i.Company = "Acer Incorporated"; i.Product = "Acer " + name;
                Items[name] = i;
            }

            public ServiceInfo Query(string name) { ServiceInfo i; return Items.TryGetValue(name, out i) ? i : null; }
            public bool Stop(string name) { return true; }
            public bool Start(string name) { Log.Add("start:" + name); return true; }
            public bool SetStartMode(string name, string mode) { Log.Add("mode:" + name + ":" + mode); return true; }
        }

        private static List<ServiceUndo> OneRecord()
        {
            ServiceUndo u = new ServiceUndo();
            u.Name = "ASMSvc";
            u.PrevStartMode = "Auto";
            u.WasRunning = true;
            u.Utc = "2026-11-17T10:00:00Z";
            List<ServiceUndo> l = new List<ServiceUndo>();
            l.Add(u);
            return l;
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "forja-m21-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public static void M21_CorruptConfig_IsLeftExactlyAsItIs_AndTheServicesAreStillRestored()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "config.json");
                byte[] junk = new byte[] { 0x7B, 0x22, 0x46, 0x61, 0x6E, 0x4D, 0x6F, 0x64, 0x65, 0x22, 0x3A };
                File.WriteAllBytes(path, junk);
                Svc sc = new Svc();
                sc.Add("ASMSvc");
                string note = InstallSystem.ReenableAcerServices(path, sc, OneRecord(), null);
                Assert.True(sc.Log.Contains("mode:ASMSvc:Automatic"), "the service came back from the state-folder record");
                Assert.True(File.ReadAllBytes(path).Length == junk.Length && BitConverter.ToString(File.ReadAllBytes(path)) == BitConverter.ToString(junk), "config.json untouched");
                Assert.True(note != null && note.IndexOf("config", StringComparison.OrdinalIgnoreCase) >= 0, "a warning says the config was not read: " + note);
                Assert.True(!File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "no neighbour files written");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void M21_MissingConfig_IsNotCreated()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Svc sc = new Svc();
                sc.Add("ASMSvc");
                string note = InstallSystem.ReenableAcerServices(path, sc, OneRecord(), null);
                Assert.Null(note);
                Assert.True(!File.Exists(path), "an uninstall never creates a config");
                Assert.True(sc.Log.Contains("mode:ASMSvc:Automatic"), "restored");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void M21_ReadableConfig_RecordsAreRestoredAndTheConfigSavedWithoutThem()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Config c = new Config();
                c.ServiceUndo = OneRecord();
                c.Save(path);
                Svc sc = new Svc();
                sc.Add("ASMSvc");
                string note = InstallSystem.ReenableAcerServices(path, sc, new List<ServiceUndo>(), null);
                Assert.Null(note);
                Assert.True(sc.Log.Contains("mode:ASMSvc:Automatic"), "restored from config.json");
                Config back;
                Assert.True(Config.TryLoad(path, out back) == ConfigRead.Ok, "still readable");
                Assert.Equal(0, back.ServiceUndo.Count);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }
    }
}
