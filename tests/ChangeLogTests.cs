using System;
using System.Collections.Generic;
using System.IO;
using Forja.Core;
using Forja.Platform;

namespace Forja.Tests
{
    public class MemoryChangeLogStore : IChangeLogStore
    {
        public List<ChangeEntry> Saved = new List<ChangeEntry>();
        public int SaveCount;
        public bool FailSave;

        public List<ChangeEntry> Load()
        {
            return new List<ChangeEntry>(Saved);
        }

        public void Save(List<ChangeEntry> entries)
        {
            SaveCount++;
            if (FailSave) throw new IOException("disk full");
            Saved = new List<ChangeEntry>(entries);
        }
    }

    public static class ChangeLogTests
    {
        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "forja-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [Test]
        public static void Record_AssignsIdTimeAndFields()
        {
            DateTime now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore(), delegate { return now; });
            ChangeEntry e = log.Record(ChangeKind.Inicializacao, "Desativado: Foo", "undo-1");
            Assert.True(!string.IsNullOrEmpty(e.Id), "id");
            Assert.Equal(now, e.WhenUtc);
            Assert.Equal(ChangeKind.Inicializacao, e.Kind);
            Assert.Equal("Desativado: Foo", e.Description);
            Assert.Equal("undo-1", e.UndoData);
            Assert.True(!e.Undone, "not undone");
        }

