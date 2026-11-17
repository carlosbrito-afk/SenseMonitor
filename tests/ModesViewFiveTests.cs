using Forja.Core;

namespace Forja.Tests
{
    // The Modes page shows up to five cards (only the confirmed modes), so each card must take ITS mode's line from the
    // view: by mode, never by position.
    public static class ModesViewFiveTests
    {
        [Test]
        public static void Index_FiveModes()
        {
            Assert.Equal(0, ModesView.Index(PerfMode.Silencioso));
            Assert.Equal(1, ModesView.Index(PerfMode.DiaADia));
            Assert.Equal(2, ModesView.Index(PerfMode.Desempenho));
            Assert.Equal(3, ModesView.Index(PerfMode.Economico));
            Assert.Equal(4, ModesView.Index(PerfMode.Turbo));
        }

        [Test]
        public static void EveryModeHasItsOwnLine()
        {
            ModesView v = ModesView.Build(null, null);
            Assert.Equal(5, v.CardLines.Length);
            foreach (PerfMode m in ModeCatalog.DisplayOrder)
            {
                Assert.Equal("sem medição", v.LineFor(m));
            }
        }

        [Test]
        public static void LinesFollowTheModeNotThePosition()
        {
            ModesView v = new ModesView();
            v.CardLines[ModesView.Index(PerfMode.Economico)] = "eco";
            v.CardLines[ModesView.Index(PerfMode.Silencioso)] = "quiet";
            v.CardLines[ModesView.Index(PerfMode.Turbo)] = "turbo";
            // a page that shows Eco, Silencioso and Turbo (positions 0, 1, 2) must not get the lines of slots 0, 1, 2
            Assert.Equal("eco", v.LineFor(PerfMode.Economico));
            Assert.Equal("quiet", v.LineFor(PerfMode.Silencioso));
            Assert.Equal("turbo", v.LineFor(PerfMode.Turbo));
            Assert.Null(v.LineFor(PerfMode.Desempenho));
        }
    }
}
