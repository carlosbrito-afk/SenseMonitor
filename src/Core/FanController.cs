using System;

namespace Forja.Core
{
    // What the user asked for (not to be confused with FanMode, the firmware encoding).
    public enum FanUserMode { Auto, Manual, Curve }

    // Pure decision logic that keeps the laptop safe while the user controls the fans.
    // The app calls Tick every 2000 ms; the controller talks to the firmware only
    // when the target changes.
    //
    // NOT thread-safe by design: every member must be called from one single thread.
    //
    // Safety rules (all enforced in Tick):
    //  - Auto: the firmware is in charge; the controller never sets a speed.
    //  - A temperature that is null, NaN, Infinity or <= 0 is "unavailable".
    //  - Thermal protection (Manual/Curve): >= 90 C on CPU or GPU -> 100%/100%
    //    until every available temperature is < 80 C.
    //  - 3 consecutive ticks without a valid CPU temperature, or any failed
    //    firmware write -> SetFanAuto and Mode = Auto (it stays Auto until the
    //    user asks again).
    //  - Suspend/Shutdown -> SetFanAuto; Resume stays Auto until one valid reading.
    //  - Manual/Curve need CustomWritesValidated (a passed hardware validation); without it
    //    the controller stays Auto and thermal protection is the firmware's own Auto curve.
    //  - RPM-response guard (when SetRpmReference was given): after a Custom write, if the
    //    fans did not move toward the target band within 3 ticks -> SetFanAuto and Mode = Auto.
    public class FanController
    {
        private const int Floor = 30;
        private const int Ceiling = 100;
        private const int HotOn = 90;
        private const int HotOff = 80;
        private const int Hysteresis = 3;
        private const int MaxNullReads = 3;
        private const int GuardTicks = 3;

        private const string StAuto = "Automático";
        private const string StNoTemp = "temperatura indisponível — automático";
        private const string StFail = "falha ao controlar a ventoinha — automático";
        private const string StSuspended = "suspenso — automático";
        private const string StAutoFail = "falha ao voltar ao automático — tentando de novo";
        private const string StResume = "aguardando leitura após retomar";
        private const string StNoResponse = "Ventoinha não respondeu ao comando — automático";
        private const string StThermalAuto = "Proteção térmica: automático";
        private const string StWatchdog = "o app ficou sem responder — automático";

        private readonly IFirmware _fw;

        // ---- when Auto may be written (see AutoAssertEnabled), back-off and pause ----
        private bool _autoAssert = true;
        private bool _dirty;           // a Custom/Máximo write was attempted: the fans may be off Auto
        private bool _backoffOn;
        private int _autoFails;
        private int _nextAutoTry;
        private bool _failAlert;
        private bool _failAlerted;
        private bool _paused;
        private bool _pausedHeat;
        private Func<int> _clock;
        private static readonly int[] BackoffMs = { 2000, 5000, 30000, 300000 };
        public const string PausedNote = "o teste de ambiente está usando as ventoinhas — automático";
        // The fans this notebook has (both until an environment profile says otherwise): the RPM guard
        // judges only those.
        private FanLayout _layout = FanLayout.Both;
        private FanUserMode _mode = FanUserMode.Auto;
        private int _manualCpu = Floor;
        private int _manualGpu = Floor;
        private FanCurve _curve = FanCurve.Default();
        private string _status = StAuto;
        // Set where a warning/explanatory status is raised, cleared on plain statuses.
        private bool _warning;
        private int _autoWrites;
        // Explanation kept while in Auto until the user changes the mode.
        private string _notice;

        private bool _curveAvailable;
        private int _nullReads;
        private bool _suspended;
        private bool _awaitingFresh;
        private bool _shutdown;
        private bool _thermal;
        private bool _thermalGpuSeen;
        private bool _curveGpuSeen;

        // Firmware state as last commanded. Unknown at construction (a previous run
        // may have crashed with a manual speed), so the first Auto tick asserts Auto.
        private bool _fwAuto;
        private int _lastCpu = -1;
        private int _lastGpu = -1;

        // Manual/Curve allowed only after the hardware validation (safe default: no).
        private bool _validated;

