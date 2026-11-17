using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Forja.Core;

namespace Forja.Platform
{
    // Real registry, always the 64-bit view (the 32-bit Run key is reached by its explicit path).
    public class RegistryAccess : IRegistryAccess
    {
        private static RegistryKey Base(RegHive hive)
        {
            return RegistryKey.OpenBaseKey(hive == RegHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        public string[] GetValueNames(RegHive hive, string subKey)
        {
            try
            {
                using (RegistryKey b = Base(hive))
                using (RegistryKey k = b.OpenSubKey(subKey, false))
                {
                    return k == null ? new string[0] : k.GetValueNames();
                }
            }
            catch (SecurityException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        public string GetString(RegHive hive, string subKey, string name)
        {
            return ReadValue(hive, subKey, name) as string;
        }

        public byte[] GetBinary(RegHive hive, string subKey, string name)
        {
            return ReadValue(hive, subKey, name) as byte[];
        }

        private static object ReadValue(RegHive hive, string subKey, string name)
        {
            try
            {
                using (RegistryKey b = Base(hive))
                using (RegistryKey k = b.OpenSubKey(subKey, false))
                {
                    return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                }
            }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void SetBinary(RegHive hive, string subKey, string name, byte[] data)
        {
            using (RegistryKey b = Base(hive))
            using (RegistryKey k = b.OpenSubKey(subKey, true) ?? b.CreateSubKey(subKey))
            {
                if (k == null) throw new IOException("chave do registro indisponível");
                k.SetValue(name, data, RegistryValueKind.Binary);
            }
        }

        public void DeleteValue(RegHive hive, string subKey, string name)
        {
            using (RegistryKey b = Base(hive))
            using (RegistryKey k = b.OpenSubKey(subKey, true))
            {
                if (k != null) k.DeleteValue(name, false);
            }
        }
    }

    // Lists what starts with Windows and flips the same switch the Task Manager uses
    // (StartupApproved). It never deletes a Run entry or a shortcut.
    public class StartupManager : IStartupService
    {
        private readonly IRegistryAccess reg;
        private readonly string userFolder;
        private readonly string commonFolder;
        private readonly ChangeLog log;
        private readonly Func<string, string> publisherOf;
        private readonly Func<string, string> resolveShortcut;
        private readonly Func<long> fileTimeNow;

        public StartupManager(IRegistryAccess reg, string userFolder, string commonFolder, ChangeLog log,
            Func<string, string> publisherOf, Func<long> fileTimeNow)
            : this(reg, userFolder, commonFolder, log, publisherOf, null, fileTimeNow)
        {
        }

        public StartupManager(IRegistryAccess reg, string userFolder, string commonFolder, ChangeLog log,
            Func<string, string> publisherOf, Func<string, string> resolveShortcut, Func<long> fileTimeNow)
        {
            if (reg == null) throw new ArgumentNullException("reg");
            this.reg = reg;
            this.userFolder = userFolder;
            this.commonFolder = commonFolder;
            this.log = log;
            this.publisherOf = publisherOf;
            this.resolveShortcut = resolveShortcut;
            this.fileTimeNow = fileTimeNow ?? delegate { return DateTime.UtcNow.ToFileTimeUtc(); };
        }

        public static StartupManager CreateDefault(ChangeLog log)
        {
            return new StartupManager(new RegistryAccess(),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                log, PublisherFromFile, ResolveShortcutTarget, null);
        }

        // ---- listing ----

        public List<StartupItem> List()
        {
            List<StartupItem> items = new List<StartupItem>();
            AddRun(items, StartupSource.UserRun);
            AddRun(items, StartupSource.MachineRun);
            AddRun(items, StartupSource.MachineRun32);
            AddFolder(items, StartupSource.UserFolder, userFolder);
            AddFolder(items, StartupSource.CommonFolder, commonFolder);
            items.Sort(delegate(StartupItem a, StartupItem b)
            {
                int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : a.Source.CompareTo(b.Source);
            });
            return items;
        }

        private void AddRun(List<StartupItem> items, StartupSource source)
        {
            RegHive hive = StartupModel.HiveOf(source);
            string runKey = StartupModel.RunSubKey(source);
            foreach (string name in reg.GetValueNames(hive, runKey))
            {
                if (string.IsNullOrEmpty(name)) continue; // the unnamed default value is not an entry
                string command = reg.GetString(hive, runKey, name) ?? "";
                StartupItem it = NewItem(source, name, name, command, StartupModel.ExtractExePath(command));
                items.Add(it);
            }
        }

        private void AddFolder(List<StartupItem> items, StartupSource source, string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (string f in files)
            {
                string file = Path.GetFileName(f);
                if (string.Equals(file, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string exe = f;
                if (resolveShortcut != null && f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = null;
                    try { target = resolveShortcut(f); }
                    catch (Exception) { }
                    if (!string.IsNullOrEmpty(target)) exe = target;
                }
                items.Add(NewItem(source, file, Path.GetFileNameWithoutExtension(file), f, exe));
            }
        }

        private StartupItem NewItem(StartupSource source, string name, string display, string command, string exePath)
        {
            StartupItem it = new StartupItem();
            it.Source = source;
            it.Name = name;
            it.DisplayName = display;
            it.Command = command;
            it.ExePath = exePath;
            it.Id = source + "|" + name;
            it.Approved = reg.GetBinary(StartupModel.HiveOf(source), StartupModel.ApprovedSubKey(source), name);
            it.Enabled = StartupModel.IsEnabled(it.Approved);
            it.RequiresAdmin = StartupModel.RequiresAdmin(source);
            string pub = null;
            if (publisherOf != null && exePath != null)
            {
                try { pub = publisherOf(exePath); }
                catch (Exception) { }
            }
            it.Publisher = string.IsNullOrEmpty(pub) ? null : pub;
            it.IsVendor = StartupModel.IsVendorPublisher(it.Publisher);
            return it;
        }

        // ---- changing ----

        public StartupChangeResult SetEnabled(StartupItem item, bool enable)
        {
            if (item == null) throw new ArgumentNullException("item");
            RegHive hive = StartupModel.HiveOf(item.Source);
            string key = StartupModel.ApprovedSubKey(item.Source);
            string label = item.DisplayName;
            try
            {
                byte[] previous = reg.GetBinary(hive, key, item.Name);
                if (StartupModel.IsEnabled(previous) == enable)
                {
                    return new StartupChangeResult(true, "«" + label + "» já estava " + (enable ? "ativado." : "desativado."));
                }
                byte[] next = StartupModel.BuildApproved(enable, fileTimeNow());
                reg.SetBinary(hive, key, item.Name, next);
                byte[] check = reg.GetBinary(hive, key, item.Name);
                if (!SameBytes(check, next))
                {
                    return new StartupChangeResult(false, "O Windows não guardou a alteração em «" + label + "». Nada foi mudado; tente de novo.");
                }
                item.Approved = next;
                item.Enabled = enable;
                string message = enable
                    ? "«" + label + "» volta a abrir com o Windows."
                    : "«" + label + "» não abre mais com o Windows. O programa continua instalado.";
                string undo = StartupModel.EncodeUndo(hive, key, item.Name, previous, next);
                string desc = (enable ? "Reativado na inicialização: " : "Desativado na inicialização: ") + label;
                try
                {
                    if (log != null) log.Record(ChangeKind.Inicializacao, desc, undo);
                }
                catch (Exception ex)
                {
                    // the registry change already happened: say so instead of "nothing changed"
                    message += " A alteração foi feita, mas o histórico não pôde ser atualizado (" + ex.Message + "). Anote-a.";
                }
                return new StartupChangeResult(true, message);
            }
            catch (UnauthorizedAccessException)
            {
                return Denied(label);
            }
            catch (SecurityException)
            {
                return Denied(label);
            }
            catch (Exception ex)
            {
                return new StartupChangeResult(false, "Não foi possível alterar «" + label + "» (" + ex.Message + "). Nada foi mudado.");
            }
        }

        private static StartupChangeResult Denied(string label)
        {
            return new StartupChangeResult(false, "O Windows negou a alteração em «" + label + "». Abra o app como administrador e tente de novo.");
        }

        // Puts back the exact bytes (or removes the StartupApproved value that did not exist).
        public StartupChangeResult Undo(ChangeEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.Kind != ChangeKind.Inicializacao)
                return new StartupChangeResult(false, "Esta entrada não é de inicialização.");
            if (entry.Undone)
                return new StartupChangeResult(false, "Esta alteração já foi desfeita.");
            StartupUndo u = StartupModel.DecodeUndo(entry.UndoData);
            if (u == null)
                return new StartupChangeResult(false, "Esta entrada não tem dados para desfazer.");
            if (!StartupModel.IsAllowedUndoTarget(u.Hive, u.SubKey))
                return new StartupChangeResult(false, "Esta entrada aponta para um lugar do Registro que o app não altera. Nada foi mudado.");
            if (u.Written == null)
                return new StartupChangeResult(false, "Esta entrada é de uma versão antiga do app e não dá para confirmar o estado atual do item. Mude a inicialização pelo Gerenciador de Tarefas.");
            try
            {
                byte[] current = reg.GetBinary(u.Hive, u.SubKey, u.Name);
                if (!SameBytes(current, u.Written))
                    return new StartupChangeResult(false, "Esse item foi alterado depois; desfazer agora poderia apagar a mudança mais recente. Nada foi mudado.");
                if (u.Previous == null) reg.DeleteValue(u.Hive, u.SubKey, u.Name);
                else reg.SetBinary(u.Hive, u.SubKey, u.Name, u.Previous);
                byte[] check = reg.GetBinary(u.Hive, u.SubKey, u.Name);
                bool restored = u.Previous == null ? check == null : SameBytes(check, u.Previous);
                if (!restored)
                    return new StartupChangeResult(false, "O Windows não confirmou a volta de «" + u.Name + "» ao estado anterior. Confira o item no Gerenciador de Tarefas e tente de novo.");
            }
            catch (UnauthorizedAccessException)
            {
                return Denied(u.Name);
            }
            catch (SecurityException)
            {
                return Denied(u.Name);
            }
            catch (Exception ex)
            {
                return new StartupChangeResult(false, "Não foi possível desfazer (" + ex.Message + "). Confira o item no Gerenciador de Tarefas.");
            }
            // The registry is back to what it was. A problem with the history file does not undo that.
            string done = "Alteração desfeita: «" + u.Name + "» voltou ao estado anterior.";
            try
            {
                if (log != null && !log.MarkUndone(entry.Id))
                    done += " O histórico não encontrou a entrada para marcá-la como desfeita.";
            }
            catch (Exception ex)
            {
                done += " O histórico não pôde ser atualizado (" + ex.Message + ").";
            }
            return new StartupChangeResult(true, done);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // ---- scheduled tasks: read only, slow (4 to 8 s), call from a background thread ----

        public List<LogonTask> ListLogonTasks()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(InstallPaths.System32("schtasks.exe"), "/Query /XML ONE");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                using (Process p = Process.Start(psi))
                {
                    string err = null;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { err = p.StandardError.ReadToEnd(); } catch (Exception) { } });
                    string xml = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return new List<LogonTask>();
                    }
                    GC.KeepAlive(err);
                    return StartupModel.ParseTasksXml(xml);
                }
            }
            catch (Exception)
            {
                return new List<LogonTask>();
            }
        }

        // ---- real-machine helpers ----

        private static string PublisherFromFile(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;
                return FileVersionInfo.GetVersionInfo(exePath).CompanyName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Shortcut target through the Windows scripting host, late bound (no extra reference).
        private static string ResolveShortcutTarget(string lnkPath)
        {
            object shell = null;
            object link = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                string target = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (link != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
                if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }
}
