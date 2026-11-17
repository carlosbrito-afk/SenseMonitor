using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Forja.Core;

namespace Forja.Platform
{
    // A folder that only administrators may write, under a place where ordinary users can create folders
    // (%ProgramData%). A locked-down DACL on a folder WE create is not enough: a standard user can create
    // %ProgramData%\SenseMonitor first and stay its owner (an owner can always rewrite the DACL, and keeps any
    // handle it opened earlier). So:
    //
    //   * A folder is NEVER adopted, repaired or emptied in place. If a level fails verification it is renamed
    //     aside, in the same parent, to "<name>.recusado-<ticks>" (MoveFile on the NAME: a link is renamed as
    //     a link, never followed; nothing inside is listed, changed or deleted, no security is set on it).
    //     If the rename fails the folder is not used (the feature stays off; a denial of service at worst).
    //   * A fresh directory is then created with the protected security descriptor applied ATOMICALLY by
    //     CreateDirectory (owner Administrators, SYSTEM + Administrators full control, optionally Users
    //     read-only, nothing inherited). If the name already exists at that moment, somebody raced us: it is
    //     treated as hostile again (bounded retries, then refuse).
    //   * The directory is opened BY HANDLE (BACKUP_SEMANTICS | OPEN_REPARSE_POINT: a link is seen as a link)
    //     and verified through the handle: not a reparse point, is a directory, owner and DACL through
    //     GetSecurityInfo (AdminFolderRules). The handle is kept open WITHOUT delete sharing for the life of
    //     the process, so the folder that was verified cannot be renamed, replaced or turned into a link
    //     while it is in use; later file creation by path therefore refers to the verified object.
    //   * Nothing needs a special privilege (no take-ownership, no restore): that is also why none is enabled.
    public static class AdminFolder
    {
        // protected DACL, owner Administrators; BU = read & execute only when asked
        public static string Sddl(bool usersCanRead)
        {
            return "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + (usersCanRead ? "(A;OICI;0x1200a9;;;BU)" : "");
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, SafeFileHandle> held = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);

        // Read-only verification of whatever is at path, by handle (no repair, nothing kept open).
        public static string Problem(string path)
        {
            string err;
            using (SafeFileHandle h = OpenDir(path, out err))
            {
                if (h == null) return err;
                return VerifyHandle(h);
            }
        }

        // Verifies the folder and, when asked to create (the caller is elevated), builds it fresh or replaces a
        // hostile one (renamed aside, never touched). note: what was set aside, for the caller to log.
        // Returns the path, or null with a reason.
        public static string Ensure(string path, bool usersCanRead, bool create, out string reason, out string note)
        {
            return Ensure(path, usersCanRead, create, Sddl(usersCanRead), out reason, out note);
        }

