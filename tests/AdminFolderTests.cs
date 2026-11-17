using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class AdminFolderRulesTests
    {
        private const string Admins = "S-1-5-32-544";
        private const string Sys = "S-1-5-18";
        private const string Users = "S-1-5-32-545";
        private const string AuthUsers = "S-1-5-11";
        private const string Me = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        private const uint Full = 0x1F01FF;
        private const uint ReadExec = 0x1200A9;

        private static List<AdminAclRule> Good(bool users)
        {
            List<AdminAclRule> l = new List<AdminAclRule>();
            l.Add(new AdminAclRule(Sys, true, Full));
            l.Add(new AdminAclRule(Admins, true, Full));
            if (users) l.Add(new AdminAclRule(Users, true, ReadExec));
            return l;
        }

        [Test]
        public static void AdminsAndSystemFull_WithOrWithoutUsersRead_IsFine()
        {
            Assert.Null(AdminFolderRules.Check(Admins, Good(false)));
            Assert.Null(AdminFolderRules.Check(Admins, Good(true)));
            Assert.Null(AdminFolderRules.Check(Sys, Good(true)));
            Assert.Null(AdminFolderRules.Check("s-1-5-32-544", Good(false)));
        }

        [Test]
        public static void AFolderOwnedByAStandardUser_IsRefused_EvenWithAPerfectDacl()
        {
            // a user pre-creates the folder and stays its owner, then locks it "properly"
            Assert.True(AdminFolderRules.Check(Me, Good(false)) != null, "user owner");
            Assert.True(AdminFolderRules.Check(Users, Good(false)) != null, "Users group as owner");
            Assert.True(AdminFolderRules.Check(AuthUsers, Good(false)) != null, "Authenticated Users as owner");
            Assert.True(AdminFolderRules.Check(null, Good(false)) != null, "no owner");
            Assert.True(AdminFolderRules.Check("S-1-3-0", Good(false)) != null, "CREATOR OWNER is not an owner");
            Assert.True(AdminFolderRules.Check("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", Good(false)) != null, "TrustedInstaller is not what we create");
        }

        [Test]
        public static void AnyOtherPrincipalInTheDacl_IsRefused()
        {
            string[] sids = { Me, AuthUsers, "S-1-1-0", "S-1-3-0", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
            foreach (string sid in sids)
            {
                List<AdminAclRule> l = Good(false);
                l.Add(new AdminAclRule(sid, true, ReadExec)); // even read-only: only Users may read
                Assert.True(AdminFolderRules.Check(Admins, l) != null, "refused: " + sid);
            }
        }

        [Test]
        public static void UsersCanReadButNeverWrite()
        {
            uint[] writes = { 0x2, 0x4, 0x40, 0x10000, 0x40000, 0x40000000, 0x10000000, Full };
            foreach (uint w in writes)
            {
                List<AdminAclRule> l = Good(false);
                l.Add(new AdminAclRule(Users, true, ReadExec | w));
                Assert.True(AdminFolderRules.Check(Admins, l) != null, "Users with 0x" + w.ToString("x"));
            }
        }

        [Test]
        public static void ADenyEntryIsUnexpected()
        {
            List<AdminAclRule> l = Good(false);
            l.Add(new AdminAclRule(Me, false, Full));
            Assert.True(AdminFolderRules.Check(Admins, l) != null, "deny");
        }

        [Test]
        public static void AdminsAndSystemMustBothHaveFullControl()
        {
            List<AdminAclRule> onlyAdmins = new List<AdminAclRule>();
            onlyAdmins.Add(new AdminAclRule(Admins, true, Full));
            Assert.True(AdminFolderRules.Check(Admins, onlyAdmins) != null, "no SYSTEM");
            List<AdminAclRule> partial = new List<AdminAclRule>();
            partial.Add(new AdminAclRule(Sys, true, Full));
            partial.Add(new AdminAclRule(Admins, true, ReadExec));
            Assert.True(AdminFolderRules.Check(Admins, partial) != null, "Administrators without full control");
            Assert.True(AdminFolderRules.Check(Admins, new List<AdminAclRule>()) != null, "empty DACL");
            Assert.True(AdminFolderRules.Check(Admins, null) != null, "unreadable");
        }

        [Test]
        public static void GenericAllCountsAsFull()
        {
            List<AdminAclRule> l = new List<AdminAclRule>();
            l.Add(new AdminAclRule(Sys, true, 0x10000000));
            l.Add(new AdminAclRule(Admins, true, 0x10000000));
            Assert.Null(AdminFolderRules.Check(Admins, l));
        }

        [Test]
        public static void TheDescriptorWeCreate_PassesItsOwnRules_WithAndWithoutUsersRead()
        {
            foreach (bool users in new bool[] { false, true })
            {
                string owner;
                List<AdminAclRule> rules;
                Assert.Null(AdminFolder.DescribeSddl(AdminFolder.Sddl(users), out owner, out rules));
                Assert.Equal(Admins, owner);
                Assert.Equal(users ? 3 : 2, rules.Count);
                Assert.Null(AdminFolderRules.Check(owner, rules));
            }
        }

        [Test]
        public static void TheDescriptor_IsProtected_SoNothingComesFromProgramData()
        {
            RawSecurityDescriptor sd = new RawSecurityDescriptor(AdminFolder.Sddl(true));
            Assert.True((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "P flag");
            Assert.True(AdminFolder.Sddl(false).StartsWith("O:BAD:P"), "owner Administrators, protected DACL");
            Assert.True(AdminFolder.Sddl(false).IndexOf("BU", StringComparison.Ordinal) < 0, "no Users in the data folder");
        }

        [Test]
        public static void ParseDescriptor_ReadsOwnerAndRules_AndFlagsAMissingDacl()
        {
            string owner;
            List<AdminAclRule> rules;
            RawSecurityDescriptor sd = new RawSecurityDescriptor("O:BUG:BUD:(A;OICI;FA;;;WD)(D;;FA;;;AU)");
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            Assert.Null(AdminFolder.ParseDescriptor(bytes, out owner, out rules));
            Assert.Equal(Users, owner);
            Assert.Equal(2, rules.Count);
            Assert.True(rules[0].Allow && rules[0].Sid == "S-1-1-0", "allow Everyone");
            Assert.True(!rules[1].Allow, "deny entry seen as deny");
            Assert.True(AdminFolderRules.Check(owner, rules) != null, "hostile descriptor refused");
            // a NULL DACL gives everybody full access: never acceptable
            RawSecurityDescriptor nul = new RawSecurityDescriptor("O:BAG:BA");
            byte[] nb = new byte[nul.BinaryLength];
            nul.GetBinaryForm(nb, 0);
            Assert.True(AdminFolder.ParseDescriptor(nb, out owner, out rules) != null, "no DACL is a problem");
            Assert.True(AdminFolder.ParseDescriptor(new byte[] { 1, 2, 3 }, out owner, out rules) != null, "garbage");
        }
    }

    public static class AdminFolderAsideTests
    {
        // Tests run without administrator rights, so a fresh folder cannot be given the Administrators owner:
        // this descriptor gives the current user ownership instead, which VerifyHandle (correctly) rejects. That is
        // enough to exercise everything BEFORE the verification: look, rename aside, create fresh, bounded retries.
        private const string UserOwnedSddl = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;OW)";

        private static string[] Asides(string parent, string name)
        {
            List<string> l = new List<string>();
            foreach (string e in Directory.GetFileSystemEntries(parent))
            {
                if (Path.GetFileName(e).StartsWith(name + ".recusado-", StringComparison.OrdinalIgnoreCase)) l.Add(e);
            }
            return l.ToArray();
        }

        private static string Sddl(string dir)
        {
            return new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        }

        [Test]
        public static void AsideName_IsInTheSameParent_WithTheTicks()
        {
            Assert.Equal(@"C:\ProgramData\SenseMonitor.recusado-123", AdminFolderNames.AsideName(@"C:\ProgramData\SenseMonitor", 123));
            Assert.Equal(@"C:\ProgramData\SenseMonitor.recusado-9", AdminFolderNames.AsideName(@"C:\ProgramData\SenseMonitor\", 9));
            Assert.True(AdminFolderNames.IsAsideName("SenseMonitor.recusado-123"), "recognized");
            Assert.True(!AdminFolderNames.IsAsideName("SenseMonitor"), "plain name");
        }

        [Test]
        public static void AHostileExistingFolder_IsRenamedAside_NotEmptied_NotTouched()
        {
            using (TempFolder t = new TempFolder())
            {
                string path = t.Sub("SenseMonitor");
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "planted.txt"), "attacker data");
                Directory.CreateDirectory(Path.Combine(path, "sub"));
                File.WriteAllText(Path.Combine(Path.Combine(path, "sub"), "deep.txt"), "x");
                string before = Sddl(path);
                string reason, note;
                string result = AdminFolder.Ensure(path, false, true, UserOwnedSddl, out reason, out note);
                Assert.Null(result);                                   // the user-owned replacement is not acceptable either
                string[] asides = Asides(t.Path, "SenseMonitor");
                Assert.True(asides.Length >= 1, "something was set aside");
                // the ORIGINAL folder is among the asides with everything still in it and its security unchanged
                bool found = false;
                foreach (string a in asides)
                {
                    if (File.Exists(Path.Combine(a, "planted.txt")) && File.Exists(Path.Combine(Path.Combine(a, "sub"), "deep.txt")))
                    {
                        found = true;
                        Assert.Equal(before, Sddl(a));
                    }
                }
                Assert.True(found, "the hostile folder was moved, whole and untouched");
                Assert.True(note != null && note.Contains("recusado"), "noted for the journal");
            }
        }

        [Test]
        public static void AJunctionAtTheName_IsRenamedAsALink_AndItsTargetIsNeverTouched()
        {
            using (TempFolder t = new TempFolder())
            using (TempFolder target = new TempFolder())
            {
                File.WriteAllText(Path.Combine(target.Path, "precious.txt"), "do not touch");
                string targetSddl = Sddl(target.Path);
                int code;
                string outp;
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                string path = t.Sub("SenseMonitor");
                Assert.True(ToolRunner.RunCapture(cmd, "/d /c mklink /J \"" + path + "\" \"" + target.Path + "\"", 15000, out code, out outp) && code == 0, "junction: " + outp);
                string reason, note;
                AdminFolder.Ensure(path, false, true, UserOwnedSddl, out reason, out note);
                // R0: the target of the junction had neither its owner nor its DACL changed, and nothing was deleted
                Assert.Equal(targetSddl, Sddl(target.Path));
                Assert.True(File.Exists(Path.Combine(target.Path, "precious.txt")), "target content intact");
                bool linkFound = false;
                foreach (string a in Asides(t.Path, "SenseMonitor"))
                {
                    if ((File.GetAttributes(a) & FileAttributes.ReparsePoint) != 0) linkFound = true;
                }
                Assert.True(linkFound, "the link itself was renamed (not its target)");
            }
        }

        [Test]
        public static void IfTheRenameFails_TheFolderIsRefused_NothingIsDone()
        {
            using (TempFolder t = new TempFolder())
            {
                string path = t.Sub("SenseMonitor");
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "keep.txt"), "x");
                // an open handle on the folder itself without delete sharing (an attacker's pre-opened handle) blocks the rename
                using (FileStream hold = new FileStream(Path.Combine(path, "keep.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    string reason, note;
                    Assert.Null(AdminFolder.Ensure(path, false, true, UserOwnedSddl, out reason, out note));
                    Assert.True(reason != null, "a reason is given");
                    Assert.True(File.Exists(Path.Combine(path, "keep.txt")) || Asides(t.Path, "SenseMonitor").Length > 0, "nothing was deleted");
                }
            }
        }

        [Test]
        public static void VerifyOnly_NeverRenamesCreatesOrChangesAnything()
        {
            using (TempFolder t = new TempFolder())
            {
                string path = t.Sub("SenseMonitor");
                Directory.CreateDirectory(path);
                string before = Sddl(path);
                string reason, note;
                Assert.Null(AdminFolder.Ensure(path, false, false, UserOwnedSddl, out reason, out note));
                Assert.True(reason.Contains("insegura"), reason);
                Assert.True(Directory.Exists(path) && Asides(t.Path, "SenseMonitor").Length == 0, "left where it was");
                Assert.Equal(before, Sddl(path));
                string missing = t.Sub("novo");
                Assert.Null(AdminFolder.Ensure(missing, false, false, UserOwnedSddl, out reason, out note));
                Assert.True(!Directory.Exists(missing), "verify-only does not create");
            }
        }

        [Test]
        public static void VerifyOnly_OfALink_RefusesWithoutFollowingOrTouching()
        {
            using (TempFolder t = new TempFolder())
            using (TempFolder target = new TempFolder())
            {
                File.WriteAllText(Path.Combine(target.Path, "keep.txt"), "x");
                string targetSddl = Sddl(target.Path);
                int code;
                string outp;
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                string link = t.Sub("SenseMonitor");
                Assert.True(ToolRunner.RunCapture(cmd, "/d /c mklink /J \"" + link + "\" \"" + target.Path + "\"", 15000, out code, out outp) && code == 0, "junction");
                string reason, note;
                Assert.Null(AdminFolder.Ensure(link, false, false, out reason, out note));
                Assert.True(reason.Contains("link"), reason);
                Assert.Equal(targetSddl, Sddl(target.Path));
                Assert.True(Directory.Exists(link), "link left as it was when only verifying");
            }
        }

        [Test]
        public static void Problem_ByHandle_FlagsAUserOwnedFolder_AMissingOne_AndAFile()
        {
            using (TempFolder t = new TempFolder())
            {
                string p = AdminFolder.Problem(t.Path);
                Assert.True(p != null && p.Contains("dono"), "a standard user's own folder is never trusted; the OWNER was read through the handle: " + p);
                Assert.True(AdminFolder.Problem(t.Sub("missing")) != null, "missing");
                File.WriteAllText(t.Sub("f.txt"), "x");
                Assert.True(AdminFolder.Problem(t.Sub("f.txt")) != null, "a file is not a folder");
            }
        }

        [Test]
        public static void AHeldHandle_BlocksRenameAndReplacement_ButNotWorkInsideTheFolder()
        {
            using (TempFolder t = new TempFolder())
            {
                string d = t.Sub("held");
                Directory.CreateDirectory(d);
                using (Microsoft.Win32.SafeHandles.SafeFileHandle h = AdminFolder.OpenHeldForTests(d))
                {
                    Assert.True(h != null && !h.IsInvalid, "opened");
                    File.WriteAllText(Path.Combine(d, "inside.txt"), "x");           // creating inside still works
                    Assert.Equal(1, Directory.GetFiles(d).Length);                    // listing too
                    Assert.Throws<IOException>(delegate { Directory.Move(d, d + ".recusado-1"); }); // but the folder cannot be moved away
                    Assert.True(Directory.Exists(d), "still there");
                }
                Directory.Move(d, d + ".moved");                                      // free once released
                Assert.True(Directory.Exists(d + ".moved"), "moved after the handle was released");
            }
        }

        [Test]
        public static void ThereIsNoWalkNorPrivilegeCodeAnyMore()
        {
            // R0/R4: nothing in AdminFolder lists, locks or deletes inside a folder it did not just create, and no
            // special privilege is enabled
            foreach (System.Reflection.MethodInfo m in typeof(AdminFolder).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                Assert.True(m.Name != "DiscardContents" && m.Name != "Lock" && m.Name != "EnablePrivileges", "removed: " + m.Name);
            }
        }
    }

    public static class CompanionRound3Tests
    {
        [Test]
        public static void CultureNamedFolders_AreCompanions()
        {
            List<string> f = FirstStage.FindCompanions(new string[] { "pt-BR", "pt", "en", "Fotos", "Documentos", "SenseMonitor.exe", "EN-us" });
            Assert.True(f.Contains("pt-BR") && f.Contains("pt") && f.Contains("en") && f.Contains("EN-us"), "culture folders (satellite assembly probing)");
            Assert.True(!f.Contains("Fotos") && !f.Contains("Documentos") && !f.Contains("SenseMonitor.exe"), "ordinary names are fine");
        }

        [Test]
        public static void Respawn_FromTheInstalledCopy_IsAlwaysAllowed()
        {
            Assert.True(FirstStage.RespawnAllowed(true, new string[] { "SenseMonitor.exe.config", "x.dll" }, "SenseMonitor.exe.config", false), "installed");
        }

        [Test]
        public static void Respawn_FromElsewhere_FollowsTheSameRule_WithOneAllowanceForTheOwnEmbeddedConfig()
        {
            string[] dev = { "SenseMonitor.exe", "SenseMonitor.exe.config", "ForjaTests.exe" };
            Assert.True(FirstStage.RespawnAllowed(false, dev, "SenseMonitor.exe.config", true), "bin\\: own config equal to the embedded one");
            Assert.True(!FirstStage.RespawnAllowed(false, dev, "SenseMonitor.exe.config", false), "a config that differs from the embedded copy");
            Assert.True(!FirstStage.RespawnAllowed(false, new string[] { "SenseMonitor.exe.config", "version.dll" }, "SenseMonitor.exe.config", true), "a DLL is never allowed");
            Assert.True(!FirstStage.RespawnAllowed(false, new string[] { "SenseMonitor.exe.manifest" }, "SenseMonitor.exe.config", true), "a manifest is never allowed");
            Assert.True(!FirstStage.RespawnAllowed(false, new string[] { "Other.exe.config" }, "SenseMonitor.exe.config", true), "only THIS exe's config, and only when it matches");
            Assert.True(FirstStage.RespawnAllowed(false, new string[] { "SenseMonitor.exe", "notes.txt" }, "SenseMonitor.exe.config", false), "a clean folder");
        }
    }
}
