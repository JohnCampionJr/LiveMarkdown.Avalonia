using System.Buffers;
using System.Security.Cryptography;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// An in-memory image byte cache with bounded, least-recently-used retention.
/// </summary>
public class RamBasedAsyncImageLoaderCache : AsyncImageLoaderCache
{
    /// <summary>
    /// Gets a shared in-memory cache instance.
    /// </summary>
    public static RamBasedAsyncImageLoaderCache Shared { get; } = new();

    /// <summary>
    /// Gets or sets the maximum cached entry size in bytes. Entries larger than
    /// this value are ignored. Set to 0 or a negative value to disable the limit.
    /// </summary>
    public static long MaxEntrySizeBytes
    {
        get => Volatile.Read(ref s_maxEntrySizeBytes);
        set => Volatile.Write(ref s_maxEntrySizeBytes, value);
    }

    private static long s_maxEntrySizeBytes = 32L * 1024L * 1024L;

    /// <summary>
    /// Gets or sets this cache's aggregate retained-byte limit. Must be positive; defaults to 64 MiB.
    /// Reducing the limit immediately removes the least recently used entries until it is satisfied.
    /// This bound also limits an individual write when <see cref="MaxEntrySizeBytes"/> is disabled.
    /// </summary>
    public long MaxCacheSizeBytes
    {
        get { lock (_gate) return _maxCacheSizeBytes; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (_gate)
            {
                _maxCacheSizeBytes = value;
                TrimToBudget();
            }
        }
    }

    /// <summary>
    /// Gets or sets this cache's retained-entry limit. Must be positive; defaults to 128.
    /// Readers own their streams and can finish reading an entry after its cache slot is evicted.
    /// </summary>
    public int MaxCacheEntries
    {
        get { lock (_gate) return _maxCacheEntries; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (_gate)
            {
                _maxCacheEntries = value;
                TrimToBudget();
            }
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Uri, LinkedListNode<RamCacheEntry>> _cache = new();
    private readonly LinkedList<RamCacheEntry> _oldestFirst = new();
    private long _maxCacheSizeBytes = 64L * 1024L * 1024L;
    private int _maxCacheEntries = 128;
    private long _retainedBytes;

    internal int CachedEntryCount { get { lock (_gate) return _cache.Count; } }
    internal long CachedByteCount { get { lock (_gate) return _retainedBytes; } }

    /// <inheritdoc/>
    public override Task<AsyncImageLoaderCacheEntry?> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_cache.TryGetValue(uri, out var node))
            {
                var cachedEntry = node.Value;
                cachedEntry.Metadata.LastAccessedAt = DateTimeOffset.UtcNow;
                MarkUsed(node);
                return Task.FromResult<AsyncImageLoaderCacheEntry?>(
                    new AsyncImageLoaderCacheEntry(new MemoryStream(cachedEntry.Bytes, false), cachedEntry.Metadata.Clone()));
            }

            return Task.FromResult<AsyncImageLoaderCacheEntry?>(null);
        }
    }

    /// <inheritdoc/>
    public override async Task SetAsync(Uri uri, Stream stream, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.NoStore)
        {
            await RemoveAsync(uri, cancellationToken).ConfigureAwait(false);
            return;
        }

        long limit;
        lock (_gate) limit = WriteLimit();
        if (stream.CanSeek && stream.Length - stream.Position > limit)
        {
            await RemoveAsync(uri, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = await ReadBoundedAsync(stream, limit, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null)
        {
            await RemoveAsync(uri, cancellationToken).ConfigureAwait(false);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var storedMetadata = metadata.Clone();
        storedMetadata.SourceUri = uri.OriginalString;
        storedMetadata.StoredAt = now;
        storedMetadata.LastAccessedAt = now;
        storedMetadata.Length = bytes.LongLength;
        storedMetadata.BodyHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A limit can be reduced while an asynchronous read is in flight. Recheck before publication.
            Remove(uri);
            if (bytes.LongLength > WriteLimit()) return;
            _cache.Add(uri, _oldestFirst.AddLast(new RamCacheEntry(uri, bytes, storedMetadata)));
            _retainedBytes += bytes.LongLength;
            TrimToBudget();
        }
    }

    /// <inheritdoc/>
    public override Task TouchAsync(Uri uri, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (metadata.NoStore)
            {
                Remove(uri);
            }
            else if (_cache.TryGetValue(uri, out var node))
            {
                node.Value.Metadata.UpdateFromValidation(metadata);
                MarkUsed(node);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RemoveAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Remove(uri);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _cache.Clear();
            _oldestFirst.Clear();
            _retainedBytes = 0;
        }

        return Task.CompletedTask;
    }

    private long WriteLimit()
    {
        var perEntry = MaxEntrySizeBytes;
        return Math.Min(int.MaxValue, perEntry > 0 ? Math.Min(perEntry, _maxCacheSizeBytes) : _maxCacheSizeBytes);
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        // For unknown lengths read at most one byte beyond the bound, without buffering that extra byte.
        // Known oversized sources are rejected before arriving here. The caller keeps ownership of stream.
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(81920, limit + 1));
        try
        {
            while (true)
            {
                var remaining = limit - memory.Length;
                var wanted = (int)Math.Min(buffer.Length, remaining + 1);
                var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0) return memory.ToArray();
                if (read > remaining) return null;
                memory.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void MarkUsed(LinkedListNode<RamCacheEntry> node)
    {
        _oldestFirst.Remove(node);
        _oldestFirst.AddLast(node);
    }

    private void Remove(Uri uri)
    {
        if (!_cache.Remove(uri, out var node)) return;
        _oldestFirst.Remove(node);
        _retainedBytes -= node.Value.Bytes.LongLength;
    }

    private void TrimToBudget()
    {
        while ((_cache.Count > _maxCacheEntries || _retainedBytes > _maxCacheSizeBytes) && _oldestFirst.First is { } node)
            Remove(node.Value.Uri);
    }

    private sealed record RamCacheEntry(Uri Uri, byte[] Bytes, AsyncImageLoaderCacheMetadata Metadata);
}