        // sddl is a parameter so tests (which run without administrator rights and cannot assign the
        // Administrators group as owner) can exercise the rename-aside and create flow.
        internal static string Ensure(string path, bool usersCanRead, bool create, string sddl, out string reason, out string note)
        {
            reason = null;
            note = null;
            lock (gate)
            {
                try
                {
                    SafeFileHandle cached;
                    if (held.TryGetValue(path, out cached))
                    {
                        if (VerifyHandle(cached) == null && SameObject(cached, path)) return path;
                        cached.Dispose();
                        held.Remove(path);
                    }
                    string lastProblem = null;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        string err;
                        SafeFileHandle h = OpenDir(path, out err);
                        string deniedProblem = null;
                        if (h == null && err == "denied") deniedProblem = "a pasta não pode ser aberta por nós (acesso negado)";
                        if (h == null && deniedProblem == null)
                        {
                            if (!NotFound(err)) { reason = err; return null; }
                            if (!create) { reason = "a pasta ainda não existe"; return null; }
                            string createErr;
                            // already-exists here means somebody created it between our open and our create:
                            // the loop opens it again, finds it unacceptable (or ours) and acts on that
                            if (!CreateFresh(path, sddl, out createErr) && createErr != "exists") { reason = createErr; return null; }
                            continue;
                        }
                        string problem = deniedProblem;
                        if (problem == null)
                        {
                            problem = VerifyHandle(h);
                            if (problem == null)
                            {
                                held[path] = h; // kept open, no delete sharing: this object stays the object at this path
                                return path;
                            }
                            h.Dispose(); // a directory with an open handle cannot be renamed
                        }
                        lastProblem = problem;
                        if (!create) { reason = "pasta insegura (" + problem + ")"; return null; }
                        string aside = AdminFolderNames.AsideName(path, DateTime.UtcNow.Ticks + attempt);
                        if (!MoveFileExW(path, aside, 0))
                        {
                            reason = "a pasta insegura (" + problem + ") não pôde ser posta de lado (erro " + Marshal.GetLastWin32Error() + "); recusada";
                            return null;
                        }
                        note = "a pasta " + path + " não passou na verificação (" + problem + "); foi renomeada para " + Path.GetFileName(aside) + " (nada dentro dela foi aberto, alterado ou apagado) e uma pasta nova foi criada";
                    }
                    reason = "não foi possível obter uma pasta segura em " + path + (lastProblem != null ? " (" + lastProblem + ")" : "");
                    return null;
                }
                catch (Exception ex)
                {
                    reason = "pasta inacessível (" + ex.GetType().Name + ")";
                    return null;
                }
            }
        }

        // Drops the held handles (the uninstaller must be able to remove the folders).
        public static void ReleaseAll()
        {
            lock (gate)
            {
                foreach (SafeFileHandle h in held.Values) h.Dispose();
                held.Clear();
            }
        }

        // ---- verification through the handle ---------------------------------------------------------

        // null = fine.
        internal static string VerifyHandle(SafeFileHandle h)
        {
            ByHandleFileInformation info;
            if (!GetFileInformationByHandle(h, out info)) return "não foi possível ler os atributos da pasta (erro " + Marshal.GetLastWin32Error() + ")";
            if ((info.Attributes & FileAttributesReparsePoint) != 0) return "a pasta é um link";
            if ((info.Attributes & FileAttributesDirectory) == 0) return "o caminho não é uma pasta";
            string owner;
            List<AdminAclRule> rules;
            string e = ReadSecurity(h, out owner, out rules);
            if (e != null) return e;
            return AdminFolderRules.Check(owner, rules);
        }

        // Owner and DACL through GetSecurityInfo(handle): no path lookup, no following of links.
        private static string ReadSecurity(SafeFileHandle h, out string owner, out List<AdminAclRule> rules)
        {
            owner = null;
            rules = null;
            IntPtr pOwner, pGroup, pDacl, pSacl, pSd;
            uint rc = GetSecurityInfo(h, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation, out pOwner, out pGroup, out pDacl, out pSacl, out pSd);
            if (rc != 0) return "não foi possível ler as permissões da pasta (erro " + rc + ")";
            try
            {
                uint len = GetSecurityDescriptorLength(pSd);
                byte[] bytes = new byte[len];
                Marshal.Copy(pSd, bytes, 0, (int)len);
                return ParseDescriptor(bytes, out owner, out rules);
            }
            finally
            {
                LocalFree(pSd);
            }
        }

        // Pure: a self-relative descriptor -> owner SID and the allow/deny rules of its DACL. A missing DACL
        // (everyone has access) is reported as a problem by returning no rules.
        internal static string ParseDescriptor(byte[] bytes, out string owner, out List<AdminAclRule> rules)
        {
            owner = null;
            rules = null;
            try
            {
                RawSecurityDescriptor sd = new RawSecurityDescriptor(bytes, 0);
                owner = sd.Owner == null ? null : sd.Owner.Value;
                if (sd.DiscretionaryAcl == null) return "a pasta não tem lista de permissões (acesso livre)";
                List<AdminAclRule> l = new List<AdminAclRule>();
                foreach (GenericAce ace in sd.DiscretionaryAcl)
                {
                    KnownAce k = ace as KnownAce;
                    if (k == null) { l.Add(null); continue; }
                    l.Add(new AdminAclRule(k.SecurityIdentifier.Value, k.AceType == AceType.AccessAllowed, (uint)k.AccessMask));
                }
                rules = l;
                return null;
            }
            catch (Exception ex)
            {
                return "descritor de segurança ilegível (" + ex.GetType().Name + ")";
            }
        }

