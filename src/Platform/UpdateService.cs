using System;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // The network, as the update check sees it (a fake in the tests).
    public interface IUpdateNet
    {
        FetchStatus GetBytes(string url, int maxBytes, out byte[] data, out string error);
        FetchStatus GetFile(string url, long maxBytes, string destPath, out string error);
    }

    public sealed class HttpUpdateNet : IUpdateNet
    {
        public FetchStatus GetBytes(string url, int maxBytes, out byte[] data, out string error)
        {
            return UpdateDownloader.GetBytes(url, maxBytes, RedirectRule(url), out data, out error);
        }

        public FetchStatus GetFile(string url, long maxBytes, string destPath, out string error)
        {
            return UpdateDownloader.GetFile(url, maxBytes, RedirectRule(url), destPath, out error);
        }

        // The API answer is never redirected; asset downloads only to the GitHub download hosts.
        private static Func<string, bool> RedirectRule(string url)
        {
            if (string.Equals(url, UpdateEndpoints.ApiLatestUrl, StringComparison.Ordinal)) return delegate(string u) { return false; };
            return UpdateEndpoints.IsAllowedRedirectUrl;
        }
    }

    public enum CheckOutcome
    {
        // Updating is switched off for this run (placeholder key, not the installed copy, unsafe folder).
        Disabled,
        // Could not reach / read GitHub; retried later, nothing blocked.
        NetworkFailed,
        // The repository has no release yet.
        NoRelease,
        UpToDate,
        // A newer release exists but is blocked (an earlier failure or rollback).
        Blocked,
        // Something failed verification: discarded, version blocked for 24 h.
        Rejected,
        // Downloaded, verified and kept as SenseMonitor.novo.exe.
        Staged
    }

    public sealed class CheckResult
    {
        public CheckOutcome Outcome;
        public UpdateVersion Version;
        public string Message;
        public UpdateManifest Manifest;

        public CheckResult(CheckOutcome o, UpdateVersion v, string msg)
        {
            Outcome = o;
            Version = v;
            Message = msg;
        }
    }

    public sealed class StagedUpdate
    {
        public UpdateVersion Version;
        public UpdateManifest Manifest;
        public string ExePath;
    }

    // Check -> verify -> stage. Order matters and each gate discards on failure:
    //   release JSON (tag > current, assets on this repo's github.com path)
    //   -> manifest + signature, signature checked over the exact bytes with the embedded key
    //   -> manifest sane, version == tag, strictly newer, not blocked, OS supported
    //   -> exe downloaded to a temp name with a hard size cap, size and SHA-256 equal the manifest,
    //      "MZ" header, embedded product version equals the manifest version
    //   -> manifest+sig copied next to it, the exe renamed to SenseMonitor.novo.exe LAST.
    public sealed class UpdateService
    {
        private readonly IUpdateNet net;
        private readonly string dir;       // staging: the downloaded files
        private readonly string dataDir;   // admin-only data: state and journal
        private readonly UpdateVersion current;
        private readonly string publicKeyXml;
        private readonly Func<DateTime> utcNow;
        private readonly Func<string, string> productVersionOf;
        private readonly Version os;
        private readonly Action<string> log;

        public UpdateService(IUpdateNet net, string stagingDir, string dataDir, UpdateVersion current, string publicKeyXml,
            Func<DateTime> utcNow, Func<string, string> productVersionOf, Version os, Action<string> log)
        {
            this.net = net;
            dir = stagingDir;
            this.dataDir = dataDir;
            this.current = current;
            this.publicKeyXml = publicKeyXml;
            this.utcNow = utcNow;
            this.productVersionOf = productVersionOf;
            this.os = os;
            this.log = log ?? delegate(string s) { };
        }

        public static string ProductVersionOfFile(string path)
        {
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion; }
            catch (Exception) { return null; }
        }

        public CheckResult CheckAndStage()
        {
            DateTime now = utcNow();
            UpdateState state = UpdateStore.LoadState(dataDir);
            state.Prune(now, current);
            state.MarkAttempt(now);
            UpdateStore.SaveState(dataDir, state);

            string err;
            byte[] api;
            FetchStatus st = net.GetBytes(UpdateEndpoints.ApiLatestUrl, UpdateEndpoints.MaxApiBytes, out api, out err);
            if (st == FetchStatus.NotFound)
            {
                state.MarkChecked(now);
                UpdateStore.SaveState(dataDir, state);
                log("verificação: nenhuma versão publicada ainda");
                return new CheckResult(CheckOutcome.NoRelease, null, "nenhuma versão publicada");
            }
            if (st != FetchStatus.Ok)
            {
                log("verificação falhou: " + err);
                return new CheckResult(CheckOutcome.NetworkFailed, null, err);
            }
            ReleaseInfo info;
            if (!ReleaseParser.TryParse(api, out info, out err))
            {
                state.MarkChecked(now);
                UpdateStore.SaveState(dataDir, state);
                log("release ignorada: " + err);
                return new CheckResult(CheckOutcome.Rejected, null, err);
            }

            UpdateVerdict v = UpdatePolicy.ForRelease(current, info.Version, state, now);
            if (v == UpdateVerdict.UpToDate)
            {
                state.MarkChecked(now);
                UpdateStore.SaveState(dataDir, state);
                log("verificação: já está na versão mais recente (" + current + "; última publicada " + info.Version + ")");
                return new CheckResult(CheckOutcome.UpToDate, info.Version, "atualizado");
            }
            if (v == UpdateVerdict.Blocked)
            {
                state.MarkChecked(now);
                UpdateStore.SaveState(dataDir, state);
                log("versão " + info.Version + " está bloqueada (falha ou reversão anterior); ignorada");
                return new CheckResult(CheckOutcome.Blocked, info.Version, "bloqueada");
            }
            log("versão " + info.Version + " encontrada (instalada: " + current + ")");

            // manifest + signature
            byte[] manifestBytes, sigBytes;
            st = net.GetBytes(info.ManifestUrl, UpdateManifestFile.MaxManifestBytes, out manifestBytes, out err);
            if (st != FetchStatus.Ok) return Network(state, now, info.Version, "manifesto", st, err);
            st = net.GetBytes(info.SignatureUrl, 4096, out sigBytes, out err);
            if (st != FetchStatus.Ok) return Network(state, now, info.Version, "assinatura", st, err);

            SignatureCheck sc = UpdateVerifier.Verify(manifestBytes, Encoding.ASCII.GetString(sigBytes), publicKeyXml);
            if (sc != SignatureCheck.Ok) return Reject(state, now, info.Version, "manifesto recusado: " + UpdateVerifier.Describe(sc));
            UpdateManifest m;
            if (!UpdateManifestFile.TryParse(manifestBytes, out m, out err)) return Reject(state, now, info.Version, "manifesto recusado: " + err);
            if (!m.Version.Equals(info.Version)) return Reject(state, now, info.Version, "manifesto recusado: versão " + m.Versao + " difere da tag " + info.Tag);
            v = UpdatePolicy.ForManifest(current, m, os, state, now);
            if (v == UpdateVerdict.OsTooOld) return Reject(state, now, info.Version, "versão exige Windows " + m.MinimoWindows + " ou mais novo");
            if (v != UpdateVerdict.Proceed) return Reject(state, now, info.Version, "manifesto recusado: versão não é mais nova que a instalada");
            if (m.Tamanho != info.ExeSize) return Reject(state, now, info.Version, "manifesto recusado: tamanho difere do arquivo listado na release");

            // the exe
            string tmp = Path.Combine(dir, UpdateStore.DownloadingName);
            st = net.GetFile(info.ExeUrl, m.Tamanho, tmp, out err);
            if (st != FetchStatus.Ok) return Network(state, now, info.Version, "executável", st, err);
            string bad = CheckDownloaded(tmp, m);
            if (bad != null)
            {
                SafeDelete(tmp);
                return Reject(state, now, info.Version, "download recusado: " + bad);
            }

            // keep it: signed manifest next to it first, the exe's final name last
            try
            {
                File.WriteAllBytes(Path.Combine(dir, UpdateStore.ManifestName), manifestBytes);
                File.WriteAllBytes(Path.Combine(dir, UpdateStore.SignatureName), sigBytes);
                string novo = Path.Combine(dir, UpdateStore.StagedName);
                SafeDelete(novo);
                File.Move(tmp, novo);
            }
            catch (Exception ex)
            {
                SafeDelete(tmp);
                log("não foi possível guardar a atualização (" + ex.GetType().Name + ")");
                return new CheckResult(CheckOutcome.NetworkFailed, info.Version, "falha ao guardar");
            }
            state.MarkChecked(now);
            UpdateStore.SaveState(dataDir, state);
            log("versão " + info.Version + " baixada e verificada (" + m.Tamanho + " bytes, SHA-256 confere); aguardando momento ocioso para aplicar");
            CheckResult r = new CheckResult(CheckOutcome.Staged, info.Version, "pronta");
            r.Manifest = m;
            return r;
        }

        // null = the file is exactly what the signed manifest describes.
        private string CheckDownloaded(string path, UpdateManifest m)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                string sha;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] head = new byte[2];
                    if (fs.Read(head, 0, 2) != 2 || head[0] != (byte)'M' || head[1] != (byte)'Z') return "não é um executável";
                    fs.Position = 0;
                    sha = UpdateManifestFile.Sha256Hex(fs);
                }
                string e = UpdateManifestFile.CheckExe(m, fi.Length, sha);
                if (e != null) return e;
                string pv = productVersionOf(path);
                if (pv != m.Versao) return "a versão embutida no executável (" + (pv ?? "?") + ") difere do manifesto";
                return null;
            }
            catch (Exception ex)
            {
                return "arquivo ilegível (" + ex.GetType().Name + ")";
            }
        }

        private CheckResult Reject(UpdateState state, DateTime now, UpdateVersion v, string why)
        {
            state.BlockForFailure(v, now, why);
            state.MarkChecked(now);
            UpdateStore.SaveState(dataDir, state);
            log(why + "; descartado, sem nova tentativa dessa versão por 24 h");
            return new CheckResult(CheckOutcome.Rejected, v, why);
        }

        private CheckResult Network(UpdateState state, DateTime now, UpdateVersion v, string what, FetchStatus st, string err)
        {
            if (st == FetchStatus.NotFound)
            {
                // The release lists the file but it is gone: do not hammer it.
                return Reject(state, now, v, what + " da release não encontrado");
            }
            log("download do " + what + " falhou: " + err);
            return new CheckResult(CheckOutcome.NetworkFailed, v, err);
        }

        private static void SafeDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception) { }
        }

        // What is waiting in the staging folder, fully re-verified (used at startup, right before
        // applying, and by the helper itself). Null + reason when anything is off.
        public static StagedUpdate VerifyStaged(string dir, UpdateVersion current, string publicKeyXml,
            Func<string, string> productVersionOf, string exePath, out string reason)
        {
            reason = null;
            try
            {
                string manifestPath = Path.Combine(dir, UpdateStore.ManifestName);
                string sigPath = Path.Combine(dir, UpdateStore.SignatureName);
                if (!File.Exists(exePath)) { reason = "nenhuma atualização guardada"; return null; }
                if (!File.Exists(manifestPath) || !File.Exists(sigPath)) { reason = "manifesto guardado ausente"; return null; }
                if ((File.GetAttributes(exePath) & FileAttributes.ReparsePoint) != 0) { reason = "arquivo guardado é um link"; return null; }
                if (new FileInfo(manifestPath).Length > UpdateManifestFile.MaxManifestBytes || new FileInfo(sigPath).Length > 4096)
                {
                    reason = "manifesto guardado grande demais";
                    return null;
                }
                byte[] mb = File.ReadAllBytes(manifestPath);
                string sig = File.ReadAllText(sigPath, Encoding.ASCII);
                SignatureCheck sc = UpdateVerifier.Verify(mb, sig, publicKeyXml);
                if (sc != SignatureCheck.Ok) { reason = "manifesto guardado: " + UpdateVerifier.Describe(sc); return null; }
                UpdateManifest m;
                string err;
                if (!UpdateManifestFile.TryParse(mb, out m, out err)) { reason = "manifesto guardado: " + err; return null; }
                if (!m.Version.IsNewerThan(current)) { reason = "a versão guardada (" + m.Versao + ") não é mais nova que a instalada"; return null; }
                string sha;
                long len = new FileInfo(exePath).Length;
                using (FileStream fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    sha = UpdateManifestFile.Sha256Hex(fs);
                }
                err = UpdateManifestFile.CheckExe(m, len, sha);
                if (err != null) { reason = "arquivo guardado: " + err; return null; }
                string pv = productVersionOf(exePath);
                if (pv != m.Versao) { reason = "a versão embutida no arquivo guardado difere do manifesto"; return null; }
                StagedUpdate s = new StagedUpdate();
                s.Version = m.Version;
                s.Manifest = m;
                s.ExePath = exePath;
                return s;
            }
            catch (Exception ex)
            {
                reason = "falha ao conferir a atualização guardada (" + ex.GetType().Name + ")";
                return null;
            }
        }
    }
}
