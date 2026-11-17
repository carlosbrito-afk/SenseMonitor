using System;
using System.Collections.Generic;
using System.Management;
using Forja.Core;

namespace Forja.Platform
{
    // Per physical disk: MSFT_PhysicalDisk (health, type, size; works without administrator rights)
    // and MSFT_StorageReliabilityCounter (temperature, wear, hours, errors; needs administrator and
    // is often partly empty on NVMe). Read-only. Empty counters stay null ("não informado").
    public static class DiskHealth
    {
        private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        // Null when the Storage provider cannot be queried at all. Never throws.
        public static DiskInfo[] Read()
        {
            try
            {
                ConnectionOptions co = new ConnectionOptions();
                co.Timeout = Timeout;
                ManagementScope scope = new ManagementScope(StorageScope, co);
                scope.Connect();

                List<DiskInfo> disks = new List<DiskInfo>();
                List<string> ids = new List<string>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"), Options()))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        using (o)
                        {
                            DiskInfo d = new DiskInfo();
                            d.Name = Text(o, "FriendlyName");
                            if (string.IsNullOrEmpty(d.Name)) d.Name = "Disco";
                            d.MediaType = DiskMap.MediaTypeText(Int(o, "MediaType"));
                            d.BusType = DiskMap.BusTypeText(Int(o, "BusType"));
                            d.SizeBytes = Long(o, "Size");
                            d.HealthCode = Int(o, "HealthStatus");
                            d.OperationalText = DiskMap.OperationalText(IntArray(o, "OperationalStatus"));
                            disks.Add(d);
                            ids.Add(Text(o, "DeviceId"));
                        }
                    }
                }

                FillReliability(scope, disks, ids);
                return disks.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static EnumerationOptions Options()
        {
            EnumerationOptions eo = new EnumerationOptions();
            eo.Timeout = Timeout;
            eo.ReturnImmediately = false;
            eo.Rewindable = false;
            return eo;
        }

        // Counters are optional: a denied query leaves every field null.
        private static void FillReliability(ManagementScope scope, List<DiskInfo> disks, List<string> ids)
        {
            try
            {
                List<ManagementBaseObject> rows = new List<ManagementBaseObject>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter"), Options()))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col) rows.Add(o);
                }
                try
                {
                    for (int i = 0; i < disks.Count; i++)
                    {
                        ManagementBaseObject match = null;
                        foreach (ManagementBaseObject row in rows)
                        {
                            string rid = Text(row, "DeviceId");
                            if (rid != null && ids[i] != null && rid == ids[i]) { match = row; break; }
                        }
                        // Fall back to pairing by position when there is exactly one of each.
                        if (match == null && disks.Count == 1 && rows.Count == 1) match = rows[0];
                        if (match == null) continue;
                        disks[i].TempC = DiskMap.SanitizeTemp(Int(match, "Temperature"));
                        disks[i].TempMaxC = DiskMap.SanitizeTemp(Int(match, "TemperatureMax"));
                        disks[i].WearPct = DiskMap.SanitizeWear(Int(match, "Wear"));
                        disks[i].ReadErrors = DiskMap.SanitizeCount(Long(match, "ReadErrorsUncorrected"));
                        disks[i].WriteErrors = DiskMap.SanitizeCount(Long(match, "WriteErrorsUncorrected"));
                        disks[i].PowerOnHours = DiskMap.SanitizeHours(Long(match, "PowerOnHours"));
                    }
                }
                finally
                {
                    foreach (ManagementBaseObject row in rows)
                    {
                        try { row.Dispose(); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static string Text(ManagementBaseObject o, string name)
        {
            try
            {
                object v = o[name];
                return v == null ? null : v.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int? Int(ManagementBaseObject o, string name)
        {
            long? v = Long(o, name);
            if (!v.HasValue || v.Value > int.MaxValue || v.Value < int.MinValue) return null;
            return (int)v.Value;
        }

        private static long? Long(ManagementBaseObject o, string name)
        {
            try
            {
                object v = o[name];
                if (v == null) return null;
                return Convert.ToInt64(v);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int[] IntArray(ManagementBaseObject o, string name)
        {
            try
            {
                Array a = o[name] as Array;
                if (a == null) return null;
                List<int> list = new List<int>();
                foreach (object x in a) list.Add(Convert.ToInt32(x));
                return list.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
