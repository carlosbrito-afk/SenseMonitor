using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Forja.Platform
{
    public enum FetchStatus
    {
        Ok,
        // HTTP 404 (e.g. the repository has no releases yet): not an error worth a retry loop.
        NotFound,
        // Network, TLS, timeout, redirect to a forbidden host, wrong status, too big...
        Failed
    }

    // HTTPS GET with the updater's rules: TLS 1.2, generic User-Agent, no credentials or machine id,
    // 30 s per request, a hard size cap, and redirects only by hand and only to allowed URLs.
    // The default certificate validation is never replaced or relaxed.
    public static class UpdateDownloader
    {
        public const int TimeoutMs = 30 * 1000;
        public const int OverallLimitMs = 3 * 60 * 1000;
        private const int MaxRedirects = 5;

        // Small response (API JSON, manifest, signature) into memory.
        public static FetchStatus GetBytes(string url, int maxBytes, Func<string, bool> allowedRedirect, out byte[] data, out string error)
        {
            data = null;
            using (MemoryStream ms = new MemoryStream())
            {
                FetchStatus st = Fetch(url, maxBytes, allowedRedirect, ms, out error);
                if (st == FetchStatus.Ok) data = ms.ToArray();
                return st;
            }
        }

        // Large response straight into a file (created fresh; removed on any failure).
        public static FetchStatus GetFile(string url, long maxBytes, Func<string, bool> allowedRedirect, string destPath, out string error)
        {
            FetchStatus st;
            try
            {
                using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    st = Fetch(url, maxBytes, allowedRedirect, fs, out error);
                }
            }
            catch (Exception ex)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); }
                catch (Exception) { }
                error = "não foi possível gravar o download (" + ex.GetType().Name + ")";
                return FetchStatus.Failed;
            }
            if (st != FetchStatus.Ok)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); }
                catch (Exception) { }
            }
            return st;
        }

        private static FetchStatus Fetch(string url, long maxBytes, Func<string, bool> allowedRedirect, Stream sink, out string error)
        {
            error = null;
            Stopwatch total = Stopwatch.StartNew();
            string current = url;
            try
            {
                // TLS 1.2 is switched ON explicitly (older .NET defaults may not offer it); whatever the system
                // already offers (TLS 1.3 on Windows 11) stays available, so the setting is only ever widened.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                for (int hop = 0; hop <= MaxRedirects; hop++)
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(current);
                    req.Method = "GET";
                    req.AllowAutoRedirect = false;
                    req.UserAgent = Forja.Core.UpdateEndpoints.UserAgent;
                    req.Accept = "application/vnd.github+json, application/octet-stream, */*";
                    req.Timeout = TimeoutMs;
                    req.ReadWriteTimeout = TimeoutMs;
                    req.KeepAlive = false;
                    req.AutomaticDecompression = DecompressionMethods.None;
                    req.Credentials = null;
                    req.PreAuthenticate = false;
                    HttpWebResponse resp;
                    try
                    {
                        resp = (HttpWebResponse)req.GetResponse();
                    }
                    catch (WebException wex)
                    {
                        resp = wex.Response as HttpWebResponse;
                        if (resp == null)
                        {
                            error = "sem resposta (" + wex.Status + ")";
                            return FetchStatus.Failed;
                        }
                    }
                    using (resp)
                    {
                        int code = (int)resp.StatusCode;
                        if (code == 404) return Fail(FetchStatus.NotFound, "não encontrado (404)", out error);
                        if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                        {
                            string loc = resp.Headers["Location"];
                            Uri next;
                            if (string.IsNullOrEmpty(loc) || !Uri.TryCreate(new Uri(current), loc, out next))
                            {
                                return Fail(FetchStatus.Failed, "redirecionamento inválido", out error);
                            }
                            if (allowedRedirect == null || !allowedRedirect(next.AbsoluteUri))
                            {
                                return Fail(FetchStatus.Failed, "redirecionamento para endereço não permitido (" + next.Host + ")", out error);
                            }
                            current = next.AbsoluteUri;
                            continue;
                        }
                        if (code != 200) return Fail(FetchStatus.Failed, "resposta HTTP " + code, out error);
                        if (resp.ContentLength > maxBytes) return Fail(FetchStatus.Failed, "arquivo maior que o limite", out error);
                        using (Stream body = resp.GetResponseStream())
                        {
                            byte[] buf = new byte[16 * 1024];
                            long got = 0;
                            int n;
                            while ((n = body.Read(buf, 0, buf.Length)) > 0)
                            {
                                got += n;
                                if (got > maxBytes) return Fail(FetchStatus.Failed, "arquivo maior que o limite", out error);
                                if (total.ElapsedMilliseconds > OverallLimitMs) return Fail(FetchStatus.Failed, "download demorou demais", out error);
                                sink.Write(buf, 0, n);
                            }
                        }
                        return FetchStatus.Ok;
                    }
                }
                return Fail(FetchStatus.Failed, "redirecionamentos demais", out error);
            }
            catch (Exception ex)
            {
                error = "falha de rede (" + ex.GetType().Name + ")";
                return FetchStatus.Failed;
            }
        }

        private static FetchStatus Fail(FetchStatus s, string msg, out string error)
        {
            error = msg;
            return s;
        }
    }
}
