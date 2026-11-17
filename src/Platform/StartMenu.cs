using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Forja.Core;

namespace Forja.Platform
{
    // The Start Menu entries of the install (all users): the shortcut files are made through the shell's own
    // ShellLink object (no script host, no extra reference). Creating and removing return null on success or a pt-BR
    // reason. The folder must be exactly <Programs>\SenseMonitor (StartMenuShortcuts.IsOurFolder); a link in its
    // place is refused, and only the two known file names are ever deleted from it.
    public static class StartMenu
    {
        public static string CommonPrograms
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms); }
        }

        public static string Create()
        {
            return Create(CommonPrograms, InstallPaths.InstalledExe, InstallPaths.InstallDir);
        }

        public static string Remove()
        {
            return Remove(CommonPrograms);
        }

        internal static string Create(string programs, string installedExe, string workingDir)
        {
            try
            {
                string dir = StartMenuShortcuts.FolderPath(programs);
                if (!StartMenuShortcuts.IsOurFolder(dir, programs)) return "pasta do Menu Iniciar inesperada";
                if (Directory.Exists(dir))
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "a pasta do Menu Iniciar é um link; recusado";
                }
                else
                {
                    Directory.CreateDirectory(dir);
                }
                string error = null;
                // The shell object is created on its own STA thread (the caller is usually an MTA worker).
                Thread t = new Thread(delegate()
                {
                    try
                    {
                        foreach (ShortcutSpec spec in StartMenuShortcuts.Specs(installedExe))
                        {
                            string file = Path.Combine(dir, spec.FileName);
                            if (File.Exists(file)) File.Delete(file);
                            Write(file, spec, workingDir);
                            if (!File.Exists(file)) { error = "o atalho " + spec.FileName + " não foi criado"; return; }
                        }
                    }
                    catch (Exception ex)
                    {
                        error = "não foi possível criar os atalhos do Menu Iniciar (" + ex.GetType().Name + ")";
                    }
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                return error;
            }
            catch (Exception ex)
            {
                return "não foi possível criar os atalhos do Menu Iniciar (" + ex.GetType().Name + ")";
            }
        }

        // Removes the two known files and the folder when nothing else is in it.
        internal static string Remove(string programs)
        {
            try
            {
                string dir = StartMenuShortcuts.FolderPath(programs);
                if (!StartMenuShortcuts.IsOurFolder(dir, programs)) return "pasta do Menu Iniciar inesperada";
                if (!Directory.Exists(dir)) return null;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return "a pasta do Menu Iniciar é um link; deixada como está";
                foreach (ShortcutSpec spec in StartMenuShortcuts.Specs(null))
                {
                    string file = Path.Combine(dir, spec.FileName);
                    if (!StartMenuShortcuts.IsOurFile(spec.FileName)) continue;
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (Exception) { return "não foi possível remover o atalho " + spec.FileName; }
                }
                InstallSystem.TryRemoveIfEmpty(dir);
                return null;
            }
            catch (Exception ex)
            {
                return "não foi possível remover os atalhos do Menu Iniciar (" + ex.GetType().Name + ")";
            }
        }

        // ---- ShellLink (STA thread) ---------------------------------------------------------------

        private static void Write(string file, ShortcutSpec spec, string workingDir)
        {
            IShellLinkW link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(spec.Target);
                link.SetArguments(spec.Arguments ?? "");
                link.SetDescription(spec.Description ?? "");
                if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
                link.SetIconLocation(spec.Target, 0);
                ((IPersistFile)link).Save(file, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        // Reads a shortcut back (tests and the install check). Runs on its own STA thread.
        internal static bool ReadLink(string file, out string target, out string arguments)
        {
            string t = null, a = null;
            bool ok = false;
            Thread th = new Thread(delegate()
            {
                IShellLinkW link = null;
                try
                {
                    link = (IShellLinkW)new ShellLink();
                    ((IPersistFile)link).Load(file, 0);
                    StringBuilder sbT = new StringBuilder(1024);
                    link.GetPath(sbT, sbT.Capacity, IntPtr.Zero, 0);
                    StringBuilder sbA = new StringBuilder(1024);
                    link.GetArguments(sbA, sbA.Capacity);
                    t = sbT.ToString();
                    a = sbA.ToString();
                    ok = true;
                }
                catch (Exception)
                {
                    ok = false;
                }
                finally
                {
                    if (link != null) Marshal.ReleaseComObject(link);
                }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            th.Join();
            target = t;
            arguments = a;
            return ok;
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
