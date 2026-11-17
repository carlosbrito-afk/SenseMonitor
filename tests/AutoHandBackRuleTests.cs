using Forja.Core;

namespace Forja.Tests
{
    // N5 (re-review 3): ONE decision for "may Auto be written to the fans now, and on which fans", shared by start-up
    // recovery, the exit paths and the uninstaller: only when a fan marker exists (its recorded mask, fingerprint-checked)
    // or the saved profile confirms fan control for this machine (existing fans only). Never on an untested notebook.
    public static class AutoHandBackRuleTests
    {
        private static FanMarkerData Marker(string layout, string fp)
        {
            FanMarkerData d = new FanMarkerData();
            d.Layout = layout;
            d.Fingerprint = fp;
            return d;
        }

        [Test]
        public static void NoMarker_NotConfirmed_NoWrite()
        {
            HandBackDecision h = AutoHandBackRule.Decide(false, null, false, null, "nitro anv15-51|v1.60");
            Assert.True(!h.Write, "nothing to hand back on an untested notebook");
            Assert.True(h.Kind == HandBackKind.Unconfirmed, "reason");
        }

        [Test]
        public static void NoMarker_Confirmed_WritesTheProfileFansOnly()
        {
            HandBackDecision h = AutoHandBackRule.Decide(false, null, true, FanLayout.CpuOnly, "x|y");
            Assert.True(h.Write, "confirmed");
            Assert.True(h.Layout == FanLayout.CpuOnly, "only the fan that exists");
        }

        [Test]
        public static void NoMarker_ConfirmedWithoutLayout_MeansBothFans()
        {
            HandBackDecision h = AutoHandBackRule.Decide(false, null, true, null, "x|y");
            Assert.True(h.Write, "confirmed");
            Assert.True(h.Layout == FanLayout.Both, "both");
        }

        [Test]
        public static void Marker_UnconfirmedModel_WritesTheRecordedMask()
        {
            HandBackDecision h = AutoHandBackRule.Decide(true, Marker("gpu", "x|y"), false, null, "x|y");
            Assert.True(h.Write, "a marker is evidence of a write");
            Assert.True(h.Layout == FanLayout.GpuOnly, "recorded mask");
            Assert.True(!h.BiosDiffers, "same machine");
        }

        [Test]
        public static void Marker_SameModelOtherBios_StillWritesTheRecordedMask()
        {
            HandBackDecision h = AutoHandBackRule.Decide(true, Marker("cpu", "nitro anv15-51|v1.60"), false, null, "nitro anv15-51|v1.61");
            Assert.True(h.Write, "bios update");
            Assert.True(h.Layout == FanLayout.CpuOnly, "recorded mask");
            Assert.True(h.BiosDiffers, "reported");
        }

        [Test]
        public static void Marker_OtherModel_Foreign_NoWrite()
        {
            HandBackDecision h = AutoHandBackRule.Decide(true, Marker("both", "predator phn16-72|v1.17"), true, FanLayout.Both, "nitro anv15-51|v1.60");
            Assert.True(!h.Write, "another machine");
            Assert.True(h.Kind == HandBackKind.Foreign, "reason");
        }

        [Test]
        public static void Marker_WithoutFingerprintOrMachineUnknown_IsAccepted()
        {
            Assert.True(AutoHandBackRule.Decide(true, Marker("cpu", null), false, null, "x|y").Write, "older marker");
            Assert.True(AutoHandBackRule.Decide(true, Marker("cpu", "x|y"), false, null, null).Write, "identity could not be read");
        }

        [Test]
        public static void Marker_WithoutLayout_OnlyWhereConfirmed()
        {
            HandBackDecision no = AutoHandBackRule.Decide(true, Marker(null, "x|y"), false, null, "x|y");
            Assert.True(!no.Write, "nobody can say which fans were written");
            Assert.True(no.Kind == HandBackKind.NoLayout, "reason");
            HandBackDecision yes = AutoHandBackRule.Decide(true, Marker(null, "x|y"), true, FanLayout.GpuOnly, "x|y");
            Assert.True(yes.Write, "the profile says which fans exist");
            Assert.True(yes.Layout == FanLayout.GpuOnly, "profile fans");
            Assert.True(!AutoHandBackRule.Decide(true, null, false, null, "x|y").Write, "a flag with no data, unconfirmed");
        }

        [Test]
        public static void ConfirmedBy_FreshProfileWithFanControl_Only()
        {
            CapabilityProfile p = CapabilityTests.Build(CapabilityTests.ThisPc(), CapabilityTests.AllPassedThisPc());
            Assert.True(AutoHandBackRule.ConfirmedBy(p, p.Fingerprint), "fresh and confirmed");
            Assert.True(!AutoHandBackRule.ConfirmedBy(p, "other|bios"), "stale after a BIOS or machine change");
            Assert.True(!AutoHandBackRule.ConfirmedBy(p, null), "identity unknown");
            Assert.True(!AutoHandBackRule.ConfirmedBy(null, "x|y"), "never tested");
        }
    }
}
