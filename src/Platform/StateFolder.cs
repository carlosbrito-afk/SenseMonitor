using System;
using System.IO;
using Forja.Core;

namespace Forja.Platform
{
    // Prepares a folder the elevated app may trust. A small interface so a test can stand in for the real folder.
    public interface IAdminFolder
    {
        // The verified path (and its parent verified too), or null = refuse the feature.
        string Prepare(string path, IJournal journal);
    }

    // %ProgramData%\SenseMonitor\estado: where the files the elevated app TRUSTS live (the fan marker, the session flag, the
    // mode-test marker, the service undo records). ONE rule for %ProgramData%\SenseMonitor, shared with the updater
    // (Platform\AdminFolder): a level that fails verification is renamed aside (never followed, never opened), a fresh
    // folder is created with the protected descriptor applied at creation, and owner and DACL are verified BY HANDLE; the
    // verified folder is then held open without delete sharing, so neither feature can rename the other's folder aside.
    // The state folder is created for administrators and SYSTEM only (no Users read: nothing in it is for the user).
    public static class StateFolder
    {
        public static IAdminFolder Impl = new AdminFolderAdapter();

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SenseMonitor", "estado"); }
        }

        // A path that was refused is not retried by this process: every retry would rename another folder aside and fill
        // %ProgramData% with renamed entries. A success is verified again every time (the folder can change under us).
        private static readonly object refusedSync = new object();
        private static readonly System.Collections.Generic.HashSet<string> refused =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string Prepare(string path, IJournal journal)
        {
            lock (refusedSync)
            {
                if (refused.Contains(path ?? "")) return null;
            }
            string result = null;
            try
            {
                result = Impl.Prepare(path, journal ?? NullJournal.Instance);
            }
            catch (Exception ex)
            {
                (journal ?? NullJournal.Instance).Write(JournalKind.Config, "pasta de estado não pôde ser verificada (" + ex.GetType().Name + ")");
            }
            if (result == null)
            {
                lock (refusedSync) { refused.Add(path ?? ""); }
            }
            return result;
        }
    }

    // The thin adapter: both levels (%ProgramData%\SenseMonitor, then estado) go through AdminFolder, the updater's
    // implementation of the rule. Folders are only CREATED (or a hostile one replaced) when this process is elevated.
    public sealed class AdminFolderAdapter : IAdminFolder
    {
        public string Prepare(string path, IJournal journal)
        {
            bool create = Cleaner.IsElevated();
            string parent = Path.GetDirectoryName(path);
            string[] levels = string.IsNullOrEmpty(parent) ? new string[] { path } : new string[] { parent, path };
            foreach (string level in levels)
            {
                string reason;
                string note;
                string r = AdminFolder.Ensure(level, false, create, out reason, out note);
                if (note != null) journal.Write(JournalKind.Config, note);
                if (r == null)
                {
                    journal.Write(JournalKind.Config, "pasta de estado " + Path.GetFileName(level) + ": " + reason + "; recurso recusado");
                    return null;
                }
            }
            return path;
        }
    }
}
