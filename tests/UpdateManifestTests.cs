using System;
using System.Text;
using Forja.Core;

namespace Forja.Tests
{
    public static class UpdateManifestTests
    {
        private static readonly string Hash = new string('a', 64);

        private static byte[] Bytes(string s) { return Encoding.UTF8.GetBytes(s); }

        private static string Json(string versao, string sha, string tamanho, string extra)
        {
            return "{\"versao\":" + versao + ",\"sha256\":" + sha + ",\"tamanho\":" + tamanho + (extra ?? "") + "}";
        }

        private static string TryErr(string json)
        {
            UpdateManifest m;
            string err;
            if (UpdateManifestFile.TryParse(Bytes(json), out m, out err))
            {
                Assert.True(err == null && m != null, "valid parse must give a manifest");
                return null;
            }
            Assert.Null(m);
            Assert.True(err != null && err.Length > 0, "error text");
            return err;
        }

        [Test]
        public static void Build_ThenParse_RoundTrips()
        {
            byte[] b = UpdateManifestFile.Build("0.2.1", Hash.ToUpperInvariant(), 123456, "2026-11-17T10:00:00Z", "Correções\ne \"melhorias\" / acentuação ç", "10.0.19041");
            UpdateManifest m;
            string err;
            Assert.True(UpdateManifestFile.TryParse(b, out m, out err), err);
            Assert.Equal("0.2.1", m.Versao);
            Assert.Equal(Hash, m.Sha256); // normalized to lower case
            Assert.Equal(123456L, m.Tamanho);
            Assert.Equal("2026-11-17T10:00:00Z", m.Data);
            Assert.Equal("Correções\ne \"melhorias\" / acentuação ç", m.Notas);
            Assert.Equal("10.0.19041", m.MinimoWindows);
            Assert.Equal("0.2.1", m.Version.ToString());
        }

        [Test]
        public static void Build_IsDeterministic_AndHasNoBom()
        {
            byte[] a = UpdateManifestFile.Build("0.2.1", Hash, 10, "2026-11-17T10:00:00Z", "n", null);
            byte[] b = UpdateManifestFile.Build("0.2.1", Hash, 10, "2026-11-17T10:00:00Z", "n", null);
            Assert.Equal(Convert.ToBase64String(a), Convert.ToBase64String(b));
            Assert.True(a[0] == (byte)'{', "starts with { (no BOM)");
        }

        [Test]
        public static void Build_RefusesInvalidFields()
        {
            Assert.Throws<ArgumentException>(delegate { UpdateManifestFile.Build("0.2", Hash, 10, null, null, null); });
            Assert.Throws<ArgumentException>(delegate { UpdateManifestFile.Build("0.2.1", "abc", 10, null, null, null); });
            Assert.Throws<ArgumentException>(delegate { UpdateManifestFile.Build("0.2.1", Hash, 0, null, null, null); });
        }

        [Test]
        public static void Parse_MinimalValid_AndExtraMembersIgnored()
        {
            Assert.Null(TryErr(Json("\"1.0.0\"", "\"" + Hash + "\"", "5", null)));
            Assert.Null(TryErr(Json("\"1.0.0\"", "\"" + Hash + "\"", "5", ",\"futuro\":{\"x\":1}")));
        }

        [Test]
        public static void Parse_RejectsBadVersion()
        {
            string[] bad = { "\"1.0\"", "\"v1.0.0\"", "\"1.0.0-rc1\"", "\"\"", "null", "\"1.0.0 \"", "7" };
            foreach (string v in bad)
            {
                Assert.True(TryErr(Json(v, "\"" + Hash + "\"", "5", null)) != null, "bad version " + v);
            }
        }

        [Test]
        public static void Parse_RejectsBadHash()
        {
            string[] bad = { "\"abc\"", "\"" + new string('g', 64) + "\"", "\"" + new string('a', 63) + "\"", "\"" + new string('a', 65) + "\"", "null", "\"" + Hash + "\\n\"" };
            foreach (string h in bad)
            {
                Assert.True(TryErr(Json("\"1.0.0\"", h, "5", null)) != null, "bad hash " + h);
            }
        }