        // RPM-response guard: reference max RPM per fan (0 = guard off) and the pending check.
        private int _maxRpmCpu;
        private int _maxRpmGpu;
        private int _guardTick = -1;   // -1 idle; else ticks since the guarded write
        private int? _guardRefCpu;
        private int? _guardRefGpu;
        private bool _guardCpuOk;
        private bool _guardGpuOk;

        // Curve hysteresis state.
        private int _curveTarget = -1;
        private double _curveRefTemp;

        public FanController(IFirmware fw)
        {
            if (fw == null) throw new ArgumentNullException("fw");
            _fw = fw;
        }

        // Why Manual/Curva/Máximo are not offered; the shell replaces it while read-only mode is on.
        public string LockedNote = FanValidation.LockedNote;

        // false = this notebook's fan control is not confirmed (untested, unknown, stale): the controller never
        // writes Auto on its own (not at start, not on resume, not at exit) unless the app itself made the fans
        // leave Auto (a Custom write was attempted, or MarkDirty). Default true (confirmed machine).
        public bool AutoAssertEnabled
        {
            get { return _autoAssert; }
            set { _autoAssert = value; }
        }

        // The "Máximo" option writes outside the controller: after it the fans must be handed back.
        public void MarkDirty()
        {
            _dirty = true;
        }

        // Anything the firmware may need handed back at exit or suspend (read from any thread).
        public bool MayNeedHandBack
        {
            get { return _autoAssert || _dirty; }
        }

        // Failed Auto writes are retried after 2 s, 5 s, 30 s, then every 5 min (instead of every tick), with one
        // alert (TakeAutoFailAlert). Urgent writes (exit, suspend, heat, a failed Custom write) ignore it. Off by
        // default so the pure tests keep their immediate retry; the app turns it on.
        public bool BackoffEnabled
        {
            get { return _backoffOn; }
            set { _backoffOn = value; }
        }

        public Func<int> Clock
        {
            get { return _clock; }
            set { _clock = value; }
        }

        private int Now()
        {
            return _clock != null ? _clock() : Environment.TickCount;
        }

        // true once per series of failures: the shell journals it / tells the user.
        public bool TakeAutoFailAlert()
        {
            bool a = _failAlert;
            _failAlert = false;
            return a;
        }

        private bool BackoffBlocks()
        {
            return _backoffOn && _autoFails > 0 && unchecked(Now() - _nextAutoTry) < 0;
        }

        private void RegisterAutoFail()
        {
            if (!_backoffOn) return;
            _autoFails++;
            _nextAutoTry = unchecked(Now() + BackoffMs[Math.Min(_autoFails - 1, BackoffMs.Length - 1)]);
            if (!_failAlerted)
            {
                _failAlerted = true;
                _failAlert = true;
            }
        }

        public bool Paused { get { return _paused; } }

        // While the environment wizard owns the firmware: the controller hands the fans back to Auto once and then
        // writes nothing (no retries, no re-asserts) except an Auto at 90 C, once per heat event. Choosing Manual,
        // Curva or Máximo meanwhile stays in Auto with a note.
        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;
            _pausedHeat = false;
            if (paused)
            {
                if (_mode != FanUserMode.Auto)
                {
                    _mode = FanUserMode.Auto;
                    EnterAuto(PausedNote, true);
                }
                else
                {
                    _notice = PausedNote;
                    _status = PausedNote;
                    _warning = true;
                }
                return;
            }
            _notice = null;
            if (_fwAuto)
            {
                _status = StAuto;
                _warning = false;
            }
        }

        public FanUserMode Mode
        {
            get { return _mode; }
            set
            {
                _notice = null;
                if (value != FanUserMode.Auto && (!_validated || _paused))
                {
                    string why = _paused ? PausedNote : LockedNote;
                    _mode = FanUserMode.Auto;
                    if (!_suspended && !_awaitingFresh) EnterAuto(why, false);
                    else _notice = why;
                    return;
                }
                if (value == FanUserMode.Curve && !_curveAvailable)
                {
                    _mode = FanUserMode.Auto;
                    if (!_suspended && !_awaitingFresh) EnterAuto(StNoTemp, false);
                    else _notice = StNoTemp;
                    return;
                }
                _mode = value;
                _curveTarget = -1;
                if (value == FanUserMode.Auto)
                {
                    if (!_suspended && !_awaitingFresh) EnterAuto(null, false);
                }
            }
        }

