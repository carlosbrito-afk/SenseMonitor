using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace Forja.Core
{
    public enum ChangeKind
    {
        Inicializacao,
        Limpeza,
        Reparo,
        PontoRestauracao
    }

    // One line of the change history. JSON member names are fixed (lower camel case) because
    // the file is meant to be readable by a person.
    [DataContract]
    public class ChangeEntry
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "whenUtc")] public string WhenUtcText;
        [DataMember(Name = "kind")] public string KindName;
        [DataMember(Name = "description")] public string Description;
        [DataMember(Name = "undoData")] public string UndoData;
        [DataMember(Name = "undone")] public bool Undone;

        public DateTime WhenUtc
        {
            get
            {
                DateTime d;
                if (DateTime.TryParse(WhenUtcText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                {
                    return DateTime.SpecifyKind(d, DateTimeKind.Utc);
                }
                return DateTime.MinValue;
            }
            set
            {
                DateTime u = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
                WhenUtcText = u.ToString("o", CultureInfo.InvariantCulture);
            }
        }

        public ChangeKind Kind
        {
            get
            {
                try { return (ChangeKind)Enum.Parse(typeof(ChangeKind), KindName ?? "", false); }
                catch (ArgumentException) { return ChangeKind.Limpeza; }
            }
            set { KindName = value.ToString(); }
        }

        // True only when something exact can be restored and it was not restored yet.
        public bool CanUndo
        {
            get { return !Undone && !string.IsNullOrEmpty(UndoData); }
        }
    }

    public interface IChangeLogStore
    {
        List<ChangeEntry> Load();
        void Save(List<ChangeEntry> entries);
    }

    // Append-only history of what the app changed on the system. Every mutating feature
    // records here; undoData is kind-specific text that the feature knows how to apply.
    public class ChangeLog
    {
        public const int MaxEntries = 500;

        private readonly IChangeLogStore store;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();
        private readonly List<ChangeEntry> entries;
        private Exception lastSaveError;

        public event EventHandler Changed;

        public ChangeLog(IChangeLogStore store)
            : this(store, null)
        {
        }

        public ChangeLog(IChangeLogStore store, Func<DateTime> clock)
        {
            if (store == null) throw new ArgumentNullException("store");
            this.store = store;
            this.clock = clock ?? delegate { return DateTime.UtcNow; };
            List<ChangeEntry> loaded = null;
            try { loaded = store.Load(); }
            catch (Exception ex) { lastSaveError = ex; }
            entries = loaded ?? new List<ChangeEntry>();
        }

        // The last failure while writing the history file, or null. A failed save never
        // throws: the system change already happened and the entry stays in memory.
        public Exception LastSaveError
        {
            get { lock (sync) { return lastSaveError; } }
        }

        // pt-BR text for the user when the last save of the history file failed, otherwise null.
        public string SaveWarning
        {
            get
            {
                Exception e = LastSaveError;
                if (e == null) return null;
                return "O histórico não pôde ser salvo (" + e.Message + "). A alteração foi feita; anote-a ou tente de novo.";
            }
        }

        public ChangeEntry Record(ChangeKind kind, string description, string undoData)
        {
            ChangeEntry e = new ChangeEntry();
            e.Id = Guid.NewGuid().ToString("N");
            e.WhenUtc = clock();
            e.Kind = kind;
            e.Description = description ?? "";
            e.UndoData = undoData;
            e.Undone = false;
            lock (sync)
            {
                entries.Add(e);
                while (entries.Count > MaxEntries) entries.RemoveAt(0);
                Persist();
            }
            RaiseChanged();
            return Copy(e);
        }

        // Newest first. Returns copies; edit through MarkUndone.
        public List<ChangeEntry> List()
        {
            lock (sync)
            {
                List<ChangeEntry> l = new List<ChangeEntry>(entries.Count);
                for (int i = entries.Count - 1; i >= 0; i--) l.Add(Copy(entries[i]));
                return l;
            }
        }

        public bool MarkUndone(string id)
        {
            bool found = false;
            lock (sync)
            {
                foreach (ChangeEntry e in entries)
                {
                    if (e.Id == id)
                    {
                        e.Undone = true;
                        found = true;
                        break;
                    }
                }
                if (found) Persist();
            }
            if (found) RaiseChanged();
            return found;
        }

        private void Persist()
        {
            try
            {
                store.Save(new List<ChangeEntry>(entries));
                lastSaveError = null;
            }
            catch (Exception ex)
            {
                lastSaveError = ex;
            }
        }

        private void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        private static ChangeEntry Copy(ChangeEntry e)
        {
            ChangeEntry c = new ChangeEntry();
            c.Id = e.Id;
            c.WhenUtcText = e.WhenUtcText;
            c.KindName = e.KindName;
            c.Description = e.Description;
            c.UndoData = e.UndoData;
            c.Undone = e.Undone;
            return c;
        }
    }
}
