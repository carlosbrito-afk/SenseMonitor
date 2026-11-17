using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Forja.Core
{
    // Where releases live and which URLs the updater may ever fetch.
    public static class UpdateEndpoints
    {
        public const string Owner = "carlosbrito-afk";
        public const string Repo = "SenseMonitor";
        public const string ApiLatestUrl = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
        public const string UserAgent = "SenseMonitor-Updater";

        public const string ExeAsset = "SenseMonitor.exe";
        public const string ManifestAsset = "manifest.json";
        public const string SignatureAsset = "manifest.json.sig";

        public const int MaxApiBytes = 1024 * 1024;

        // Hosts a download may be redirected to. GitHub serves release assets from
        // objects.githubusercontent.com and, since late 2025, release-assets.githubusercontent.com.
        private static readonly string[] RedirectHosts =
        {
            "objects.githubusercontent.com", "release-assets.githubusercontent.com"
        };

        // The URL of an asset as listed by the API: https, host github.com, this repo's
        // releases/download/<tag>/<name>, nothing else (no credentials, port, query, fragment,
        // dot segments, encoded separators).
        public static bool IsAllowedAssetUrl(string url, string tag, string assetName)
        {
            Uri u;
            if (!TryHttps(url, out u)) return false;
            if (!string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return false;
            if (url.IndexOf("..", StringComparison.Ordinal) >= 0 || url.IndexOf('%') >= 0 || url.IndexOf('\\') >= 0) return false;
            string expected = "/" + Owner + "/" + Repo + "/releases/download/" + tag + "/" + assetName;
            return string.Equals(u.AbsolutePath, expected, StringComparison.OrdinalIgnoreCase);
        }

        // A redirect target while downloading: https and one of the two GitHub asset CDN hosts.
        public static bool IsAllowedRedirectUrl(string url)
        {
            Uri u;
            if (!TryHttps(url, out u)) return false;
            for (int i = 0; i < RedirectHosts.Length; i++)
            {
                if (string.Equals(u.Host, RedirectHosts[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool TryHttps(string url, out Uri u)
        {
            u = null;
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(u.UserInfo)) return false;
            if (u.Port != 443) return false;
            // IDN / lookalike hosts: only plain ASCII names are ever compared.
            for (int i = 0; i < u.Host.Length; i++)
            {
                if (u.Host[i] > 126) return false;
            }
            return true;
        }
    }

    [DataContract]
    internal sealed class GhAsset
    {
        [DataMember(Name = "name")] public string Name = null;
        [DataMember(Name = "browser_download_url")] public string Url = null;
        [DataMember(Name = "size")] public long Size = 0;
    }

    [DataContract]
    internal sealed class GhRelease
    {
        [DataMember(Name = "tag_name")] public string Tag = null;
        [DataMember(Name = "draft")] public bool Draft = false;
        [DataMember(Name = "prerelease")] public bool Prerelease = false;
        [DataMember(Name = "assets")] public List<GhAsset> Assets = null;
    }

    // What the releases/latest answer boils down to.
    public sealed class ReleaseInfo
    {
        public string Tag = null;
        public UpdateVersion Version;
        public string ExeUrl;
        public string ManifestUrl;
        public string SignatureUrl;
        public long ExeSize;
    }

    public static class ReleaseParser
    {
        // Strict: a draft/prerelease, a tag that is not "v<major.minor.patch>", a missing or
        // duplicated asset, or an asset URL outside this repo's github.com release path is an
        // error. Unknown JSON members are ignored.
        public static bool TryParse(byte[] json, out ReleaseInfo info, out string error)
        {
            info = null;
            error = null;
            if (json == null || json.Length == 0 || json.Length > UpdateEndpoints.MaxApiBytes)
            {
                error = "resposta da API vazia ou grande demais";
                return false;
            }
            GhRelease r;
            try
            {
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream(json, false))
                {
                    r = new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(ms) as GhRelease;
                }
            }
            catch (Exception)
            {
                error = "resposta da API ilegível";
                return false;
            }
            if (r == null) { error = "resposta da API ilegível"; return false; }
            if (r.Draft || r.Prerelease) { error = "release é rascunho ou pré-lançamento"; return false; }
            if (r.Tag == null || r.Tag.Length < 6 || r.Tag[0] != 'v') { error = "tag inválida"; return false; }
            UpdateVersion v;
            if (!UpdateVersion.TryParse(r.Tag.Substring(1), out v)) { error = "tag inválida"; return false; }

            ReleaseInfo ri = new ReleaseInfo();
            ri.Tag = r.Tag;
            ri.Version = v;
            if (r.Assets == null) { error = "release sem arquivos"; return false; }
            if (!Pick(r, UpdateEndpoints.ExeAsset, out ri.ExeUrl, out ri.ExeSize, out error)) return false;
            long ignored;
            if (!Pick(r, UpdateEndpoints.ManifestAsset, out ri.ManifestUrl, out ignored, out error)) return false;
            if (!Pick(r, UpdateEndpoints.SignatureAsset, out ri.SignatureUrl, out ignored, out error)) return false;
            if (ri.ExeSize <= 0 || ri.ExeSize > UpdateManifestFile.MaxExeBytes) { error = "tamanho do executável fora do limite"; return false; }
            info = ri;
            return true;
        }

        private static bool Pick(GhRelease r, string name, out string url, out long size, out string error)
        {
            url = null;
            size = 0;
            error = null;
            int count = 0;
            for (int i = 0; i < r.Assets.Count; i++)
            {
                GhAsset a = r.Assets[i];
                if (a != null && string.Equals(a.Name, name, StringComparison.Ordinal))
                {
                    count++;
                    url = a.Url;
                    size = a.Size;
                }
            }
            if (count != 1) { error = count == 0 ? "arquivo ausente na release: " + name : "arquivo repetido na release: " + name; return false; }
            if (!UpdateEndpoints.IsAllowedAssetUrl(url, r.Tag, name)) { error = "endereço de download recusado para " + name; return false; }
            return true;
        }
    }
}
