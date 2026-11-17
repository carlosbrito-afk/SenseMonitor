using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The event log selection checked against real channel names: the channels of one Windows 11
    // PC that had records when the cleanup review listed them (read-only, Get-WinEvent -ListLog).
    // The format is "name|kind|records". Adding a channel here that must stay, or one that may go,
    // is the way to change the allowlist on purpose.
    public static class CleanupEventGoldenTests
    {
        private static readonly string[] RealChannels =
        {
            "Application|Administrative|10029",
            "Microsoft-Client-Licensing-Platform/Admin|Administrative|1627",
            "Microsoft-Windows-AAD/Operational|Operational|10",
            "Microsoft-Windows-AppID/Operational|Operational|304",
            "Microsoft-Windows-Application-Experience/Program-Compatibility-Assistant|Operational|131",
            "Microsoft-Windows-Application-Experience/Program-Telemetry|Operational|545",
            "Microsoft-Windows-AppLocker/EXE and DLL|Operational|28",
            "Microsoft-Windows-AppLocker/MSI and Script|Operational|251",
            "Microsoft-Windows-AppModel-Runtime/Admin|Administrative|1556",
            "Microsoft-Windows-AppReadiness/Admin|Administrative|1463",
            "Microsoft-Windows-AppReadiness/Operational|Operational|190",
            "Microsoft-Windows-AppXDeployment/Operational|Operational|349",
            "Microsoft-Windows-AppXDeploymentServer/Operational|Operational|6805",
            "Microsoft-Windows-AppXDeployment-Server/Operational|Operational|152",
            "Microsoft-Windows-AppxPackaging/Operational|Operational|1920",
            "Microsoft-Windows-Audio/Operational|Operational|1889",
            "Microsoft-Windows-Audio/PlaybackManager|Operational|1897",
            "Microsoft-Windows-Authentication User Interface/Operational|Operational|1",
            "Microsoft-Windows-BackgroundTaskInfrastructure/Operational|Operational|35",
            "Microsoft-Windows-Biometrics/Operational|Operational|132",
            "Microsoft-Windows-BitLocker/BitLocker Management|Operational|37",
            "Microsoft-Windows-Bits-Client/Operational|Operational|1153",
            "Microsoft-Windows-CertificateServicesClient-Lifecycle-System/Operational|Operational|56",
            "Microsoft-Windows-CloudFiles-Filter/Operational|Operational|56",
            "Microsoft-Windows-CloudRestoreLauncher/Operational|Operational|1296",
            "Microsoft-Windows-CloudStore/Initialization|Operational|42",
            "Microsoft-Windows-CloudStore/Operational|Operational|1696",
            "Microsoft-Windows-CodeIntegrity/Operational|Operational|1354",
            "Microsoft-Windows-Containers-BindFlt/Operational|Operational|56",
            "Microsoft-Windows-Containers-Wcifs/Operational|Operational|56",
            "Microsoft-Windows-Crypto-DPAPI/Operational|Operational|386",
            "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin|Administrative|725",
            "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Operational|Operational|712",
            "Microsoft-Windows-DeviceSetupManager/Admin|Administrative|2218",
            "Microsoft-Windows-DeviceSetupManager/Operational|Operational|31",
            "Microsoft-Windows-Dhcp-Client/Admin|Administrative|404",
            "Microsoft-Windows-Dhcpv6-Client/Admin|Administrative|170",
            "Microsoft-Windows-Diagnosis-DPS/Operational|Operational|455",
            "Microsoft-Windows-Diagnosis-PCW/Operational|Operational|2244",
            "Microsoft-Windows-Diagnosis-Scheduled/Operational|Operational|84",
            "Microsoft-Windows-Diagnosis-Scripted/Admin|Administrative|12",
            "Microsoft-Windows-Diagnosis-Scripted/Operational|Operational|48",
            "Microsoft-Windows-EnhancedStorage-EhStorClass/Operational|Operational|358",
            "Microsoft-Windows-Fault-Tolerant-Heap/Operational|Operational|5",
            "Microsoft-Windows-HelloForBusiness/Operational|Operational|1367",
            "Microsoft-Windows-Kernel-Boot/Operational|Operational|373",
            "Microsoft-Windows-Kernel-Cache/Operational|Operational|759",
            "Microsoft-Windows-Kernel-Dump/Operational|Operational|62",
            "Microsoft-Windows-Kernel-EventTracing/Admin|Administrative|33",
            "Microsoft-Windows-Kernel-PnP/Configuration|Operational|1057",
            "Microsoft-Windows-Kernel-PnP/Device Management|Operational|327",
            "Microsoft-Windows-Kernel-PnP/Driver Watchdog|Operational|38",
            "Microsoft-Windows-Kernel-WHEA/Operational|Operational|507",
            "Microsoft-Windows-Known Folders API Service|Operational|1979",
            "Microsoft-Windows-LanguagePackSetup/Operational|Operational|37",
            "Microsoft-Windows-MF-FrameServer/Camera_DeviceMFT|Operational|1868",
            "Microsoft-Windows-MF-FrameServer/Camera_FrameServer|Operational|1859",
            "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot|Administrative|17",
            "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/ManagementService|Administrative|7",
            "Microsoft-Windows-MUI/Operational|Operational|18",
            "Microsoft-Windows-NcdAutoSetup/Operational|Operational|39",
            "Microsoft-Windows-NCSI/Operational|Operational|542",
            "Microsoft-Windows-NetworkProfile/Operational|Operational|2188",
            "Microsoft-Windows-NlaSvc/Operational|Operational|11",
            "Microsoft-Windows-Ntfs/Operational|Operational|2180",
            "Microsoft-Windows-Ntfs/WHC|Operational|58",
            "Microsoft-Windows-Partition/Diagnostic|Operational|65",
            "Microsoft-Windows-PCI/Operational|Operational|1876",
            "Microsoft-Windows-PCRPF/PCRPF|Operational|380",
            "Microsoft-Windows-Perflib/Operational|Operational|1431",
            "Microsoft-Windows-PowerShell/Operational|Operational|2803",
            "Microsoft-Windows-Provisioning-Diagnostics-Provider/Admin|Administrative|725",
            "Microsoft-Windows-PushNotification-Platform/Operational|Operational|1528",
            "Microsoft-Windows-ReadyBoost/Operational|Operational|38",
            "Microsoft-Windows-Resource-Exhaustion-Detector/Operational|Operational|88",
            "Microsoft-Windows-Resource-Exhaustion-Resolver/Operational|Operational|98",
            "Microsoft-Windows-Security-LessPrivilegedAppContainer/Operational|Operational|1927",
            "Microsoft-Windows-Security-Mitigations/KernelMode|Operational|355",
            "Microsoft-Windows-Security-SPP-UX-Notifications/ActionCenter|Operational|5",
            "Microsoft-Windows-ShellCommon-StartLayoutPopulation/Operational|Operational|572",
            "Microsoft-Windows-Shell-Core/AppDefaults|Operational|1798",
            "Microsoft-Windows-Shell-Core/Operational|Operational|2281",
            "Microsoft-Windows-StateRepository/Operational|Operational|7562",
            "Microsoft-Windows-Storage-ClassPnP/Operational|Operational|2",
            "Microsoft-Windows-StorageManagement/Operational|Operational|679",
            "Microsoft-Windows-StorageSpaces-Driver/Operational|Operational|59",
            "Microsoft-Windows-StorageVolume/Operational|Operational|238",
            "Microsoft-Windows-Store/Operational|Operational|25507",
            "Microsoft-Windows-Storsvc/Diagnostic|Operational|31",
            "Microsoft-Windows-TaskScheduler/Maintenance|Operational|569",
            "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational|Operational|1213",
            "Microsoft-Windows-Time-Service/Operational|Operational|797",
            "Microsoft-Windows-TWinUI/Operational|Operational|19",
            "Microsoft-Windows-TZSync/Operational|Operational|27",
            "Microsoft-Windows-UniversalTelemetryClient/Operational|Operational|2066",
            "Microsoft-Windows-USB-UCMUCSICX/Operational|Operational|8",
            "Microsoft-Windows-User Device Registration/Admin|Administrative|44",
            "Microsoft-Windows-User Profile Service/Operational|Operational|257",
            "Microsoft-Windows-UserPnp/DeviceInstall|Operational|103",
            "Microsoft-Windows-UserSettingsBackup-Orchestrator/Operational|Operational|365",
            "Microsoft-Windows-VolumeSnapshot-Driver/Operational|Operational|1058",
            "Microsoft-Windows-Wcmsvc/Operational|Operational|1091",
            "Microsoft-Windows-WebAuthN/Operational|Operational|690",
            "Microsoft-Windows-WER-Diag/Operational|Operational|8",
            "Microsoft-Windows-WerKernel/Operational|Operational|15",
            "Microsoft-Windows-WER-PayloadHealth/Operational|Operational|206",
            "Microsoft-Windows-WFP/Operational|Operational|31",
            "Microsoft-Windows-Windows Defender/Operational|Operational|2645",
            "Microsoft-Windows-Windows Firewall With Advanced Security/Firewall|Operational|1068",
            "Microsoft-Windows-Windows Firewall With Advanced Security/FirewallDiagnostics|Operational|4",
            "Microsoft-Windows-WindowsSystemAssessmentTool/Operational|Operational|292",
            "Microsoft-Windows-WindowsUpdateClient/Operational|Operational|1077",
            "Microsoft-Windows-WinINet-Config/ProxyConfigChanged|Operational|3",
            "Microsoft-Windows-Winlogon/Operational|Operational|2502",
            "Microsoft-Windows-WinRM/Operational|Operational|392",
            "Microsoft-Windows-WLAN-AutoConfig/Operational|Operational|1114",
            "Microsoft-Windows-WMI-Activity/Operational|Operational|1175",
            "Microsoft-Windows-WPD-ClassInstaller/Operational|Operational|1",
            "Microsoft-Windows-WPD-MTPClassDriver/Operational|Operational|56",
            "OneApp_IGCC|Administrative|728",
            "Setup|Operational|2435",
            "System|Administrative|19536",
            "Windows PowerShell|Administrative|568",
        };

        // Everything that may be cleared out of the list above: application, network status,
        // store, telemetry and shell noise. Sorted the way EventLogFilter.Select sorts.
        private static readonly string[] Expected =
        {
            "Microsoft-Windows-Application-Experience/Program-Compatibility-Assistant",
            "Microsoft-Windows-Application-Experience/Program-Telemetry",
            "Microsoft-Windows-AppReadiness/Operational",
            "Microsoft-Windows-AppXDeployment-Server/Operational",
            "Microsoft-Windows-AppXDeployment/Operational",
            "Microsoft-Windows-AppXDeploymentServer/Operational",
            "Microsoft-Windows-AppxPackaging/Operational",
            "Microsoft-Windows-Audio/Operational",
            "Microsoft-Windows-Audio/PlaybackManager",
            "Microsoft-Windows-BackgroundTaskInfrastructure/Operational",
            "Microsoft-Windows-Bits-Client/Operational",
            "Microsoft-Windows-CloudRestoreLauncher/Operational",
            "Microsoft-Windows-CloudStore/Initialization",
            "Microsoft-Windows-CloudStore/Operational",
            "Microsoft-Windows-Diagnosis-DPS/Operational",
            "Microsoft-Windows-Diagnosis-PCW/Operational",
            "Microsoft-Windows-Diagnosis-Scheduled/Operational",
            "Microsoft-Windows-Diagnosis-Scripted/Operational",
            "Microsoft-Windows-Known Folders API Service",
            "Microsoft-Windows-LanguagePackSetup/Operational",
            "Microsoft-Windows-MF-FrameServer/Camera_DeviceMFT",
            "Microsoft-Windows-MF-FrameServer/Camera_FrameServer",
            "Microsoft-Windows-MUI/Operational",
            "Microsoft-Windows-NCSI/Operational",
            "Microsoft-Windows-NetworkProfile/Operational",
            "Microsoft-Windows-NlaSvc/Operational",
            "Microsoft-Windows-Perflib/Operational",
            "Microsoft-Windows-PowerShell/Operational",
            "Microsoft-Windows-PushNotification-Platform/Operational",
            "Microsoft-Windows-Shell-Core/AppDefaults",
            "Microsoft-Windows-Shell-Core/Operational",
            "Microsoft-Windows-ShellCommon-StartLayoutPopulation/Operational",
            "Microsoft-Windows-StateRepository/Operational",
            "Microsoft-Windows-Store/Operational",
            "Microsoft-Windows-TWinUI/Operational",
            "Microsoft-Windows-TZSync/Operational",
            "Microsoft-Windows-UniversalTelemetryClient/Operational",
            "Microsoft-Windows-UserSettingsBackup-Orchestrator/Operational",
            "Microsoft-Windows-Wcmsvc/Operational",
            "Microsoft-Windows-WindowsSystemAssessmentTool/Operational",
            "Microsoft-Windows-WinINet-Config/ProxyConfigChanged",
            "Microsoft-Windows-WLAN-AutoConfig/Operational"
        };

        // Channels that must never be selected, by name, with the reason they matter.
        private static readonly string[] MustStay =
        {
            "System", "Application", "Security", "Setup", "Windows PowerShell",
            "Microsoft-Windows-Kernel-WHEA/Operational",          // hardware errors
            "Microsoft-Windows-Kernel-Dump/Operational",          // blue screen dump writing
            "Microsoft-Windows-Kernel-Boot/Operational",
            "Microsoft-Windows-Kernel-PnP/Driver Watchdog",
            "Microsoft-Windows-WER-Diag/Operational",
            "Microsoft-Windows-WerKernel/Operational",
            "Microsoft-Windows-WER-PayloadHealth/Operational",
            "Microsoft-Windows-Fault-Tolerant-Heap/Operational",   // crash-adjacent
            "Microsoft-Windows-Resource-Exhaustion-Detector/Operational",
            "Microsoft-Windows-Resource-Exhaustion-Resolver/Operational",
            "Microsoft-Windows-Crashdump/Operational",             // empty here, full after a blue screen
            "Microsoft-Windows-PCI/Operational",                   // hardware
            "Microsoft-Windows-USB-UCMUCSICX/Operational",
            "Microsoft-Windows-UserPnp/DeviceInstall",
            "Microsoft-Windows-Partition/Diagnostic",
            "Microsoft-Windows-Storsvc/Diagnostic",
            "Microsoft-Windows-VolumeSnapshot-Driver/Operational",
            "Microsoft-Windows-WPD-ClassInstaller/Operational",
            "Microsoft-Windows-WPD-MTPClassDriver/Operational",
            "Microsoft-Windows-Ntfs/Operational",
            "Microsoft-Windows-StorageSpaces-Driver/Operational",
            "Microsoft-Windows-DriverFrameworks-UserMode/Operational",   // disabled here, enabled elsewhere
            "Microsoft-Windows-AppLocker/EXE and DLL",             // audit trail
            "Microsoft-Windows-AppLocker/MSI and Script",
            "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational",
            "Microsoft-Windows-Winlogon/Operational",
            "Microsoft-Windows-WinRM/Operational",
            "Microsoft-Windows-Windows Defender/Operational",
            "Microsoft-Windows-Windows Firewall With Advanced Security/Firewall",
            "Microsoft-Windows-CodeIntegrity/Operational",
            "Microsoft-Windows-BitLocker/BitLocker Management",
            "Microsoft-Windows-WindowsUpdateClient/Operational",   // which update came before a crash
            "Microsoft-Windows-Time-Service/Operational",
            "Microsoft-Windows-WMI-Activity/Operational",
            "Microsoft-Windows-Diagnostics-Performance/Operational",
            "Microsoft-Windows-Dxgkrnl/Operational"
        };

        private static List<EventChannelInfo> Parse(string[] lines)
        {
            List<EventChannelInfo> l = new List<EventChannelInfo>();
            foreach (string line in lines)
            {
                string[] p = line.Split('|');
                EventChannelInfo c = new EventChannelInfo();
                c.Name = p[0];
                c.Kind = (EventChannelKind)Enum.Parse(typeof(EventChannelKind), p[1]);
                c.Records = long.Parse(p[2]);
                l.Add(c);
            }
            return l;
        }

        [Test]
        public static void Golden_RealChannelList_SelectsExactlyTheAllowlistedNoise()
        {
            List<string> pick = EventLogFilter.Select(Parse(RealChannels));
            string got = string.Join("\n", pick.ToArray());
            string want = string.Join("\n", Expected);
            Assert.Equal(want, got);
        }

        [Test]
        public static void Golden_CrashAndHardwareChannels_AreNeverSelected_EvenWhenTheyHaveRecords()
        {
            List<EventChannelInfo> all = Parse(RealChannels);
            // channels that are empty or disabled on this PC but full on another one
            foreach (string name in MustStay)
            {
                EventChannelInfo c = new EventChannelInfo();
                c.Name = name;
                c.Kind = EventChannelKind.Operational;
                c.Records = 5000;
                all.Add(c);
            }
            List<string> pick = EventLogFilter.Select(all);
            foreach (string name in MustStay)
            {
                foreach (string p in pick)
                    Assert.True(!string.Equals(p, name, StringComparison.OrdinalIgnoreCase), "must stay: " + name);
            }
            Assert.Equal(Expected.Length, pick.Count);
        }

        [Test]
        public static void Golden_ChannelWindowsAddsLater_IsKeptBecauseItIsNotOnTheAllowlist()
        {
            List<EventChannelInfo> all = new List<EventChannelInfo>();
            string[] future = { "Microsoft-Windows-BrandNewSubsystem/Operational", "Microsoft-Windows-Foo/Analytic", "Contoso-Agent/Operational" };
            foreach (string n in future)
            {
                EventChannelInfo c = new EventChannelInfo();
                c.Name = n;
                c.Kind = EventChannelKind.Operational;
                c.Records = 10;
                all.Add(c);
            }
            Assert.Equal(0, EventLogFilter.Select(all).Count);
        }

        [Test]
        public static void Golden_AllowlistedNamesAreAllAcceptedByTheSecondGuard()
        {
            foreach (string name in Expected)
            {
                Assert.True(EventLogFilter.IsAllowed(name), "allowed: " + name);
                Assert.True(!EventLogFilter.IsExcluded(name), "not excluded by the denylist: " + name);
                Assert.True(EventLogFilter.IsSafeName(name), "safe for a command line: " + name);
            }
        }

        [Test]
        public static void Allowlist_MatchesWholeFamiliesOnly()
        {
            Assert.True(EventLogFilter.IsAllowed("Microsoft-Windows-NCSI/Operational"), "family member");
            Assert.True(EventLogFilter.IsAllowed("microsoft-windows-ncsi/operational"), "any case");
            Assert.True(EventLogFilter.IsAllowed("Microsoft-Windows-Known Folders API Service"), "exact entry");
            Assert.True(!EventLogFilter.IsAllowed("Microsoft-Windows-NCSI"), "the bare family name is not a channel of it");
            Assert.True(!EventLogFilter.IsAllowed("Microsoft-Windows-NCSIevil/Operational"), "a longer name is not the family");
            Assert.True(!EventLogFilter.IsAllowed("Microsoft-Windows-AppXDeployment-Foo/Operational"), "prefix without a boundary");
            Assert.True(!EventLogFilter.IsAllowed("Microsoft-Windows-PowerShell/Admin"), "only the Operational channel of PowerShell");
            Assert.True(!EventLogFilter.IsAllowed(null) && !EventLogFilter.IsAllowed(""), "null and empty");
        }

        [Test]
        public static void Denylist_NowAlsoCoversCrashHardwareAndAuditFamilies()
        {
            string[] never =
            {
                "Microsoft-Windows-Crashdump/Operational", "Microsoft-Windows-Resource-Exhaustion-Detector/Operational",
                "Microsoft-Windows-Fault-Tolerant-Heap/Operational", "Microsoft-Windows-PCI/Operational",
                "Microsoft-Windows-USB-UCMUCSICX/Operational", "Microsoft-Windows-UserPnp/DeviceInstall",
                "Microsoft-Windows-Partition/Diagnostic", "Microsoft-Windows-VolumeSnapshot-Driver/Operational",
                "Microsoft-Windows-Storsvc/Diagnostic", "Microsoft-Windows-DriverFrameworks-UserMode/Operational",
                "Microsoft-Windows-Dxgkrnl/Operational", "Microsoft-Windows-AppLocker/EXE and DLL"
            };
            foreach (string n in never) Assert.True(EventLogFilter.IsExcluded(n), "second guard excludes " + n);
        }
    }
}
