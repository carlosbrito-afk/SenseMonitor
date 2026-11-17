using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class StagingAclTests
    {
        private const string Users = "S-1-5-32-545";
        private const string Everyone = "S-1-1-0";
        private const string AuthUsers = "S-1-5-11";
        private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        private static List<AclEntry> Default()
        {
            List<AclEntry> l = new List<AclEntry>();
            l.Add(new AclEntry(StagingAcl.TrustedInstaller, true));
            l.Add(new AclEntry(StagingAcl.LocalSystem, true));
            l.Add(new AclEntry(StagingAcl.Administrators, true));
            l.Add(new AclEntry(Users, false));            // read & execute only
            l.Add(new AclEntry(StagingAcl.CreatorOwner, true));
            return l;
        }

        [Test]
        public static void ProgramFilesDefault_IsSafe()
        {
            Assert.Null(StagingAcl.Check(StagingAcl.Administrators, Default()));
            Assert.Null(StagingAcl.Check(StagingAcl.TrustedInstaller, Default()));
            Assert.Null(StagingAcl.Check(StagingAcl.LocalSystem, Default()));
        }

        [Test]
        public static void UsersOrEveryoneCanModify_IsUnsafe()
        {
            string[] who = { Users, Everyone, AuthUsers, SomeUser };
            foreach (string sid in who)
            {
                List<AclEntry> l = Default();
                l.Add(new AclEntry(sid, true));
                Assert.True(StagingAcl.Check(StagingAcl.Administrators, l) != null, "unsafe for " + sid);
            }
        }

        [Test]
        public static void ReadOnlyEntriesForAnyone_AreFine()
        {
            List<AclEntry> l = Default();
            l.Add(new AclEntry(Everyone, false));
            l.Add(new AclEntry(SomeUser, false));
            Assert.Null(StagingAcl.Check(StagingAcl.Administrators, l));
        }

        [Test]
        public static void OwnerThatIsNotAdministrator_IsUnsafe()
        {
            Assert.True(StagingAcl.Check(SomeUser, Default()) != null, "user owner");
            Assert.True(StagingAcl.Check(Users, Default()) != null, "users group owner");
            Assert.True(StagingAcl.Check(null, Default()) != null, "no owner");
            Assert.True(StagingAcl.Check("", Default()) != null, "empty owner");
            Assert.True(StagingAcl.Check(StagingAcl.CreatorOwner, Default()) != null, "creator owner is not a real owner");
        }

        [Test]
        public static void UnreadableList_IsUnsafe()
        {
            Assert.True(StagingAcl.Check(StagingAcl.Administrators, null) != null, "null list");
        }

        [Test]
        public static void SidComparison_IgnoresCase()
        {
            List<AclEntry> l = new List<AclEntry>();
            l.Add(new AclEntry("s-1-5-32-544", true));
            Assert.Null(StagingAcl.Check("s-1-5-18", l));
        }
    }
}
