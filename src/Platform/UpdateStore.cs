using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // Two admin-only folders, each verified before every use (not a reparse point, owner and ACL):
    //
    // STAGING  %ProgramFiles%\SenseMonitor\atualizacao   (inherits the Program Files ACL)
    //   SenseMonitor.baixando        download in progress
    //   SenseMonitor.novo.exe        the verified staged release
    //   manifest.json(.sig)          the signed manifest it was verified against (re-checked at apply time)
    //   SenseMonitor.aplicador.exe   a hash-checked copy of the CURRENT exe: the apply helper
    //   SenseMonitor.falhou.exe      a release that was rolled back
    //
    // DATA     %ProgramData%\SenseMonitor\atualizacao    (created here with a protected DACL: SYSTEM and
    //          Administrators full control, Users read-only, nothing inherited from ProgramData, where any
    //          user may create folders)
    //   estado.json(.bak)            UpdateState (last check, blocklist)
    //   iniciou-ok.flag              written by a healthy new app: its version
    //   atualizado.txt               tells the new app to show the "atualizado" balloon
    //   aplicar.json / pronto.flag   the app's instructions to the helper, and the helper's "ready" answer
    //   aplicador.lock               held by the running helper
    //   atualizacao.log              the update journal
    //
    // Nothing is trusted just because it is there: every staged file is re-verified (signature, version,
    // size, SHA-256) before it is run, and the user profile is never used by the updater.
    public static class UpdateStore
    {
        public const string DirName = "atualizacao";
        public const string DownloadingName = "SenseMonitor.baixando";
        public const string StagedName = "SenseMonitor.novo.exe";
        public const string HelperName = "SenseMonitor.aplicador.exe";
        public const string FailedName = "SenseMonitor.falhou.exe";
        public const string PreviousName = "SenseMonitor.anterior.exe";
        public const string ManifestName = "manifest.json";
        public const string SignatureName = "manifest.json.sig";

        public const string StateName = "estado.json";
        public const string OkFlagName = "iniciou-ok.flag";
        public const string UpdatedMarkerName = "atualizado.txt";
        public const string InstructionsName = "aplicar.json";
        public const string ReadyName = "pronto.flag";
        public const string LockName = "aplicador.lock";
        public const string LogName = "atualizacao.log";
        public const string LogDirName = "logs";

        // ---- staging (Program Files) ---------------------------------------------------------------

        public static string StageDir
        {
            get { return Path.Combine(InstallPaths.InstallDir, DirName); }
        }

        public static string PathOf(string name)
        {
            return Path.Combine(StageDir, name);
        }

        public static string PreviousExe
        {
            get { return Path.Combine(InstallPaths.InstallDir, PreviousName); }
        }

        public static string HelperExe
        {
            get { return PathOf(HelperName); }
        }

        // The staging folder when it is safe to use (exists or is created, no reparse points, admin-only
        // ACL on it and on the install folder); null and a reason otherwise.
        public static string SecureStageDir(bool create, out string reason)
        {
            reason = null;
            try
            {
                string install = InstallPaths.InstallDir;
                if (!Directory.Exists(install)) { reason = "o app não está instalado em Arquivos de Programas"; return null; }
                if (IsReparse(install)) { reason = "a pasta de instalação é um link"; return null; }
                reason = AclProblem(install);
                if (reason != null) return null;
                string dir = StageDir;
                if (!Directory.Exists(dir))
                {
                    if (!create) { reason = "pasta de atualização ainda não existe"; return null; }
                    Directory.CreateDirectory(dir);
                }
                if (IsReparse(dir)) { reason = "a pasta de atualização é um link"; return null; }
                reason = AclProblem(dir);
                if (reason != null) return null;
                return dir;
            }
            catch (Exception ex)
            {
                reason = "pasta de atualização inacessível (" + ex.GetType().Name + ")";
                return null;
            }
        }

        // ---- data (ProgramData) --------------------------------------------------------------------

        public static string DataRoot
        {
            get { return UserData.Common; }
        }

        public static string DataDir
        {
            get { return Path.Combine(DataRoot, DirName); }
        }

        public static string DataPathOf(string name)
        {
            return Path.Combine(DataDir, name);
        }

        // Only this subfolder lets non-administrators read (the journal); the lock, the state and the
        // handshake files in DataDir itself stay administrators/SYSTEM only, because read access also lets a
        // process open a file with no sharing and make the elevated side fail on it.
        public static string LogDir
        {
            get { return Path.Combine(DataDir, LogDirName); }
        }

        // Notes about folders that were set aside (renamed, never touched) and replaced; written to the journal as soon as
        // the journal's own folder is usable (the journal cannot be used while its folder is being replaced).
        private static readonly List<string> pendingNotes = new List<string>();

        internal static List<string> TakePendingNotes()
        {
            lock (pendingNotes)
            {
                List<string> l = new List<string>(pendingNotes);
                pendingNotes.Clear();
                return l;
            }
        }

        private static void Note(string n)
        {
            if (n == null) return;
            lock (pendingNotes) { pendingNotes.Add(n); }
        }

        private static string Level(string path, bool usersCanRead, bool create, out string reason)
        {
            string note;
            string r = AdminFolder.Ensure(path, usersCanRead, create, out reason, out note);
            Note(note);
            return r;
        }

        // The data folder when it is safe: both levels pass AdminFolder (not a link, owner Administrators/SYSTEM,
        // DACL exactly SYSTEM + Administrators, all checked BY HANDLE; a folder somebody else created first, or whose
        // owner or DACL is wrong, is renamed aside untouched and a fresh one is created with the descriptor applied at
        // creation, when we are allowed to create; verified only otherwise; nothing is ever repaired or emptied in place).
        public static string SecureDataDir(bool create, out string reason)
        {
            reason = null;
            string root = Level(DataRoot, false, create, out reason);
            if (root == null) { reason = "pasta de dados da atualização: " + reason; return null; }
            string dir = Level(DataDir, false, create, out reason);
            if (dir == null) { reason = "pasta de dados da atualização: " + reason; return null; }
            return dir;
        }

        // The journal folder: the data folder (verified first) plus its own "logs" subfolder, which also gives Users
        // read-only. The lock, the state and the handshake files in DataDir itself stay administrators/SYSTEM only,
        // because read access also lets a process open a file with no sharing and make the elevated side fail.
        public static string SecureLogDir(bool create, out string reason)
        {
            if (SecureDataDir(create, out reason) == null) return null;
            string d = Level(LogDir, true, create, out reason);
            if (d == null) { reason = "pasta do histórico: " + reason; return null; }
            return d;
        }

        private static bool IsReparse(string dir)
        {
            return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
        }

        // null = the folder can only be written by administrators / SYSTEM / TrustedInstaller.
        public static string AclProblem(string dir)
        {
            try
            {
                DirectorySecurity ds = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                IdentityReference owner = ds.GetOwner(typeof(SecurityIdentifier));
                List<AclEntry> entries = new List<AclEntry>();
                foreach (FileSystemAccessRule r in ds.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (r.AccessControlType != AccessControlType.Allow) continue;
                    // Inherit-only entries count too: they decide who can write the CHILDREN.
                    bool modifies = StagingAcl.AllowsModify((int)r.FileSystemRights);
                    entries.Add(new AclEntry(r.IdentityReference.Value, modifies));
                }
                return StagingAcl.Check(owner == null ? null : owner.Value, entries);
            }
            catch (Exception ex)
            {
                return "não foi possível ler as permissões da pasta (" + ex.GetType().Name + ")";
            }
        }

        // ---- state (in the data folder) --------------------------------------------------------------

        // The main file; when it is damaged, the previous copy (a rolled-back release must stay blocked
        // even if estado.json was corrupted); only when neither is readable, a fresh state.
        public static UpdateState LoadState(string dir)
        {
            try
            {
                string p = Path.Combine(dir, StateName);
                UpdateState s;
                if (File.Exists(p) && UpdateState.TryParse(File.ReadAllBytes(p), out s)) return s;
                string bak = p + ".bak";
                if (File.Exists(bak) && UpdateState.TryParse(File.ReadAllBytes(bak), out s)) return s;
            }
            catch (Exception)
            {
            }
            return new UpdateState();
        }

        public static void SaveState(string dir, UpdateState s)
        {
            try
            {
                string p = Path.Combine(dir, StateName);
                string tmp = p + ".tmp";
                File.WriteAllBytes(tmp, s.ToBytes());
                UpdateState current;
                // keep the last readable copy as .bak before replacing it
                if (File.Exists(p) && UpdateState.TryParse(File.ReadAllBytes(p), out current)) File.Copy(p, p + ".bak", true);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch (Exception ex)
            {
                UpdateLog.Append("não foi possível gravar o estado da atualização (" + ex.GetType().Name + ")");
            }
        }

        // ---- flags and markers (data folder; every use re-verifies the folder) -----------------------

        public static bool WriteOkFlag(string version)
        {
            return WriteText(OkFlagName, version);
        }

        public static void ClearOkFlag()
        {
            Remove(OkFlagName);
        }

        public static bool OkFlagMatches(string version)
        {
            return ReadText(OkFlagName) == version;
        }

        public static bool WriteUpdatedMarker(string version)
        {
            return WriteText(UpdatedMarkerName, version);
        }

        public static string ReadUpdatedMarker()
        {
            return ReadText(UpdatedMarkerName);
        }

        public static void ClearUpdatedMarker()
        {
            Remove(UpdatedMarkerName);
        }

        public static bool WriteText(string name, string text)
        {
            return WriteBytes(name, new UTF8Encoding(false).GetBytes(text));
        }

        public static bool WriteBytes(string name, byte[] data)
        {
            string reason;
            string dir = SecureDataDir(true, out reason);
            if (dir == null) return false;
            try
            {
                string p = Path.Combine(dir, name);
                string tmp = p + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string ReadText(string name)
        {
            byte[] b = ReadBytes(name, 64);
            return b == null ? null : Encoding.UTF8.GetString(b).Trim();
        }

        public static byte[] ReadBytes(string name, int maxBytes)
        {
            string reason;
            string dir = SecureDataDir(false, out reason);
            if (dir == null) return null;
            try
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) return null;
                if (new FileInfo(p).Length > maxBytes) return null;
                return File.ReadAllBytes(p);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Remove(string name)
        {
            string reason;
            string dir = SecureDataDir(false, out reason);
            if (dir == null) return;
            try
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception)
            {
            }
        }

        // Leftovers of an interrupted download, of a rolled-back release and of an earlier helper copy
        // (that one is deleted only when no process is using it: a running image cannot be deleted).
        public static void CleanLeftovers(string stageDir)
        {
            string[] names = { DownloadingName, FailedName, HelperName };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    string p = Path.Combine(stageDir, names[i]);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // The update journal: %ProgramData%\SenseMonitor\atualizacao\atualizacao.log, in the admin-only data
    // folder (verified at every line; the user profile is never touched). Names and profile paths are
    // masked like erro.log. If the folder is not usable the line goes to Fallback (the app's erro.log).
    public static class UpdateLog
    {
        private const long MaxBytes = 128 * 1024;
        private static volatile bool disabled;

        // Set by the app (Program.LogLine); used only when the admin-only folder cannot be.
        public static Action<string> Fallback;

        // The uninstaller removes the folder the journal lives in: no more lines after that.
        public static void Disable()
        {
            disabled = true;
        }

        public static string FilePath
        {
            get { return Path.Combine(UpdateStore.LogDir, UpdateStore.LogName); }
        }

        public static void Append(string line)
        {
            if (disabled || string.IsNullOrEmpty(line)) return;
            string scrubbed = LogText.ScrubForCurrentUser(line);
            try
            {
                string reason;
                string dir = UpdateStore.SecureLogDir(true, out reason);
                if (dir == null)
                {
                    Action<string> f = Fallback;
                    if (f != null) f("atualização: " + scrubbed + " [diário seguro indisponível: " + reason + "]");
                    return;
                }
                string path = Path.Combine(dir, UpdateStore.LogName);
                foreach (string pending in UpdateStore.TakePendingNotes())
                {
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + LogText.ScrubForCurrentUser(pending) + "\r\n", Encoding.UTF8);
                }
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + scrubbed + "\r\n", Encoding.UTF8);
            }
            catch (Exception)
            {
                // nowhere else to report
            }
        }
    }
}
