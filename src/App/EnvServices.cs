using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Forja.Core;
using Forja.Platform;

namespace Forja.App
{
    // The outcome of the fan test step: the routine's report, or why it could not start.
    internal sealed class FanRun
    {
        public FanValidationReport Report;
        public string Error;
    }

    // Everything the wizard needs from the machine, behind one interface so the window can be drawn and
    // driven without hardware (the screenshot harness uses a fake). The blocking calls are made from a
    // worker thread of the wizard; AcquireFirmware, ReleaseFirmware and ListenKey from the UI thread.
    internal interface IEnvServices
    {
        bool ReadOnly { get; }
        string AppVersion { get; }

        // Read-only detection (worker thread).
        DetectionRaw Detect();
        List<ConflictItem> ScanConflicts();
        // User-triggered (worker thread): stops and disables the Acer services, saving their state first.
        DisableOutcome DisableConflicts();

        // Firmware ownership (UI thread): while a writing step runs, the tray's own fan controller and mode writers
        // are paused (the host app hands the fans back to Automático first and writes nothing until Release).
        void AcquireFirmware();
        void ReleaseFirmware();

        // The writing steps (worker thread). Every write goes through the guarded firmware: journal,
        // read-only mode, fan marker and the fan sentinel. Each step records what it touches BEFORE the first write.
        FanRun RunFanTest(FanLayout layout, Action<FanProgress> progress);
        void CancelFanTest();
        ModeTestOutcome RunModeTest(List<PerfMode> candidates, Func<PerfMode, bool> confirm, Action<ModeTestProgress> progress);
        void CancelModeTest();

        // UI thread. null = the keyboard hook could not be installed.
        IDisposable ListenKey(KeyCandidate[] candidates, Action<KeyCandidate> onPress);

        // Cancel, close, exit, crash: undoes ONLY what this run changed (the fans of the tested layout to Automático,
        // the profile to the one read before the mode test). Nothing touched = no firmware write at all.
        void RestoreTouched();

        // Undo records of services stopped while no config existed (the installer's run); empty otherwise.
        List<ServiceUndo> ServiceUndoRecords { get; }

        // One line in the journal (no personal data).
        void Journal(string line);
    }

    // Listens for the candidate key codes only (the same NitroKey filter the app uses).
    internal sealed class KeyListen : IDisposable
    {
        private readonly KeyHook hook;

        public KeyListen(KeyCandidate[] candidates, Action<KeyCandidate> onPress)
        {
            NitroKey filter = new NitroKey(candidates);
            hook = new KeyHook(filter, delegate
            {
                KeyCandidate c = filter.LastFired;
                if (c != null) onPress(c);
            });
        }

        public bool Installed { get { return hook.IsInstalled; } }

        public void Dispose()
        {
            hook.Dispose();
        }
    }

    internal sealed class EnvServices : IEnvServices
    {
        private readonly Config cfg;
        private readonly string cfgPath;
        private readonly bool readOnly;
        private readonly Action afterSave;
        private readonly Action<bool> onOwnership;
        // True when no config was given: the run happens before the app is installed (nothing is read from or
        // written to config.json; the undo records go to %ProgramData% and travel in the EnvOutcome).
        private readonly bool detached;
        private readonly UndoStore undoStore;
        private readonly EnvTouch touch = new EnvTouch();
        private readonly IModeMarker marker;
        private volatile FanValidationRunner fanRunner;
        private volatile ModeTestRunner modeRunner;
        private volatile bool testRunning;
        private PerfMode? detectedMode;
        private bool owning;

        // cfg: the live config (the tray's own, or the one a standalone run loaded); null when the run
        // happens before the app is installed (installer). afterSave runs on the calling thread after a
        // successful SaveOutcome (the tray refreshes its pages there). onOwnership(true/false): the tray pauses and
        // resumes its own firmware writers around each writing step (null for a standalone run: nothing else writes).
        public EnvServices(Config cfg, string cfgPath, bool readOnly, Action afterSave, Action<bool> onOwnership)
        {
            detached = cfg == null;
            this.cfg = cfg ?? new Config();
            this.cfgPath = cfgPath;
            this.readOnly = readOnly;
            this.afterSave = afterSave;
            this.onOwnership = onOwnership;
            marker = Safety.ModeMarker; // modo-teste.flag in the verified state folder
            if (detached)
            {
                undoStore = new UndoStore();
                // records of an earlier cancelled run stay (their original state is the one to undo to)
                this.cfg.ServiceUndo = undoStore.Load();
            }
        }

