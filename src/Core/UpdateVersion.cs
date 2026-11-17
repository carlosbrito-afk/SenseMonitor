using System;
using System.Text.RegularExpressions;

namespace Forja.Core
{
    // Release version "major.minor.patch": exactly three plain decimal numbers (no leading
    // zeros, no sign, no suffix, ASCII digits only). Anything else is not a version the updater
    // will act on. Comparison is numeric ("0.10.0" is newer than "0.9.0").
    public sealed class UpdateVersion : IComparable<UpdateVersion>
    {
        private static readonly Regex Shape = new Regex(
            @"\A(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\z", RegexOptions.CultureInvariant);

        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;

        public UpdateVersion(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public static bool TryParse(string text, out UpdateVersion version)
        {
            version = null;
            if (text == null) return false;
            Match m = Shape.Match(text);
            if (!m.Success) return false;
            version = new UpdateVersion(
                int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        public int CompareTo(UpdateVersion other)
        {
            if (other == null) return 1;
            if (Major != other.Major) return Major < other.Major ? -1 : 1;
            if (Minor != other.Minor) return Minor < other.Minor ? -1 : 1;
            if (Patch != other.Patch) return Patch < other.Patch ? -1 : 1;
            return 0;
        }

        // Strictly greater: an equal or older version is never an update (no downgrade).
        public bool IsNewerThan(UpdateVersion other)
        {
            return CompareTo(other) > 0;
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);
        }

        public override bool Equals(object obj)
        {
            UpdateVersion o = obj as UpdateVersion;
            return o != null && CompareTo(o) == 0;
        }

        public override int GetHashCode()
        {
            return (Major * 397 ^ Minor) * 397 ^ Patch;
        }
    }
}
