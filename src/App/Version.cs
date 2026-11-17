using System.Reflection;

// The single source of the app version. publish\lancar.ps1 rewrites the Text line below
// (keep it on ONE line, in exactly this shape); everything else derives from it.
[assembly: AssemblyTitle("SenseMonitor")]
[assembly: AssemblyProduct("SenseMonitor")]
[assembly: AssemblyVersion(Forja.App.AppVersion.Text + ".0")]
[assembly: AssemblyFileVersion(Forja.App.AppVersion.Text + ".0")]
[assembly: AssemblyInformationalVersion(Forja.App.AppVersion.Text)]

namespace Forja.App
{
    public static class AppVersion
    {
        public const string Text = "0.2.3";
    }
}