        // The descriptor of a freshly built SDDL, for tests of the rules against what we create.
        internal static string DescribeSddl(string sddl, out string owner, out List<AdminAclRule> rules)
        {
            RawSecurityDescriptor sd = new RawSecurityDescriptor(sddl);
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            return ParseDescriptor(bytes, out owner, out rules);
        }

        // ---- handles, creation, rename ---------------------------------------------------------------

        // FILE_LIST_DIRECTORY is in the access mask ON PURPOSE: an open that asks only for attribute/control
        // access takes no part in sharing checks (verified: the folder could still be renamed). With list access
        // and no FILE_SHARE_DELETE, the held handle makes any rename or replacement of this folder fail with a
        // sharing violation, while creating and listing files inside it keeps working.
        private static SafeFileHandle OpenDir(string path, out string error)
        {
            error = null;
            SafeFileHandle h = CreateFile(path, FileListDirectory | FileReadAttributes | ReadControl, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                if (err == 2 || err == 3) error = "not-found";
                else if (err == 5) error = "denied"; // a folder we may not even look at: not ours, treated as hostile
                else error = "não foi possível abrir a pasta (erro " + err + ")";
                return null;
            }
            return h;
        }

        // For tests: the same open that Ensure keeps.
        internal static SafeFileHandle OpenHeldForTests(string path)
        {
            string err;
            return OpenDir(path, out err);
        }

        private static bool NotFound(string err)
        {
            return err == "not-found";
        }

        private static bool SameObject(SafeFileHandle heldHandle, string path)
        {
            string err;
            using (SafeFileHandle now = OpenDir(path, out err))
            {
                if (now == null) return false;
                ByHandleFileInformation a, b;
                if (!GetFileInformationByHandle(heldHandle, out a) || !GetFileInformationByHandle(now, out b)) return false;
                return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
            }
        }

        // CreateDirectoryW with the descriptor in SECURITY_ATTRIBUTES: the DACL and the owner exist from the
        // first instant. createErr = "exists" when the name was already there.
        private static bool CreateFresh(string path, string sddl, out string createErr)
        {
            createErr = null;
            IntPtr sd;
            uint len;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out len))
            {
                createErr = "descritor de segurança inválido (erro " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            try
            {
                SecurityAttributes sa = new SecurityAttributes();
                sa.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                sa.Descriptor = sd;
                sa.InheritHandle = false;
                if (CreateDirectoryW(path, ref sa)) return true;
                int err = Marshal.GetLastWin32Error();
                if (err == 183) { createErr = "exists"; return false; }
                createErr = "não foi possível criar a pasta (erro " + err + ")";
                return false;
            }
            finally
            {
                LocalFree(sd);
            }
        }

        // ---- Win32 ---------------------------------------------------------------------------------

        private const uint FileListDirectory = 0x1, FileReadAttributes = 0x80, ReadControl = 0x20000;
        private const uint ShareRead = 1, ShareWrite = 2;   // deliberately no FILE_SHARE_DELETE
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileAttributesDirectory = 0x10, FileAttributesReparsePoint = 0x400;
        private const int SeFileObject = 1;
        private const uint OwnerSecurityInformation = 1, DaclSecurityInformation = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityInfo(SafeFileHandle h, int objectType, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr p);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, ref SecurityAttributes sa);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string from, string to, uint flags);
    }
}
