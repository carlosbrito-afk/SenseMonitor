using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Only services that really are Acer's are offered for stopping; undo records survive an installer cancel.
    public static class ConflictRecognitionTests
    {
        private sealed class Svc : IServiceControl
        {
            public readonly Dictionary<string, ServiceInfo> Items = new Dictionary<string, ServiceInfo>();
            public readonly List<string> Log = new List<string>();

            public void Add(string name, string state, string mode, string path, string company, string product, bool delayed)
            {
                ServiceInfo i = new ServiceInfo();
                i.Name = name; i.DisplayName = name; i.State = state; i.StartMode = mode;
                i.PathName = path; i.Company = company; i.Product = product; i.Delayed = delayed;
                Items[name] = i;
            }

            public ServiceInfo Query(string name)
            {
                ServiceInfo i;
                return Items.TryGetValue(name, out i) ? i : null;
            }

            public bool Stop(string name) { Log.Add("stop:" + name); Items[name].State = "Stopped"; return true; }
            public bool Start(string name) { Log.Add("start:" + name); Items[name].State = "Running"; return true; }
            public bool SetStartMode(string name, string mode)
            {
                Log.Add("mode:" + name + ":" + mode);
                Items[name].StartMode = mode == "Disabled" ? "Disabled" : mode == "Manual" ? "Manual" : "Auto";
                return true;
            }
        }

        private const string Store = @"C:\WINDOWS\System32\DriverStore\FileRepository\";

        // The five real services of this PC (from Win32_Service and the file version info).
        private static Svc ThisPcServices()
        {
            Svc s = new Svc();
            s.Add("AASSvc", "Running", "Auto", Store + @"predatorservice.inf_amd64_c634eb8e856fb962\AcerCentralService.exe", "Acer Incorporated", "AcerAgentService", false);
            s.Add("AcerDeviceEnablingServiceV2", "Running", "Auto", Store + @"acerdeviceenablingservicecomponent.inf_amd64_0a9a6f50fc73b215\ADESv2Svc.exe", "Acer Incorporated", "Acer Device Enabling Service V2", false);
            s.Add("AcerLightingService", "Running", "Auto", Store + @"predatorservice.inf_amd64_c634eb8e856fb962\AcerLightingService.exe", "Acer Incorporated", "Acer Lighting Service", false);
            s.Add("AcerServiceSvc", "Running", "Auto", Store + @"acerservicecomponent.inf_amd64_c9ec0382fd426cdf\AcerServiceWrapper.exe", "CloudBees, Inc.", "Windows Service Wrapper", false);
            s.Add("ASMSvc", "Running", "Auto", Store + @"sysmonitorservice.inf_amd64_9a721f842c75068a\x64\AcerSystemCentralService.exe", "Acer Incorporated", "Acer System Monitoring", false);
            return s;
        }

        [Test]
        public static void ThisPcFiveServices_AreAllRecognized()
        {
            List<ConflictItem> l = ConflictServices.Scan(ThisPcServices());
            Assert.Equal(5, l.Count);
            foreach (ConflictItem c in l)
            {
                Assert.True(c.Recognized, c.Name + " is recognized");
                Assert.True(c.NeedsAction, c.Name + " needs action");
            }
        }

        [Test]
        public static void SameNameFromAnotherVendor_NotRecognized_NeverTouched()
        {
            Svc s = new Svc();
            s.Add("ASMSvc", "Running", "Auto", @"C:\Program Files\Contoso\Monitor\ASMSvc.exe", "Contoso Ltd", "Monitor", false);
            s.Add("AASSvc", "Running", "Auto", @"C:\Program Files\Acer\Agent\agent.exe", "Acer Incorporated", "Agent", false);
            List<ConflictItem> l = ConflictServices.Scan(s);
            ConflictItem other = l.Find(delegate(ConflictItem c) { return c.Name == "ASMSvc"; });
            Assert.True(other != null && !other.Recognized && !other.NeedsAction, "listed, not recognized, no action");
            Config cfg = new Config();
            DisableOutcome o = ConflictServices.DisableAll(s, cfg, delegate { return true; }, null, "t");
            Assert.True(!s.Log.Contains("mode:ASMSvc:Disabled") && !s.Log.Contains("stop:ASMSvc"), "the other vendor's service is left alone");
            Assert.True(s.Log.Contains("mode:AASSvc:Disabled"), "the Acer one is handled");
            Assert.Equal(1, o.Disabled);
        }

        [Test]
        public static void AcerCompany_ButAnUnrelatedFolder_NotRecognized()
        {
            Svc s = new Svc();
            s.Add("ASMSvc", "Running", "Auto", @"C:\Users\x\AppData\Local\Temp\ASMSvc.exe", "Acer Incorporated", "x", false);
            Assert.True(!ConflictServices.Scan(s)[0].Recognized, "an Acer name in the file info is not enough");
        }

        [Test]
        public static void WrapperInAnAcerDriverPackage_Recognized_ButNotInARandomFolder()
        {
            Svc s = new Svc();
            s.Add("AcerServiceSvc", "Running", "Auto", Store + @"acerservicecomponent.inf_amd64_c9ec0382fd426cdf\AcerServiceWrapper.exe", "CloudBees, Inc.", "Windows Service Wrapper", false);
            Assert.True(ConflictServices.Scan(s)[0].Recognized, "the real one");
            s.Add("AcerServiceSvc", "Running", "Auto", @"C:\Tools\AcerServiceWrapper.exe", "CloudBees, Inc.", "Windows Service Wrapper", false);
            Assert.True(!ConflictServices.Scan(s)[0].Recognized, "same file name outside an Acer package");
            s.Add("AcerServiceSvc", "Running", "Auto", Store + @"otherpackage.inf_amd64_1\AcerServiceWrapper.exe", "CloudBees, Inc.", "Windows Service Wrapper", false);
            Assert.True(!ConflictServices.Scan(s)[0].Recognized, "driver store but not an Acer package");
        }

        [Test]
        public static void NoPath_NotRecognized()
        {
            Svc s = new Svc();
            s.Add("ASMSvc", "Running", "Auto", null, null, null, false);
            Assert.True(!ConflictServices.Scan(s)[0].Recognized, "a service we cannot identify is never touched");
        }

        [Test]
        public static void AnyNeedsAction_IgnoresUnrecognized()
        {
            Svc s = new Svc();
            s.Add("ASMSvc", "Running", "Auto", @"C:\Program Files\Contoso\ASMSvc.exe", "Contoso", "x", false);
            Assert.True(!ConflictServices.AnyNeedsAction(ConflictServices.Scan(s)), "nothing offered");
        }

        // ---- M3: state recording ----

        [Test]
        public static void UnreadableStartMode_RefusesToDisable()
        {
            Svc s = new Svc();
            s.Add("ASMSvc", "Running", null, Store + @"sysmonitorservice.inf_amd64_1\AcerSystemCentralService.exe", "Acer Incorporated", "x", false);
            Config cfg = new Config();
            DisableOutcome o = ConflictServices.DisableAll(s, cfg, delegate { return true; }, null, "t");
            Assert.Equal(0, s.Log.Count);
            Assert.Equal(1, o.Failed);
            Assert.Equal(0, cfg.ServiceUndo.Count);
        }

        [Test]
        public static void DelayedAutoStart_IsRecordedAndRestored()
        {
            Svc s = new Svc();
            s.Add("ASMSvc", "Running", "Auto", Store + @"sysmonitorservice.inf_amd64_1\AcerSystemCentralService.exe", "Acer Incorporated", "x", true);
            Config cfg = new Config();
            ConflictServices.DisableAll(s, cfg, delegate { return true; }, null, "t");
            Assert.True(cfg.ServiceUndo[0].PrevDelayed, "delay recorded");
            ConflictServices.UndoAll(s, cfg, delegate { return true; }, null);
            Assert.True(s.Log.Contains("mode:ASMSvc:AutomaticDelayed"), "restored with the delay: " + string.Join(",", s.Log.ToArray()));
        }

        // ---- M4: the config list is replaced, never mutated under the serializer ----

        [Test]
        public static void DisableAll_ReplacesTheListInsteadOfMutatingIt()
        {
            Svc s = ThisPcServices();
            Config cfg = new Config();
            List<ServiceUndo> before = cfg.ServiceUndo;
            ConflictServices.DisableAll(s, cfg, delegate { return true; }, null, "t");
            Assert.True(!object.ReferenceEquals(before, cfg.ServiceUndo), "a new list object");
            Assert.Equal(0, before.Count);
            Assert.Equal(5, cfg.ServiceUndo.Count);
        }

        // ---- I7: undo records persisted at the moment of disabling (installer cancel) ----

        [Test]
        public static void UndoFile_RoundTrips()
        {
            List<ServiceUndo> l = new List<ServiceUndo>();
            ServiceUndo u = new ServiceUndo();
            u.Name = "ASMSvc"; u.PrevStartMode = "Auto"; u.WasRunning = true; u.PrevDelayed = true; u.Utc = "t";
            l.Add(u);
            string json = ServiceUndoFile.ToJson(l);
            List<ServiceUndo> back = ServiceUndoFile.FromJson(json);
            Assert.Equal(1, back.Count);
            Assert.Equal("ASMSvc", back[0].Name);
            Assert.True(back[0].WasRunning && back[0].PrevDelayed, "flags");
        }

        [Test]
        public static void UndoFile_GarbageReadsEmpty()
        {
            Assert.Equal(0, ServiceUndoFile.FromJson("lixo").Count);
            Assert.Equal(0, ServiceUndoFile.FromJson(null).Count);
        }

        [Test]
        public static void UndoStore_SaveLoadClear_AndTheDisablePersistsEachStep()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-undo-" + Guid.NewGuid().ToString("N"));
            try
            {
                Forja.Platform.UndoStore store = new Forja.Platform.UndoStore(dir, false);
                Assert.Equal(0, store.Load().Count);
                Svc s = ThisPcServices();
                Config cfg = new Config();
                // the detached services layer persists with store.Save after every change
                ConflictServices.DisableAll(s, cfg, delegate { return store.Save(cfg.ServiceUndo); }, null, "t");
                Assert.Equal(5, store.Load().Count);
                // a cancelled installer: a later run merges the records into the real config
                Config real = new Config();
                EnvApply.MergeServiceUndo(real, store.Load());
                Assert.Equal(5, real.ServiceUndo.Count);
                Assert.True(store.Clear(), "cleared after the merge");
                Assert.Equal(0, store.Load().Count);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