        [Test]
        public static void Parse_RejectsBadSize()
        {
            string[] bad = { "0", "-5", "20971521", "99999999999999999", "\"x\"" };
            foreach (string s in bad)
            {
                Assert.True(TryErr(Json("\"1.0.0\"", "\"" + Hash + "\"", s, null)) != null, "bad size " + s);
            }
            Assert.Null(TryErr(Json("\"1.0.0\"", "\"" + Hash + "\"", "20971520", null)));
        }

        [Test]
        public static void Parse_RejectsGarbageAndOversize()
        {
            Assert.True(TryErr("not json") != null, "garbage");
            Assert.True(TryErr("") != null, "empty");
            Assert.True(TryErr("[]") != null, "array");
            Assert.True(TryErr("null") != null, "null");
            string huge = Json("\"1.0.0\"", "\"" + Hash + "\"", "5", ",\"notas\":\"" + new string('x', 70000) + "\"");
            Assert.True(TryErr(huge) != null, "over 64 KB");
            string longNotes = Json("\"1.0.0\"", "\"" + Hash + "\"", "5", ",\"notas\":\"" + new string('x', 4001) + "\"");
            Assert.True(TryErr(longNotes) != null, "notes over limit");
            Assert.True(TryErr(Json("\"1.0.0\"", "\"" + Hash + "\"", "5", ",\"minimoWindows\":\"abc\"")) != null, "bad min windows");
        }

        [Test]
        public static void OsSupported_ComparesMinimum()
        {
            UpdateManifest m = new UpdateManifest();
            Assert.True(UpdateManifestFile.OsSupported(m, new Version(6, 1)), "no minimum");
            m.MinimoWindows = "10.0.19041";
            Assert.True(UpdateManifestFile.OsSupported(m, new Version(10, 0, 22631)), "newer");
            Assert.True(UpdateManifestFile.OsSupported(m, new Version(10, 0, 19041)), "equal");
            Assert.True(!UpdateManifestFile.OsSupported(m, new Version(10, 0, 18363)), "older");
            Assert.True(!UpdateManifestFile.OsSupported(m, null), "unknown OS with a minimum");
        }

        [Test]
        public static void CheckExe_MatchesSizeAndHash()
        {
            byte[] exe = Bytes("MZ fake exe payload");
            string sha = UpdateManifestFile.Sha256Hex(exe);
            UpdateManifest m = new UpdateManifest();
            m.Tamanho = exe.Length;
            m.Sha256 = sha;
            Assert.Null(UpdateManifestFile.CheckExe(m, exe.Length, sha));
            Assert.Null(UpdateManifestFile.CheckExe(m, exe.Length, sha.ToUpperInvariant()));
            Assert.True(UpdateManifestFile.CheckExe(m, exe.Length + 1, sha) != null, "size");
            Assert.True(UpdateManifestFile.CheckExe(m, exe.Length, UpdateManifestFile.Sha256Hex(Bytes("other"))) != null, "hash");
            Assert.True(UpdateManifestFile.CheckExe(m, exe.Length, null) != null, "null hash");
            Assert.True(UpdateManifestFile.CheckExe(null, 1, sha) != null, "null manifest");
        }

        [Test]
        public static void Sha256Hex_KnownVector()
        {
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", UpdateManifestFile.Sha256Hex(Bytes("abc")));
        }