        public List<ServiceUndo> ServiceUndoRecords
        {
            get { return detached ? new List<ServiceUndo>(cfg.ServiceUndo) : new List<ServiceUndo>(); }
        }

        public bool ReadOnly { get { return readOnly; } }

        public string AppVersion
        {
            get
            {
                try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
                catch (Exception) { return null; }
            }
        }

        private static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        public void Journal(string line)
        {
            try { Safety.Journal.Write(JournalKind.Action, "teste de ambiente: " + line); }
            catch (Exception) { }
        }

        // ---- detection (read-only) -----------------------------------------------------------

        public DetectionRaw Detect()
        {
            GuardedFirmware fw = null;
            try { fw = Safety.Wrap(new WmiFirmware()); }
            catch (Exception) { fw = null; }
            DetectionRaw raw = EnvProbe.Read(fw);
            detectedMode = raw.ProfileCode.HasValue ? ModeCatalog.FromCode(raw.ProfileCode.Value) : null;
            DetectionResult d = DetectionResult.Interpret(raw);
            Journal("detecção: modelo=" + (raw.Model ?? "-") + "; bios=" + (raw.Bios ?? "-")
                + "; sensores=" + (raw.SensorBitmapRaw.HasValue ? "0x" + raw.SensorBitmapRaw.Value.ToString("X", CultureInfo.InvariantCulture) : "-")
                + "; perfis=" + (raw.ProfileBitmapRaw.HasValue ? "0x" + raw.ProfileBitmapRaw.Value.ToString("X", CultureInfo.InvariantCulture) : "-")
                + "; perfil atual=" + (raw.ProfileCode.HasValue ? raw.ProfileCode.Value.ToString(CultureInfo.InvariantCulture) : "-")
                + "; classe gamer=" + (raw.FirmwareClass ? "sim" : "não") + "; respondeu=" + (raw.FirmwareAnswered ? "sim" : "não")
                + "; perfis confiáveis=" + (d.Profiles.Plausible ? "sim" : "não") + "; sensores confiáveis=" + (d.Sensors.Plausible ? "sim" : "não"));
            return raw;
        }

        public List<ConflictItem> ScanConflicts()
        {
            return ConflictServices.Scan(new WmiServiceControl());
        }

        public DisableOutcome DisableConflicts()
        {
            Journal("serviços da Acer: o usuário pediu para parar e desativar");
            return ConflictServices.DisableAll(new WmiServiceControl(), cfg, SaveConfig, Safety.Journal, Now());
        }

