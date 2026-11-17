using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The embedded model table (docs\2026-11-17-pesquisa-modelos-acer.md, section 7) and its lookup.
    public static class ModelTableTests
    {
        [Test]
        public static void ExtractCode_FromDmiModelNames()
        {
            Assert.Equal("ANV15-51", ModelTable.ExtractCode("Nitro ANV15-51"));
            Assert.Equal("ANV15-51", ModelTable.ExtractCode("  nitro anv15-51  "));
            Assert.Equal("PHN16-72", ModelTable.ExtractCode("Predator PHN16-72"));
            Assert.Equal("AN515-58", ModelTable.ExtractCode("Nitro AN515-58"));
            Assert.Equal("PHN16S-71", ModelTable.ExtractCode("Predator PHN16S-71"));
            Assert.Equal("A715-42G", ModelTable.ExtractCode("Aspire A715-42G"));
            Assert.Equal("PT14-51", ModelTable.ExtractCode("Predator PT14-51"));
            Assert.Equal("AN16S-61", ModelTable.ExtractCode("Nitro AN16S-61"));
        }

        [Test]
        public static void ExtractCode_NoCode_ReturnsNull()
        {
            Assert.Null(ModelTable.ExtractCode(null));
            Assert.Null(ModelTable.ExtractCode(""));
            Assert.Null(ModelTable.ExtractCode("System Product Name"));
            Assert.Null(ModelTable.ExtractCode("Latitude 5520"));
        }

        [Test]
        public static void Find_ThisPc()
        {
            ModelEntry e = ModelTable.Find("Nitro ANV15-51");
            Assert.True(e != null, "ANV15-51 is in the table");
            Assert.Equal('A', e.Confidence);
            Assert.Equal(2, e.Fans);
            Assert.Equal(Tri.Yes, e.Sensors);
            Assert.True(e.ProfilesKnown, "profiles known");
            Assert.True(new List<PerfMode>(e.Profiles).Contains(PerfMode.Economico), "Eco expected");
            Assert.True(!new List<PerfMode>(e.Profiles).Contains(PerfMode.Turbo), "no Turbo on this model");
            Assert.Equal(KeyKind.KeyboardKey, e.Key);
        }

        [Test]
        public static void Find_IsCaseAndSpaceInsensitive()
        {
            Assert.True(ModelTable.Find("nitro  anv15-51") != null, "lowercase, double space");
            Assert.True(ModelTable.Find("ANV15-51") != null, "bare code");
        }

        [Test]
        public static void Find_UnknownModel_Null()
        {
            Assert.Null(ModelTable.Find("Nitro ANV15-99"));
            Assert.Null(ModelTable.Find("Latitude 5520"));
            Assert.Null(ModelTable.Find(null));
        }

        [Test]
        public static void Anv1552_ProfileBreaksWithBios()
        {
            ModelEntry e = ModelTable.Find("Nitro ANV15-52");
            Assert.True(e != null, "listed");
            Assert.True(e.ProfileBiosDependent, "profile depends on the BIOS");
            Assert.True(HasQuirk(e, "BIOS"), "quirk text mentions the BIOS");
        }

        [Test]
        public static void Phn1451_DifferentSensorIds()
        {
            ModelEntry e = ModelTable.Find("Predator PHN14-51");
            Assert.True(e != null, "listed");
            Assert.True(e.SensorIdsDiffer, "sensor ids differ");
        }

        [Test]
        public static void SeriesS_RgbThroughHid()
        {
            Assert.Equal(RgbKind.HidI2c, ModelTable.Find("Predator PHN16S-71").Rgb);
            Assert.Equal(RgbKind.HidI2c, ModelTable.Find("Nitro ANV16S-41").Rgb);
            Assert.Equal(RgbKind.HidI2c, ModelTable.Find("Nitro AN16S-61").Rgb);
            Assert.True(HasQuirk(ModelTable.Find("Nitro ANV16S-41"), "I2C"), "quirk explains I2C");
        }

        [Test]
        public static void Helios300_NoProfileAndTurboDanger()
        {
            ModelEntry e = ModelTable.Find("Predator PH315-53");
            Assert.True(e != null, "listed");
            Assert.True(e.ProfilesKnown && e.Profiles.Length == 0, "no platform profile");
            Assert.True(e.TurboDanger, "Turbo is the overclock path");
        }

        [Test]
        public static void Anv1651_NoKeyChannel_Anv1642()
        {
            Assert.Equal(KeyKind.NoChannel, ModelTable.Find("Nitro ANV16-42").Key);
        }

        [Test]
        public static void HeliosNeo_ListsTurbo()
        {
            ModelEntry e = ModelTable.Find("Predator PHN16-71");
            Assert.True(new List<PerfMode>(e.Profiles).Contains(PerfMode.Turbo), "Turbo expected");
        }

        [Test]
        public static void AspireIsNotAGamingEntry()
        {
            ModelEntry e = ModelTable.Find("Aspire A715-42G");
            Assert.True(e != null, "listed");
            Assert.Equal(Tri.Unknown, e.Sensors);
            Assert.True(!e.ProfilesKnown, "profiles unknown");
        }

        [Test]
        public static void EveryEntryIsWellFormed()
        {
            Dictionary<string, string> seen = new Dictionary<string, string>();
            foreach (ModelEntry e in ModelTable.All)
            {
                Assert.True(!string.IsNullOrEmpty(e.Family), "family");
                Assert.True(e.ModelCodes != null && e.ModelCodes.Length > 0, "codes of " + e.Family);
                Assert.True(e.Confidence == 'A' || e.Confidence == 'M' || e.Confidence == 'B', "confidence of " + e.Family);
                Assert.True(e.Fans >= -1 && e.Fans <= 2, "fan count of " + e.Family);
                foreach (string c in e.ModelCodes)
                {
                    Assert.Equal(c.ToUpperInvariant(), c);
                    Assert.True(!seen.ContainsKey(c), "code " + c + " listed twice (" + (seen.ContainsKey(c) ? seen[c] : "") + " and " + e.Family + ")");
                    seen[c] = e.Family;
                    Assert.Equal(c, ModelTable.ExtractCode("Acer " + c));
                }
                if (e.ProfilesKnown) Assert.True(e.Profiles != null, "profiles array of " + e.Family);
                if (e.Quirks != null)
                {
                    foreach (string q in e.Quirks) Assert.True(!string.IsNullOrEmpty(q), "empty quirk in " + e.Family);
                }
            }
            Assert.True(ModelTable.All.Count >= 12, "the table covers the families of section 7");
        }

        [Test]
        public static void BrandHint_NitroPredatorDetected()
        {
            Assert.True(ModelTable.LooksLikeAcerGaming("Nitro ANV15-99"), "Nitro");
            Assert.True(ModelTable.LooksLikeAcerGaming("Predator PHN99-71"), "Predator");
            Assert.True(ModelTable.LooksLikeAcerGaming("Helios 300"), "Helios");
            Assert.True(!ModelTable.LooksLikeAcerGaming("Aspire 5"), "Aspire");
            Assert.True(!ModelTable.LooksLikeAcerGaming(null), "null");
        }

        private static bool HasQuirk(ModelEntry e, string fragment)
        {
            if (e == null || e.Quirks == null) return false;
            foreach (string q in e.Quirks)
            {
                if (q.Contains(fragment)) return true;
            }
            return false;
        }
    }
}
