namespace Forja.Core
{
    public enum HandBackKind
    {
        // Auto may be written, on Layout.
        Write,
        // The marker was written on another machine: dropped, nothing written.
        Foreign,
        // No marker and this notebook's fan control is not confirmed: nothing to hand back, nothing written.
        Unconfirmed,
        // A marker that does not say which fans were written, on a notebook that is not confirmed: no safe mask.
        NoLayout
    }

    public sealed class HandBackDecision
    {
        public HandBackKind Kind;
        // The fans the Auto write must address (set when Kind is Write).
        public FanLayout Layout;
        // The marker is from the same model but another BIOS (a BIOS update): still handed back, with a note.
        public bool BiosDiffers;

        public bool Write { get { return Kind == HandBackKind.Write; } }
    }

    // ONE decision for "may Auto be written to the fans now, and on which fans", shared by start-up recovery, the
    // exit/suspend/crash paths and the uninstaller. Auto is written only when
    //  (a) a fan marker exists (this app wrote Custom/Máximo): with the mask RECORDED in it, never a both-fan mask on a
    //      one-fan notebook; a marker from another machine, or one that does not say which fans on a notebook whose
    //      fan control is not confirmed, never writes; or
    //  (b) the saved profile confirms the fan control of THIS notebook (current model and BIOS): only the fans it has.
    // Never on an untested, stale or Unavailable notebook without a marker.
    public static class AutoHandBackRule
    {
        public static HandBackDecision Decide(bool markerSet, FanMarkerData marker, bool fanControlConfirmed, FanLayout profileLayout, string fingerprint)
        {
            return Decide(markerSet, marker, fanControlConfirmed, profileLayout, fingerprint, null);
        }

        // processLayout: the fans THIS process chose to address (FanLayoutStore.CurrentIfSet; null in a process that never chose,
        // such as the uninstaller: then there is no fallback and the hand-back needs a recorded or profile-confirmed layout). Used only when a marker of this machine
        // exists but does not say which fans (or could not be read twice): the in-process paths (exit, crash, uninstall) then
        // hand back what this process itself was writing, instead of skipping the hand-back. It never overrides a recorded
        // layout and never makes another machine's marker write.
        public static HandBackDecision Decide(bool markerSet, FanMarkerData marker, bool fanControlConfirmed, FanLayout profileLayout, string fingerprint, FanLayout processLayout)
        {
            HandBackDecision h = new HandBackDecision();
            if (!markerSet)
            {
                if (!fanControlConfirmed)
                {
                    h.Kind = HandBackKind.Unconfirmed;
                    return h;
                }
                h.Kind = HandBackKind.Write;
                h.Layout = profileLayout ?? FanLayout.Both;
                return h;
            }

            string mfp = marker == null ? null : marker.Fingerprint;
            if (!string.IsNullOrEmpty(mfp) && !string.IsNullOrEmpty(fingerprint) && !MachineFingerprint.Same(mfp, fingerprint))
            {
                // The same model with another BIOS is the same notebook after an update; anything else is another machine.
                if (!MachineFingerprint.SameModel(mfp, fingerprint))
                {
                    h.Kind = HandBackKind.Foreign;
                    return h;
                }
                h.BiosDiffers = true;
            }

            FanLayout layout = marker == null ? null : marker.GetLayout();
            if (layout == null && fanControlConfirmed) layout = profileLayout ?? FanLayout.Both;
            if (layout == null) layout = processLayout;
            if (layout == null)
            {
                h.Kind = HandBackKind.NoLayout;
                return h;
            }
            h.Kind = HandBackKind.Write;
            h.Layout = layout;
            return h;
        }

        // The saved profile confirms fan control for THIS machine: fresh (model + BIOS match) and the capability confirmed.
        public static bool ConfirmedBy(CapabilityProfile env, string fingerprint)
        {
            if (env == null) return false;
            if (!MachineFingerprint.Same(env.Fingerprint, fingerprint)) return false;
            return env.Allows(Cap.FanControl);
        }
    }
}
