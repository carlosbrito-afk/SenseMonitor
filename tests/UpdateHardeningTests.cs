using System;
using System.Collections.Generic;
using System.Text;
using Forja.Core;

namespace Forja.Tests
{
    public static class ProcessKindsTests
    {
        private const string Exe = "\"C:\\Program Files\\SenseMonitor\\SenseMonitor.exe\"";

        [Test]
        public static void PlainStart_IsTheApp()
        {
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify(Exe));
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify(Exe + " --autoteste --auto"));
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify("C:\\x\\SenseMonitor.exe"));
        }

        [Test]
        public static void SentinelAndItsLauncher_AreSentinels()
        {
            Assert.Equal(AppProcessKind.Sentinel, ProcessKinds.Classify(Exe + " --sentinela"));
            Assert.Equal(AppProcessKind.Sentinel, ProcessKinds.Classify(Exe + " --SENTINELA"));
            Assert.Equal(AppProcessKind.Sentinel, ProcessKinds.Classify(Exe + " --sentinela-iniciar"));
            Assert.Equal(AppProcessKind.Sentinel, ProcessKinds.Classify("C:\\x\\SenseMonitor.exe --sentinela"));
        }

        [Test]
        public static void Helper_IsRecognized()
        {
            Assert.Equal(AppProcessKind.Helper, ProcessKinds.Classify("\"C:\\a b\\SenseMonitor.aplicador.exe\" --aplicar-atualizacao"));
        }

        [Test]
        public static void ASwitchInsideAQuotedPath_IsNotASwitch()
        {
            // a folder literally named "--sentinela" must not make the app look like the sentinel
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify("\"C:\\tmp --sentinela\\SenseMonitor.exe\""));
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify("C:\\x\\SenseMonitor.exe \"--sentinela-not\""));
        }

        [Test]
        public static void UnreadableCommandLine_CountsAsTheApp_SoItIsWaitedFor()
        {
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify(null));
            Assert.Equal(AppProcessKind.App, ProcessKinds.Classify(""));
        }

        [Test]
        public static void Tokens_HandleQuotesAndTabs()
        {
            List<string> t = ProcessKinds.Tokens("\"C:\\a b\\x.exe\"  --one\t\"two words\" three");
            Assert.Equal(4, t.Count);
            Assert.Equal("C:\\a b\\x.exe", t[0]);
            Assert.Equal("--one", t[1]);
            Assert.Equal("two words", t[2]);
            Assert.Equal("three", t[3]);
        }
    }

    public static class FirstStageTests
    {
        private static StartupRoute R(StartupAction a)
        {
            return new StartupRoute(a);
        }

        private static readonly List<string> None = new List<string>();

        [Test]
        public static void FindCompanions_ConfigDllManifestLocal_FilesAndFolders()
        {
            List<string> f = FirstStage.FindCompanions(new string[] {
                "SenseMonitor.exe", "SenseMonitor.exe.config", "VERSION.DLL", "SenseMonitor.exe.manifest", "SenseMonitor.exe.local",
                "notes.txt", "photo.jpg", "x.dll.bak", "my.configuration", "Setup.CONFIG" });
            Assert.Equal(5, f.Count);
            Assert.True(f.Contains("SenseMonitor.exe.config") && f.Contains("VERSION.DLL") && f.Contains("SenseMonitor.exe.manifest")
                && f.Contains("SenseMonitor.exe.local") && f.Contains("Setup.CONFIG"), "the five offenders");
        }

        [Test]
        public static void FindCompanions_CleanFolder_FindsNothing()
        {
            Assert.Equal(0, FirstStage.FindCompanions(new string[] { "SenseMonitor.exe", "notes.txt" }).Count);
            Assert.Equal(0, FirstStage.FindCompanions(null).Count);
            Assert.Equal(0, FirstStage.FindCompanions(new string[] { null, "" }).Count);
        }

        [Test]
        public static void NotElevated_AsksForElevationOnlyAfterTheCheck()
        {
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.Install), false, false, None));
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.Uninstall), false, true, None));
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.RunApp), false, true, None));
            Assert.Equal(FirstStageAction.ShowInstaller, FirstStage.Decide(R(StartupAction.OfferInstall), false, false, None));
        }

        [Test]
        public static void CompanionFiles_RefuseBeforeAnyElevation_ForInstallAndOffer()
        {
            List<string> bad = new List<string>(new string[] { "SenseMonitor.exe.config" });
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(R(StartupAction.Install), false, false, bad));
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(R(StartupAction.OfferInstall), false, false, bad));
        }

        [Test]
        public static void CompanionFiles_AlsoRefusedWhenAlreadyElevated_AFileCanBePlantedAfterTheUacQuestion()
        {
            List<string> bad = new List<string>(new string[] { "version.dll" });
            Assert.Equal(FirstStageAction.RefuseCompanions, FirstStage.Decide(R(StartupAction.Install), true, false, bad));
        }

        [Test]
        public static void InstalledCopy_HasItsOwnConfigAndThatIsFine()
        {
            List<string> own = new List<string>(new string[] { "SenseMonitor.exe.config" });
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.Install), true, true, own));
            Assert.Equal(FirstStageAction.Elevate, FirstStage.Decide(R(StartupAction.RunApp), false, true, own));
        }

        [Test]
        public static void OtherLaunches_OfAnAlreadyElevatedProcess_AreNotBlocked()
        {
            List<string> bad = new List<string>(new string[] { "x.dll" });
            // (a non-elevated launch from such a folder is refused now: see ElevationGuardTests)
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.RunApp), true, false, bad));
        }

        [Test]
        public static void Elevated_Proceeds_AndBadArgumentsOrHelperNeverRelaunch()
        {
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.Install), true, false, None));
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.OfferInstall), true, false, None));
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.BadArguments), false, false, None));
            Assert.Equal(FirstStageAction.Proceed, FirstStage.Decide(R(StartupAction.ApplyUpdate), false, true, None));
        }
    }

    public static class ApplyInstructionsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        private static readonly string Nonce = "0123456789abcdef0123456789abcdef";

        private static string Err(byte[] json, DateTime now)
        {
            ApplyInstructions i;
            string err;
            if (ApplyInstructionsFile.TryParse(json, now, out i, out err))
            {
                Assert.True(i != null && err == null, "ok gives an object");
                return null;
            }
            Assert.Null(i);
            Assert.True(err != null, "error text");
            return err;
        }

        [Test]
        public static void RoundTrip()
        {
            byte[] b = ApplyInstructionsFile.Build(4321, "0.2.1", Now, Nonce);
            ApplyInstructions i;
            string err;
            Assert.True(ApplyInstructionsFile.TryParse(b, Now.AddSeconds(30), out i, out err), err);
            Assert.Equal(4321, i.Pid);
            Assert.Equal("0.2.1", i.Versao);
            Assert.Equal(Nonce, i.Nonce);
        }

        [Test]
        public static void TooOld_OrFromTheFuture_IsRefused()
        {
            byte[] b = ApplyInstructionsFile.Build(1, "0.2.1", Now, Nonce);
            Assert.True(Err(b, Now.AddMinutes(11)) != null, "11 min old");
            Assert.Null(Err(b, Now.AddMinutes(9)));
            Assert.True(Err(b, Now.AddMinutes(-5)) != null, "written in the future");
        }

        [Test]
        public static void BadFields_AreRefused()
        {
            Assert.True(Err(ApplyInstructionsFile.Build(0, "0.2.1", Now, Nonce), Now) != null, "pid 0");
            Assert.True(Err(ApplyInstructionsFile.Build(-4, "0.2.1", Now, Nonce), Now) != null, "negative pid");
            Assert.True(Err(ApplyInstructionsFile.Build(5, "0.2", Now, Nonce), Now) != null, "version");
            Assert.True(Err(ApplyInstructionsFile.Build(5, "0.2.1", Now, "short"), Now) != null, "nonce");
            Assert.True(Err(ApplyInstructionsFile.Build(5, "0.2.1", Now, Nonce.ToUpperInvariant()), Now) != null, "nonce must be lower-case hex");
            Assert.True(Err(Encoding.UTF8.GetBytes("{\"pid\":5,\"versao\":\"0.2.1\",\"criadoUtc\":\"ontem\",\"nonce\":\"" + Nonce + "\"}"), Now) != null, "date");
        }

        [Test]
        public static void GarbageAndHuge_AreRefused()
        {
            Assert.True(Err(null, Now) != null, "null");
            Assert.True(Err(new byte[0], Now) != null, "empty");
            Assert.True(Err(Encoding.UTF8.GetBytes("not json"), Now) != null, "garbage");
            Assert.True(Err(Encoding.UTF8.GetBytes("null"), Now) != null, "null json");
            Assert.True(Err(new byte[5000], Now) != null, "huge");
        }

        [Test]
        public static void Nonce_IsRandom32Hex()
        {
            string a = ApplyInstructionsFile.NewNonce(), b = ApplyInstructionsFile.NewNonce();
            Assert.Equal(32, a.Length);
            Assert.True(a != b, "two nonces differ");
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(a, "^[0-9a-f]{32}$"), "lower hex");
        }
    }

    public static class KeyVaultTests
    {
        private const int Fast = 1000; // injected floor + count: keeps the suite fast
        private const string Pass = "correct horse battery staple";
        private const string Key = "<RSAKeyValue><Modulus>AAAA</Modulus><Exponent>AQAB</Exponent><P>pp</P><Q>qq</Q><" + "D>dd</" + "D></RSAKeyValue>";

        [Test]
        public static void ProductionConstants_AreUnchanged()
        {
            Assert.Equal(600000, KeyVault.DefaultIterations);
            Assert.Equal(200000, KeyVault.MinIterations);
        }

        [Test]
        public static void FileWrittenWithNIterations_RoundTrips_AndStoresN()
        {
            byte[] file = KeyVault.Protect(Key, Pass, 1234, Fast);
            Assert.Equal(1234, (file[4] << 24) | (file[5] << 16) | (file[6] << 8) | file[7]);
            string err;
            Assert.Equal(Key, KeyVault.Unprotect(file, Pass, out err, Fast));
            // the public (production) reader refuses it: below the real minimum
            Assert.Null(KeyVault.Unprotect(file, Pass, out err));
        }

        [Test]
        public static void ProtectUnprotect_RoundTrips()
        {
            byte[] file = KeyVault.Protect(Key, Pass, Fast, Fast);
            string err;
            Assert.Equal(Key, KeyVault.Unprotect(file, Pass, out err, Fast));
            Assert.Null(err);
        }

        [Test]
        public static void TheFileHoldsNoPlaintext_AndEachProtectionDiffers()
        {
            byte[] a = KeyVault.Protect(Key, Pass, Fast, Fast);
            byte[] b = KeyVault.Protect(Key, Pass, Fast, Fast);
            string text = Encoding.UTF8.GetString(a);
            Assert.True(text.IndexOf("RSAKeyValue", StringComparison.Ordinal) < 0 && text.IndexOf("Modulus", StringComparison.Ordinal) < 0, "no plaintext markers");
            Assert.True(Convert.ToBase64String(a) != Convert.ToBase64String(b), "random salt and iv");
            Assert.Equal("SMK1", Encoding.ASCII.GetString(a, 0, 4));
        }

        [Test]
        public static void WrongPassphrase_IsRefused()
        {
            byte[] file = KeyVault.Protect(Key, Pass, Fast, Fast);
            string err;
            Assert.Null(KeyVault.Unprotect(file, "another long passphrase", out err, Fast));
            Assert.True(err != null, "error");
            Assert.Null(KeyVault.Unprotect(file, "", out err, Fast));
            Assert.Null(KeyVault.Unprotect(file, null, out err, Fast));
        }

        [Test]
        public static void AnyAlteredByte_IsRefused()
        {
            byte[] file = KeyVault.Protect(Key, Pass, Fast, Fast);
            int[] spots = { 9, 24, 40, file.Length - 40, file.Length - 1 };
            foreach (int at in spots)
            {
                byte[] t = (byte[])file.Clone();
                t[at] ^= 0x01;
                string err;
                Assert.Null(KeyVault.Unprotect(t, Pass, out err, Fast));
            }
        }

        [Test]
        public static void TruncatedOrForeignFile_IsRefused()
        {
            byte[] file = KeyVault.Protect(Key, Pass, Fast, Fast);
            string err;
            byte[] cut = new byte[file.Length - 3];
            Array.Copy(file, cut, cut.Length);
            Assert.Null(KeyVault.Unprotect(cut, Pass, out err, Fast));
            Assert.Null(KeyVault.Unprotect(new byte[10], Pass, out err, Fast));
            Assert.Null(KeyVault.Unprotect(null, Pass, out err, Fast));
            Assert.Null(KeyVault.Unprotect(Encoding.UTF8.GetBytes(Key), Pass, out err, Fast));
        }

        [Test]
        public static void IterationsBelowTheMinimum_AreRefusedBothWays()
        {
            Assert.Throws<ArgumentException>(delegate { KeyVault.Protect(Key, Pass, KeyVault.MinIterations - 1); });
            // a file whose header was rewritten to a cheap iteration count (downgrade) is refused before any work
            byte[] file = KeyVault.Protect(Key, Pass, Fast, Fast);
            file[4] = 0; file[5] = 0; file[6] = 0; file[7] = 1;
            string err;
            Assert.Null(KeyVault.Unprotect(file, Pass, out err, Fast));
            Assert.True(err.Contains("iterações"), err);
        }

        [Test]
        public static void ShortPassphrase_IsRefused()
        {
            Assert.True(KeyVault.PassphraseProblem("short") != null, "short");
            Assert.True(KeyVault.PassphraseProblem(null) != null, "null");
            Assert.Null(KeyVault.PassphraseProblem("twelve chars!"));
            Assert.Throws<ArgumentException>(delegate { KeyVault.Protect(Key, "short", Fast, Fast); });
        }

        [Test]
        public static void RealPrivateKey_SurvivesTheVault_AndStillSigns()
        {
            byte[] file = KeyVault.Protect(TestKeys.PrivA, Pass, Fast, Fast);
            string err;
            string back = KeyVault.Unprotect(file, Pass, out err, Fast);
            byte[] data = Encoding.UTF8.GetBytes("manifest");
            string sig = UpdateSigner.Sign(data, back);
            Assert.Equal(SignatureCheck.Ok, UpdateVerifier.Verify(data, sig, TestKeys.PubA));
        }
    }

    public static class StagingAclMaskTests
    {
        [Test]
        public static void WriteLikeRights_AreModifications_IncludingUnmappedGenericBits()
        {
            int[] rights = { 0x2, 0x4, 0x10, 0x40, 0x100, 0x10000, 0x40000, 0x80000, 0x10000000, 0x40000000, 0x1F01FF };
            foreach (int r in rights) Assert.True(StagingAcl.AllowsModify(r), "0x" + r.ToString("x") + " modifies");
        }

        [Test]
        public static void ReadAndExecute_IsNotAModification()
        {
            // ReadData 0x1, ReadEA 0x8, ExecuteFile 0x20, ReadAttributes 0x80, ReadPermissions 0x20000, Synchronize 0x100000
            int readExec = 0x1 | 0x8 | 0x20 | 0x80 | 0x20000 | 0x100000;
            Assert.True(!StagingAcl.AllowsModify(readExec), "read & execute");
            Assert.True(!StagingAcl.AllowsModify(0x80000000 > int.MaxValue ? unchecked((int)0x80000000) : 0), "GENERIC_READ alone");
            Assert.True(!StagingAcl.AllowsModify(0x20000000), "GENERIC_EXECUTE alone");
        }
    }
}
