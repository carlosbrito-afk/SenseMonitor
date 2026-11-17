using System;
using System.IO;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The shared safety plumbing of one process: where the journal and the two flags live, whether
    // read-only mode is on, and how to wrap a WmiFirmware so its writes are journaled, blocked in
    // read-only mode and tracked by the fan marker. Everything defaults to the real files, so code
    // that never configured anything (the crash path, the isolated Auto writer) still journals and
    // clears the marker. The journal and the settings live in %APPDATA%\SenseMonitor; the files the elevated app TRUSTS
    // (fan marker, session flag, mode-test marker, service undo records) live in the verified admin-only folder
    // %ProgramData%\SenseMonitor\estado (StateDir), shared by the app and its sentinel.
    public static class Safety
    {
        public const string MarkerFileName = "ventoinha-custom.flag";
        public const string SessionFileName = "sessao-aberta.flag";
        public const string SourceApp = "app";
        public const string SourceSentinel = "sentinela";

        private static readonly object sync = new object();
        private static string source = SourceApp;
        private static bool readOnly;
        private static IJournal journal;
        private static IFlag marker;
        private static IFlag session;
        private static IModeMarker modeMarker;
        private static bool stateResolved;
        private static string stateDir;
        private static string fingerprint;
        private static Func<bool> ensureSentinel;

        // %APPDATA%\SenseMonitor, or the folder in SENSEMONITOR_DATA_DIR (lets a test run the helper
        // modes against an empty folder without touching the real journal and flags).
        // Test seam (the tests share the assembly): beats the environment variable.
        internal static string DirOverride
        {
            get { return UserData.Override; }
            set { UserData.Override = value; }
        }

        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(DirOverride)) return DirOverride;
                string over = Environment.GetEnvironmentVariable("SENSEMONITOR_DATA_DIR");
                if (!string.IsNullOrEmpty(over)) return over;
                return UserData.Roaming;
            }
        }

        // %ProgramData%\SenseMonitor\estado, VERIFIED (not a reparse point, owned by Administrators/SYSTEM, admin-only DACL;
        // a folder that fails is renamed aside without being opened and a fresh protected one is created, see
        // StateFolder), or null when it cannot be trusted. The files the elevated app trusts (fan marker, session flag,
        // mode-test marker) live here, never in the user-writable %APPDATA%. DirOverride (a test seam in the same
        // assembly) beats everything; SENSEMONITOR_DATA_DIR is honoured only by a process that is not elevated or is a
        // test host (HonourDataDirVariable): an elevated app always uses the verified folder, whatever the environment says.
        public static string StateDir
        {
            get
            {
                lock (sync)
                {
                    if (!string.IsNullOrEmpty(DirOverride)) return DirOverride;
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SENSEMONITOR_DATA_DIR"))
                        && HonourDataDirVariable(Cleaner.IsElevated(), IsTestHost())) return Dir;
                    if (!stateResolved)
                    {
                        stateResolved = true;
                        stateDir = StateFolder.Prepare(StateFolder.DefaultPath, Journal);
                        if (stateDir == null) Journal.Write(JournalKind.Config, "pasta de estado não confiável: marcadores indisponíveis; escritas manuais de ventoinha recusadas");
                    }
                    return stateDir;
                }
            }
        }

        // Whether SENSEMONITOR_DATA_DIR may redirect the trusted state files: an elevated process that is not a test host
        // must not let the environment (which a standard user can influence) pick the folder its markers are read from.
        internal static bool HonourDataDirVariable(bool elevated, bool testHost)
        {
            return !elevated || testHost;
        }

        private static bool IsTestHost()
        {
            return UserData.IsTestHost;
        }

        // MachineFingerprint of this machine (model + BIOS), set once at start by the app and the tools; written
        // into the markers so they are replayed only here. null = unknown.
        public static string Fingerprint
        {
            get { return fingerprint; }
            set { fingerprint = value; }
        }

        // "app" (default) or "sentinela"; set once at the start of the process, before Journal is used.
        public static void SetSource(string value)
        {
            lock (sync)
            {
                source = value;
                journal = null;
                marker = null;
                session = null;
                modeMarker = null;
            }
        }

        public static bool ReadOnly
        {
            get { return readOnly; }
            set { readOnly = value; }
        }

        // Called before a Custom/Máximo write (after the marker is set): the app starts the
        // sentinel here. false refuses the write.
        public static Func<bool> EnsureSentinel
        {
            get { return ensureSentinel; }
            set { ensureSentinel = value; }
        }

        public static IJournal Journal
        {
            get
            {
                lock (sync)
                {
                    if (journal == null) journal = new FileJournal(Path.Combine(Dir, "diario.log"), source, JournalRotation.DefaultMaxBytes, null);
                    return journal;
                }
            }
        }

        // ventoinha-custom.flag: exists from before the first Custom/Máximo write until Auto is confirmed.
        public static IFlag Marker
        {
            get
            {
                lock (sync)
                {
                    if (marker == null)
                    {
                        string d = StateDir;
                        marker = d == null ? (IFlag)new UnsafeFlag() : new FanMarker(Path.Combine(d, MarkerFileName));
                    }
                    return marker;
                }
            }
        }

        // sessao-aberta.flag: exists while an app run is in progress (cleared on a clean exit).
        public static IFlag Session
        {
            get
            {
                lock (sync)
                {
                    if (session == null)
                    {
                        string d = StateDir;
                        session = d == null ? (IFlag)new UnsafeFlag() : new FileFlag(Path.Combine(d, SessionFileName));
                    }
                    return session;
                }
            }
        }

        // modo-teste.flag: the profile read before the environment wizard's mode test, on this machine.
        public static IModeMarker ModeMarker
        {
            get
            {
                lock (sync)
                {
                    if (modeMarker == null)
                    {
                        string d = StateDir;
                        modeMarker = d == null ? (IModeMarker)new NullModeMarker() : new FileModeMarker(Path.Combine(d, FileModeMarker.FileName));
                    }
                    return modeMarker;
                }
            }
        }

        // The fans the fan marker says were written (null = not recorded): what the sentinel hands back.
        public static FanLayout MarkerLayout()
        {
            IFanMarker fm = Marker as IFanMarker;
            if (fm == null || !fm.IsSet) return null;
            return fm.Read().GetLayout();
        }

        // Whether Auto may be written to the fans now, and on which ones (AutoHandBackRule): the fan marker and the SAVED profile
        // (read without writing anything), for the exit, suspend, crash and uninstall paths. Call it off the UI thread: it
        // may read the machine identity from WMI when the process has not set it yet.
        public static HandBackDecision HandBackNow()
        {
            IFanMarker fm = Marker as IFanMarker;
            bool set = Marker.IsSet;
            // A failed or empty read (another thread's write has the file for a moment) is read once more before it is
            // believed; if the marker still says nothing, the fans THIS process addresses are handed back (m19).
            FanMarkerData md = set && fm != null ? FanMarkerReads.ReadWithRetry(fm.Read, delegate { Thread.Sleep(40); }) : null;
            CapabilityProfile env = FanLayoutStore.ReadSavedProfile();
            string fp = Fingerprint;
            // The machine identity costs a WMI query (slow when cold): only read it when something is compared with it (m20).
            bool needIdentity = env != null || (md != null && !string.IsNullOrEmpty(md.Fingerprint));
            if (string.IsNullOrEmpty(fp) && needIdentity)
            {
                try
                {
                    DetectionRaw id = EnvProbe.ReadIdentity();
                    fp = MachineFingerprint.Make(id == null ? null : id.Model, id == null ? null : id.Bios);
                    if (!string.IsNullOrEmpty(fp)) Fingerprint = fp;
                }
                catch (Exception)
                {
                    fp = null;
                }
            }
            bool confirmed = AutoHandBackRule.ConfirmedBy(env, fp);
            return AutoHandBackRule.Decide(set, md, confirmed, env == null ? null : env.Layout, fp, FanLayoutStore.CurrentIfSet);
        }

        // The firmware as the rest of the app must see it. One per thread that talks to WMI.
        public static GuardedFirmware Wrap(IFirmware raw)
        {
            return Wrap(raw, Journal);
        }

        // For a caller that retries (the sentinel): its own, long-lived journal wrapper.
        public static GuardedFirmware Wrap(IFirmware raw, IJournal journalOverride)
        {
            GuardedFirmware g = new GuardedFirmware(raw, journalOverride, Marker, readOnly, ensureSentinel, null);
            g.Fingerprint = fingerprint;
            return g;
        }

        // For the command-line tools: the switch, or the saved setting read WITHOUT creating or
        // changing any file (a missing or unreadable config means "not read-only").
        public static bool ReadOnlyRequestedFromDisk()
        {
            Config c = null;
            try
            {
                Config loaded;
                if (Config.TryLoad(Config.DefaultPath, out loaded) == ConfigRead.Ok) c = loaded;
            }
            catch (Exception)
            {
            }
            return ReadOnlyRequested(Environment.GetCommandLineArgs(), c);
        }

        // --somente-leitura on the command line, or the saved setting.
        public static bool ReadOnlyRequested(string[] args, Config config)
        {
            if (config != null && config.DiagnosticReadOnly) return true;
            if (args != null)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--somente-leitura", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }
}
