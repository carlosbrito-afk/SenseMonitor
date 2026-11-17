using System;
using System.Threading;
using System.Xml;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class NitroKeyTests
    {
        private const int Down = 0x100;
        private const int Up = 0x101;

        [Test]
        public static void NitroKey_Press_TriggersOnce()
        {
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "keydown fires");
            Assert.True(!k.OnKey(Up, 0xFF, 0x75, 0x81, 1100), "keyup does not fire");
        }

        [Test]
        public static void Repeat_TriggersOnce()
        {
            NitroKey k = new NitroKey();
            int count = 0;
            int t = 100000;
            if (k.OnKey(Down, 0xFF, 0x75, 0x1, t)) count++;
            t += 500; // initial repeat delay
            for (int n = 0; n < 60; n++)
            {
                if (k.OnKey(Down, 0xFF, 0x75, 0x1, t)) count++;
                t += 33;
            }
            if (k.OnKey(Up, 0xFF, 0x75, 0x81, t)) count++;
            Assert.Equal(1, count);
        }

        [Test]
        public static void HeldKeyEmitsPairs_TriggersOnce()
        {
            NitroKey k = new NitroKey();
            int count = 0;
            for (int t = 5000; t < 7000; t += 250)
            {
                if (k.OnKey(Down, 0xFF, 0x75, 0x1, t)) count++;
                if (k.OnKey(Up, 0xFF, 0x75, 0x81, t + 20)) count++;
            }
            Assert.Equal(1, count);
        }

        [Test]
        public static void TwoPressesTwoSecondsApart_TriggersTwice()
        {
            NitroKey k = new NitroKey();
            int count = 0;
            for (int press = 0; press < 2; press++)
            {
                int t0 = 10000 + press * 2000;
                for (int t = t0; t < t0 + 500; t += 250)
                {
                    if (k.OnKey(Down, 0xFF, 0x75, 0x1, t)) count++;
                    if (k.OnKey(Up, 0xFF, 0x75, 0x81, t + 20)) count++;
                }
            }
            Assert.Equal(2, count);
        }

        [Test]
        public static void StuckDown_RecoversAfterGap()
        {
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "press 1, keyup lost");
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 6000), "press 2 counts");
            Assert.True(!k.OnKey(Down, 0xFF, 0x75, 0x1, 6600), "burst suppressed");
        }

        [Test]
        public static void OtherKeys_Ignored()
        {
            NitroKey k = new NitroKey();
            Assert.True(!k.OnKey(Down, 0x41, 0x1E, 0, 1000), "letter A down");
            Assert.True(!k.OnKey(Up, 0x41, 0x1E, 0x80, 1050), "letter A up");
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1100), "target fires, unaffected by typing");
            // typing between target events must not refresh the debounce timestamp
            Assert.True(!k.OnKey(Down, 0x41, 0x1E, 0, 1600), "other key");
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1900), "700 ms after last TARGET event");
        }

        [Test]
        public static void SameScanDifferentVk_Ignored()
        {
            NitroKey k = new NitroKey();
            Assert.True(!k.OnKey(Down, 0x41, 0x75, 0, 1000), "scan matches, vk not");
            Assert.True(!k.OnKey(Down, 0xFF, 0x76, 0, 1000), "vk matches, scan not");
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1001), "ignored keys left no trace");
        }

        [Test]
        public static void SysKeyDown_AlsoCounts()
        {
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(0x104, 0xFF, 0x75, 0x1, 1000), "WM_SYSKEYDOWN fires");
            Assert.True(!k.OnKey(0x105, 0xFF, 0x75, 0x81, 1050), "WM_SYSKEYUP does not fire");
            Assert.True(k.OnKey(0x100, 0xFF, 0x75, 0x1, 2000), "fires again after quiet");
        }

        [Test]
        public static void Reset_AllowsImmediatePress()
        {
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "press");
            Assert.True(!k.OnKey(Down, 0xFF, 0x75, 0x1, 1100), "suppressed");
            k.Reset();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1200), "press right after reset");
        }

        [Test]
        public static void TimeWraparound_Debounces()
        {
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, int.MaxValue - 100), "before wrap");
            Assert.True(!k.OnKey(Down, 0xFF, 0x75, 0x1, int.MinValue + 400), "burst across wrap");
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, int.MinValue + 400 + 2000), "later press across wrap");
        }

        [Test]
        public static void KeyHook_NoSyncContext_Throws()
        {
            SynchronizationContext old = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Assert.Throws<InvalidOperationException>(delegate { new KeyHook(new NitroKey(), delegate { }); });
            }
            finally { SynchronizationContext.SetSynchronizationContext(old); }
        }

        [Test]
        public static void KeyHook_InstallsOnOwnThread_DisposesCleanly()
        {
            SynchronizationContext old = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try
            {
                KeyHook h = new KeyHook(new NitroKey(), delegate { });
                Assert.True(h.IsInstalled, "installed");
                h.Reset();
                h.Dispose();
                Assert.True(!h.IsInstalled, "uninstalled");
                h.Dispose(); // idempotent
            }
            finally { SynchronizationContext.SetSynchronizationContext(old); }
        }

        [Test]
        public static void Autostart_BuildCreateArgs_UsesXmlFile()
        {
            string a = Autostart.BuildCreateArgs(@"C:\Temp\my dir\forja.xml");
            Assert.Equal(@"/Create /TN SenseMonitor /XML ""C:\Temp\my dir\forja.xml"" /F", a);
        }

        [Test]
        public static void Autostart_TaskXml_HasBatterySafeSettings()
        {
            string x = Autostart.BuildTaskXml(@"C:\Program Files\Forja & Co\Forja.exe", @"PC\usuario");
            XmlDocument d = new XmlDocument();
            d.LoadXml(x); // must be well-formed despite '&'
            XmlNamespaceManager ns = new XmlNamespaceManager(d.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Assert.Equal("\"C:\\Program Files\\Forja & Co\\Forja.exe\"", d.SelectSingleNode("//t:Exec/t:Command", ns).InnerText);
            Assert.Equal("false", d.SelectSingleNode("//t:Settings/t:DisallowStartIfOnBatteries", ns).InnerText);
            Assert.Equal("false", d.SelectSingleNode("//t:Settings/t:StopIfGoingOnBatteries", ns).InnerText);
            Assert.Equal("PT0S", d.SelectSingleNode("//t:Settings/t:ExecutionTimeLimit", ns).InnerText);
            Assert.Equal("IgnoreNew", d.SelectSingleNode("//t:Settings/t:MultipleInstancesPolicy", ns).InnerText);
            Assert.Equal("true", d.SelectSingleNode("//t:Settings/t:AllowHardTerminate", ns).InnerText);
            Assert.Equal("HighestAvailable", d.SelectSingleNode("//t:Principal/t:RunLevel", ns).InnerText);
            Assert.Equal(@"PC\usuario", d.SelectSingleNode("//t:LogonTrigger/t:UserId", ns).InnerText);
        }

        [Test]
        public static void SingleInstance_TryCreate_SecondIsNotFirst()
        {
            string name = @"Local\Forja.Test." + Guid.NewGuid().ToString("N");
            Mutex first;
            Mutex second;
            Assert.True(SingleInstance.TryCreate(name, out first), "first owns");
            try
            {
                Assert.True(!SingleInstance.TryCreate(name, out second), "second is not first");
                Assert.Null(second);
            }
            finally { first.ReleaseMutex(); first.Close(); }
        }

        [Test]
        public static void SingleInstance_ShowMessageId_IsRegistered()
        {
            Assert.True(SingleInstance.ShowMessageId >= 0xC000, "registered messages are 0xC000+");
        }

        [Test]
        public static void SystemNames_UseProductName()
        {
            Assert.Equal("SenseMonitor", Autostart.TaskName);
            Assert.Equal(@"Global\SenseMonitor.SingleInstance", SingleInstance.MutexName);
            Assert.Equal("SenseMonitor.Show", SingleInstance.MessageName);
        }
    }
}
