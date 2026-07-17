using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Reader;

public readonly record struct PageRenderCacheKey(
    DocumentId DocumentId,
    int PageIndex,
    double ZoomFactor,
    PageRotation Rotation,
    double DpiScaleX,
    double DpiScaleY,
    RenderQuality Quality);

public sealed class PageRenderCache<TValue>
{
    private readonly int _capacity;
    private readonly Dictionary<PageRenderCacheKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recentlyUsed = [];

    public PageRenderCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public bool TryGet(PageRenderCacheKey key, out TValue? value)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            value = default;
            return false;
        }

        _recentlyUsed.Remove(node);
        _recentlyUsed.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(PageRenderCacheKey key, TValue value)
    {
        if (_entries.TryGetValue(key, out var existingNode))
        {
            existingNode.Value = new CacheEntry(key, value);
            _recentlyUsed.Remove(existingNode);
            _recentlyUsed.AddFirst(existingNode);
            return;
        }

        var node = _recentlyUsed.AddFirst(new CacheEntry(key, value));
        _entries.Add(key, node);

        if (_entries.Count <= _capacity)
        {
            return;
        }

        var leastRecentlyUsed = _recentlyUsed.Last!;
        _recentlyUsed.RemoveLast();
        _entries.Remove(leastRecentlyUsed.Value.Key);
    }

    public void ClearDocument(DocumentId documentId)
    {
        var node = _recentlyUsed.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Key.DocumentId == documentId)
            {
                _recentlyUsed.Remove(node);
                _entries.Remove(node.Value.Key);
            }

            node = next;
        }
    }

    private sealed record CacheEntry(PageRenderCacheKey Key, TValue Value);
}
