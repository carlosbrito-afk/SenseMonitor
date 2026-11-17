using System;
using System.Management;
using Forja.Core;

namespace Forja.Platform
{
    // Not thread-safe: each instance is used from one thread only (the UI thread's, or the
    // isolated emergency writer's own instance).
    public class WmiFirmware : IFirmware
    {
        private ManagementObject instance;
        private bool available;
        // After 5 consecutive failed calls the WMI object is fetched again, at most every 30 s
        // (class missing at logon, provider restarted) instead of failing forever or hammering.
        private readonly ReconnectPolicy reconnect = new ReconnectPolicy();

        public WmiFirmware()
        {
            Connect();
        }

        private void Connect()
        {
            ManagementObject found = null;
            try
            {
                using (ManagementClass mc = new ManagementClass(@"root\WMI", "AcerGamingFunction", null))
                {
                    foreach (ManagementObject mo in mc.GetInstances())
                    {
                        found = mo;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                found = null;
            }
            ManagementObject old = instance;
            instance = found;
            available = found != null;
            if (old != null && !object.ReferenceEquals(old, found))
            {
                try { old.Dispose(); }
                catch (Exception) { }
            }
        }

        private void Failed()
        {
            if (reconnect.Failure(Environment.TickCount)) Connect();
        }

        public bool Available { get { return available; } }

        // Get* methods take a UInt32 input.
        private ulong? Get(string method, uint input)
        {
            try
            {
                if (!available)
                {
                    Failed();
                    return null;
                }
                ManagementBaseObject p = instance.GetMethodParameters(method);
                p["gmInput"] = input;
                ManagementBaseObject o = instance.InvokeMethod(method, p, null);
                if (o == null || o["gmOutput"] == null)
                {
                    Failed();
                    return null;
                }
                reconnect.Success();
                return Convert.ToUInt64(o["gmOutput"]);
            }
            catch (Exception)
            {
                Failed();
                return null;
            }
        }

        // Set* methods take a UInt64 input and answer gmOutput == 0 on success.
        private bool Set(string method, ulong input)
        {
            try
            {
                if (!available)
                {
                    Failed();
                    return false;
                }
                ManagementBaseObject p = instance.GetMethodParameters(method);
                p["gmInput"] = input;
                ManagementBaseObject o = instance.InvokeMethod(method, p, null);
                if (o == null || o["gmOutput"] == null)
                {
                    Failed();
                    return false;
                }
                // The call worked (a non-zero gmOutput is the firmware refusing the code).
                reconnect.Success();
                return Convert.ToUInt64(o["gmOutput"]) == 0;
            }
            catch (Exception)
            {
                Failed();
                return false;
            }
        }

        private int? Sensor(byte id)
        {
            ulong? raw = Get("GetGamingSysInfo", (uint)Codes.SensorQuery(id));
            return raw.HasValue ? Codes.DecodeSensor(raw.Value) : null;
        }

        public SensorReading ReadSensors()
        {
            SensorReading r = new SensorReading();
            int? v = Sensor(Codes.SensorCpuTemp);
            if (v.HasValue) r.CpuTempC = v.Value;
            v = Sensor(Codes.SensorGpuTemp);
            if (v.HasValue) r.GpuTempC = v.Value;
            v = Sensor(Codes.SensorCpuFan);
            if (v.HasValue) r.CpuFanRpm = v.Value;
            v = Sensor(Codes.SensorGpuFan);
            if (v.HasValue) r.GpuFanRpm = v.Value;
            return r;
        }

        public PerfMode? GetMode()
        {
            ulong? raw = Get("GetGamingMiscSetting", Codes.MiscProfile);
            if (!raw.HasValue) return null;
            byte? p = Codes.DecodeProfile(raw.Value);
            if (!p.HasValue) return null;
            if (p.Value == Codes.ProfileQuiet) return PerfMode.Silencioso;
            if (p.Value == Codes.ProfileBalanced) return PerfMode.DiaADia;
            if (p.Value == Codes.ProfilePerformance) return PerfMode.Desempenho;
            return null;
        }

        public bool SetMode(PerfMode m)
        {
            byte profile = Codes.ProfileBalanced;
            if (m == PerfMode.Silencioso) profile = Codes.ProfileQuiet;
            else if (m == PerfMode.Desempenho) profile = Codes.ProfilePerformance;
            if (!Set("SetGamingMiscSetting", Codes.ProfileSet(profile))) return false;
            // gmOutput == 0 only means "accepted"; confirm the profile really changed.
            PerfMode? now = GetMode();
            return now.HasValue && now.Value == m;
        }

        public bool SetFanAuto()
        {
            return Set("SetGamingFanBehavior", Codes.FanBehavior(FanMode.Auto, FanMode.Auto));
        }

        // Not part of IFirmware: the "Máximo" option (offered only after the hardware
        // validation) and the validation routine itself.
        public bool SetFanMax()
        {
            if (!Set("SetGamingFanBehavior", Codes.FanBehavior(FanMode.Max, FanMode.Max))) return false;
            FanMode? cpu;
            FanMode? gpu;
            return GetFanBehavior(out cpu, out gpu) && cpu == FanMode.Max && gpu == FanMode.Max;
        }

        // For FanPage's isFanMax delegate: true/false from a read-back, null if unreadable.
        public bool? IsFanMax()
        {
            FanMode? cpu;
            FanMode? gpu;
            bool ok = GetFanBehavior(out cpu, out gpu);
            return Codes.MaxStillActive(ok, cpu, gpu);
        }

        // Reads back the fan behavior (read layout, see Codes.DecodeFanBehavior).
        public bool GetFanBehavior(out FanMode? cpu, out FanMode? gpu)
        {
            cpu = null;
            gpu = null;
            ulong? raw = Get("GetGamingFanBehavior", Codes.FanBehaviorQuery);
            return raw.HasValue && Codes.DecodeFanBehavior(raw.Value, out cpu, out gpu);
        }

        public bool SetFanPercent(int cpuPct, int gpuPct)
        {
            // The speed-write encoding is accepted by the firmware but its effect is proven only
            // by the hardware validation (--validar-ventoinha); until it passes nothing calls this
            // outside that routine. Any failed step restores Auto (see FanSequence).
            return FanSequence.SetPercent(Set, cpuPct, gpuPct);
        }
    }
}
