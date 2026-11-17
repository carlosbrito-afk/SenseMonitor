using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Fix round 2 (re-review 1): start-up recovery never writes to an unconfirmed model, the marker says which fans
    // were written and on which machine, a failing recovery backs off across starts, trusted state files are only
    // replayed on the machine that wrote them, undo only for Acer's five services.
    public static class RecoveryRoundTwoTests
    {
        // A firmware that remembers the layout it was given when Auto was written.
        private sealed class LayoutFw : IFirmware, IFirmwareExt, IFirmwareLayout
        {
            public FanLayout Layout { get; set; }
            public readonly List<string> Calls = new List<string>();
            public bool AutoOk = true;
            public bool Available { get { return true; } }
            public SensorReading ReadSensors() { return new SensorReading(); }
            public PerfMode? GetMode() { return PerfMode.DiaADia; }
            public bool SetMode(PerfMode m) { return true; }
            public bool SetFanAuto() { Calls.Add("auto:" + FanMarkerData.LayoutText(Layout ?? FanLayout.Both)); return AutoOk; }
            public Action OnPercent;
            public bool SetFanPercent(int a, int b) { if (OnPercent != null) OnPercent(); return true; }
            public bool SetFanMax() { return true; }
            public bool? IsFanMax() { return false; }
            public bool GetFanBehavior(out FanMode? cpu, out FanMode? gpu)
            {
                // not Auto: the previous run left it off Auto
                cpu = FanMode.Custom;
                gpu = FanMode.Custom;
                return true;
            }
        }

        private sealed class MemFlag : IFlag
        {
            public bool Set_;
            public bool IsSet { get { return Set_; } }
            public bool Set() { Set_ = true; return true; }
            public bool Clear() { Set_ = false; return true; }
        }

        private sealed class MemFanMarker : IFanMarker
        {
            public bool Present;
            public FanMarkerData Data = new FanMarkerData();
            public bool IsSet { get { return Present; } }
            public bool Set() { Present = true; return true; }
            public bool Clear() { Present = false; Data = new FanMarkerData(); return true; }
            public FanMarkerData Read() { return Data; }
            public bool Set(FanLayout layout, string fingerprint)
            {
                Present = true;
                Data.Layout = FanMarkerData.LayoutText(layout);
                Data.Fingerprint = fingerprint;
                return true;
            }
            public bool Update(FanMarkerData d) { Data = d; return true; }
        }

        private sealed class ListJournal : IJournal
        {
            public readonly List<string> Lines = new List<string>();
            public void Write(string kind, string text) { Lines.Add(kind + " " + text); }
        }

        private static GuardedFirmware Guard(LayoutFw fw)
        {
            return new GuardedFirmware(fw, NullJournal.Instance, null, false, null, null);
        }

        private static RecoveryContext Ctx(bool confirmed, FanLayout layout, string fp)
        {
            RecoveryContext c = new RecoveryContext();
            c.FanControlConfirmed = confirmed;
            c.ProfileLayout = layout;
            c.Fingerprint = fp;
            return c;
        }

        // ---- marker data ----

        [Test]
        public static void MarkerData_RoundTrips()
        {
            FanMarkerData d = new FanMarkerData();
            d.Layout = "cpu";
            d.Fingerprint = "nitro anv15-51|v1.60";
            d.Fails = 3;
            d.NextUtcTicks = 123456789L;
            d.Alerted = true;
            FanMarkerData b = FanMarkerData.Parse(d.ToText());
            Assert.Equal("cpu", b.Layout);
            Assert.Equal("nitro anv15-51|v1.60", b.Fingerprint);
            Assert.Equal(3, b.Fails);
            Assert.Equal(123456789L, b.NextUtcTicks);
            Assert.True(b.Alerted, "alerted");
            Assert.True(b.GetLayout() == FanLayout.CpuOnly, "layout object");
        }

        [Test]
        public static void MarkerData_OldTimestampMarker_MeansNothingRecorded()
        {
            FanMarkerData b = FanMarkerData.Parse("2026-11-17 10:00:00\r\n");
            Assert.True(b.GetLayout() == null, "no layout");
            Assert.Null(b.Fingerprint);
            Assert.Equal(0, b.Fails);
        }

        [Test]
        public static void MarkerData_GarbageValuesAreIgnored()
        {
            FanMarkerData b = FanMarkerData.Parse("layout=everything\r\nfails=-4\r\nnext=abc\r\nfp=\r\n");
            Assert.True(b.GetLayout() == null, "unknown layout");
            Assert.Equal(0, b.Fails);
            Assert.Equal(0L, b.NextUtcTicks);
        }

        [Test]
        public static void MarkerData_LayoutText_CoversTheThree()
        {
            Assert.Equal("cpu", FanMarkerData.LayoutText(FanLayout.CpuOnly));
            Assert.Equal("gpu", FanMarkerData.LayoutText(FanLayout.GpuOnly));
            Assert.Equal("both", FanMarkerData.LayoutText(FanLayout.Both));
            Assert.Null(FanMarkerData.LayoutText(null));
        }

        // ---- N1: start-up recovery ----

        [Test]
        public static void UncleanSession_NoMarker_NeverTested_NoWrite()
        {
            LayoutFw fw = new LayoutFw();
            MemFlag session = new MemFlag();
            session.Set_ = true;
            ListJournal j = new ListJournal();
            RecoveryResult r = StartupRecovery.Run(Guard(fw), new MemFanMarker(), session, j, Ctx(false, null, "x|y"));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!r.Wrote, "nothing written");
            Assert.True(r.Needed, "still reported");
        }

        [Test]
        public static void UncleanSession_NoMarker_ConfirmedModel_WritesOnlyTheProfileFans()
        {
            LayoutFw fw = new LayoutFw();
            MemFlag session = new MemFlag();
            session.Set_ = true;
            RecoveryResult r = StartupRecovery.Run(Guard(fw), new MemFanMarker(), session, new ListJournal(), Ctx(true, FanLayout.CpuOnly, "x|y"));
            Assert.Equal("auto:cpu", string.Join("|", fw.Calls.ToArray()));
            Assert.True(r.Wrote, "wrote");
        }

        [Test]
        public static void Marker_WithLayoutCpu_WritesCpuOnlyAuto_EvenOnAnUnconfirmedModel()
        {
            LayoutFw fw = new LayoutFw();
            MemFanMarker m = new MemFanMarker();
            m.Set(FanLayout.CpuOnly, "x|y");
            RecoveryResult r = StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), Ctx(false, null, "x|y"));
            Assert.Equal("auto:cpu", string.Join("|", fw.Calls.ToArray()));
            Assert.True(r.Wrote, "wrote");
        }

        [Test]
        public static void Marker_WithoutLayout_Unconfirmed_NoWrite_AndDiscarded()
        {
            LayoutFw fw = new LayoutFw();
            MemFanMarker m = new MemFanMarker();
            m.Set(); // an older marker: no layout recorded
            ListJournal j = new ListJournal();
            StartupRecovery.Run(Guard(fw), m, new MemFlag(), j, Ctx(false, null, "x|y"));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "discarded: nobody can say which fans were written, and it must not loop");
        }

        [Test]
        public static void Marker_FromAnotherMachine_Discarded_NoWrite()
        {
            LayoutFw fw = new LayoutFw();
            MemFanMarker m = new MemFanMarker();
            m.Set(FanLayout.Both, "predator phn16-72|v1.17");
            StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), Ctx(true, FanLayout.Both, "nitro anv15-51|v1.60"));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "a roamed marker is dropped");
        }

        [Test]
        public static void FailingRecovery_BacksOffAcrossStarts_AndAlertsOnce()
        {
            LayoutFw fw = new LayoutFw();
            fw.AutoOk = false;
            MemFanMarker m = new MemFanMarker();
            m.Set(FanLayout.Both, "x|y");
            long now = 1000000000L;
            RecoveryContext c = Ctx(true, FanLayout.Both, "x|y");
            c.UtcTicks = delegate { return now; };

            RecoveryResult first = StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), c);
            Assert.True(first.Alert, "the first failure alerts");
            Assert.Equal(1, m.Data.Fails);
            Assert.True(m.Present, "marker kept");

            now += 10; // a restart a moment later: inside the 2 s window
            RecoveryResult second = StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), c);
            Assert.True(second.Skipped, "postponed");
            Assert.True(!second.Alert, "no alert");
            Assert.Equal(1, fw.Calls.Count);

            now += FanMarkerData.BackoffTicks(1) + 10; // window over
            RecoveryResult third = StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), c);
            Assert.Equal(2, fw.Calls.Count);
            Assert.True(!third.Alert, "alerted only once");
            Assert.Equal(2, m.Data.Fails);
        }

        [Test]
        public static void Recovery_LegacyFourArgumentCall_KeepsTheOldBehaviour()
        {
            LayoutFw fw = new LayoutFw();
            MemFlag session = new MemFlag();
            session.Set_ = true;
            RecoveryResult r = StartupRecovery.Run(Guard(fw), new MemFlag(), session, new ListJournal());
            Assert.True(r.Wrote, "wrote like before");
        }

        // ---- the guard records the layout and the machine when it raises the marker ----

        [Test]
        public static void Guard_RaisesTheMarkerWithTheLayoutAndTheFingerprint()
        {
            LayoutFw fw = new LayoutFw();
            fw.Layout = FanLayout.CpuOnly;
            MemFanMarker m = new MemFanMarker();
            GuardedFirmware g = new GuardedFirmware(fw, NullJournal.Instance, m, false, null, null);
            g.Fingerprint = "nitro anv15-51|v1.60";
            Assert.True(g.SetFanPercent(60, 60), "write ok");
            Assert.True(m.Present, "raised");
            Assert.Equal("cpu", m.Data.Layout);
            Assert.Equal("nitro anv15-51|v1.60", m.Data.Fingerprint);
        }

        // ---- Fix round 3 (m11): a fan marker survives a BIOS-only change on the same model ----

        [Test]
        public static void Marker_AfterABiosUpdate_StillHandsBackWithTheRecordedMask()
        {
            LayoutFw fw = new LayoutFw();
            MemFanMarker m = new MemFanMarker();
            m.Set(FanLayout.CpuOnly, "nitro anv15-51|v1.60");
            // the profile is stale after the BIOS change: not confirmed, and the fingerprint now carries the new BIOS
            RecoveryResult r = StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), Ctx(false, null, "nitro anv15-51|v1.61"));
            Assert.Equal("auto:cpu", string.Join("|", fw.Calls.ToArray()));
            Assert.True(r.Wrote, "wrote");
        }

        [Test]
        public static void Marker_AnotherModel_StillDiscarded_EvenWithTheSameBios()
        {
            LayoutFw fw = new LayoutFw();
            MemFanMarker m = new MemFanMarker();
            m.Set(FanLayout.Both, "predator phn16-72|v1.60");
            StartupRecovery.Run(Guard(fw), m, new MemFlag(), new ListJournal(), Ctx(true, FanLayout.Both, "nitro anv15-51|v1.60"));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "dropped");
        }

        [Test]
        public static void SameModel_ComparesOnlyTheModelPart()
        {
            Assert.True(MachineFingerprint.SameModel("nitro anv15-51|v1.60", "nitro anv15-51|v1.61"), "bios update");
            Assert.True(MachineFingerprint.SameModel("Nitro ANV15-51|v1.60", "nitro anv15-51|v1.60"), "case");
            Assert.True(!MachineFingerprint.SameModel("nitro anv15-51|v1.60", "nitro anv15-52|v1.60"), "other model");
            Assert.True(!MachineFingerprint.SameModel(null, "a|b"), "unknown");
            Assert.True(!MachineFingerprint.SameModel("|v1", "|v1"), "no model at all is no identity");
        }

        // ---- Fix round 3 (m10): the re-raised marker keeps the layout and the machine ----

        [Test]
        public static void Guard_ReRaisedMarker_KeepsTheLayoutAndTheFingerprint()
        {
            LayoutFw fw = new LayoutFw();
            fw.Layout = FanLayout.CpuOnly;
            MemFanMarker m = new MemFanMarker();
            // an Auto write that finished while this Custom write was in flight clears the marker
            fw.OnPercent = delegate { m.Clear(); };
            GuardedFirmware g = new GuardedFirmware(fw, NullJournal.Instance, m, false, null, null);
            g.Fingerprint = "nitro anv15-51|v1.60";
            Assert.True(g.SetFanPercent(60, 60), "write ok");
            Assert.True(m.Present, "re-asserted after the clear");
            Assert.Equal("cpu", m.Data.Layout);
            Assert.Equal("nitro anv15-51|v1.60", m.Data.Fingerprint);
        }

        [Test]
        public static void MarkerUnion_Rules()
        {
            Assert.Equal("cpu", FanMarkerData.Union("cpu", "cpu"));
            Assert.Equal("both", FanMarkerData.Union("cpu", "gpu"));
            Assert.Equal("both", FanMarkerData.Union("both", "cpu"));
            Assert.Equal("gpu", FanMarkerData.Union(null, "gpu"));
            Assert.Equal("cpu", FanMarkerData.Union("cpu", null));
            Assert.Null(FanMarkerData.Union(null, null));
        }

        // ---- mode marker: fingerprint, announced mode, never Turbo ----

        private sealed class MemMode : IModeMarker
        {
            public bool Present;
            public PerfMode? Value;
            public string Fp;
            public bool Exists { get { return Present; } }
            public bool Set(PerfMode before) { return Set(before, null); }
            public bool Set(PerfMode before, string fingerprint) { Present = true; Value = before; Fp = fingerprint; return true; }
            public PerfMode? Read() { return Value; }
            public string ReadFingerprint() { return Fp; }
            public bool Clear() { Present = false; Value = null; return true; }
        }

        private static ModeRecoveryContext ModeCtx(string fp, params PerfMode[] allowed)
        {
            ModeRecoveryContext c = new ModeRecoveryContext();
            c.Fingerprint = fp;
            c.Allowed = delegate(PerfMode m) { return new List<PerfMode>(allowed).Contains(m); };
            return c;
        }

        [Test]
        public static void ModeMarker_OtherMachine_DiscardedNoWrite()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Turbo;
            MemMode m = new MemMode();
            m.Set(PerfMode.Silencioso, "other|v1");
            ModeRecovery.Run(fw, m, null, ModeCtx("nitro anv15-51|v1.60", PerfMode.Silencioso, PerfMode.DiaADia));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "discarded");
        }

        [Test]
        public static void ModeMarker_NoFingerprintRecorded_Discarded()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Turbo;
            MemMode m = new MemMode();
            m.Set(PerfMode.Silencioso, null);
            ModeRecovery.Run(fw, m, null, ModeCtx("x|y", PerfMode.Silencioso, PerfMode.DiaADia));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "an unlabelled marker is not trusted");
        }

        [Test]
        public static void ModeMarker_SameMachine_Replayed()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            MemMode m = new MemMode();
            m.Set(PerfMode.Silencioso, "x|y");
            ModeRecovery.Run(fw, m, null, ModeCtx("x|y", PerfMode.Silencioso, PerfMode.DiaADia));
            Assert.Equal("mode:Silencioso", string.Join("|", fw.Calls.ToArray()));
            Assert.True(!m.Present, "cleared");
        }

        [Test]
        public static void ModeMarker_Turbo_NeverReplayed_BalancedInstead()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            MemMode m = new MemMode();
            m.Set(PerfMode.Turbo, "x|y");
            ModeRecovery.Run(fw, m, null, ModeCtx("x|y", PerfMode.Turbo, PerfMode.DiaADia));
            Assert.Equal("mode:DiaADia", string.Join("|", fw.Calls.ToArray()));
        }

        [Test]
        public static void ModeMarker_ModeNotAnnouncedByTheProfile_FallsBackToDiaADia()
        {
            // Fix round 3 (N4): the trusted marker says Desempenho but the profile only announces Dia a dia
            // (first run, no profile yet): Eco/Turbo must not stay set, so the balanced profile is written.
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            MemMode m = new MemMode();
            m.Set(PerfMode.Desempenho, "x|y");
            ModeRecovery.Run(fw, m, null, ModeCtx("x|y", PerfMode.DiaADia));
            Assert.Equal("mode:DiaADia", string.Join("|", fw.Calls.ToArray()));
            Assert.True(!m.Present, "cleared");
        }

        [Test]
        public static void ModeMarker_NeitherTheModeNorDiaADiaAnnounced_NothingWritten()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            MemMode m = new MemMode();
            m.Set(PerfMode.Desempenho, "x|y");
            ModeRecovery.Run(fw, m, null, ModeCtx("x|y"));
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "discarded: not even the balanced profile is announced");
        }

        [Test]
        public static void ModeMarker_ReadOnlyMode_DiscardsInsteadOfKeepingForever()
        {
            FakeFirmware fw = new FakeFirmware();
            fw.ModeValue = PerfMode.Economico;
            MemMode m = new MemMode();
            m.Set(PerfMode.Silencioso, "x|y");
            ModeRecoveryContext c = ModeCtx("x|y", PerfMode.Silencioso, PerfMode.DiaADia);
            c.ReadOnly = true;
            ModeRecovery.Run(fw, m, null, c);
            Assert.Equal(0, fw.Calls.Count);
            Assert.True(!m.Present, "discarded: read-only mode never writes");
        }

        // ---- N3: undo only for Acer's five services ----

        private static ServiceUndo Rec(string name, string mode)
        {
            ServiceUndo u = new ServiceUndo();
            u.Name = name;
            u.PrevStartMode = mode;
            u.WasRunning = true;
            return u;
        }

        [Test]
        public static void Undo_Record_OnlyTheFiveNames_AndAutoOrManual()
        {
            Assert.True(ConflictServices.ValidRecord(Rec("ASMSvc", "Auto")), "known name");
            Assert.True(ConflictServices.ValidRecord(Rec("asmsvc", "Manual")), "case-insensitive");
            Assert.True(!ConflictServices.ValidRecord(Rec("WinDefend", "Auto")), "foreign service");
            Assert.True(!ConflictServices.ValidRecord(Rec("MpsSvc", "Disabled")), "foreign service");
            Assert.True(!ConflictServices.ValidRecord(Rec("ASMSvc", "Disabled")), "a Disabled record is never legitimate");
            Assert.True(!ConflictServices.ValidRecord(Rec("ASMSvc", null)), "no mode");
            Assert.True(!ConflictServices.ValidRecord(null), "null");
        }

        [Test]
        public static void Undo_ForeignRecordsInTheConfig_AreIgnored()
        {
            Config c = new Config();
            c.ServiceUndo.Add(Rec("WinDefend", "Auto"));
            c.ServiceUndo.Add(Rec("EventLog", "Disabled"));
            Svc s = new Svc();
            s.Add("WinDefend", @"C:\Program Files\Windows Defender\x.exe", "Microsoft");
            UndoOutcome o = ConflictServices.UndoAll(s, c, delegate { return true; }, null);
            Assert.Equal(0, s.Log.Count);
            Assert.Equal(0, o.Restored);
            Assert.Equal(0, c.ServiceUndo.Count);
        }

        [Test]
        public static void Undo_RecognitionIsRerunOnTheLiveService()
        {
            Config c = new Config();
            c.ServiceUndo.Add(Rec("ASMSvc", "Auto"));
            Svc s = new Svc();
            // the name now belongs to another vendor's binary
            s.Add("ASMSvc", @"C:\Program Files\Contoso\ASMSvc.exe", "Contoso Ltd");
            UndoOutcome o = ConflictServices.UndoAll(s, c, delegate { return true; }, null);
            Assert.Equal(0, s.Log.Count);
            Assert.Equal(0, o.Restored);
            Assert.Equal(0, c.ServiceUndo.Count);
        }

        [Test]
        public static void Undo_RealAcerService_StillRestored()
        {
            Config c = new Config();
            c.ServiceUndo.Add(Rec("ASMSvc", "Auto"));
            Svc s = new Svc();
            s.Add("ASMSvc", @"C:\WINDOWS\System32\DriverStore\FileRepository\sysmonitorservice.inf_amd64_1\x64\AcerSystemCentralService.exe", "Acer Incorporated");
            UndoOutcome o = ConflictServices.UndoAll(s, c, delegate { return true; }, null);
            Assert.Equal(1, o.Restored);
            Assert.True(s.Log.Contains("mode:ASMSvc:Automatic"), string.Join(",", s.Log.ToArray()));
        }

        [Test]
        public static void Merge_FiltersForeignRecords()
        {
            Config c = new Config();
            EnvApply.MergeServiceUndo(c, new List<ServiceUndo> { Rec("WinDefend", "Auto"), Rec("ASMSvc", "Auto"), Rec("AASSvc", "Disabled") });
            Assert.Equal(1, c.ServiceUndo.Count);
            Assert.Equal("ASMSvc", c.ServiceUndo[0].Name);
        }

        [Test]
        public static void UndoFile_FromJson_FiltersForeignRecords()
        {
            List<ServiceUndo> l = new List<ServiceUndo> { Rec("WinDefend", "Auto"), Rec("ASMSvc", "Manual") };
            List<ServiceUndo> back = ServiceUndoFile.FromJson(ServiceUndoFile.ToJson(l));
            Assert.Equal(1, back.Count);
            Assert.Equal("ASMSvc", back[0].Name);
        }

        // ---- m4: token match, not substring ----

        [Test]
        public static void Recognize_TokenNotSubstring()
        {
            ServiceInfo i = new ServiceInfo();
            i.PathName = @"C:\Program Files\Tracer\ASMSvc.exe";
            i.Company = "Placer Software";
            i.Product = "Racer";
            Assert.True(!ConflictServices.Recognize("ASMSvc", i), "tracer, placer and racer are not Acer");
            i.PathName = @"C:\Program Files\Acer\ASMSvc.exe";
            i.Company = "Acer Incorporated";
            Assert.True(ConflictServices.Recognize("ASMSvc", i), "the real thing");
        }

        // ---- m5: one or two samples never confirm ----

        [Test]
        public static void FewSamples_DoNotConfirmTheSensors()
        {
            DetectionRaw r = CapabilityTests.ThisPc();
            r.LiveSamples = new List<SensorReading> { r.Live };
            CapabilityProfile p = CapabilityBuilder.Build(DetectionResult.Interpret(r), new EnvTestResults(), "t");
            Assert.True(p.State(Cap.Sensors) != CapState.Confirmed, "one reading is not a judgement");
            Assert.True(p.State(Cap.FanControl) != CapState.Untested, "so no fan test");
        }

        private sealed class Svc : IServiceControl
        {
            public readonly Dictionary<string, ServiceInfo> Items = new Dictionary<string, ServiceInfo>();
            public readonly List<string> Log = new List<string>();

            public void Add(string name, string path, string company)
            {
                ServiceInfo i = new ServiceInfo();
                i.Name = name; i.DisplayName = name; i.State = "Stopped"; i.StartMode = "Disabled";
                i.PathName = path; i.Company = company; i.Product = company;
                Items[name] = i;
            }

            public ServiceInfo Query(string name) { ServiceInfo i; return Items.TryGetValue(name, out i) ? i : null; }
            public bool Stop(string name) { Log.Add("stop:" + name); return true; }
            public bool Start(string name) { Log.Add("start:" + name); return true; }
            public bool SetStartMode(string name, string mode) { Log.Add("mode:" + name + ":" + mode); return true; }
        }
    }
}
