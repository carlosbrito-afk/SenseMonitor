using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    internal sealed class FakeNet : IUpdateNet
    {
        public Dictionary<string, byte[]> Bodies = new Dictionary<string, byte[]>();
        public Dictionary<string, FetchStatus> Status = new Dictionary<string, FetchStatus>();
        public List<string> Requests = new List<string>();

        public FetchStatus GetBytes(string url, int maxBytes, out byte[] data, out string error)
        {
            data = null;
            error = null;
            Requests.Add(url);
            FetchStatus st;
            if (Status.TryGetValue(url, out st) && st != FetchStatus.Ok) { error = "fake " + st; return st; }
            byte[] b;
            if (!Bodies.TryGetValue(url, out b)) { error = "fake missing"; return FetchStatus.NotFound; }
            if (b.Length > maxBytes) { error = "too big"; return FetchStatus.Failed; }
            data = b;
            return FetchStatus.Ok;
        }

        public FetchStatus GetFile(string url, long maxBytes, string destPath, out string error)
        {
            error = null;
            Requests.Add(url);
            FetchStatus st;
            if (Status.TryGetValue(url, out st) && st != FetchStatus.Ok) { error = "fake " + st; return st; }
            byte[] b;
            if (!Bodies.TryGetValue(url, out b)) { error = "fake missing"; return FetchStatus.NotFound; }
            if (b.Length > maxBytes) { error = "too big"; return FetchStatus.Failed; }
            File.WriteAllBytes(destPath, b);
            return FetchStatus.Ok;
        }
    }

    public static class UpdateServiceTests
    {
        private const string Tag = "v0.3.0";
        private const string Base = "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.3.0/";
        private static readonly DateTime Now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        private sealed class World : IDisposable
        {
            public string Dir;       // staging
            public string DataDir;   // admin-only data (state)
            public FakeNet Net = new FakeNet();
            public byte[] Exe;
            public byte[] ManifestBytes;
            public string Sig;
            public string ProductVersion = "0.3.0";
            public List<string> Log = new List<string>();
            public string KeyXml = TestKeys.PubA;
            public UpdateVersion Current;

            public World(string version)
            {
                string root = Path.Combine(Path.GetTempPath(), "forja-upd-" + Guid.NewGuid().ToString("N"));
                Dir = Path.Combine(root, "stage");
                DataDir = Path.Combine(root, "data");
                Directory.CreateDirectory(Dir);
                Directory.CreateDirectory(DataDir);
                UpdateVersion.TryParse("0.2.0", out Current);
                Exe = new byte[4000];
                new Random(7).NextBytes(Exe);
                Exe[0] = (byte)'M';
                Exe[1] = (byte)'Z';
                Publish(version, Tag);
            }

            // (Re)builds a consistent, correctly signed release for this world.
            public void Publish(string manifestVersion, string tag)
            {
                ManifestBytes = UpdateManifestFile.Build(manifestVersion, UpdateManifestFile.Sha256Hex(Exe), Exe.Length, "2026-11-17T10:00:00Z", "notas", null);
                Sig = UpdateSigner.Sign(ManifestBytes, TestKeys.PrivA);
                string b = Base.Replace("v0.3.0", tag);
                Net.Bodies[UpdateEndpoints.ApiLatestUrl] = Encoding.UTF8.GetBytes(
                    "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":["
                    + "{\"name\":\"SenseMonitor.exe\",\"browser_download_url\":\"" + b + "SenseMonitor.exe\",\"size\":" + Exe.Length + "},"
                    + "{\"name\":\"manifest.json\",\"browser_download_url\":\"" + b + "manifest.json\",\"size\":" + ManifestBytes.Length + "},"
                    + "{\"name\":\"manifest.json.sig\",\"browser_download_url\":\"" + b + "manifest.json.sig\",\"size\":" + Sig.Length + "}]}");
                Net.Bodies[b + "SenseMonitor.exe"] = Exe;
                Net.Bodies[b + "manifest.json"] = ManifestBytes;
                Net.Bodies[b + "manifest.json.sig"] = Encoding.ASCII.GetBytes(Sig);
            }

            public UpdateService Service()
            {
                return new UpdateService(Net, Dir, DataDir, Current, KeyXml, delegate { return Now; },
                    delegate(string p) { return ProductVersion; }, new Version(10, 0, 22631), delegate(string s) { Log.Add(s); });
            }

            public bool Has(string name) { return File.Exists(Path.Combine(Dir, name)); }

            public bool BlockedNow(string v)
            {
                UpdateVersion uv;
                UpdateVersion.TryParse(v, out uv);
                return UpdateStore.LoadState(DataDir).IsBlocked(uv, Now);
            }

            public void Dispose()
            {
                try { Directory.Delete(Path.GetDirectoryName(Dir), true); }
                catch (Exception) { }
            }
        }

        [Test]
        public static void HappyPath_StagesVerifiedUpdate()
        {
            using (World w = new World("0.3.0"))
            {
                CheckResult r = w.Service().CheckAndStage();
                Assert.Equal(CheckOutcome.Staged, r.Outcome);
                Assert.Equal("0.3.0", r.Version.ToString());
                Assert.True(w.Has("SenseMonitor.novo.exe") && w.Has("manifest.json") && w.Has("manifest.json.sig"), "staged files");
                Assert.True(!w.Has("SenseMonitor.baixando"), "no leftover download");
                UpdateState s = UpdateStore.LoadState(w.DataDir);
                Assert.True(s.LastCheckUtc.HasValue, "check recorded");
                string why;
                StagedUpdate st = UpdateService.VerifyStaged(w.Dir, w.Current, w.KeyXml, delegate(string p) { return "0.3.0"; }, Path.Combine(w.Dir, "SenseMonitor.novo.exe"), out why);
                Assert.True(st != null, why);
                Assert.Equal("0.3.0", st.Version.ToString());
            }
        }

        [Test]
        public static void RequestsAreOnlyApiThenThreeAssets_InOrder()
        {
            using (World w = new World("0.3.0"))
            {
                w.Service().CheckAndStage();
                Assert.Equal(4, w.Net.Requests.Count);
                Assert.Equal(UpdateEndpoints.ApiLatestUrl, w.Net.Requests[0]);
                Assert.Equal(Base + "manifest.json", w.Net.Requests[1]);
                Assert.Equal(Base + "manifest.json.sig", w.Net.Requests[2]);
                Assert.Equal(Base + "SenseMonitor.exe", w.Net.Requests[3]);
            }
        }

        [Test]
        public static void NoReleaseYet_Is404_NotAnError()
        {
            using (World w = new World("0.3.0"))
            {
                w.Net.Status[UpdateEndpoints.ApiLatestUrl] = FetchStatus.NotFound;
                CheckResult r = w.Service().CheckAndStage();
                Assert.Equal(CheckOutcome.NoRelease, r.Outcome);
                Assert.Equal(1, w.Net.Requests.Count);
                Assert.True(!w.Has("SenseMonitor.novo.exe"), "nothing staged");
                Assert.True(UpdateStore.LoadState(w.DataDir).LastCheckUtc.HasValue, "counts as a completed check");
            }
        }

        [Test]
        public static void ApiUnreachable_IsRetriedLater_NothingBlocked()
        {
            using (World w = new World("0.3.0"))
            {
                w.Net.Status[UpdateEndpoints.ApiLatestUrl] = FetchStatus.Failed;
                CheckResult r = w.Service().CheckAndStage();
                Assert.Equal(CheckOutcome.NetworkFailed, r.Outcome);
                UpdateState s = UpdateStore.LoadState(w.DataDir);
                Assert.True(!s.LastCheckUtc.HasValue, "not a completed check");
                Assert.True(s.LastAttemptUtc.HasValue, "attempt recorded (1 h backoff)");
                Assert.True(!w.BlockedNow("0.3.0"), "not blocked");
            }
        }

        [Test]
        public static void SameVersion_OrOlder_StopsAfterOneRequest()
        {
            using (World w = new World("0.3.0"))
            {
                UpdateVersion.TryParse("0.3.0", out w.Current);
                Assert.Equal(CheckOutcome.UpToDate, w.Service().CheckAndStage().Outcome);
                Assert.Equal(1, w.Net.Requests.Count);
                UpdateVersion.TryParse("0.4.0", out w.Current);
                w.Net.Requests.Clear();
                Assert.Equal(CheckOutcome.UpToDate, w.Service().CheckAndStage().Outcome);
                Assert.Equal(1, w.Net.Requests.Count);
            }
        }

        [Test]
        public static void SignatureFromAnotherKey_IsRejected_AndExeNeverDownloaded()
        {
            using (World w = new World("0.3.0"))
            {
                w.Sig = UpdateSigner.Sign(w.ManifestBytes, TestKeys.PrivB);
                w.Net.Bodies[Base + "manifest.json.sig"] = Encoding.ASCII.GetBytes(w.Sig);
                CheckResult r = w.Service().CheckAndStage();
                Assert.Equal(CheckOutcome.Rejected, r.Outcome);
                Assert.True(!w.Net.Requests.Contains(Base + "SenseMonitor.exe"), "exe not fetched");
                Assert.True(!w.Has("SenseMonitor.novo.exe") && !w.Has("manifest.json"), "nothing staged");
                Assert.True(w.BlockedNow("0.3.0"), "blocked 24 h");
                Assert.True(w.Log.Exists(delegate(string l) { return l.Contains("24 h"); }), "journal line");
            }
        }

        [Test]
        public static void ManifestTamperedAfterSigning_IsRejected()
        {
            using (World w = new World("0.3.0"))
            {
                byte[] evil = UpdateManifestFile.Build("0.3.0", UpdateManifestFile.Sha256Hex(new byte[] { 1, 2, 3 }), 3, "2026-11-17T10:00:00Z", "x", null);
                w.Net.Bodies[Base + "manifest.json"] = evil; // signature still the old manifest's
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe"), "nothing staged");
            }
        }

        [Test]
        public static void PlaceholderKey_RejectsEverything()
        {
            using (World w = new World("0.3.0"))
            {
                w.KeyXml = UpdateKey.PlaceholderMarker;
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe"), "nothing staged");
            }
        }

        [Test]
        public static void ExeWithWrongHash_IsDiscarded()
        {
            using (World w = new World("0.3.0"))
            {
                byte[] other = (byte[])w.Exe.Clone();
                other[100] ^= 0xFF; // same size, different content, signed manifest says otherwise
                w.Net.Bodies[Base + "SenseMonitor.exe"] = other;
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe") && !w.Has("SenseMonitor.baixando"), "discarded");
                Assert.True(w.BlockedNow("0.3.0"), "blocked");
            }
        }

        [Test]
        public static void ExeWithWrongSize_IsDiscarded()
        {
            using (World w = new World("0.3.0"))
            {
                byte[] shorter = new byte[w.Exe.Length - 10];
                Array.Copy(w.Exe, shorter, shorter.Length);
                w.Net.Bodies[Base + "SenseMonitor.exe"] = shorter;
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe") && !w.Has("SenseMonitor.baixando"), "discarded");
            }
        }

        [Test]
        public static void ExeBiggerThanManifest_IsNotAccepted()
        {
            using (World w = new World("0.3.0"))
            {
                byte[] bigger = new byte[w.Exe.Length + 5000];
                Array.Copy(w.Exe, bigger, w.Exe.Length);
                w.Net.Bodies[Base + "SenseMonitor.exe"] = bigger;
                CheckResult r = w.Service().CheckAndStage();
                Assert.True(r.Outcome != CheckOutcome.Staged, "must not stage");
                Assert.True(!w.Has("SenseMonitor.novo.exe") && !w.Has("SenseMonitor.baixando"), "nothing left");
            }
        }

        [Test]
        public static void NotAnExecutable_IsDiscarded()
        {
            using (World w = new World("0.3.0"))
            {
                w.Exe[0] = (byte)'#';
                w.Publish("0.3.0", Tag); // hash and manifest consistent, but no MZ header
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe"), "nothing staged");
            }
        }

        [Test]
        public static void EmbeddedVersionMismatch_IsDiscarded()
        {
            using (World w = new World("0.3.0"))
            {
                w.ProductVersion = "0.2.9";
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe"), "nothing staged");
            }
        }

        [Test]
        public static void ManifestVersionDifferentFromTag_IsRejected_DowngradeReplay()
        {
            using (World w = new World("0.3.0"))
            {
                // an old, genuinely signed 0.1.0 manifest + exe presented under a v0.3.0 tag
                w.Publish("0.1.0", Tag);
                CheckResult r = w.Service().CheckAndStage();
                Assert.Equal(CheckOutcome.Rejected, r.Outcome);
                Assert.True(!w.Has("SenseMonitor.novo.exe"), "nothing staged");
            }
        }

        [Test]
        public static void BlockedVersion_IsSkippedWithoutFetchingMore()
        {
            using (World w = new World("0.3.0"))
            {
                w.Sig = UpdateSigner.Sign(w.ManifestBytes, TestKeys.PrivB);
                w.Net.Bodies[Base + "manifest.json.sig"] = Encoding.ASCII.GetBytes(w.Sig);
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                w.Net.Requests.Clear();
                Assert.Equal(CheckOutcome.Blocked, w.Service().CheckAndStage().Outcome);
                Assert.Equal(1, w.Net.Requests.Count);
            }
        }

        [Test]
        public static void ExeListedButGone_IsRejected_ButNetworkErrorIsNot()
        {
            using (World w = new World("0.3.0"))
            {
                w.Net.Status[Base + "SenseMonitor.exe"] = FetchStatus.Failed;
                Assert.Equal(CheckOutcome.NetworkFailed, w.Service().CheckAndStage().Outcome);
                Assert.True(!w.BlockedNow("0.3.0"), "network error: not blocked");
                w.Net.Status[Base + "SenseMonitor.exe"] = FetchStatus.NotFound;
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.True(w.BlockedNow("0.3.0"), "missing asset: blocked 24 h");
            }
        }

        [Test]
        public static void ReleaseFromAnotherRepoPath_IsIgnored()
        {
            using (World w = new World("0.3.0"))
            {
                string evil = Encoding.UTF8.GetString(w.Net.Bodies[UpdateEndpoints.ApiLatestUrl]).Replace("carlosbrito-afk/SenseMonitor", "attacker/SenseMonitor");
                w.Net.Bodies[UpdateEndpoints.ApiLatestUrl] = Encoding.UTF8.GetBytes(evil);
                Assert.Equal(CheckOutcome.Rejected, w.Service().CheckAndStage().Outcome);
                Assert.Equal(1, w.Net.Requests.Count);
            }
        }

        [Test]
        public static void NewerStagedFile_ReplacesAnOlderOne()
        {
            using (World w = new World("0.3.0"))
            {
                File.WriteAllText(Path.Combine(w.Dir, "SenseMonitor.novo.exe"), "old staged");
                Assert.Equal(CheckOutcome.Staged, w.Service().CheckAndStage().Outcome);
                Assert.Equal(w.Exe.Length, (int)new FileInfo(Path.Combine(w.Dir, "SenseMonitor.novo.exe")).Length);
            }
        }

        // ---- VerifyStaged: what sits in the folder is re-verified before it is ever run --------

        private static StagedUpdate Verify(World w, out string why)
        {
            return UpdateService.VerifyStaged(w.Dir, w.Current, w.KeyXml, delegate(string p) { return w.ProductVersion; }, Path.Combine(w.Dir, "SenseMonitor.novo.exe"), out why);
        }

        private static World Staged()
        {
            World w = new World("0.3.0");
            Assert.Equal(CheckOutcome.Staged, w.Service().CheckAndStage().Outcome);
            return w;
        }

        [Test]
        public static void VerifyStaged_DetectsSwappedExe()
        {
            using (World w = Staged())
            {
                byte[] evil = (byte[])w.Exe.Clone();
                evil[500] ^= 1;
                File.WriteAllBytes(Path.Combine(w.Dir, "SenseMonitor.novo.exe"), evil);
                string why;
                Assert.Null(Verify(w, out why));
                Assert.True(why != null, "reason");
            }
        }

        [Test]
        public static void VerifyStaged_DetectsSwappedManifest_OrSignature()
        {
            using (World w = Staged())
            {
                string why;
                string sigPath = Path.Combine(w.Dir, "manifest.json.sig");
                string good = File.ReadAllText(sigPath);
                File.WriteAllText(sigPath, UpdateSigner.Sign(w.ManifestBytes, TestKeys.PrivB));
                Assert.Null(Verify(w, out why));
                File.WriteAllText(sigPath, good);
                Assert.True(Verify(w, out why) != null, "restored is fine again: " + why);
                byte[] evil = UpdateManifestFile.Build("0.3.0", UpdateManifestFile.Sha256Hex(new byte[] { 9 }), 1, "2026-11-17T10:00:00Z", "x", null);
                File.WriteAllBytes(Path.Combine(w.Dir, "manifest.json"), evil);
                Assert.Null(Verify(w, out why));
            }
        }

        [Test]
        public static void VerifyStaged_RefusesNotNewerAndMissingFiles()
        {
            using (World w = Staged())
            {
                string why;
                UpdateVersion.TryParse("0.3.0", out w.Current);
                Assert.Null(Verify(w, out why));
                UpdateVersion.TryParse("0.2.0", out w.Current);
                File.Delete(Path.Combine(w.Dir, "manifest.json"));
                Assert.Null(Verify(w, out why));
            }
            using (World w2 = new World("0.3.0"))
            {
                string why;
                Assert.Null(Verify(w2, out why)); // nothing staged at all
                Assert.True(why != null, "reason");
            }
        }
    }
}