        public int ManualCpuPct
        {
            get { return _manualCpu; }
            set { _manualCpu = Clamp(value); }
        }

        public int ManualGpuPct
        {
            get { return _manualGpu; }
            set { _manualGpu = Clamp(value); }
        }

        // Always a normalized curve; null or invalid input becomes the default curve.
        public FanCurve Curve
        {
            get { return _curve; }
            set
            {
                _curve = (value == null ? FanCurve.Default() : value.Normalize());
                _curveTarget = -1;
            }
        }

        public bool CurveAvailable { get { return _curveAvailable; } }

        // True once the hardware validation passed (Config.ManualFanAllowed). Turning it off
        // while the user drives the fans hands them back to the firmware at once.
        public bool CustomWritesValidated
        {
            get { return _validated; }
            set
            {
                _validated = value;
                if (!value && _mode != FanUserMode.Auto)
                {
                    _mode = FanUserMode.Auto;
                    if (!_suspended && !_awaitingFresh && !_shutdown) EnterAuto(LockedNote, true);
                    else _notice = LockedNote;
                }
            }
        }

        // Settled RPM at Máximo per fan (from the validation). Both > 0 turn the RPM-response
        // guard on; otherwise it is off.
        public void SetRpmReference(int cpuMaxRpm, int gpuMaxRpm)
        {
            _maxRpmCpu = cpuMaxRpm > 0 ? cpuMaxRpm : 0;
            _maxRpmGpu = gpuMaxRpm > 0 ? gpuMaxRpm : 0;
            _guardTick = -1;
        }

        public bool RpmGuardEnabled
        {
            get { return (!_layout.Cpu || _maxRpmCpu > 0) && (!_layout.Gpu || _maxRpmGpu > 0); }
        }

        // The fans the notebook has (null = both). The page and the guard follow it.
        public void SetFanLayout(FanLayout layout)
        {
            _layout = layout ?? FanLayout.Both;
            _guardTick = -1;
        }

        public FanLayout Layout { get { return _layout; } }

        public string Status { get { return _status; } }

        // True when the last Auto write is known to have succeeded (false at construction,
        // after a failed SetFanAuto, and while a manual speed is applied).
        public bool FirmwareAutoConfirmed { get { return _fwAuto; } }

        // The firmware was put in Auto by another path (an isolated emergency write while
        // this controller's own write could not run). Counts as a successful Auto write,
        // AutoWriteCount included (the fan page's "Máximo" reconcile relies on it).
        public void NotifyFirmwareAuto()
        {
            _fwAuto = true;
            _autoWrites++;
            _lastCpu = -1;
            _lastGpu = -1;
            _curveTarget = -1;
            if (_status == StAutoFail)
            {
                _status = _notice ?? StAuto;
                _warning = _notice != null && _notice != StAuto;
            }
        }

        // The watchdog thread wrote Auto through the isolated path because no firmware tick
        // completed for 10 s while the user drove the fans: the firmware is Auto now, and the
        // controller leaves Manual/Curve with an explanation (the user picks the mode again).
        // The isolated write counts (AutoWriteCount, so the page drops Máximo) but is not trusted
        // as the final state: a SetFanPercent stuck on the hung UI thread may land after it, so
        // the next tick writes Auto again from the UI thread.
        public void NotifyWatchdogAuto()
        {
            NotifyFirmwareAuto();
            _fwAuto = false;
            _guardTick = -1;
            if (_mode != FanUserMode.Auto)
            {
                _mode = FanUserMode.Auto;
                _thermal = false;
                _notice = StWatchdog;
                _status = StWatchdog;
                _warning = true;
            }
        }

        // True while Auto is showing an explanation (fallback after a failure, suspend,
        // waiting for a reading, failed return to Auto); false for plain Auto and for
        // user-driven modes.
        public bool StatusIsWarning { get { return _warning; } }

        // Number of SetFanAuto writes the firmware accepted. Lets an owner of firmware state
        // the controller does not track (the page's "Máximo") know Auto was written since.
        public int AutoWriteCount { get { return _autoWrites; } }

