using System;
using System.IO;
using Forja.Core;

namespace Forja.Platform
{
    // gestos.json (what each gesture of the NitroSense key does) and widget.json (the monitoring
    // widget), in the app's data folder next to alertas.json. Same atomic write; a missing or damaged
    // file loads as the defaults. Never throws.
    public static class OverlayStore
    {
        public const string GestureFile = "gestos.json";
        public const string WidgetFile = "widget.json";

        public static string GesturePath { get { return Path.Combine(Safety.Dir, GestureFile); } }
        public static string WidgetPath { get { return Path.Combine(Safety.Dir, WidgetFile); } }

        public static GestureSettings LoadGestures()
        {
            return LoadGestures(GesturePath);
        }

        public static GestureSettings LoadGestures(string path)
        {
            GestureSettings s = AlertStore.Read<GestureSettings>(path);
            return (s ?? new GestureSettings()).Normalize();
        }

        public static bool SaveGestures(GestureSettings s)
        {
            return SaveGestures(GesturePath, s);
        }

        public static bool SaveGestures(string path, GestureSettings s)
        {
            return AlertStore.Write(path, s == null ? new GestureSettings() : s.Copy().Normalize());
        }

        public static WidgetSettings LoadWidget()
        {
            return LoadWidget(WidgetPath);
        }

        public static WidgetSettings LoadWidget(string path)
        {
            WidgetSettings s = AlertStore.Read<WidgetSettings>(path);
            return (s ?? new WidgetSettings()).Normalize();
        }

        public static bool SaveWidget(WidgetSettings s)
        {
            return SaveWidget(WidgetPath, s);
        }

        public static bool SaveWidget(string path, WidgetSettings s)
        {
            return AlertStore.Write(path, s == null ? new WidgetSettings() : s.Copy().Normalize());
        }
    }
}
