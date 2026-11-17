using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The key test listens ONLY for the candidate codes of the research and counts presses with the
    // existing debounce of NitroKey.
    public static class KeyCandidateTests
    {
        private const int Down = 0x100;
        private const int Up = 0x101;

        [Test]
        public static void Default_ListensOnlyForOurCode()
        {
            NitroKey k = new NitroKey();
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "our code");
            Assert.True(k.LastFired == KeyCandidates.Ours, "which one");
        }

        [Test]
        public static void AtF5_AnyVk()
        {
            NitroKey k = new NitroKey(KeyCandidates.All);
            Assert.True(k.OnKey(Down, 0x7A, 0xF5, 0x1, 1000), "scan 0xF5 with an unrelated vk");
            Assert.True(k.LastFired == KeyCandidates.AtF5, "AT F5 candidate");
        }

        [Test]
        public static void Ours_RequiresTheVk()
        {
            NitroKey k = new NitroKey(KeyCandidates.All);
            Assert.True(!k.OnKey(Down, 0x41, 0x75, 0x1, 1000), "scan 0x75 with another vk is not our key");
            Assert.True(k.LastFired == null, "nothing fired");
        }

        [Test]
        public static void OtherKeys_NeverFireAndNeverRemembered()
        {
            NitroKey k = new NitroKey(KeyCandidates.All);
            int[][] others = { new int[] { 0x41, 0x1E }, new int[] { 0x0D, 0x1C }, new int[] { 0x20, 0x39 }, new int[] { 0xFF, 0x76 }, new int[] { 0x70, 0x3B } };
            int t = 5000;
            foreach (int[] o in others)
            {
                Assert.True(!k.OnKey(Down, o[0], o[1], 0x1, t), "down ignored");
                Assert.True(!k.OnKey(Up, o[0], o[1], 0x81, t + 10), "up ignored");
                t += 50;
            }
            Assert.True(k.LastFired == null, "no candidate matched");
            // the first real press right after the other keys still fires: they left no state behind
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, t + 10), "first real press fires");
        }

        [Test]
        public static void NoCandidates_NeverFires()
        {
            NitroKey k = new NitroKey(new KeyCandidate[0]);
            Assert.True(!k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "empty list");
            Assert.True(!k.OnKey(Down, 0x00, 0xF5, 0x1, 1100), "empty list");
        }

        [Test]
        public static void NullList_TreatedAsEmpty()
        {
            NitroKey k = new NitroKey((KeyCandidate[])null);
            Assert.True(!k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "null list");
        }

        [Test]
        public static void DebouncePerCandidate_Independent()
        {
            NitroKey k = new NitroKey(KeyCandidates.All);
            Assert.True(k.OnKey(Down, 0xFF, 0x75, 0x1, 1000), "ours");
            // a press of the other code 100 ms later is a different key: it has its own debounce
            Assert.True(k.OnKey(Down, 0x00, 0xF5, 0x1, 1100), "F5 is not debounced by ours");
            Assert.True(!k.OnKey(Down, 0xFF, 0x75, 0x1, 1200), "ours repeats inside the window");
        }

        [Test]
        public static void ThreePresses_CounterCompletes()
        {
            KeyTestState s = new KeyTestState();
            NitroKey k = new NitroKey(KeyCandidates.All);
            int t = 10000;
            for (int press = 0; press < 3; press++)
            {
                // held key: down/up pairs inside the debounce count once
                for (int n = 0; n < 4; n++)
                {
                    if (k.OnKey(Down, 0xFF, 0x75, 0x1, t)) s.Pressed(k.LastFired);
                    if (k.OnKey(Up, 0xFF, 0x75, 0x81, t + 20)) s.Pressed(k.LastFired);
                    t += 200;
                }
                t += 1500;
            }
            Assert.Equal(3, s.Presses);
            Assert.True(s.Complete, "three presses complete the test");
            Assert.True(s.Chosen == KeyCandidates.Ours, "chosen candidate");
        }

        [Test]
        public static void FirstCandidateLocks_OtherIgnoredAfterwards()
        {
            KeyTestState s = new KeyTestState();
            s.Pressed(KeyCandidates.AtF5);
            s.Pressed(KeyCandidates.Ours); // noise from the other code does not count
            s.Pressed(KeyCandidates.AtF5);
            Assert.Equal(2, s.Presses);
            Assert.True(s.Chosen == KeyCandidates.AtF5, "locked on F5");
            Assert.True(!s.Complete, "two presses are not enough");
            s.Pressed(KeyCandidates.AtF5);
            Assert.True(s.Complete, "third press completes");
        }

        [Test]
        public static void PressedAfterComplete_DoesNotOvercount()
        {
            KeyTestState s = new KeyTestState();
            for (int i = 0; i < 6; i++) s.Pressed(KeyCandidates.Ours);
            Assert.Equal(KeyTestState.Target, s.Presses);
        }

        [Test]
        public static void NullCandidate_Ignored()
        {
            KeyTestState s = new KeyTestState();
            s.Pressed(null);
            Assert.Equal(0, s.Presses);
        }

        [Test]
        public static void Reset_StartsOver()
        {
            KeyTestState s = new KeyTestState();
            s.Pressed(KeyCandidates.Ours);
            s.Reset();
            Assert.Equal(0, s.Presses);
            Assert.True(s.Chosen == null, "no candidate after reset");
        }

        [Test]
        public static void ById_AndForProfile()
        {
            Assert.True(KeyCandidates.ById(KeyCandidates.AtF5Id) == KeyCandidates.AtF5, "by id");
            Assert.True(KeyCandidates.ById("nope") == null, "unknown id");
            Assert.True(KeyCandidates.ById(null) == null, "null id");

            // no profile / key never tested: the code the app always used
            KeyCandidate[] c = KeyCandidates.ForProfile(null);
            Assert.Equal(1, c.Length);
            Assert.True(c[0] == KeyCandidates.Ours, "legacy default");

            CapabilityProfile p = new CapabilityProfile();
            p.Set(Cap.Key, CapState.Untested, "x");
            Assert.True(KeyCandidates.ForProfile(p)[0] == KeyCandidates.Ours, "untested keeps the default");

            p.Set(Cap.Key, CapState.Confirmed, "x");
            p.KeyCandidateId = KeyCandidates.AtF5Id;
            c = KeyCandidates.ForProfile(p);
            Assert.Equal(1, c.Length);
            Assert.True(c[0] == KeyCandidates.AtF5, "confirmed candidate only");

            p.Set(Cap.Key, CapState.Unavailable, "x");
            Assert.Equal(0, KeyCandidates.ForProfile(p).Length);
            p.Set(Cap.Key, CapState.Unknown, "x"); // a timeout in the test: keep the code the app always used
            Assert.Equal(1, KeyCandidates.ForProfile(p).Length);
        }

        [Test]
        public static void CandidateList_IsExactlyTheResearchCodes()
        {
            // the privacy promise: these are the only codes the key test can ever look at
            Assert.Equal(2, KeyCandidates.All.Length);
            Assert.Equal(0xFF, KeyCandidates.Ours.Vk);
            Assert.Equal(0x75, KeyCandidates.Ours.Scan);
            Assert.Equal(-1, KeyCandidates.AtF5.Vk);
            Assert.Equal(0xF5, KeyCandidates.AtF5.Scan);
        }
    }
}
