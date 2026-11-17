using System;
using Forja.Core;

namespace Forja.Tests
{
    public static class LruCacheTests
    {
        [Test]
        public static void LruCache_NeverExceedsCapacity()
        {
            LruCache<int, int> c = new LruCache<int, int>(64);
            for (int i = 0; i < 1000; i++) c.Set(i, i * 2);
            Assert.Equal(64, c.Count);
            Assert.Equal(64, c.Capacity);
            int v;
            Assert.True(c.TryGet(999, out v) && v == 1998, "newest entry kept");
            Assert.True(c.TryGet(936, out v), "the 64 newest are kept");
            Assert.True(!c.TryGet(935, out v), "older entries evicted");
        }

        [Test]
        public static void LruCache_EvictsLeastRecentlyUsed()
        {
            LruCache<string, int> c = new LruCache<string, int>(2);
            c.Set("a", 1);
            c.Set("b", 2);
            int v;
            Assert.True(c.TryGet("a", out v) && v == 1, "a present");
            c.Set("c", 3); // b is now the least recently used
            Assert.True(!c.TryGet("b", out v), "b evicted");
            Assert.True(c.TryGet("a", out v), "a kept (was read)");
            Assert.True(c.TryGet("c", out v) && v == 3, "c present");
        }

        [Test]
        public static void LruCache_SetExistingUpdatesWithoutGrowing()
        {
            LruCache<string, int> c = new LruCache<string, int>(2);
            c.Set("a", 1);
            c.Set("b", 2);
            c.Set("a", 10); // refreshes a
            Assert.Equal(2, c.Count);
            c.Set("c", 3); // evicts b
            int v;
            Assert.True(c.TryGet("a", out v) && v == 10, "a updated and kept");
            Assert.True(!c.TryGet("b", out v), "b evicted");
        }

        [Test]
        public static void LruCache_ClearAndCapacityGuard()
        {
            LruCache<int, int> c = new LruCache<int, int>(3);
            c.Set(1, 1);
            c.Clear();
            Assert.Equal(0, c.Count);
            int v;
            Assert.True(!c.TryGet(1, out v), "cleared");
            Assert.Throws<ArgumentOutOfRangeException>(delegate { new LruCache<int, int>(0); });
        }
    }
}