        public void Tick(SensorReading r)
        {
            if (_shutdown || _suspended) return;

            double? cpu = r == null ? null : Valid(r.CpuTempC);
            double? gpu = r == null ? null : Valid(r.GpuTempC);

            if (_paused)
            {
                // Nothing is written while the wizard owns the firmware, except Auto at 90 C (once per heat event).
                double? hot = Max(cpu, gpu);
                if (hot.HasValue && hot.Value >= HotOn && !_pausedHeat)
                {
                    _pausedHeat = true;
                    EnterAuto(StThermalAuto, true);
                }
                else if (!hot.HasValue || hot.Value < HotOff)
                {
                    _pausedHeat = false;
                }
                return;
            }

            if (cpu.HasValue)
            {
                _nullReads = 0;
                _curveAvailable = true;
            }
            else
            {
                if (_nullReads < 1000) _nullReads++;
                if (_nullReads >= MaxNullReads) _curveAvailable = false;
            }

            if (_awaitingFresh)
            {
                // Firmware must be in Auto while waiting; retry if not confirmed.
                if (!_fwAuto && !BackoffBlocks()) EnterAuto(StResume, true);
                if (!cpu.HasValue) return;
                _awaitingFresh = false;
                _notice = null;
                _curveTarget = -1;
            }

            if (_mode == FanUserMode.Auto)
            {
                // Firmware in charge; only retry a previously failed SetFanAuto (with back-off, when on).
                if (!_fwAuto && BackoffBlocks()) return;
                EnterAuto(_notice, !_fwAuto);
                return;
            }

            // Did the fans follow the last Custom write?
            if (_guardTick >= 0 && r != null)
            {
                if (!_guardCpuOk) _guardCpuOk = RpmGuard.Responded(_guardRefCpu, r.CpuFanRpm, _lastCpu, _maxRpmCpu);
                if (!_guardGpuOk) _guardGpuOk = RpmGuard.Responded(_guardRefGpu, r.GpuFanRpm, _lastGpu, _maxRpmGpu);
                if (_guardCpuOk && _guardGpuOk) _guardTick = -1;
                else if (++_guardTick >= GuardTicks)
                {
                    _mode = FanUserMode.Auto;
                    EnterAuto(StNoResponse, true);
                    return;
                }
            }

            if (_nullReads >= MaxNullReads)
            {
                _mode = FanUserMode.Auto;
                EnterAuto(StNoTemp, true);
                return;
            }

            // Thermal protection. Entering/raising may use any valid temperature;
            // releasing needs the CPU valid and < 80 and the GPU valid and < 80
            // (or never valid while protected). A lost sensor never releases it.
            double? hottest = Max(cpu, gpu);
            if (hottest.HasValue && hottest.Value >= HotOn && !_thermal)
            {
                _thermal = true;
                _thermalGpuSeen = false;
            }
            if (_thermal)
            {
                if (gpu.HasValue) _thermalGpuSeen = true;
                bool cpuCool = cpu.HasValue && cpu.Value < HotOff;
                bool gpuCool = gpu.HasValue ? gpu.Value < HotOff : !_thermalGpuSeen;
                if (cpuCool && gpuCool)
                {
                    _thermal = false;
                    _curveTarget = -1;
                }
            }

            int wantCpu;
            int wantGpu;
            string status;
            if (_thermal && !_validated)
            {
                // Unvalidated Custom writes are never trusted for protection: the firmware's own curve.
                _mode = FanUserMode.Auto;
                EnterAuto(StThermalAuto, true);
                return;
            }
            if (_thermal)
            {
                wantCpu = Ceiling;
                wantGpu = Ceiling;
                status = "Proteção térmica: 100%";
            }
            else if (_mode == FanUserMode.Manual)
            {
                wantCpu = Clamp(_manualCpu);
                wantGpu = Clamp(_manualGpu);
                status = _layout.Count == 2 ? "Manual: CPU " + wantCpu + "% / GPU " + wantGpu + "%"
                    : "Manual: " + (_layout.Cpu ? wantCpu : wantGpu) + "%";
            }
            else
            {
                if (hottest.HasValue) UpdateCurveTarget(hottest.Value, cpu.HasValue, gpu.HasValue);
                if (_curveTarget < 0)
                {
                    // Curve requested but no temperature seen yet: nothing to send.
                    return;
                }
                wantCpu = _curveTarget;
                wantGpu = _curveTarget;
                status = "Curva: " + _curveTarget + "%";
            }

            if (_fwAuto || wantCpu != _lastCpu || wantGpu != _lastGpu)
            {
                _dirty = true;
                bool ok = _fw.SetFanPercent(wantCpu, wantGpu);
                if (!ok)
                {
                    _mode = FanUserMode.Auto;
                    EnterAuto(StFail, true);
                    return;
                }
                _fwAuto = false;
                _lastCpu = wantCpu;
                _lastGpu = wantGpu;
                StartGuard(r);
            }
            _status = status;
            _warning = _thermal;
        }

