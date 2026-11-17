using System;
using System.Collections.Generic;

namespace Forja.Core
{
    // Small least-recently-used map with a fixed capacity, for UI-thread caches (not thread-safe).
    // Values are dropped silently on eviction, so only use it for values that need no disposal.
    public sealed class LruCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _order; // First = most recently used

        public LruCache(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity", "capacity must be at least 1");
            _capacity = capacity;
            _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
            _order = new LinkedList<KeyValuePair<TKey, TValue>>();
        }

        public int Capacity
        {
            get { return _capacity; }
        }

        public int Count
        {
            get { return _map.Count; }
        }

        public bool TryGet(TKey key, out TValue value)
        {
            LinkedListNode<KeyValuePair<TKey, TValue>> node;
            if (_map.TryGetValue(key, out node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default(TValue);
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            LinkedListNode<KeyValuePair<TKey, TValue>> node;
            if (_map.TryGetValue(key, out node))
            {
                _order.Remove(node);
            }
            else if (_map.Count >= _capacity)
            {
                LinkedListNode<KeyValuePair<TKey, TValue>> last = _order.Last;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
            node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
            _order.AddFirst(node);
            _map[key] = node;
        }

        public void Clear()
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
