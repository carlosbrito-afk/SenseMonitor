using System;
using System.IO;
using System.Text;
using Forja.Core;

namespace Forja.Platform
{
    // ventoinha-custom.flag in the verified state folder: exists from before the first Custom/Máximo write until Auto is
    // confirmed. Written through to disk (the evidence has to survive a process kill or a power loss right after). It
    // records WHICH fans were written, on which machine, and how the recovery is going (FanMarkerData).
    public sealed class FanMarker : IFanMarker
    {
        private readonly string filePath;

        public FanMarker(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            filePath = path;
        }

        public bool IsSet
        {
            get
            {
                try { return File.Exists(filePath); }
                catch (Exception) { return false; }
            }
        }

        public FanMarkerData Read()
        {
            try
            {
                if (!File.Exists(filePath)) return new FanMarkerData();
                // Shares the way the writer does: File.ReadAllText asks for FileShare.Read, which fails while a Write has the file open.
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    return FanMarkerData.Parse(sr.ReadToEnd());
                }
            }
            catch (Exception)
            {
                return new FanMarkerData();
            }
        }

        // Without a layout (an older caller): the marker still exists, it just does not say which fans.
        public bool Set()
        {
            if (IsSet) return true;
            return Write(new FanMarkerData());
        }

        public bool Set(FanLayout layout, string fingerprint)
        {
            if (IsSet)
            {
                // Already raised by an earlier write of this session: keep its record, widen it if this write addresses more.
                FanMarkerData cur = Read();
                string widened = FanMarkerData.Union(cur.Layout, FanMarkerData.LayoutText(layout));
                bool changed = widened != cur.Layout;
                cur.Layout = widened;
                if (string.IsNullOrEmpty(cur.Fingerprint) && !string.IsNullOrEmpty(fingerprint)) { cur.Fingerprint = fingerprint; changed = true; }
                if (changed) Write(cur);
                return true;
            }
            FanMarkerData d = new FanMarkerData();
            d.Layout = FanMarkerData.LayoutText(layout);
            d.Fingerprint = fingerprint;
            return Write(d);
        }

        public bool Update(FanMarkerData data)
        {
            return IsSet && Write(data);
        }

        private bool Write(FanMarkerData d)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                byte[] body = Encoding.UTF8.GetBytes(d.ToText());
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 16, FileOptions.WriteThrough))
                {
                    fs.Write(body, 0, body.Length);
                    fs.Flush(true);
                }
                return File.Exists(filePath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Clear()
        {
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    if (File.Exists(filePath)) File.Delete(filePath);
                    if (!File.Exists(filePath)) return true;
                }
                catch (Exception)
                {
                }
            }
            return !IsSet;
        }
    }

    // Stands in for a state file when the state folder cannot be trusted: nothing can be raised (so the guard refuses
    // Custom/Máximo writes, fail closed) and nothing is ever read as set.
    public sealed class UnsafeFlag : IFlag
    {
        public bool IsSet { get { return false; } }
        public bool Set() { return false; }
        public bool Clear() { return true; }
    }

    public sealed class NullModeMarker : IModeMarker
    {
        public bool Exists { get { return false; } }
        public bool Set(PerfMode before) { return false; }
        public bool Set(PerfMode before, string fingerprint) { return false; }
        public PerfMode? Read() { return null; }
        public string ReadFingerprint() { return null; }
        public bool Clear() { return true; }
    }
}