        public void OnSuspend()
        {
            _suspended = true;
            _awaitingFresh = false;
            EnterAuto(StSuspended, true);
        }

        public void OnResume()
        {
            if (_shutdown) return;
            _suspended = false;
            _awaitingFresh = true;
            _thermal = false;
            _curveTarget = -1;
            // Idempotent: firmware is forced to Auto here unless already confirmed.
            EnterAuto(StResume, !_fwAuto);
        }

        public void Shutdown()
        {
            _shutdown = true;
            EnterAuto(StAuto, true);
        }

        // Hands control back to the firmware. Without force, SetFanAuto is skipped
        // when the firmware is already confirmed Auto (no redundant writes).
        // The notice (may be null) is kept as status while Auto; a failed
        // SetFanAuto is reported instead and retried on the next tick.
        private void EnterAuto(string notice, bool force)
        {
            if (force || !_fwAuto)
            {
                if (_autoAssert || _dirty)
                {
                    _fwAuto = _fw.SetFanAuto();
                    if (_fwAuto)
                    {
                        _autoWrites++;
                        _dirty = false;
                        _autoFails = 0;
                        _failAlerted = false;
                    }
                    else
                    {
                        RegisterAutoFail();
                    }
                }
                else
                {
                    // Fan control of this notebook is not confirmed and the app never left Auto: the firmware is in
                    // charge, there is nothing to assert or hand back (and no write to a model we have not proved).
                    _fwAuto = true;
                }
            }
            _warning = !_fwAuto || notice != null;
            _lastCpu = -1;
            _lastGpu = -1;
            _curveTarget = -1;
            _thermal = false;
            _guardTick = -1;
            _notice = notice;
            _status = !_fwAuto ? StAutoFail : (notice ?? StAuto);
        }

        // Arms the RPM check for the write just sent; the reading of this tick (taken before
        // the write) is the reference. Already in the target band = nothing to wait for.
        private void StartGuard(SensorReading r)
        {
            if (!RpmGuardEnabled)
            {
                _guardTick = -1;
                return;
            }
            _guardRefCpu = r == null ? null : r.CpuFanRpm;
            _guardRefGpu = r == null ? null : r.GpuFanRpm;
            _guardCpuOk = !_layout.Cpu || RpmGuard.InBand(_guardRefCpu, _lastCpu, _maxRpmCpu);
            _guardGpuOk = !_layout.Gpu || RpmGuard.InBand(_guardRefGpu, _lastGpu, _maxRpmGpu);
            _guardTick = (_guardCpuOk && _guardGpuOk) ? -1 : 0;
        }

        private void UpdateCurveTarget(double temp, bool cpuValid, bool gpuValid)
        {
            int raw = _curve.Evaluate(temp);
            if (_curveTarget < 0)
            {
                _curveTarget = raw;
                _curveRefTemp = temp;
                _curveGpuSeen = gpuValid;
            }
            else if (raw > _curveTarget)
            {
                _curveTarget = raw;
                _curveRefTemp = temp;
                if (gpuValid) _curveGpuSeen = true;
            }
            else if (raw < _curveTarget && temp <= _curveRefTemp - Hysteresis
                     && cpuValid && (gpuValid || !_curveGpuSeen))
            {
                // Lowering needs the same sensors that raised the target.
                _curveTarget = raw;
                _curveRefTemp = temp;
                _curveGpuSeen = gpuValid;
            }
        }

        private static double? Valid(double? t)
        {
            if (!t.HasValue) return null;
            double v = t.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return null;
            return v;
        }

        private static double? Max(double? a, double? b)
        {
            if (a.HasValue && b.HasValue) return Math.Max(a.Value, b.Value);
            return a.HasValue ? a : b;
        }

        private static int Clamp(int v)
        {
            return v < Floor ? Floor : (v > Ceiling ? Ceiling : v);
        }
    }
}
