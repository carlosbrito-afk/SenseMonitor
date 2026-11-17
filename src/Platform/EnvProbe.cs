using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The read-only detection of the environment test. Reads Windows' own facts (model, BIOS, CPU, GPUs,
    // RAM), checks which Acer WMI classes exist (schema only: nothing is called on them) and asks the
    // firmware the two announcements the research marks as safe: the sensor bitmap and the profile bitmap
    // (plus the current profile and one set of live readings). Never writes. Never reads serial numbers,
    // UUIDs or the computer name. Everything catches: a failed query leaves its field empty.
    public static class EnvProbe
    {
        private const string WmiScope = @"root\WMI";
        private const int PauseBetweenCallsMs = 50;

        // Manufacturer, model, family and BIOS version: enough for the fingerprint. Fast, no firmware access.
        public static DetectionRaw ReadIdentity()
        {
            DetectionRaw r = new DetectionRaw();
            FillIdentity(r);
            return r;
        }

        // The whole detection. fw may be null (the firmware is then not asked). Runs on one worker thread.
        public static DetectionRaw Read(IFirmware fw)
        {
            DetectionRaw r = new DetectionRaw();
            FillIdentity(r);
            FillHardware(r);

            r.FirmwareClass = ClassExists(WmiScope, "AcerGamingFunction");
            r.BatteryClass = ClassExists(WmiScope, "BatteryControl");
            r.ActionClass = ClassExists(WmiScope, "APGeAction");
            r.EventClass = ClassExists(WmiScope, "APGeEvent");
            r.KeyboardLightMethod = r.FirmwareClass && AnyMethodExists(WmiScope, "AcerGamingFunction",
                "SetGamingKBBacklight", "GetGamingKBBacklight", "SetGamingRgbKb", "GetGamingRgbKb");
            r.EneKb5130 = HidDevicePresent("VID_0CF2&PID_5130");

            if (fw != null && fw.Available)
            {
                IFirmwareProbe probe = fw as IFirmwareProbe;
                if (probe != null)
                {
                    r.SensorBitmapRaw = probe.ReadSensorBitmapRaw();
                    Thread.Sleep(PauseBetweenCallsMs);
                    r.ProfileBitmapRaw = probe.ReadProfileBitmapRaw();
                    Thread.Sleep(PauseBetweenCallsMs);
                    r.ProfileCode = probe.ReadProfileCode();
                    Thread.Sleep(PauseBetweenCallsMs);
                }
                // About five readings one second apart: the temperatures that will guard the fan test are judged on them.
                for (int k = 0; k < 5; k++)
                {
                    if (k > 0) Thread.Sleep(1000);
                    SensorReading one = null;
                    try { one = fw.ReadSensors(); }
                    catch (Exception) { one = null; }
                    if (one != null) r.LiveSamples.Add(one);
                }
                r.Live = r.LiveSamples.Count > 0 ? r.LiveSamples[r.LiveSamples.Count - 1] : new SensorReading();
                r.FirmwareAnswered = r.SensorBitmapRaw.HasValue || r.ProfileBitmapRaw.HasValue || r.ProfileCode.HasValue
                    || r.Live.CpuTempC.HasValue || r.Live.GpuTempC.HasValue || r.Live.CpuFanRpm.HasValue || r.Live.GpuFanRpm.HasValue;
            }
            return r;
        }

        private static void FillIdentity(DetectionRaw r)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemFamily, TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        r.Manufacturer = Text(mo["Manufacturer"]);
                        r.Model = Text(mo["Model"]);
                        r.SystemFamily = Text(mo["SystemFamily"]);
                        object mem = mo["TotalPhysicalMemory"];
                        if (mem != null) r.RamBytes = Convert.ToInt64(mem);
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
            try
            {
                // The BIOS version only; the serial number is never selected.
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        r.Bios = Text(mo["SMBIOSBIOSVersion"]);
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void FillHardware(DetectionRaw r)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        r.Cpu = Text(mo["Name"]);
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        string n = Text(mo["Name"]);
                        if (IsVirtualAdapter(n)) continue;
                        if (!r.Gpus.Contains(n)) r.Gpus.Add(n);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // Software display adapters (remote desktop, virtual monitors) are not the notebook's graphics.
        private static bool IsVirtualAdapter(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            string l = n.ToLowerInvariant();
            return l.StartsWith("microsoft ") || l.Contains("virtual") || l.Contains("parsec") || l.Contains("remote") || l.Contains("citrix");
        }

        // The class (schema) exists. Needs no administrator and calls nothing.
        private static bool ClassExists(string scope, string name)
        {
            try
            {
                using (ManagementClass mc = new ManagementClass(scope, name, null))
                {
                    mc.Get();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool AnyMethodExists(string scope, string className, params string[] methods)
        {
            try
            {
                using (ManagementClass mc = new ManagementClass(scope, className, null))
                {
                    mc.Get();
                    foreach (string m in methods)
                    {
                        foreach (MethodData md in mc.Methods)
                        {
                            if (string.Equals(md.Name, m, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // Is a HID device with this vendor/product id enumerated (only the id is looked at, never its data).
        private static bool HidDevicePresent(string vidPid)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%" + vidPid + "%'"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static string Text(object o)
        {
            if (o == null) return null;
            string s = Convert.ToString(o);
            return s == null ? null : s.Trim();
        }
    }
}
