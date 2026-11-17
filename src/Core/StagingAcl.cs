using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // One allow-entry of a folder's access list, reduced to what matters here.
    public sealed class AclEntry
    {
        public string Sid;
        // The entry lets this principal write, add files, delete, change permissions or take ownership.
        public bool CanModify;

        public AclEntry(string sid, bool canModify)
        {
            Sid = sid;
            CanModify = canModify;
        }
    }

    // The update folder (and the install folder above it) must be writable only by
    // administrators / SYSTEM / TrustedInstaller; otherwise a non-elevated process could swap
    // the staged exe between verification and use. The updater refuses to work in a folder whose
    // ACL says otherwise (someone ran icacls on it, or it was created somewhere unusual).
    public static class StagingAcl
    {
        public const string LocalSystem = "S-1-5-18";
        public const string Administrators = "S-1-5-32-544";
        public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        public const string CreatorOwner = "S-1-3-0";

        // FileSystemRights bits that let a principal put, replace or remove files, or change who may:
        // WriteData/AddFile 0x2, AppendData/AddSubdirectory 0x4, WriteEA 0x10, DeleteChild 0x40,
        // WriteAttributes 0x100, Delete 0x10000, WriteDac 0x40000, WriteOwner 0x80000, and the GENERIC
        // bits that an ACE can still carry unmapped: GENERIC_ALL 0x10000000, GENERIC_WRITE 0x40000000.
        public const uint ModifyMask = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

        public static bool AllowsModify(int rights)
        {
            return ((uint)rights & ModifyMask) != 0;
        }

        private static bool Trusted(string sid)
        {
            return string.Equals(sid, LocalSystem, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, Administrators, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, TrustedInstaller, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, CreatorOwner, StringComparison.OrdinalIgnoreCase);
        }

        // null = safe; otherwise a pt-BR reason. Deny entries are ignored (only allows count),
        // which is the conservative reading.
        public static string Check(string ownerSid, IList<AclEntry> allowEntries)
        {
            if (string.IsNullOrEmpty(ownerSid) || !Trusted(ownerSid) || ownerSid == CreatorOwner)
            {
                return "o dono da pasta não é o administrador";
            }
            if (allowEntries == null) return "lista de permissões ilegível";
            for (int i = 0; i < allowEntries.Count; i++)
            {
                AclEntry e = allowEntries[i];
                if (e == null || !e.CanModify) continue;
                if (!Trusted(e.Sid)) return "a pasta aceita escrita de " + e.Sid;
            }
            return null;
        }
    }
}
