namespace LiveMarkdown.Avalonia;

/// <summary>Thread-safe token retention with independent count and weight bounds. The weight is an
/// estimate of retained text/token storage, not a measurement of managed or native heap bytes.</summary>
internal sealed class SyntaxTokenCache<T>(int capacity, long weightLimit, long entryWeightLimit) where T : class
{
    private sealed record Entry((string Language, string Text) Key, T Value, long Weight);
    private readonly Dictionary<(string Language, string Text), LinkedListNode<Entry>> _byKey = [];
    private readonly LinkedList<Entry> _oldestFirst = [];
    private readonly object _gate = new();
    private long _weight;

    internal int Count { get { lock (_gate) return _byKey.Count; } }
    internal long Weight { get { lock (_gate) return _weight; } }

    internal bool TryGetValue((string Language, string Text) key, out T? value)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var node)) { value = null; return false; }
            _oldestFirst.Remove(node);
            _oldestFirst.AddLast(node);
            value = node.Value.Value;
            return true;
        }
    }

    internal bool Store((string Language, string Text) key, T value, long weight)
    {
        if (capacity <= 0 || weight < 0 || weight > entryWeightLimit || weight > weightLimit) return false;
        lock (_gate)
        {
            if (_byKey.Remove(key, out var previous)) Remove(previous);
            var node = _oldestFirst.AddLast(new Entry(key, value, weight));
            _byKey.Add(key, node);
            _weight += weight;
            while (_byKey.Count > capacity || _weight > weightLimit)
            {
                var oldest = _oldestFirst.First!;
                _byKey.Remove(oldest.Value.Key);
                Remove(oldest);
            }
            return true;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _oldestFirst.Remove(node);
        _weight -= node.Value.Weight;
    }
}
