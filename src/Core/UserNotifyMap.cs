namespace Forja.Core
{
    // Maps the value of shell32's SHQueryUserNotificationState to what the alert engine needs.
    // QUERY_USER_NOTIFICATION_STATE: 1 not present (locked / screen saver), 2 busy (a full-screen
    // app), 3 running D3D full screen (a game), 4 presentation mode, 5 accepts notifications,
    // 6 quiet time (focus assist), 7 a Store app in full screen. Pure; no handle to any other
    // process is ever opened (the question goes to the shell).
    public static class UserNotifyMap
    {
        public static AlertContext ToContext(int state)
        {
            AlertContext c = new AlertContext();
            if (state == 2 || state == 3 || state == 4 || state == 7) c.Fullscreen = true;
            if (state == 1 || state == 6) c.OsQuiet = true;
            return c;
        }
    }
}
