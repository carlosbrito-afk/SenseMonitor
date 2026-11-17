using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Forja.Core;

namespace Forja.Platform
{
    // The raw, read-only readings of --autoteste that no existing class offers. Nothing here
    // deletes, toggles or creates anything permanent; each method returns its own outcome and
    // never throws.
    public static class AutoProbes
    {
        // Installs the real KeyHook on its own thread, keeps it 1 s and removes it. KeyHook needs
        // a SynchronizationContext on the constructing thread; a bare one is enough because the
        // press callback is a no-op (no key is read, stored or logged by this check).
        public static void HookRoundTrip(out bool installed, out bool removed)
        {
            installed = false;
            removed = false;
            SynchronizationContext previous = SynchronizationContext.Current;
            KeyHook hook = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                hook = new KeyHook(new NitroKey(), delegate { });
                installed = hook.IsInstalled;
                Thread.Sleep(1000);
                installed = installed && hook.IsInstalled;
                hook.Dispose();
                removed = !hook.IsInstalled;
                hook = null;
            }
            catch (Exception)
            {
            }
            finally
            {
                if (hook != null)
                {
                    try { hook.Dispose(); }
                    catch (Exception) { }
                }
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        // The mutex is held by this process (the tool took it); a second attempt must be refused.
        // Switch of the probe process: tries to take the single-instance mutex and exits 0 when it
        // is turned away, 1 when it got it (see Program.Main).
        public const string ProbeSwitch = "--sondar-instancia";

        // A REAL second copy of this program is started with ProbeSwitch: blocked = it was turned
        // away (exit 0); null = it could not be started or did not answer in 10 s.
        public static void MutexState(out bool heldByUs, out bool? secondBlocked)
        {
            heldByUs = false;
            secondBlocked = null;
            try
            {
                heldByUs = SingleInstance.TryAcquire(); // already ours: true without a new mutex
                string exe;
                using (Process me = Process.GetCurrentProcess())
                {
                    exe = me.MainModule.FileName;
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe, ProbeSwitch);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process child = Process.Start(psi))
                {
                    if (!child.WaitForExit(10000))
                    {
                        try { child.Kill(); }
                        catch (Exception) { }
                        return;
                    }
                    if (child.ExitCode == 0) secondBlocked = true;
                    else if (child.ExitCode == 1) secondBlocked = false;
                }
            }
            catch (Exception)
            {
            }
        }

        // Writes a small file in the temperature-log folder, reads it back and removes it (and
        // the folder if this check created it).
        public static void TempLogRoundTrip(out bool wrote, out bool readBack, out string error, out bool inOwnFolder)
        {
            wrote = false;
            readBack = false;
            error = null;
            string dir = TempLogStore.DefaultDirectory();
            inOwnFolder = Directory.Exists(dir);
            if (!inOwnFolder)
            {
                // The real folder does not exist yet: do not create it. Exercise the real store in a
                // temporary folder instead (it writes a CSV line and reads it back).
                TempFolderRoundTrip(out wrote, out readBack, out error);
                return;
            }
            string path = null;
            try
            {
                path = Path.Combine(dir, "autoteste-" + Guid.NewGuid().ToString("N") + ".tmp");
                string payload = "autoteste " + DateTime.UtcNow.ToString("o");
                File.WriteAllText(path, payload, Encoding.UTF8);
                wrote = true;
                readBack = File.ReadAllText(path, Encoding.UTF8) == payload;
                // the store's own reader must also cope with the folder
                new TempLogStore(dir).Summaries(1);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
            }
            finally
            {
                try { if (path != null && File.Exists(path)) File.Delete(path); }
                catch (Exception) { }
            }
        }

        internal static void TempFolderRoundTrip(out bool wrote, out bool readBack, out string error)
        {
            wrote = false;
            readBack = false;
            error = null;
            string dir = Path.Combine(Path.GetTempPath(), "sm-autoteste-" + Guid.NewGuid().ToString("N"));
            try
            {
                TempLogStore store = new TempLogStore(dir);
                SensorReading r = new SensorReading();
                r.CpuTempC = 50;
                r.GpuTempC = 45;
                r.CpuFanRpm = 2000;
                r.GpuFanRpm = 2000;
                DateTime now = DateTime.Now;
                store.Append(now, r);
                wrote = Directory.Exists(dir) && Directory.GetFiles(dir, "*.csv").Length > 0;
                readBack = store.Summaries(1, now).Length > 0;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }

        // Registry policy (HKLM, read-only): true = System Restore is switched off, null = unreadable.
        public static bool? RestoreDisabledByPolicy()
        {
            try
            {
                bool seen = false;
                string[] keys =
                {
                    @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore",
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"
                };
                foreach (string k in keys)
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(k, false))
                    {
                        if (key == null) continue;
                        seen = true;
                        object v = key.GetValue("DisableSR");
                        if (v != null && Convert.ToInt32(v) == 1) return true;
                    }
                }
                return seen ? false : (bool?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Existing restore points (root\default SystemRestore, enumeration only); null = cannot list.
        public static int? RestorePointCount()
        {
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\default");
                scope.Connect();
                int n = 0;
                using (ManagementClass mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
                using (ManagementObjectCollection items = mc.GetInstances())
                {
                    foreach (ManagementObject o in items)
                    {
                        o.Dispose();
                        n++;
                    }
                }
                return n;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool SystemToolExists(string exe)
        {
            try { return File.Exists(InstallPaths.System32(exe)); }
            catch (Exception) { return false; }
        }
    }
}
