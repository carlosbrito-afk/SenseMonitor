using System;
using System.Threading;

namespace Forja.Platform
{
    // Emergency "fans to Auto" that works while the UI (STA) thread is inside a sent message
    // (logoff, suspend), where COM refuses its outgoing calls. Runs SetFanAuto on a dedicated
    // MTA thread with its OWN WmiFirmware instance (never the UI thread's, whose COM objects
    // would marshal back to the blocked STA and deadlock) and waits with a timeout.
    public static class FanSafety
    {
        public static bool SetAutoIsolated(int timeoutMs)
        {
            bool ok = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    WmiFirmware fw = new WmiFirmware();
                    ok = fw.Available && fw.SetFanAuto();
                }
                catch (Exception)
                {
                    ok = false;
                }
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.FanAuto";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            // Join is a full fence, so ok is read after the thread's write.
            return t.Join(timeoutMs) && ok;
        }

        // Same isolation, for the performance test's crash path: mode Dia a dia (read back)
        // and fans Automático. True only when both were confirmed.
        public static bool SetSafeStateIsolated(int timeoutMs)
        {
            bool ok = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    WmiFirmware fw = new WmiFirmware();
                    if (!fw.Available) return;
                    // WmiFirmware.SetMode reads the profile back itself; two tries each.
                    bool mode = fw.SetMode(Forja.Core.PerfMode.DiaADia) || fw.SetMode(Forja.Core.PerfMode.DiaADia);
                    bool auto = fw.SetFanAuto() || fw.SetFanAuto();
                    ok = mode && auto;
                }
                catch (Exception)
                {
                    ok = false;
                }
            });
            t.IsBackground = true;
            t.Name = "SenseMonitor.SafeState";
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            return t.Join(timeoutMs) && ok;
        }
    }
}
