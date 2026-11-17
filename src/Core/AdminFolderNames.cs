using System;

namespace Forja.Core
{
    public static class AdminFolderNames
    {
        // A folder that failed verification is renamed to this, next to itself (same parent, so the rename is a
        // plain MoveFile that needs DELETE_CHILD on the parent only): "<name>.recusado-<ticks>". Never deleted
        // or touched afterwards. The ticks make the name unique; callers retry with new ticks if it is taken.
        public static string AsideName(string path, long ticks)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("caminho vazio");
            string trimmed = path.TrimEnd('\\', '/');
            return trimmed + ".recusado-" + ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool IsAsideName(string name)
        {
            return name != null && name.IndexOf(".recusado-", StringComparison.OrdinalIgnoreCase) > 0;
        }
    }
}