        // Called before and during every disable: in a detached run the records go to %ProgramData% (admin-only) at the
        // moment of disabling, so a cancelled or failed install cannot lose them.
        private bool SaveConfig()
        {
            if (detached) return undoStore.Save(cfg.ServiceUndo);
            try
            {
                cfg.Save(cfgPath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- firmware ownership -------------------------------------------------------------------

        public void AcquireFirmware()
        {
            if (owning) return;
            owning = true;
            if (onOwnership != null)
            {
                try { onOwnership(true); }
                catch (Exception ex) { Program.LogError(ex); }
            }
        }

        public void ReleaseFirmware()
        {
            if (!owning) return;
            owning = false;
            if (onOwnership != null)
            {
                try { onOwnership(false); }
                catch (Exception ex) { Program.LogError(ex); }
            }
        }

        // ---- the writing steps -----------------------------------------------------------------

        public FanRun RunFanTest(FanLayout layout, Action<FanProgress> progress)
        {
            FanRun run = new FanRun();
            if (readOnly)
            {
                run.Error = "O modo somente leitura está ligado: o teste das ventoinhas envia comandos ao firmware e não rodou.";
                return run;
            }
            HeartbeatPump pump = null;
            GuardedFirmware fw = null;
            FanLayout previous = FanLayoutStore.CurrentIfSet; // null = this process had chosen nothing
            testRunning = true;
            // Recorded BEFORE the first write: cancel/exit/crash from here on hand the fans of this layout back.
            touch.TouchFans(layout);
            bool autoConfirmed = false;
            try
            {
                // The fan sentinel guards this test like it guards the app's own Custom/Máximo writes.
                Safety.EnsureSentinel = Sentinel.EnsureRunning;
                pump = new HeartbeatPump();
                // The layout first: every WmiFirmware built from here on addresses only these fans.
                FanLayoutStore.Set(layout);
                fw = Safety.Wrap(new WmiFirmware());
                if (!fw.Available)
                {
                    run.Error = "O firmware não respondeu (o app precisa estar como administrador).";
                    touch.ClearFans(); // nothing was written
                    return run;
                }
                FanValidationRunner runner = new FanValidationRunner(fw, fw.SetFanMax, Thread.Sleep, 50, layout);
                runner.Progress = progress;
                fanRunner = runner;
                run.Report = runner.RunFans();
                Journal("ventoinhas: " + run.Report.Summary());
            }
            catch (Exception ex)
            {
                run.Error = "Erro durante o teste (" + ex.GetType().Name + ").";
            }
            finally
            {
                try { if (fw != null && fw.Available) autoConfirmed = fw.SetFanAuto(); }
                catch (Exception) { }
                if (fw == null || autoConfirmed) touch.ClearFans(); // nothing left to hand back (or nothing was ever written)
                // the tentative layout is kept only if the wizard is finished and saved
                if (previous == null) FanLayoutStore.Unset();
                else FanLayoutStore.Set(previous);
                fanRunner = null;
                testRunning = false;
                if (pump != null) pump.Dispose();
            }
            return run;
        }

        public void CancelFanTest()
        {
            FanValidationRunner r = fanRunner;
            if (r != null) r.Cancel();
        }

        public ModeTestOutcome RunModeTest(List<PerfMode> candidates, Func<PerfMode, bool> confirm, Action<ModeTestProgress> progress)
        {
            GuardedFirmware fw = Safety.Wrap(new WmiFirmware());
            // A marker left by an earlier interrupted test comes first: it holds the profile the USER had, which a new
            // marker must never overwrite. Only if it is gone (restored or discarded) does a new test start.
            if (marker.Exists)
            {
                try { ModeRecovery.Run(fw, marker, Safety.Journal, TrayApp.ModeRecoveryContextFor(cfg, readOnly)); }
                catch (Exception) { }
                if (marker.Exists)
                {
                    ModeTestOutcome leftover = new ModeTestOutcome();
                    leftover.Lines.Add("um teste de modos anterior ainda não foi desfeito; nenhum modo foi trocado");
                    leftover.EndedBalanced = true;
                    Journal("modos: " + leftover.Lines[0]);
                    return leftover;
                }
            }
            // The profile the user had before: that is what a cancel, an exit or a crash goes back to.
            PerfMode? before = null;
            try { before = fw.GetMode(); }
            catch (Exception) { before = null; }
            if (!before.HasValue) before = detectedMode;
            testRunning = true;
            // Marker first (a process kill leaves it for the next start), then the touched flag, then the first write.
            if (!readOnly && !marker.Set(before ?? PerfMode.DiaADia, Safety.Fingerprint))
            {
                testRunning = false;
                ModeTestOutcome refused = new ModeTestOutcome();
                refused.Lines.Add("não foi possível gravar o marcador do teste de modos; nenhum modo foi trocado");
                refused.EndedBalanced = true;
                Journal("modos: " + refused.Lines[0]);
                return refused;
            }
            if (!readOnly) touch.TouchMode(before);
            ModeTestRunner runner = new ModeTestRunner(fw, Thread.Sleep);
            runner.Confirm = confirm;
            runner.Progress = progress;
            modeRunner = runner;
            try
            {
                ModeTestOutcome o = runner.Run(candidates);
                Journal("modos: " + string.Join("; ", o.Lines.ToArray()) + (o.EndedBalanced ? "; terminou em Dia a dia" : "; NÃO confirmou Dia a dia no fim"));
                // Ended on Dia a dia: the test is over (a cancel afterwards still puts the user's earlier profile back).
                if (o.EndedBalanced && !o.Cancelled) marker.Clear();
                return o;
            }
            finally
            {
                modeRunner = null;
                testRunning = false;
            }
        }

        public void CancelModeTest()
        {
            ModeTestRunner r = modeRunner;
            if (r != null) r.Cancel();
        }

        public IDisposable ListenKey(KeyCandidate[] candidates, Action<KeyCandidate> onPress)
        {
            try
            {
                KeyListen k = new KeyListen(candidates, onPress);
                if (!k.Installed)
                {
                    k.Dispose();
                    return null;
                }
                return k;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Cancel / close / exit / crash. A test still running is first asked to stop and given a moment to finish by
        // itself (it ends in Automático / Dia a dia); then whatever this run touched is put back to the state read
        // before it, from an isolated writer. Nothing touched: not a single firmware call.
        public void RestoreTouched()
        {
            CancelFanTest();
            CancelModeTest();
            for (int waited = 0; testRunning && waited < 3000; waited += 50) Thread.Sleep(50);
            RestorePlan plan = touch.Plan();
            if (plan.IsEmpty) return;
            RestoreResult r = null;
            try { r = FanSafety.RestoreIsolated(plan, 4000); }
            catch (Exception) { r = null; }
            bool fansOk = r == null ? false : r.FansOk;
            bool modeOk = r == null ? false : r.ModeOk;
            Journal("restaurado o que o teste mexeu: ventoinhas " + (plan.Fans ? (fansOk ? "automático" : "NÃO confirmado") : "não mexidas")
                + "; modo " + (plan.Mode ? ModeCatalog.Name(plan.Target) + (modeOk ? "" : " (NÃO confirmado)") : "não mexido"));
            if (plan.Mode && modeOk) marker.Clear();
            if ((!plan.Fans || fansOk) && (!plan.Mode || modeOk)) touch.Clear();
        }

        // The installer saved the outcome itself (EnvOutcomeSaver): the run finished on purpose, so Dia a dia and Automático
        // are the intended end state and a later exit has nothing of this run to put back.
        public void MarkRunFinished()
        {
            touch.Clear();
        }

        // The host's "finished" handler of the standalone wizard: stores the profile (and the single-machine flags
        // the rest of the app reads) in config.json. The installer does not call this: it keeps the EnvOutcome
        // and saves it after the install, with EnvOutcomeSaver.
        public bool SaveOutcome(EnvOutcome o)
        {
            if (detached || o == null || o.Profile == null) return false; // a detached run has no config to write: the installer saves the outcome itself
            EnvApply.Apply(cfg, o.Profile, o.Results, Now());
            EnvApply.MergeServiceUndo(cfg, o.ServiceUndo);
            FanLayoutStore.Set(o.Profile.Layout ?? FanLayout.Both);
            bool ok;
            try { cfg.Save(cfgPath); ok = true; }
            catch (Exception) { ok = false; }
            CapabilityProfile profile = o.Profile;
            Journal("perfil salvo: " + (ok ? "sim" : "não") + "; modos liberados=" + profile.ConfirmedModes().Count.ToString(CultureInfo.InvariantCulture)
                + "; controle de ventoinha=" + (profile.Allows(Cap.FanControl) ? "liberado" : "bloqueado"));
            if (ok)
            {
                touch.Clear(); // the run finished on purpose: Dia a dia and Automático are the intended end state
                if (afterSave != null)
                {
                    try { afterSave(); }
                    catch (Exception) { }
                }
            }
            return ok;
        }
    }

    // For the installer host: saves a kept EnvOutcome into the config of the freshly installed app (it can be a
    // brand new config). Same effects as the standalone wizard's save. The %ProgramData% undo file is removed once
    // its records are safely in the config.
    internal static class EnvOutcomeSaver
    {
        public static bool Save(Config cfg, string path, EnvOutcome o)
        {
            if (cfg == null || o == null || o.Profile == null) return false;
            EnvApply.Apply(cfg, o.Profile, o.Results, EnvSession.NowUtc());
            UndoStore store = new UndoStore();
            EnvApply.MergeServiceUndo(cfg, o.ServiceUndo);
            EnvApply.MergeServiceUndo(cfg, store.Load());
            try
            {
                cfg.Save(path);
            }
            catch (Exception)
            {
                return false;
            }
            store.Clear();
            return true;
        }
    }
}
