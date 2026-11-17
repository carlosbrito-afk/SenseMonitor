using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Acer's own services can write to the same fans and profiles as this app: the wizard may stop them
    // and set them to Disabled ONLY on the user's click, remembers what each was, and Settings can undo it.
    public static class ConflictServicesTests
    {
        private sealed class FakeServices : IServiceControl
        {
            public readonly Dictionary<string, ServiceInfo> Items = new Dictionary<string, ServiceInfo>();
            public readonly List<string> Log = new List<string>();
            public readonly HashSet<string> FailStop = new HashSet<string>();
            public readonly HashSet<string> FailMode = new HashSet<string>();
            public readonly HashSet<string> FailStart = new HashSet<string>();

            public void Add(string name, string state, string startMode)
            {
                ServiceInfo i = new ServiceInfo();
                i.Name = name;
                i.DisplayName = name + " (exibição)";
                i.State = state;
                i.StartMode = startMode;
                i.PathName = @"C:\Program Files\Acer\" + name + @"\" + name + ".exe";
                i.Company = "Acer Incorporated";
                i.Product = "Acer " + name;
                Items[name] = i;
            }

            public ServiceInfo Query(string name)
            {
                ServiceInfo i;
                if (!Items.TryGetValue(name, out i)) return null;
                ServiceInfo copy = new ServiceInfo();
                copy.Name = i.Name;
                copy.DisplayName = i.DisplayName;
                copy.State = i.State;
                copy.StartMode = i.StartMode;
                copy.Delayed = i.Delayed;
                copy.PathName = i.PathName;
                copy.Company = i.Company;
                copy.Product = i.Product;
                return copy;
            }

            public bool Stop(string name)
            {
                Log.Add("stop:" + name);
                if (!Items.ContainsKey(name) || FailStop.Contains(name)) return false;
                Items[name].State = "Stopped";
                return true;
            }

            public bool Start(string name)
            {
                Log.Add("start:" + name);
                if (!Items.ContainsKey(name) || FailStart.Contains(name)) return false;
                Items[name].State = "Running";
                return true;
            }

            public bool SetStartMode(string name, string mode)
            {
                Log.Add("mode:" + name + ":" + mode);
                if (!Items.ContainsKey(name) || FailMode.Contains(name)) return false;
                Items[name].StartMode = mode == "Automatic" ? "Auto" : mode;
                return true;
            }
        }

        private static FakeServices AllFive()
        {
            FakeServices f = new FakeServices();
            f.Add("ASMSvc", "Running", "Auto");
            f.Add("AASSvc", "Running", "Auto");
            f.Add("AcerServiceSvc", "Stopped", "Manual");
            f.Add("AcerLightingService", "Running", "Auto");
            f.Add("AcerDeviceEnablingServiceV2", "Running", "Auto");
            return f;
        }

        private static List<string> persisted(Config c)
        {
            List<string> l = new List<string>();
            foreach (ServiceUndo u in c.ServiceUndo) l.Add(u.Name + "=" + u.PrevStartMode + "/" + (u.WasRunning ? "run" : "stop"));
            return l;
        }

        [Test]
        public static void ServiceNames_AreTheFiveOfTheResearch()
        {
            List<string> n = new List<string>(ConflictServices.Names);
            Assert.Equal(5, n.Count);
            foreach (string s in new string[] { "ASMSvc", "AASSvc", "AcerServiceSvc", "AcerLightingService", "AcerDeviceEnablingServiceV2" })
            {
                Assert.True(n.Contains(s), s);
            }
        }

        [Test]
        public static void Scan_ListsOnlyInstalledServices()
        {
            FakeServices f = new FakeServices();
            f.Add("ASMSvc", "Running", "Auto");
            f.Add("Spooler", "Running", "Auto"); // not an Acer service name: never looked at
            List<ConflictItem> l = ConflictServices.Scan(f);
            Assert.Equal(1, l.Count);
            Assert.Equal("ASMSvc", l[0].Name);
            Assert.True(l[0].Running, "running");
            Assert.True(l[0].NeedsAction, "needs action");
        }

        [Test]
        public static void Scan_StoppedButNotDisabled_StillNeedsAction()
        {
            FakeServices f = new FakeServices();
            f.Add("AcerServiceSvc", "Stopped", "Manual");
            ConflictItem i = ConflictServices.Scan(f)[0];
            Assert.True(!i.Running, "not running");
            Assert.True(i.NeedsAction, "it can still be started by Windows");
        }

        [Test]
        public static void Scan_StoppedAndDisabled_NoAction()
        {
            FakeServices f = new FakeServices();
            f.Add("ASMSvc", "Stopped", "Disabled");
            ConflictItem i = ConflictServices.Scan(f)[0];
            Assert.True(!i.NeedsAction, "already handled");
            Assert.True(!ConflictServices.AnyNeedsAction(ConflictServices.Scan(f)), "nothing to do");
        }

        [Test]
        public static void Scan_NoServices_Empty()
        {
            Assert.Equal(0, ConflictServices.Scan(new FakeServices()).Count);
        }

        [Test]
        public static void Disable_SavesPreviousStateBeforeTouchingAnything()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            List<string> order = new List<string>();
            int persistCalls = 0;
            Func<bool> persist = delegate
            {
                persistCalls++;
                order.Add("persist:" + f.Log.Count); // how many service calls had happened by then
                return true;
            };
            DisableOutcome r = ConflictServices.DisableAll(f, c, persist, null, "2026-11-17T12:00:00Z");
            Assert.True(persistCalls >= 1, "persisted");
            Assert.Equal("persist:0", order[0]); // before the first service call
            Assert.Equal(5, c.ServiceUndo.Count);
            Assert.True(persisted(c).Contains("ASMSvc=Auto/run"), "ASMSvc previous state");
            Assert.True(persisted(c).Contains("AcerServiceSvc=Manual/stop"), "stopped one stays stopped on undo");
            Assert.Equal(5, r.Disabled);
            Assert.Equal(0, r.Failed);
            foreach (string name in ConflictServices.Names)
            {
                ServiceInfo i = f.Query(name);
                Assert.Equal("Disabled", i.StartMode);
                Assert.Equal("Stopped", i.State);
            }
        }

        [Test]
        public static void Disable_PersistFails_ServiceUntouched()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            DisableOutcome r = ConflictServices.DisableAll(f, c, delegate { return false; }, null, "t");
            Assert.Equal(0, f.Log.Count);
            Assert.Equal(0, r.Disabled);
            Assert.Equal(5, r.Failed);
            Assert.Equal(0, c.ServiceUndo.Count);
        }

        [Test]
        public static void Disable_StopFails_Reported_ModeStillDisabled()
        {
            FakeServices f = AllFive();
            f.FailStop.Add("ASMSvc");
            Config c = new Config();
            DisableOutcome r = ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            Assert.Equal(1, r.Failed);
            Assert.True(r.Messages.Count > 0, "a message explains it");
            Assert.Equal("Disabled", f.Query("ASMSvc").StartMode);
        }

        [Test]
        public static void Disable_ModeFails_RecordRemoved_NothingToUndo()
        {
            FakeServices f = AllFive();
            f.FailMode.Add("AASSvc");
            Config c = new Config();
            DisableOutcome r = ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            Assert.Equal(1, r.Failed);
            Assert.True(!persisted(c).Exists(delegate(string s) { return s.StartsWith("AASSvc"); }), "no record for a service that was not changed");
        }

        [Test]
        public static void Disable_OnlyServicesThatNeedIt()
        {
            FakeServices f = new FakeServices();
            f.Add("ASMSvc", "Stopped", "Disabled");
            f.Add("AASSvc", "Running", "Auto");
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            Assert.Equal(1, c.ServiceUndo.Count);
            Assert.True(!f.Log.Contains("mode:ASMSvc:Disabled"), "an already disabled service is left alone");
        }

        [Test]
        public static void Disable_Twice_KeepsTheOriginalState()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            f.Items["ASMSvc"].StartMode = "Manual"; // something re-enabled it (Windows Update)
            f.Items["ASMSvc"].State = "Running";
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            Assert.True(persisted(c).Contains("ASMSvc=Auto/run"), "the first state is the one to undo to: " + string.Join(",", persisted(c).ToArray()));
            Assert.Equal(5, c.ServiceUndo.Count);
        }

        [Test]
        public static void Undo_RestoresModeAndRunningState()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            UndoOutcome u = ConflictServices.UndoAll(f, c, delegate { return true; }, null);
            Assert.Equal(5, u.Restored);
            Assert.Equal(0, u.Failed);
            Assert.Equal(0, c.ServiceUndo.Count);
            Assert.Equal("Auto", f.Query("ASMSvc").StartMode);
            Assert.Equal("Running", f.Query("ASMSvc").State);
            Assert.Equal("Manual", f.Query("AcerServiceSvc").StartMode);
            Assert.Equal("Stopped", f.Query("AcerServiceSvc").State); // it was stopped before: not started
        }

        [Test]
        public static void Undo_FailureKeepsTheRecord()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            f.FailMode.Add("ASMSvc");
            UndoOutcome u = ConflictServices.UndoAll(f, c, delegate { return true; }, null);
            Assert.Equal(1, u.Failed);
            Assert.Equal(1, c.ServiceUndo.Count);
            Assert.Equal("ASMSvc", c.ServiceUndo[0].Name);
        }

        [Test]
        public static void Undo_ServiceGone_RecordDropped()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            f.Items.Remove("AASSvc");
            UndoOutcome u = ConflictServices.UndoAll(f, c, delegate { return true; }, null);
            Assert.Equal(0, c.ServiceUndo.Count);
            Assert.Equal(4, u.Restored);
        }

        [Test]
        public static void Undo_NothingRecorded_DoesNothing()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            UndoOutcome u = ConflictServices.UndoAll(f, c, delegate { return true; }, null);
            Assert.Equal(0, f.Log.Count);
            Assert.Equal(0, u.Restored);
        }

        [Test]
        public static void Undo_PersistFailureAfterwards_IsReported()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "t");
            UndoOutcome u = ConflictServices.UndoAll(f, c, delegate { return false; }, null);
            Assert.True(u.PersistFailed, "the user is told the record was not saved");
        }

        [Test]
        public static void UndoRecords_SurviveConfigRoundTrip()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            ConflictServices.DisableAll(f, c, delegate { return true; }, null, "2026-11-17T12:00:00Z");
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-undo-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                c.Save(path);
                Config back = Config.Load(path);
                Assert.Equal(5, back.ServiceUndo.Count);
                Assert.True(persisted(back).Contains("ASMSvc=Auto/run"), "state kept");
            }
            finally
            {
                foreach (string p in new string[] { path, path + ".bak", path + ".tmp" })
                {
                    try { System.IO.File.Delete(p); } catch (Exception) { }
                }
            }
        }

        [Test]
        public static void OlderConfigWithoutTheField_LoadsEmpty()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-old-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                System.IO.File.WriteAllText(path, "{\"FanMode\":\"Auto\"}");
                Config c = Config.Load(path);
                Assert.True(c.ServiceUndo != null && c.ServiceUndo.Count == 0, "empty list, not null");
            }
            finally
            {
                foreach (string p in new string[] { path, path + ".bak", path + ".tmp" })
                {
                    try { System.IO.File.Delete(p); } catch (Exception) { }
                }
            }
        }

        [Test]
        public static void Journal_RecordsEachChange()
        {
            FakeServices f = AllFive();
            Config c = new Config();
            List<string> lines = new List<string>();
            RecordingJournal j = new RecordingJournal(lines);
            ConflictServices.DisableAll(f, c, delegate { return true; }, j, "t");
            Assert.True(lines.Count >= 5, "one line per service at least: " + lines.Count);
            Assert.True(lines[0].StartsWith(JournalKind.Action), lines[0]);
        }

        private sealed class RecordingJournal : IJournal
        {
            private readonly List<string> lines;
            public RecordingJournal(List<string> lines) { this.lines = lines; }
            public void Write(string kind, string text) { lines.Add(kind + " " + text); }
        }

        [Test]
        public static void Explain_PlainWords()
        {
            string t = ConflictServices.Explanation;
            Assert.True(t.Contains("NitroSense") || t.Contains("PredatorSense"), "names the apps");
            Assert.True(t.Contains("desfazer") || t.Contains("Desfazer"), "mentions the undo");
            Assert.True(t.Contains("desinstal"), "says nothing is uninstalled");
        }
    }
}
