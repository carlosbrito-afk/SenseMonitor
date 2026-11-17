using System;
using Microsoft.Win32;

namespace Forja.Platform
{
    public class PowerEvents : IDisposable
    {
        public event Action Suspending;
        public event Action Resumed;

        private bool disposed;

        public PowerEvents()
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            Action h = null;
            if (e.Mode == PowerModes.Suspend) h = Suspending;
            else if (e.Mode == PowerModes.Resume) h = Resumed;
            if (h != null) h();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
