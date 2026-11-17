using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Forja.Platform
{
    // Starts a process OUTSIDE the job object of the caller (CREATE_BREAKAWAY_FROM_JOB). A tray app started by its scheduled
    // task lives in that task's job; something it starts as a child lives there too. This is the fallback of the Settings
    // uninstall when the on-demand task could not be used. When the job does not allow breakaway the call fails (the Win32
    // error says so) and nothing is started: the caller then tells the user to use Apps & features.
    public static class JobBreakaway
    {
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        // "exe" args, the exe quoted (arguments is used as given: callers pass constants only).
        public static string CommandLine(string exe, string arguments)
        {
            string line = "\"" + exe + "\"";
            if (!string.IsNullOrEmpty(arguments)) line += " " + arguments;
            return line;
        }

        // true = started outside the job. false = not started; error is the Win32 error (5 = the job does not allow breakaway).
        public static bool Start(string exe, string arguments, out int error)
        {
            error = 0;
            try
            {
                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                string dir = System.IO.Path.GetDirectoryName(exe);
                bool ok = CreateProcessW(exe, new StringBuilder(CommandLine(exe, arguments)), IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, dir, ref si, out pi);
                if (!ok)
                {
                    error = Marshal.GetLastWin32Error();
                    if (error == 0) error = -1;
                    return false;
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return true;
            }
            catch (Exception)
            {
                error = -2;
                return false;
            }
        }
    }
}
