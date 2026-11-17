using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // One rule of a folder's DACL, reduced to what the admin-only check needs.
    public sealed class AdminAclRule
    {
        public string Sid;
        public bool Allow;
        public uint Rights;

        public AdminAclRule(string sid, bool allow, uint rights)
        {
            Sid = sid;
            Allow = allow;
            Rights = rights;
        }
    }

    // The verdict for a folder that must be administrators-only (ProgramData\SenseMonitor and below). Stricter
    // than StagingAcl (which judges the Program Files tree): a standard user can pre-create a folder under
    // ProgramData and stay its OWNER, and an owner can always rewrite the DACL, so the OWNER is checked, and
    // the DACL must be exactly SYSTEM and Administrators with full control, plus (at most) read-only for Users.
    // Anything else (a deny entry, CREATOR OWNER, Authenticated Users, a user's own SID, TrustedInstaller)
    // makes the folder untrustworthy: it is renamed aside untouched and replaced, never used as it is.
    public static class AdminFolderRules
    {
        public const string System = "S-1-5-18";
        public const string Administrators = "S-1-5-32-544";
        public const string Users = "S-1-5-32-545";
        // FILE_ALL_ACCESS (0x1F01FF) or the unmapped GENERIC_ALL bit
        public const uint FullControl = 0x1F01FF;
        public const uint GenericAll = 0x10000000;

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFull(uint rights)
        {
            return (rights & FullControl) == FullControl || (rights & GenericAll) != 0;
        }

        // null = fine; otherwise a pt-BR reason.
        public static string Check(string ownerSid, IList<AdminAclRule> rules)
        {
            if (string.IsNullOrEmpty(ownerSid) || !(Same(ownerSid, Administrators) || Same(ownerSid, System)))
            {
                return "o dono da pasta não é Administradores nem o sistema";
            }
            if (rules == null) return "lista de permissões ilegível";
            bool systemFull = false, adminsFull = false;
            for (int i = 0; i < rules.Count; i++)
            {
                AdminAclRule r = rules[i];
                if (r == null) return "regra de permissão ilegível";
                if (!r.Allow) return "a pasta tem uma regra de negação inesperada";
                if (Same(r.Sid, System)) { if (IsFull(r.Rights)) systemFull = true; continue; }
                if (Same(r.Sid, Administrators)) { if (IsFull(r.Rights)) adminsFull = true; continue; }
                if (Same(r.Sid, Users))
                {
                    if (StagingAcl.AllowsModify((int)r.Rights)) return "a pasta aceita escrita de Usuários";
                    continue; // read-only for Users is the most that is allowed
                }
                return "a pasta tem uma regra para outra conta (" + r.Sid + ")";
            }
            if (!systemFull || !adminsFull) return "a pasta não dá controle total a Administradores e ao sistema";
            return null;
        }
    }
}