        [Test]
        public static void SignedManifest_EndToEnd_OnlyIfSignatureCoversExactBytes()
        {
            byte[] mb = UpdateManifestFile.Build("0.3.0", Hash, 99, "2026-11-17T10:00:00Z", "ok", null);
            string sig = UpdateSigner.Sign(mb, TestKeys.PrivA);
            Assert.Equal(SignatureCheck.Ok, UpdateVerifier.Verify(mb, sig, TestKeys.PubA));
            // re-encoding the same logical manifest (e.g. with whitespace) is a different byte string
            string pretty = Encoding.UTF8.GetString(mb).Replace(",", ", ");
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(Bytes(pretty), sig, TestKeys.PubA));
        }
    }

    public static class UpdateEndpointsTests
    {
        private const string Base = "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/";

        [Test]
        public static void AssetUrl_Accepted()
        {
            Assert.True(UpdateEndpoints.IsAllowedAssetUrl(Base + "SenseMonitor.exe", "v0.2.1", "SenseMonitor.exe"), "exe");
            Assert.True(UpdateEndpoints.IsAllowedAssetUrl(Base + "manifest.json.sig", "v0.2.1", "manifest.json.sig"), "sig");
            Assert.True(UpdateEndpoints.IsAllowedAssetUrl("https://GitHub.com/CARLOSBRITO-AFK/sensemonitor/releases/download/v0.2.1/manifest.json", "v0.2.1", "manifest.json"), "case");
        }

        [Test]
        public static void AssetUrl_Rejected()
        {
            string[] bad =
            {
                "http://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://github.com.evil.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://evil.com/github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://github.com@evil.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://user:pw@github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://github.com:8443/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://objects.githubusercontent.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://github.com/other/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "https://github.com/carlosbrito-afk/Other/releases/download/v0.2.1/SenseMonitor.exe",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.0/SenseMonitor.exe",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/Other.exe",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe?x=1",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe#f",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/../../../../evil/SenseMonitor.exe",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/%2e%2e/SenseMonitor.exe",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe%00.png",
                "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1\\SenseMonitor.exe",
                "//github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "ftp://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/SenseMonitor.exe",
                "file:///c:/x.exe", "", null, "github.com/x"
            };
            foreach (string u in bad)
            {
                Assert.True(!UpdateEndpoints.IsAllowedAssetUrl(u, "v0.2.1", "SenseMonitor.exe"), "must reject: " + (u ?? "null"));
            }
        }

        [Test]
        public static void RedirectUrl_OnlyGithubDownloadHostsOverHttps()
        {
            Assert.True(UpdateEndpoints.IsAllowedRedirectUrl("https://objects.githubusercontent.com/github-production-release-asset/1/2?X-Amz-Signature=abc&x=y"), "objects");
            Assert.True(UpdateEndpoints.IsAllowedRedirectUrl("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r"), "release-assets");
            Assert.True(!UpdateEndpoints.IsAllowedRedirectUrl("https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v1.0.0/x"), "github.com is where the asset URL starts; a redirect must go to a CDN host");
            string[] bad =
            {
                "http://objects.githubusercontent.com/x", "https://objects.githubusercontent.com.evil.io/x", "https://evil.io/objects.githubusercontent.com",
                "https://githubusercontent.com/x", "https://raw.githubusercontent.com/x", "https://api.github.com/x", "https://objects.githubusercontent.com@evil.io/x",
                "https://objects.githubusercontent.com:444/x", "https://\u0261ithub.com/x", "https://github.com.attacker.net/x", "", null
            };
            foreach (string u in bad)
            {
                Assert.True(!UpdateEndpoints.IsAllowedRedirectUrl(u), "must reject redirect: " + (u ?? "null"));
            }
        }
    }

    public static class ReleaseParserTests
    {
        private const string B = "https://github.com/carlosbrito-afk/SenseMonitor/releases/download/v0.2.1/";

        private static string Asset(string name, string url, long size)
        {
            return "{\"name\":\"" + name + "\",\"browser_download_url\":\"" + url + "\",\"size\":" + size + ",\"state\":\"uploaded\"}";
        }

        private static string Release(string tag, string flags, params string[] assets)
        {
            return "{\"id\":1,\"tag_name\":\"" + tag + "\"," + flags + "\"assets\":[" + string.Join(",", assets) + "],\"body\":\"x\"}";
        }

        private static string Good()
        {
            return Release("v0.2.1", "\"draft\":false,\"prerelease\":false,",
                Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 1000000),
                Asset("manifest.json", B + "manifest.json", 300),
                Asset("manifest.json.sig", B + "manifest.json.sig", 512));
        }

        private static string Err(string json)
        {
            ReleaseInfo info;
            string err;
            bool ok = ReleaseParser.TryParse(Encoding.UTF8.GetBytes(json), out info, out err);
            if (ok) { Assert.True(info != null && err == null, "ok gives info"); return null; }
            Assert.Null(info);
            Assert.True(err != null, "error text");
            return err;
        }

        [Test]
        public static void Parse_GoodRelease()
        {
            ReleaseInfo info;
            string err;
            Assert.True(ReleaseParser.TryParse(Encoding.UTF8.GetBytes(Good()), out info, out err), err);
            Assert.Equal("v0.2.1", info.Tag);
            Assert.Equal("0.2.1", info.Version.ToString());
            Assert.Equal(B + "SenseMonitor.exe", info.ExeUrl);
            Assert.Equal(B + "manifest.json", info.ManifestUrl);
            Assert.Equal(B + "manifest.json.sig", info.SignatureUrl);
            Assert.Equal(1000000L, info.ExeSize);
        }

        [Test]
        public static void Parse_RejectsDraftAndPrerelease()
        {
            Assert.True(Err(Good().Replace("\"draft\":false", "\"draft\":true")) != null, "draft");
            Assert.True(Err(Good().Replace("\"prerelease\":false", "\"prerelease\":true")) != null, "pre");
        }

        [Test]
        public static void Parse_RejectsBadTags()
        {
            string[] bad = { "0.2.1", "v0.2", "v0.2.1-beta", "V0.2.1", "latest", "", "v" };
            foreach (string t in bad)
            {
                string j = Release(t, "", Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 10),
                    Asset("manifest.json", B + "manifest.json", 10), Asset("manifest.json.sig", B + "manifest.json.sig", 10));
                Assert.True(Err(j) != null, "tag " + t);
            }
        }

        [Test]
        public static void Parse_RejectsMissingDuplicateOrForeignAssets()
        {
            Assert.True(Err(Release("v0.2.1", "", Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 10), Asset("manifest.json", B + "manifest.json", 10))) != null, "no sig");
            Assert.True(Err(Release("v0.2.1", "")) != null, "no assets");
            Assert.True(Err("{\"tag_name\":\"v0.2.1\"}") != null, "assets null");
            Assert.True(Err(Release("v0.2.1", "", Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 10), Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 10),
                Asset("manifest.json", B + "manifest.json", 10), Asset("manifest.json.sig", B + "manifest.json.sig", 10))) != null, "duplicate");
            // an asset from another place
            Assert.True(Err(Release("v0.2.1", "", Asset("SenseMonitor.exe", "https://evil.example/SenseMonitor.exe", 10),
                Asset("manifest.json", B + "manifest.json", 10), Asset("manifest.json.sig", B + "manifest.json.sig", 10))) != null, "foreign url");
            // an asset url of the right repo but another tag
            Assert.True(Err(Release("v0.2.1", "", Asset("SenseMonitor.exe", B.Replace("v0.2.1", "v0.1.0") + "SenseMonitor.exe", 10),
                Asset("manifest.json", B + "manifest.json", 10), Asset("manifest.json.sig", B + "manifest.json.sig", 10))) != null, "other tag url");
        }

        [Test]
        public static void Parse_RejectsExeSizeOutOfBounds()
        {
            string tooBig = Release("v0.2.1", "", Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 21 * 1024 * 1024),
                Asset("manifest.json", B + "manifest.json", 10), Asset("manifest.json.sig", B + "manifest.json.sig", 10));
            Assert.True(Err(tooBig) != null, "too big");
            string zero = Release("v0.2.1", "", Asset("SenseMonitor.exe", B + "SenseMonitor.exe", 0),
                Asset("manifest.json", B + "manifest.json", 10), Asset("manifest.json.sig", B + "manifest.json.sig", 10));
            Assert.True(Err(zero) != null, "zero");
        }

        [Test]
        public static void Parse_NotFoundAndGarbageBodies()
        {
            // what the API returns when the repo has no releases: {"message":"Not Found",...}
            Assert.True(Err("{\"message\":\"Not Found\",\"documentation_url\":\"https://docs.github.com/rest\",\"status\":\"404\"}") != null, "404 body");
            Assert.True(Err("<html>proxy login</html>") != null, "html");
            Assert.True(Err("") != null, "empty");
            Assert.True(Err("null") != null, "null");
        }
    }
}