        [Test]
        public static void List_IsNewestFirst()
        {
            DateTime t = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore(), delegate { return t; });
            log.Record(ChangeKind.Limpeza, "primeiro", null);
            log.Record(ChangeKind.Reparo, "segundo", null);
            log.Record(ChangeKind.PontoRestauracao, "terceiro", null);
            List<ChangeEntry> l = log.List();
            Assert.Equal(3, l.Count);
            Assert.Equal("terceiro", l[0].Description);
            Assert.Equal("segundo", l[1].Description);
            Assert.Equal("primeiro", l[2].Description);
        }

        [Test]
        public static void MarkUndone_SetsFlagAndPersists()
        {
            MemoryChangeLogStore store = new MemoryChangeLogStore();
            ChangeLog log = new ChangeLog(store);
            ChangeEntry e = log.Record(ChangeKind.Inicializacao, "x", "u");
            Assert.True(log.MarkUndone(e.Id), "mark returns true");
            Assert.True(log.List()[0].Undone, "undone in list");
            Assert.True(store.Saved[0].Undone, "undone persisted");
            Assert.True(!log.MarkUndone("does-not-exist"), "unknown id returns false");
        }

        [Test]
        public static void CanUndo_OnlyWhenUndoDataAndNotUndone()
        {
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            ChangeEntry a = log.Record(ChangeKind.Inicializacao, "a", "u");
            ChangeEntry b = log.Record(ChangeKind.Limpeza, "b", null);
            Assert.True(a.CanUndo, "a can undo");
            Assert.True(!b.CanUndo, "b cannot undo (no data)");
            log.MarkUndone(a.Id);
            Assert.True(!log.List()[1].CanUndo, "a cannot undo twice");
        }

        [Test]
        public static void Record_LoadsExistingEntriesFromStore()
        {
            MemoryChangeLogStore store = new MemoryChangeLogStore();
            ChangeLog first = new ChangeLog(store);
            first.Record(ChangeKind.Limpeza, "antigo", null);
            ChangeLog second = new ChangeLog(store);
            second.Record(ChangeKind.Reparo, "novo", null);
            List<ChangeEntry> l = second.List();
            Assert.Equal(2, l.Count);
            Assert.Equal("novo", l[0].Description);
            Assert.Equal("antigo", l[1].Description);
        }

        [Test]
        public static void Record_SaveFailure_DoesNotThrowAndKeepsEntryInMemory()
        {
            MemoryChangeLogStore store = new MemoryChangeLogStore();
            store.FailSave = true;
            ChangeLog log = new ChangeLog(store);
            ChangeEntry e = log.Record(ChangeKind.Limpeza, "x", null);
            Assert.True(e != null, "entry returned");
            Assert.Equal(1, log.List().Count);
            Assert.True(log.LastSaveError != null, "error exposed");
        }

        [Test]
        public static void Record_RaisesChanged()
        {
            ChangeLog log = new ChangeLog(new MemoryChangeLogStore());
            int n = 0;
            log.Changed += delegate { n++; };
            ChangeEntry e = log.Record(ChangeKind.Limpeza, "x", "u");
            log.MarkUndone(e.Id);
            Assert.Equal(2, n);
        }

        [Test]
        public static void Record_TrimsOldestBeyondLimit()
        {
            MemoryChangeLogStore store = new MemoryChangeLogStore();
            ChangeLog log = new ChangeLog(store);
            for (int i = 0; i < ChangeLog.MaxEntries + 5; i++) log.Record(ChangeKind.Limpeza, "e" + i, null);
            List<ChangeEntry> l = log.List();
            Assert.Equal(ChangeLog.MaxEntries, l.Count);
            Assert.Equal("e" + (ChangeLog.MaxEntries + 4), l[0].Description);
        }

        [Test]
        public static void Store_RoundTripsThroughFile()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "sub", "historico.json");
                ChangeLogStore store = new ChangeLogStore(path);
                DateTime when = new DateTime(2026, 11, 20, 10, 30, 15, DateTimeKind.Utc).AddTicks(1234567);
                ChangeLog log = new ChangeLog(store, delegate { return when; });
                ChangeEntry e = log.Record(ChangeKind.Inicializacao, "Desativado: Água \"x\"", "v1;HKCU;k;n;0200");
                log.Record(ChangeKind.PontoRestauracao, "Ponto criado", null);
                log.MarkUndone(e.Id);

                ChangeLog reread = new ChangeLog(new ChangeLogStore(path));
                List<ChangeEntry> l = reread.List();
                Assert.Equal(2, l.Count);
                Assert.Equal("Ponto criado", l[0].Description);
                Assert.Equal(ChangeKind.PontoRestauracao, l[0].Kind);
                Assert.Equal("Desativado: Água \"x\"", l[1].Description);
                Assert.Equal("v1;HKCU;k;n;0200", l[1].UndoData);
                Assert.True(l[1].Undone, "undone survives");
                Assert.Equal(when, l[1].WhenUtc);
                Assert.Equal(DateTimeKind.Utc, l[1].WhenUtc.Kind);
                Assert.Equal(e.Id, l[1].Id);
                string text = File.ReadAllText(path);
                Assert.True(text.Contains("\"whenUtc\"") && text.Contains("\"undoData\"") && text.Contains("\"kind\""), "json member names");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Store_MissingFile_ReturnsEmpty()
        {
            string dir = TempDir();
            try
            {
                ChangeLogStore store = new ChangeLogStore(Path.Combine(dir, "nada.json"));
                Assert.Equal(0, store.Load().Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Store_CorruptFile_StartsFreshAndKeepsBackup()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "historico.json");
                File.WriteAllText(path, "{ not json at all !!");
                ChangeLogStore store = new ChangeLogStore(path);
                Assert.Equal(0, store.Load().Count);
                Assert.True(File.Exists(path + ".bak"), "bak created");
                Assert.Equal("{ not json at all !!", File.ReadAllText(path + ".bak"));
                // new entries can be written afterwards
                ChangeLog log = new ChangeLog(store);
                log.Record(ChangeKind.Limpeza, "ok", null);
                Assert.Equal(1, new ChangeLogStore(path).Load().Count);
                Assert.Equal("{ not json at all !!", File.ReadAllText(path + ".bak"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void Store_DefaultPath_IsUnderAppDataSenseMonitor()
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SenseMonitor", "historico.json");
            Assert.Equal(expected, Path.Combine(UserData.RealRoaming, "historico.json"));
            Assert.Equal(Path.Combine(UserData.Roaming, "historico.json"), ChangeLogStore.DefaultPath);
        }

        [Test]
        public static void Store_EmptyFile_IsTreatedAsEmptyLog()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "historico.json");
                File.WriteAllText(path, "");
                Assert.Equal(0, new ChangeLogStore(path).Load().Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public static void SaveWarning_ExplainsAFailedSave_AndClearsAfterASuccessfulOne()
        {
            MemoryChangeLogStore store = new MemoryChangeLogStore();
            ChangeLog log = new ChangeLog(store);
            Assert.Null(log.SaveWarning);
            store.FailSave = true;
            log.Record(ChangeKind.Limpeza, "x", null);
            string w = log.SaveWarning;
            Assert.True(w != null, "warning present");
            Assert.True(w.Contains("O histórico não pôde ser salvo"), w);
            Assert.True(w.Contains("A alteração foi feita; anote-a ou tente de novo."), w);
            store.FailSave = false;
            log.Record(ChangeKind.Limpeza, "y", null);
            Assert.Null(log.SaveWarning);
        }
    }
}
