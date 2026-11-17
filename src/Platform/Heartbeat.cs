using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Forja.Platform
{
    // Shared memory between the app and its sentinel (same user, same session, both elevated):
    //   bytes 0-7   Stopwatch timestamp of the app's last firmware tick (the heartbeat)
    //   bytes 8-11  how many times the sentinel handed the fans back while the app was frozen
    // The app creates it and calls Beat; the sentinel opens it and reads the age. If the app
    // dies the section lives on while the sentinel holds it. Never throws.
    public sealed class Heartbeat : IDisposable
    {
        public const string Name = @"Local\SenseMonitor.Heartbeat";
        private const int Size = 16;

        private readonly MemoryMappedFile file;
        private readonly MemoryMappedViewAccessor view;

        private Heartbeat(MemoryMappedFile file, MemoryMappedViewAccessor view)
        {
            this.file = file;
            this.view = view;
        }

        // The app's side: creates the section (or opens the one a previous instance left).
        public static Heartbeat Create()
        {
            return Create(Name);
        }

        // A named section of its own (tests must never touch the real app's heartbeat).
        public static Heartbeat Create(string name)
        {
            try
            {
                MemoryMappedFile f = MemoryMappedFile.CreateOrOpen(name, Size);
                return new Heartbeat(f, f.CreateViewAccessor(0, Size));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The sentinel's side: null while the app has not created it.
        public static Heartbeat TryOpen()
        {
            return TryOpen(Name);
        }

        public static Heartbeat TryOpen(string name)
        {
            try
            {
                MemoryMappedFile f = MemoryMappedFile.OpenExisting(name);
                return new Heartbeat(f, f.CreateViewAccessor(0, Size));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Beat()
        {
            try { view.Write(0, Stopwatch.GetTimestamp()); }
            catch (Exception) { }
        }

        // Queries are comparable between processes of the same boot and do not jump when the
        // wall clock is changed (NTP, the user), unlike DateTime.
        public static long AgeMsFrom(long nowTimestamp, long thenTimestamp, long frequency)
        {
            if (frequency <= 0) return 0;
            long d = nowTimestamp - thenTimestamp;
            if (d < 0) return 0;
            return (long)(d * 1000.0 / frequency);
        }

        // Milliseconds since the last Beat; null when there was none yet.
        public long? AgeMs()
        {
            try
            {
                long t = view.ReadInt64(0);
                if (t <= 0) return null;
                return AgeMsFrom(Stopwatch.GetTimestamp(), t, Stopwatch.Frequency);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public int Interventions
        {
            get
            {
                try { return view.ReadInt32(8); }
                catch (Exception) { return 0; }
            }
        }

        public void BumpIntervention()
        {
            try { view.Write(8, view.ReadInt32(8) + 1); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            try { view.Dispose(); }
            catch (Exception) { }
            try { file.Dispose(); }
            catch (Exception) { }
        }
    }
}
