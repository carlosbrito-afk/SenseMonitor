using System;
using System.Collections.Generic;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    // In-memory registry. Keys are case-insensitive, like the real one.
    public class FakeRegistry : IRegistryAccess
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Writes = new List<string>();
        public readonly List<string> Deletes = new List<string>();
        public Exception ThrowOnWrite;
        public bool IgnoreWrites;   // the write returns normally but nothing changes

        private static string K(RegHive hive, string key, string name)
        {
            return hive + "|" + key + "|" + name;
        }

        public void PutString(RegHive hive, string key, string name, string value)
        {
            values[K(hive, key, name)] = value;
        }

        public void PutBinary(RegHive hive, string key, string name, byte[] value)
        {
            values[K(hive, key, name)] = value;
        }

        public string[] GetValueNames(RegHive hive, string subKey)
        {
            string prefix = hive + "|" + subKey + "|";
            List<string> names = new List<string>();
            foreach (string k in values.Keys)
            {
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) names.Add(k.Substring(prefix.Length));
            }
            return names.ToArray();
        }

        public string GetString(RegHive hive, string subKey, string name)
        {
            object o;
            return values.TryGetValue(K(hive, subKey, name), out o) ? o as string : null;
        }

        public byte[] GetBinary(RegHive hive, string subKey, string name)
        {
            object o;
            byte[] b = values.TryGetValue(K(hive, subKey, name), out o) ? o as byte[] : null;
            return b == null ? null : (byte[])b.Clone();
        }

        public void SetBinary(RegHive hive, string subKey, string name, byte[] data)
        {
            if (ThrowOnWrite != null) throw ThrowOnWrite;
            Writes.Add(K(hive, subKey, name));
            if (IgnoreWrites) return;
            values[K(hive, subKey, name)] = (byte[])data.Clone();
        }

        public void DeleteValue(RegHive hive, string subKey, string name)
        {
            if (ThrowOnWrite != null) throw ThrowOnWrite;
            Deletes.Add(K(hive, subKey, name));
            if (IgnoreWrites) return;
            values.Remove(K(hive, subKey, name));
        }
    }

    public static class StartupTests
    {
        private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ApprRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
        private const string ApprFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        private static byte[] Approved(byte first)
        {
            byte[] b = new byte[12];
            b[0] = first;
            return b;
        }

        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static StartupManager NewManager(FakeRegistry reg, ChangeLog log, string userFolder, string commonFolder)
        {
            return new StartupManager(reg, userFolder, commonFolder, log,
                delegate(string exe) { return exe != null && exe.EndsWith("nvidia.exe", StringComparison.OrdinalIgnoreCase) ? "NVIDIA Corporation" : (exe != null && exe.EndsWith("app.exe", StringComparison.OrdinalIgnoreCase) ? "Acme Ltda" : null); },
                delegate { return 0x01DC000000000000L; });
        }

        // ---- pure model ----

        [Test]
        public static void IsEnabled_FollowsFirstByte()
        {
            Assert.True(StartupModel.IsEnabled(null), "missing value means enabled");
            Assert.True(StartupModel.IsEnabled(new byte[0]), "empty means enabled");
            Assert.True(StartupModel.IsEnabled(Approved(0x02)), "02 enabled");
            Assert.True(StartupModel.IsEnabled(Approved(0x06)), "06 enabled");
            Assert.True(!StartupModel.IsEnabled(Approved(0x03)), "03 disabled");
        }

        [Test]
        public static void BuildApproved_Disable_Has03AndFileTime()
        {
            byte[] b = StartupModel.BuildApproved(false, 0x0102030405060708L);
            Assert.Equal(12, b.Length);
            Assert.Equal((byte)0x03, b[0]);
            Assert.Equal((byte)0, b[1]);
            Assert.Equal((byte)0, b[3]);
            Assert.Equal((byte)0x08, b[4]);
            Assert.Equal((byte)0x01, b[11]);
        }

        [Test]
        public static void BuildApproved_Enable_Has02AndZeroedTime()
        {
            byte[] b = StartupModel.BuildApproved(true, 0x0102030405060708L);
            Assert.Equal(12, b.Length);
            Assert.Equal((byte)0x02, b[0]);
            for (int i = 1; i < 12; i++) Assert.Equal((byte)0, b[i]);
        }

        [Test]
        public static void ExtractExePath_HandlesQuotesArgsAndEnv()
        {
            Assert.Equal(@"C:\Program Files\Foo\foo.exe", StartupModel.ExtractExePath("\"C:\\Program Files\\Foo\\foo.exe\" --minimized"));
            Assert.Equal(@"C:\Program Files\Foo\foo.exe", StartupModel.ExtractExePath(@"C:\Program Files\Foo\foo.exe -min"));
            Assert.Equal(@"C:\Tools\a.exe", StartupModel.ExtractExePath(@"C:\Tools\a.exe"));
            Assert.Equal("rundll32.exe", StartupModel.ExtractExePath("rundll32.exe shell32.dll,Foo"));
            string sys = Environment.GetEnvironmentVariable("SystemRoot");
            Assert.Equal(sys + @"\system32\x.exe", StartupModel.ExtractExePath("%SystemRoot%\\system32\\x.exe /s"));
            Assert.Null(StartupModel.ExtractExePath(""));
            Assert.Null(StartupModel.ExtractExePath(null));
        }

        [Test]
        public static void UndoData_RoundTrips_WithPreviousBytes()
        {
            byte[] prev = new byte[] { 0x06, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            byte[] written = new byte[] { 0x03, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1 };
            string s = StartupModel.EncodeUndo(RegHive.LocalMachine, ApprRun32, "My;App|x \u00e7", prev, written);
            StartupUndo u = StartupModel.DecodeUndo(s);
            Assert.True(u != null, "decoded");
            Assert.Equal(RegHive.LocalMachine, u.Hive);
            Assert.Equal(ApprRun32, u.SubKey);
            Assert.Equal("My;App|x \u00e7", u.Name);
            Assert.Equal(12, u.Previous.Length);
            Assert.Equal((byte)0x06, u.Previous[0]);
            Assert.Equal((byte)8, u.Previous[11]);
            Assert.True(u.Written != null && u.Written.Length == 12, "written bytes round trip");
            Assert.Equal((byte)0x03, u.Written[0]);
            Assert.Equal((byte)1, u.Written[11]);
        }

        [Test]
        public static void DecodeUndo_OldV1Format_HasNoWrittenBytes()
        {
            StartupUndo u = StartupModel.DecodeUndo("v1;HKCU;" + Uri.EscapeDataString(ApprRun) + ";A;020000000000000000000000");
            Assert.True(u != null, "v1 still decodes");
            Assert.Null(u.Written);
            Assert.Equal((byte)2, u.Previous[0]);
        }

        [Test]
        public static void UndoData_NoPrevious_RoundTripsAsNull()
        {
            StartupUndo u = StartupModel.DecodeUndo(StartupModel.EncodeUndo(RegHive.CurrentUser, ApprRun, "A", null, Approved(0x03)));
            Assert.True(u != null, "decoded");
            Assert.Null(u.Previous);
        }

        [Test]
        public static void DecodeUndo_Garbage_ReturnsNull()
        {
            Assert.Null(StartupModel.DecodeUndo("lixo"));
            Assert.Null(StartupModel.DecodeUndo(null));
            Assert.Null(StartupModel.DecodeUndo("v1;XXX;k;n;-"));
        }

        [Test]
        public static void IsVendorPublisher_FlagsMicrosoftAndDrivers()
        {
            Assert.True(StartupModel.IsVendorPublisher("Microsoft Corporation"), "ms");
            Assert.True(StartupModel.IsVendorPublisher("NVIDIA Corporation"), "nvidia");
            Assert.True(StartupModel.IsVendorPublisher("Realtek Semiconductor"), "realtek");
            Assert.True(!StartupModel.IsVendorPublisher("Acme Ltda"), "other");
            Assert.True(!StartupModel.IsVendorPublisher(null), "null");
        }

        private const string TasksXml = @"<?xml version=""1.0""?>
<Tasks>
<!-- \Foo\Logon Task -->
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Settings><Enabled>true</Enabled></Settings>
  <Actions Context=""Author""><Exec><Command>C:\Foo\foo.exe</Command><Arguments>-x</Arguments></Exec></Actions>
</Task>
<!-- \Microsoft\Windows\Bar\Disabled -->
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Settings><Enabled>false</Enabled></Settings>
  <Actions Context=""Author""><Exec><Command>%windir%\bar.exe</Command></Exec></Actions>
</Task>
<!-- \Baz\Timed -->
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><TimeTrigger><StartBoundary>2026-10-12T22:11:00</StartBoundary></TimeTrigger></Triggers>
  <Actions Context=""Author""><Exec><Command>C:\baz.exe</Command></Exec></Actions>
</Task>
<!-- \Qux\TriggerOff -->
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>
  <Actions Context=""Author""><ComHandler><ClassId>{1}</ClassId></ComHandler></Actions>
</Task>
</Tasks>";

        [Test]
        public static void ParseTasksXml_KeepsOnlyLogonTasks()
        {
            List<LogonTask> t = StartupModel.ParseTasksXml(TasksXml);
            Assert.Equal(3, t.Count);
            LogonTask foo = t.Find(delegate(LogonTask x) { return x.Name == @"\Foo\Logon Task"; });
            Assert.True(foo != null && foo.Enabled && !foo.IsSystem, "foo enabled, not system");
            Assert.Equal(@"C:\Foo\foo.exe", foo.Command);
            LogonTask bar = t.Find(delegate(LogonTask x) { return x.Name.EndsWith("Disabled"); });
            Assert.True(bar != null && !bar.Enabled && bar.IsSystem, "bar disabled, system");
            LogonTask qux = t.Find(delegate(LogonTask x) { return x.Name.EndsWith("TriggerOff"); });
            Assert.True(qux != null && !qux.Enabled, "trigger disabled counts as disabled");
        }

        [Test]
        public static void ParseTasksXml_Garbage_ReturnsEmpty()
        {
            Assert.Equal(0, StartupModel.ParseTasksXml("<Tasks><oops").Count);
            Assert.Equal(0, StartupModel.ParseTasksXml("").Count);
            Assert.Equal(0, StartupModel.ParseTasksXml(null).Count);
        }

        // ---- manager over a fake registry ----

        [Test]
        public static void List_UnionsRunWithApproved_AndIgnoresOrphans()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "\"C:\\Apps\\app.exe\" -tray");
            reg.PutString(RegHive.CurrentUser, Run, "Beta", "C:\\Apps\\beta.exe");
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Beta", Approved(0x03));
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Orphan", Approved(0x03));
            reg.PutString(RegHive.LocalMachine, Run, "Gamma", "C:\\Drivers\\nvidia.exe");
            reg.PutBinary(RegHive.LocalMachine, ApprRun, "Gamma", Approved(0x06));
            reg.PutString(RegHive.LocalMachine, Run32, "Delta", "C:\\Old\\delta.exe");
            reg.PutBinary(RegHive.LocalMachine, ApprRun32, "Delta", Approved(0x02));
            StartupManager m = NewManager(reg, new ChangeLog(new MemoryChangeLogStore()), null, null);
            List<StartupItem> items = m.List();
            Assert.Equal(4, items.Count);
            StartupItem alpha = items.Find(delegate(StartupItem i) { return i.Name == "Alpha"; });
            StartupItem beta = items.Find(delegate(StartupItem i) { return i.Name == "Beta"; });
            StartupItem gamma = items.Find(delegate(StartupItem i) { return i.Name == "Gamma"; });
            StartupItem delta = items.Find(delegate(StartupItem i) { return i.Name == "Delta"; });
            Assert.True(alpha.Enabled, "no approved entry means enabled");
            Assert.Equal("Acme Ltda", alpha.Publisher);
            Assert.Equal(@"C:\Apps\app.exe", alpha.ExePath);
            Assert.True(!alpha.RequiresAdmin, "hkcu no admin");
            Assert.True(!beta.Enabled, "03 means disabled");
            Assert.True(gamma.Enabled, "06 means enabled");
            Assert.True(gamma.RequiresAdmin, "hklm needs admin");
            Assert.True(gamma.IsVendor, "nvidia flagged");
            Assert.Equal(StartupSource.MachineRun32, delta.Source);
            Assert.True(delta.Enabled, "run32 enabled");
        }

        [Test]
        public static void List_ReadsStartupFolders_WithApprovedState()
        {
            string user = TempDir();
            string common = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(user, "App.lnk"), "x");
                File.WriteAllText(Path.Combine(user, "desktop.ini"), "x");
                File.WriteAllText(Path.Combine(common, "Shared.bat"), "x");
                FakeRegistry reg = new FakeRegistry();
                reg.PutBinary(RegHive.CurrentUser, ApprFolder, "App.lnk", Approved(0x03));
                StartupManager m = NewManager(reg, new ChangeLog(new MemoryChangeLogStore()), user, common);
                List<StartupItem> items = m.List();
                Assert.Equal(2, items.Count);
                StartupItem app = items.Find(delegate(StartupItem i) { return i.Name == "App.lnk"; });
                StartupItem shared = items.Find(delegate(StartupItem i) { return i.Name == "Shared.bat"; });
                Assert.Equal(StartupSource.UserFolder, app.Source);
                Assert.True(!app.Enabled, "app.lnk disabled");
                Assert.Equal("App", app.DisplayName);
                Assert.Equal(StartupSource.CommonFolder, shared.Source);
                Assert.True(shared.Enabled && shared.RequiresAdmin, "common folder: enabled, admin");
            }
            finally { Directory.Delete(user, true); Directory.Delete(common, true); }
        }

        [Test]
        public static void SetEnabled_Disable_WritesApprovedAndRecordsPreviousBytes()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Alpha", new byte[] { 0x02, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 });
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            StartupItem item = m.List()[0];

            StartupChangeResult r = m.SetEnabled(item, false);
            Assert.True(r.Ok, "ok: " + r.Message);
            byte[] now = reg.GetBinary(RegHive.CurrentUser, ApprRun, "Alpha");
            Assert.Equal((byte)0x03, now[0]);
            Assert.Equal(12, now.Length);
            Assert.Equal((byte)0x01, now[11]);
            // the Run entry itself is untouched
            Assert.Equal("C:\\Apps\\app.exe", reg.GetString(RegHive.CurrentUser, Run, "Alpha"));
            Assert.Equal(0, reg.Deletes.Count);
            Assert.Equal(1, log.List().Count);
            ChangeEntry e = log.List()[0];
            Assert.Equal(ChangeKind.Inicializacao, e.Kind);
            Assert.True(e.CanUndo, "undoable");
            Assert.True(e.Description.Contains("Alpha"), "description names the item");
            StartupUndo u = StartupModel.DecodeUndo(e.UndoData);
            Assert.Equal((byte)0x02, u.Previous[0]);
            Assert.Equal((byte)9, u.Previous[4]);
        }

        [Test]
        public static void SetEnabled_Enable_WritesZeroedStateAndUndoRestoresExactBytes()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Beta", "C:\\Apps\\beta.exe");
            byte[] original = new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Beta", original);
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            StartupItem item = m.List()[0];
            Assert.True(!item.Enabled, "starts disabled");

            Assert.True(m.SetEnabled(item, true).Ok, "enable ok");
            byte[] now = reg.GetBinary(RegHive.CurrentUser, ApprRun, "Beta");
            Assert.Equal((byte)0x02, now[0]);
            Assert.Equal((byte)0, now[4]);

            ChangeEntry e = log.List()[0];
            StartupChangeResult u = m.Undo(e);
            Assert.True(u.Ok, "undo ok: " + u.Message);
            byte[] back = reg.GetBinary(RegHive.CurrentUser, ApprRun, "Beta");
            Assert.Equal(12, back.Length);
            for (int i = 0; i < 12; i++) Assert.Equal(original[i], back[i]);
            Assert.True(log.List()[0].Undone, "entry marked undone");
        }

        [Test]
        public static void Undo_WhenThereWasNoApprovedValue_DeletesOnlyTheApprovedValue()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            StartupItem item = m.List()[0];
            Assert.True(m.SetEnabled(item, false).Ok, "disable");
            Assert.True(m.Undo(log.List()[0]).Ok, "undo");
            Assert.Null(reg.GetBinary(RegHive.CurrentUser, ApprRun, "Alpha"));
            Assert.Equal(1, reg.Deletes.Count);
            Assert.True(reg.Deletes[0].IndexOf("StartupApproved", StringComparison.OrdinalIgnoreCase) >= 0, "approved value deleted");
            Assert.Equal("C:\\Apps\\app.exe", reg.GetString(RegHive.CurrentUser, Run, "Alpha"));
        }

        [Test]
        public static void SetEnabled_Machine_UsesLocalMachineApproved()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.LocalMachine, Run32, "Delta", "C:\\Old\\delta.exe");
            StartupManager m = NewManager(reg, new ChangeLog(new MemoryChangeLogStore()), null, null);
            StartupItem item = m.List()[0];
            Assert.True(m.SetEnabled(item, false).Ok, "ok");
            Assert.Equal((byte)0x03, reg.GetBinary(RegHive.LocalMachine, ApprRun32, "Delta")[0]);
            Assert.Null(reg.GetBinary(RegHive.CurrentUser, ApprRun32, "Delta"));
        }

        [Test]
        public static void SetEnabled_AccessDenied_FailsWithGuidanceAndNoHistory()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.LocalMachine, Run, "Gamma", "C:\\g.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            StartupItem item = m.List()[0];
            reg.ThrowOnWrite = new UnauthorizedAccessException("denied");
            StartupChangeResult r = m.SetEnabled(item, false);
            Assert.True(!r.Ok, "fails");
            Assert.True(r.Message.ToLowerInvariant().Contains("administrador"), "tells what to do");
            Assert.Equal(0, log.List().Count);
        }

        [Test]
        public static void SetEnabled_AlreadyInState_DoesNothing()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\a.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            StartupItem item = m.List()[0];
            StartupChangeResult r = m.SetEnabled(item, true);
            Assert.True(r.Ok, "ok");
            Assert.Equal(0, reg.Writes.Count);
            Assert.Equal(0, log.List().Count);
        }

        [Test]
        public static void Undo_AlreadyUndone_IsRefused()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\a.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            m.SetEnabled(m.List()[0], false);
            ChangeEntry e = log.List()[0];
            Assert.True(m.Undo(e).Ok, "first undo");
            reg.Writes.Clear();
            StartupChangeResult second = m.Undo(log.List()[0]);
            Assert.True(!second.Ok, "second undo refused");
            Assert.Equal(0, reg.Writes.Count);
        }

        [Test]
        public static void Undo_RefusesForeignEntryKinds()
        {
            FakeRegistry reg = new FakeRegistry();
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            ChangeEntry e = log.Record(ChangeKind.Limpeza, "x", "algo");
            Assert.True(!m.Undo(e).Ok, "not a startup entry");
        }

        // ---- fix round 1: undo checks the current state, verifies, and tells the truth ----

        [Test]
        public static void Undo_RefusesWhenTheItemWasChangedAfterwards()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            Assert.True(m.SetEnabled(m.List()[0], false).Ok, "disable");
            // someone enables it again from the Task Manager
            byte[] theirs = Approved(0x02);
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Alpha", theirs);
            reg.Writes.Clear();
            reg.Deletes.Clear();
            StartupChangeResult r = m.Undo(log.List()[0]);
            Assert.True(!r.Ok, "refused");
            Assert.True(r.Message.Contains("alterado depois"), "says why: " + r.Message);
            Assert.True(r.Message.Contains("mudança mais recente"), "says the risk: " + r.Message);
            Assert.Equal(0, reg.Writes.Count);
            Assert.Equal(0, reg.Deletes.Count);
            Assert.True(!log.List()[0].Undone, "entry stays undoable-looking only if state matches; not marked undone");
            Assert.Equal((byte)0x02, reg.GetBinary(RegHive.CurrentUser, ApprRun, "Alpha")[0]);
        }

        [Test]
        public static void Undo_RefusesWhenTheValueWasRemovedAfterwards()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            Assert.True(m.SetEnabled(m.List()[0], false).Ok, "disable");
            reg.DeleteValue(RegHive.CurrentUser, ApprRun, "Alpha");
            reg.Writes.Clear();
            StartupChangeResult r = m.Undo(log.List()[0]);
            Assert.True(!r.Ok, "refused when the value is gone");
            Assert.Equal(0, reg.Writes.Count);
        }

        [Test]
        public static void Undo_OldEntryWithoutWrittenBytes_IsRefusedSafely()
        {
            FakeRegistry reg = new FakeRegistry();
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            string v1 = "v1;HKCU;" + Uri.EscapeDataString(ApprRun) + ";Alpha;-";
            ChangeEntry e = log.Record(ChangeKind.Inicializacao, "Desativado na inicializa\u00e7\u00e3o: Alpha", v1);
            StartupChangeResult r = m.Undo(e);
            Assert.True(!r.Ok, "cannot confirm the current state, so no undo");
            Assert.Equal(0, reg.Writes.Count + reg.Deletes.Count);
        }

        [Test]
        public static void Undo_VerifiesByReadingBack_AndDoesNotClaimSuccessWhenItDidNotStick()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Alpha", new byte[] { 0x02, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 });
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            Assert.True(m.SetEnabled(m.List()[0], false).Ok, "disable");
            reg.IgnoreWrites = true;
            StartupChangeResult r = m.Undo(log.List()[0]);
            Assert.True(!r.Ok, "write that does not stick is a failure");
            Assert.True(!log.List()[0].Undone, "not marked undone");
        }

        [Test]
        public static void Undo_VerifiesDeletionToo()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            Assert.True(m.SetEnabled(m.List()[0], false).Ok, "disable");
            reg.IgnoreWrites = true;
            Assert.True(!m.Undo(log.List()[0]).Ok, "delete that does not stick is a failure");
        }

        [Test]
        public static void Undo_HistoryUpdateFailsAfterRestore_StillReportsTheRestore()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            reg.PutBinary(RegHive.CurrentUser, ApprRun, "Alpha", new byte[] { 0x02, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 });
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            StartupManager m = NewManager(reg, log, null, null);
            Assert.True(m.SetEnabled(m.List()[0], false).Ok, "disable");
            log.Changed += delegate { throw new InvalidOperationException("history boom"); };
            StartupChangeResult r = m.Undo(log.List()[0]);
            Assert.True(r.Ok, "the registry was restored, so this is a success: " + r.Message);
            Assert.True(!r.Message.Contains("Nada foi mudado"), "must not claim nothing changed: " + r.Message);
            Assert.True(r.Message.Contains("histórico"), "mentions the history problem: " + r.Message);
            Assert.Equal((byte)0x02, reg.GetBinary(RegHive.CurrentUser, ApprRun, "Alpha")[0]);
        }

        [Test]
        public static void SetEnabled_HistoryRecordFailsAfterWrite_DoesNotClaimNothingChanged()
        {
            FakeRegistry reg = new FakeRegistry();
            reg.PutString(RegHive.CurrentUser, Run, "Alpha", "C:\\Apps\\app.exe");
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            log.Changed += delegate { throw new InvalidOperationException("history boom"); };
            StartupManager m = NewManager(reg, log, null, null);
            StartupItem item = m.List()[0];
            StartupChangeResult r = m.SetEnabled(item, false);
            Assert.True(r.Ok, "the change was made: " + r.Message);
            Assert.True(!r.Message.Contains("Nada foi mudado"), "must not claim nothing changed: " + r.Message);
            Assert.True(r.Message.Contains("histórico"), "mentions the history problem: " + r.Message);
            Assert.Equal((byte)0x03, reg.GetBinary(RegHive.CurrentUser, ApprRun, "Alpha")[0]);
        }

        [Test]
        public static void UndoableIds_OnlyTheNewestPendingEntryPerItem()
        {
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            byte[] off = Approved(0x03);
            byte[] on = Approved(0x02);
            ChangeEntry a = log.Record(ChangeKind.Inicializacao, "A", StartupModel.EncodeUndo(RegHive.CurrentUser, ApprRun, "X", null, off));
            ChangeEntry b = log.Record(ChangeKind.Inicializacao, "B", StartupModel.EncodeUndo(RegHive.CurrentUser, ApprRun, "x", off, on));   // same item, different case
            ChangeEntry c = log.Record(ChangeKind.Inicializacao, "C", StartupModel.EncodeUndo(RegHive.CurrentUser, ApprRun, "Y", null, off));
            ChangeEntry other = log.Record(ChangeKind.Limpeza, "D", null);
            Dictionary<string, bool> ids = StartupModel.UndoableEntryIds(log.List());
            Assert.True(ids.ContainsKey(b.Id) && ids.ContainsKey(c.Id), "newest per item offered");
            Assert.True(!ids.ContainsKey(a.Id), "older entry of the same item not offered");
            Assert.True(!ids.ContainsKey(other.Id), "cleanup never");
            log.MarkUndone(b.Id);
            ids = StartupModel.UndoableEntryIds(log.List());
            Assert.True(ids.ContainsKey(a.Id), "once the newer one is undone the older becomes the newest pending");
            Assert.True(!ids.ContainsKey(b.Id), "undone entries are not offered");
        }
    }
}
