using System;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public static class RestorePointTests
    {
        [Test]
        public static void BuildDescription_PrefixesProductName()
        {
            Assert.Equal("SenseMonitor: desativar item de inicialização", RestorePoint.BuildDescription("desativar item de inicialização"));
        }

        [Test]
        public static void BuildDescription_TruncatesTo64Chars()
        {
            string d = RestorePoint.BuildDescription(new string('x', 200));
            Assert.Equal(64, d.Length);
            Assert.True(d.StartsWith("SenseMonitor: "), "prefix kept");
        }

        [Test]
        public static void BuildDescription_BlankAction_StillValid()
        {
            Assert.Equal("SenseMonitor: manutenção", RestorePoint.BuildDescription("  "));
            Assert.Equal("SenseMonitor: manutenção", RestorePoint.BuildDescription(null));
        }

        [Test]
        public static void Classify_ZeroAndNewPoint_IsCreated()
        {
            RestorePointResult r = RestorePoint.Classify(0, 0, true);
            Assert.Equal(RestorePointStatus.Created, r.Status);
            Assert.True(r.Ok, "ok");
        }

        [Test]
        public static void Classify_ZeroButNoNewPoint_IsFrequencyLimit()
        {
            RestorePointResult r = RestorePoint.Classify(0, 0, false);
            Assert.Equal(RestorePointStatus.FrequencyLimit, r.Status);
            Assert.True(!r.Ok, "not ok");
            Assert.True(r.UserMessage.Contains("24"), "mentions 24 h");
        }

        [Test]
        public static void Classify_ZeroAndUnknownNewPoint_IsCreated()
        {
            // enumeration of existing points can be denied; the call itself reported success
            Assert.Equal(RestorePointStatus.Created, RestorePoint.Classify(0, 0, null).Status);
        }

        [Test]
        public static void Classify_ServiceDisabledCode_IsProtectionDisabled()
        {
            Assert.Equal(RestorePointStatus.ProtectionDisabled, RestorePoint.Classify(1058, 0, null).Status);
            Assert.Equal(RestorePointStatus.ProtectionDisabled, RestorePoint.Classify(1060, 0, null).Status);
        }

        [Test]
        public static void Classify_ExceptionHresultServiceDisabled_IsProtectionDisabled()
        {
            Assert.Equal(RestorePointStatus.ProtectionDisabled, RestorePoint.Classify(-1, unchecked((int)0x80070422), null).Status);
        }

        [Test]
        public static void Classify_AccessDenied()
        {
            Assert.Equal(RestorePointStatus.AccessDenied, RestorePoint.Classify(-1, unchecked((int)0x80070005), null).Status);
            Assert.Equal(RestorePointStatus.AccessDenied, RestorePoint.Classify(-1, unchecked((int)0x80041003), null).Status);
            Assert.Equal(RestorePointStatus.AccessDenied, RestorePoint.Classify(5, 0, null).Status);
        }

        [Test]
        public static void Classify_OtherCode_IsFailedWithCode()
        {
            RestorePointResult r = RestorePoint.Classify(112, 0, null);
            Assert.Equal(RestorePointStatus.Failed, r.Status);
            Assert.Equal(112, r.Code);
            Assert.True(r.UserMessage.Length > 10, "message");
        }

        [Test]
        public static void UserMessages_FollowCopyRules()
        {
            foreach (RestorePointStatus s in Enum.GetValues(typeof(RestorePointStatus)))
            {
                RestorePointResult r = new RestorePointResult(s, 0, null);
                string m = r.UserMessage.ToLowerInvariant();
                Assert.True(m.Length > 0, "message for " + s);
                Assert.True(!m.Contains("please") && !m.Contains("por favor") && !m.Contains("successfully") && !m.Contains("com sucesso"), "copy rule: " + s);
            }
        }

        [Test]
        public static void Create_WithFakeBackend_RecordsInChangeLogOnlyWhenCreated()
        {
            MemoryChangeLogStore store = new MemoryChangeLogStore();
            ChangeLog log = new ChangeLog(store);
            FakeRestoreBackend ok = new FakeRestoreBackend();
            ok.Result = new RestorePointResult(RestorePointStatus.Created, 0, null);
            RestorePoint rp = new RestorePoint(ok, log);
            RestorePointResult r = rp.Create("limpeza de arquivos temporários");
            Assert.Equal(RestorePointStatus.Created, r.Status);
            Assert.Equal("SenseMonitor: limpeza de arquivos temporários", ok.LastDescription);
            Assert.Equal(1, log.List().Count);
            Assert.Equal(ChangeKind.PontoRestauracao, log.List()[0].Kind);
            Assert.True(!log.List()[0].CanUndo, "restore points are not undone from the app");

            FakeRestoreBackend lim = new FakeRestoreBackend();
            lim.Result = new RestorePointResult(RestorePointStatus.FrequencyLimit, 0, null);
            RestorePoint rp2 = new RestorePoint(lim, log);
            Assert.Equal(RestorePointStatus.FrequencyLimit, rp2.Create("x").Status);
            Assert.Equal(1, log.List().Count);
        }

        [Test]
        public static void Create_BackendThrows_ReturnsFailedNeverThrows()
        {
            FakeRestoreBackend bad = new FakeRestoreBackend();
            bad.Throw = new InvalidOperationException("boom");
            RestorePoint rp = new RestorePoint(bad, null);
            RestorePointResult r = rp.Create("x");
            Assert.Equal(RestorePointStatus.Failed, r.Status);
        }
    }

    public class FakeRestoreBackend : IRestorePointBackend
    {
        public RestorePointResult Result;
        public string LastDescription;
        public Exception Throw;

        public RestorePointResult CreateRestorePoint(string description)
        {
            LastDescription = description;
            if (Throw != null) throw Throw;
            return Result;
        }
    }
}
