namespace Forja.Core
{
    // The crash-log window shared by the Saúde page and the start-up check for a new unexpected
    // restart. (The strip and Settings no longer write crash text: see StripText and FailureText.)
    public static class Stability
    {
        // How far back the Windows log is read.
        public const int CrashWindowDays = 30;
    }
}
