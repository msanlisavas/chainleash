namespace ChainLeash.Agent;

/// Thread-safe membership set with a hard capacity — replay protection that cannot be
/// grown without bound by feeding it garbage keys. Once full, the OLDEST entry is
/// evicted (FIFO): for co-sign replay defense the recent window is the one that matters,
/// and resolved proposals are independently rejected via chain state anyway.
///
/// Implemented as a dict + linked list under one lock (not dict + queue): a released key
/// must leave NO stale eviction entry behind, or a later re-claim of the same key could
/// be evicted prematurely — re-opening exactly the replay window this set closes.
public sealed class BoundedSet(int capacity)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly LinkedList<string> _order = new();

    /// Atomically claim a key. False if it is already held (replay).
    public bool TryAdd(string key)
    {
        lock (_lock)
        {
            if (_nodes.ContainsKey(key)) return false;
            _nodes[key] = _order.AddLast(key);
            if (_nodes.Count > capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _nodes.Remove(oldest.Value);
            }
            return true;
        }
    }

    /// Release a claim (e.g. a not-yet-on-chain hash the client may legitimately retry).
    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_nodes.Remove(key, out var node)) _order.Remove(node);
        }
    }
}
