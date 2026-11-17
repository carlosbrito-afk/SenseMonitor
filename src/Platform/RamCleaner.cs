using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forja.Core;

namespace Forja.Platform
{
    // RAMMap-style cleaning through NtSetSystemInformation(SystemMemoryListInformation):
    // empty ALL working sets (one system-wide call, reaches protected/system processes),
    // flush the modified list, purge the standby list. Needs administrator.
    public static class RamCleaner
    {
        private const int SystemMemoryListInformation = 80;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x2;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        // SYSTEM_MEMORY_LIST_INFORMATION: 22 pointer-sized counters (pages):
        // [0] Zero, [1] Free, [2] Modified, [3] ModifiedNoWrite, [4] Bad,
        // [5..12] PageCountByPriority (= standby list), [13..20] Repurposed, [21] ModifiedPageFile.
        private const int MemoryListCounters = 22;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previous, IntPtr returnLength);

        private static volatile bool queryPrivilegeTried;

        // Runs the three steps and measures before / before the purge / after.
        // Never throws; each step's NTSTATUS is recorded in the result.
        public static RamCleanResult CleanDetailed()
        {
            RamCleanResult r = new RamCleanResult();
            try
            {
                r.Privileged = EnablePrivilege("SeProfileSingleProcessPrivilege");
                EnablePrivilege("SeIncreaseQuotaPrivilege");
                EnablePrivilege("SeDebugPrivilege");
                r.Before = Snapshot();

                r.EmptyStatus = Command(MemoryEmptyWorkingSets);
                if (r.EmptyStatus != 0)
                {
                    EmptyEachWorkingSet(); // fallback: only processes we can open
                    r.UsedFallback = true;
                }
                r.FlushStatus = Command(MemoryFlushModifiedList);
                r.BeforePurge = Snapshot();
                r.PurgeStatus = Command(MemoryPurgeStandbyList);
                r.After = Snapshot();
            }
            catch (Exception)
            {
                // keep whatever was measured
            }
            return r;
        }

        // Memory as Task Manager splits it. Fields that cannot be read stay -1.
        public static MemorySnapshot Snapshot()
        {
            MemorySnapshot s = new MemorySnapshot();
            try
            {
                // Reading the memory lists needs the profile privilege; enable it once.
                if (!queryPrivilegeTried)
                {
                    queryPrivilegeTried = true;
                    EnablePrivilege("SeProfileSingleProcessPrivilege");
                }
                s.Total = SystemStats.TotalPhysBytes();
                s.Available = SystemStats.AvailablePhysBytes();
                int size = MemoryListCounters * IntPtr.Size;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int returned;
                    if (NtQuerySystemInformation(SystemMemoryListInformation, buf, size, out returned) == 0)
                    {
                        byte[] raw = new byte[size];
                        Marshal.Copy(buf, raw, 0, size);
                        ParseMemoryLists(raw, IntPtr.Size, Environment.SystemPageSize, s);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch (Exception)
            {
            }
            return s;
        }

        // Fills Standby / Modified / Free from a raw SYSTEM_MEMORY_LIST_INFORMATION buffer.
        public static bool ParseMemoryLists(byte[] raw, int pointerSize, long pageSize, MemorySnapshot into)
        {
            if (raw == null || into == null || raw.Length < MemoryListCounters * pointerSize) return false;
            into.Free = (Counter(raw, 0, pointerSize) + Counter(raw, 1, pointerSize)) * pageSize;
            into.Modified = (Counter(raw, 2, pointerSize) + Counter(raw, 3, pointerSize)) * pageSize;
            long standby = 0;
            for (int i = 5; i < 13; i++) standby += Counter(raw, i, pointerSize);
            into.Standby = standby * pageSize;
            return true;
        }

        private static long Counter(byte[] raw, int index, int pointerSize)
        {
            return pointerSize == 8 ? BitConverter.ToInt64(raw, index * pointerSize) : BitConverter.ToUInt32(raw, index * pointerSize);
        }

        // NTSTATUS of one SystemMemoryListInformation command (0 = success).
        private static int Command(int command)
        {
            try
            {
                int c = command;
                return NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void EmptyEachWorkingSet()
        {
            Process[] all;
            try
            {
                all = Process.GetProcesses();
            }
            catch (Exception)
            {
                return;
            }
            foreach (Process p in all)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    // Open by pid so we never touch Process.Handle (which throws for protected processes).
                    h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (h != IntPtr.Zero) EmptyWorkingSet(h);
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (h != IntPtr.Zero) CloseHandle(h);
                    p.Dispose();
                }
            }
        }

        private static bool EnablePrivilege(string name)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                if (!LookupPrivilegeValue(null, name, out tp.Luid)) return false;
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                // Succeeds with ERROR_NOT_ALL_ASSIGNED (1300) when the privilege is not held.
                return Marshal.GetLastWin32Error() == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }
}
