using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class RamImageCacheRetentionTests
{
    private long _entryLimit;

    [SetUp]
    public void SaveEntryLimit()
    {
        _entryLimit = RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes;
        RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 32L * 1024L * 1024L;
    }

    [TearDown]
    public void RestoreEntryLimit() => RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = _entryLimit;

    [Test]
    public async Task Small_entries_survive_GC_and_readers_have_independent_positions_and_metadata()
    {
        var cache = new RamBasedAsyncImageLoaderCache();
        await Seed(cache);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await using var first = await cache.GetAsync(UriFor("seed"), CancellationToken.None);
        await using var second = await cache.GetAsync(UriFor("seed"), CancellationToken.None);
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
        var firstEntry = first!;
        var secondEntry = second!;
        firstEntry.Stream.Position = 2;
        firstEntry.Metadata.SetHttpETag("changed by reader");
        Assert.Multiple(() =>
        {
            Assert.That(secondEntry.Stream.Position, Is.Zero);
            Assert.That(secondEntry.Metadata.GetHttpETag(), Is.EqualTo("original"));
            Assert.That(secondEntry.Metadata.BodyHash, Has.Length.EqualTo(64));
            Assert.That(secondEntry.Metadata.Length, Is.EqualTo(3));
        });
        Assert.That(await Bytes(cache, "seed"), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Seed(RamBasedAsyncImageLoaderCache cache)
    {
        using var source = new MemoryStream([1, 2, 3]);
        var metadata = AsyncImageLoaderCacheMetadata.Create(UriFor("seed"));
        metadata.SetHttpETag("original");
        await cache.SetAsync(UriFor("seed"), source, metadata, CancellationToken.None);
    }

    [Test]
    public async Task Entry_and_byte_limits_evict_the_least_recently_used_item_without_flushing_the_cache()
    {
        var cache = new RamBasedAsyncImageLoaderCache { MaxCacheEntries = 2, MaxCacheSizeBytes = 6 };
        await Put(cache, "a", [1, 2]);
        await Put(cache, "b", [3, 4]);
        Assert.That(await Bytes(cache, "a"), Is.EqualTo(new byte[] { 1, 2 })); // a becomes most recent
        await Put(cache, "c", [5, 6]);
        Assert.That(await Bytes(cache, "b"), Is.Null);
        Assert.That(await Bytes(cache, "a"), Is.Not.Null);
        Assert.That(cache.CachedEntryCount, Is.EqualTo(2));
        Assert.That(cache.CachedByteCount, Is.EqualTo(4));

        await Put(cache, "d", [7, 8, 9, 10, 11]);
        Assert.That(await Bytes(cache, "a"), Is.Null);
        Assert.That(await Bytes(cache, "c"), Is.Null);
        Assert.That(await Bytes(cache, "d"), Is.EqualTo(new byte[] { 7, 8, 9, 10, 11 }));
        Assert.That(cache.CachedByteCount, Is.EqualTo(5));
        Assert.That(cache.CachedEntryCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Replacement_remove_and_clear_update_the_budget_without_invalidating_outstanding_readers()
    {
        var cache = new RamBasedAsyncImageLoaderCache { MaxCacheSizeBytes = 8 };
        await Put(cache, "a", [1, 2, 3]);
        await using var old = await cache.GetAsync(UriFor("a"), CancellationToken.None);
        await Put(cache, "a", [4, 5, 6, 7, 8]);
        Assert.That(cache.CachedEntryCount, Is.EqualTo(1));
        Assert.That(cache.CachedByteCount, Is.EqualTo(5));
        Assert.That(await Bytes(cache, "a"), Is.EqualTo(new byte[] { 4, 5, 6, 7, 8 }));

        await cache.RemoveAsync(UriFor("a"), CancellationToken.None);
        Assert.That(cache.CachedByteCount, Is.Zero);
        Assert.That(cache.CachedEntryCount, Is.Zero);
        using var copied = new MemoryStream();
        await old!.Stream.CopyToAsync(copied);
        Assert.That(copied.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));

        await Put(cache, "b", [1, 2]);
        await Put(cache, "c", [3, 4]);
        await cache.ClearAsync(CancellationToken.None);
        Assert.That(cache.CachedByteCount, Is.Zero);
        Assert.That(cache.CachedEntryCount, Is.Zero);
        Assert.That(await Bytes(cache, "c"), Is.Null);
    }

    [Test]
    public async Task Lowering_instance_budgets_trims_immediately_in_recency_order()
    {
        var cache = new RamBasedAsyncImageLoaderCache { MaxCacheSizeBytes = 10, MaxCacheEntries = 3 };
        await Put(cache, "a", [1, 2, 3]);
        await Put(cache, "b", [4, 5, 6]);
        await Put(cache, "c", [7, 8, 9]);
        await Bytes(cache, "a");
        cache.MaxCacheSizeBytes = 6;
        Assert.That(await Bytes(cache, "b"), Is.Null);
        Assert.That(cache.CachedByteCount, Is.EqualTo(6));
        cache.MaxCacheEntries = 1;
        Assert.That(await Bytes(cache, "c"), Is.Null);
        Assert.That(await Bytes(cache, "a"), Is.Not.Null);
        Assert.That(cache.CachedByteCount, Is.EqualTo(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.MaxCacheEntries = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.MaxCacheSizeBytes = 0);
    }

    [Test]
    public async Task Touch_preserves_body_validators_and_freshness_and_marks_the_item_recent()
    {
        var cache = new RamBasedAsyncImageLoaderCache { MaxCacheEntries = 2 };
        var metadata = AsyncImageLoaderCacheMetadata.Create(UriFor("a"));
        metadata.SetHttpETag("old");
        metadata.SetContentType("image/png");
        metadata.NoCache = true;
        using (var source = new MemoryStream([1, 2, 3]))
            await cache.SetAsync(UriFor("a"), source, metadata, CancellationToken.None);
        await Put(cache, "b", [4]);
        var validation = AsyncImageLoaderCacheMetadata.Create(UriFor("a"));
        validation.SetHttpETag("new");
        validation.ExpiresAt = new DateTimeOffset(2080, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await cache.TouchAsync(UriFor("a"), validation, CancellationToken.None);
        await Put(cache, "c", [5]);
        Assert.That(await Bytes(cache, "b"), Is.Null);
        await using var entry = await cache.GetAsync(UriFor("a"), CancellationToken.None);
        Assert.That(entry, Is.Not.Null);
        var cacheEntry = entry!;
        Assert.Multiple(() =>
        {
            Assert.That(cacheEntry.Metadata.GetHttpETag(), Is.EqualTo("new"));
            Assert.That(cacheEntry.Metadata.GetContentType(), Is.EqualTo("image/png"));
            Assert.That(cacheEntry.Metadata.ExpiresAt, Is.EqualTo(validation.ExpiresAt));
            Assert.That(cacheEntry.Metadata.NoCache, Is.False);
            Assert.That(cacheEntry.Metadata.Length, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task No_store_writes_and_validation_remove_old_bytes_without_reading_the_source()
    {
        var cache = new RamBasedAsyncImageLoaderCache();
        await Put(cache, "a", [1]);
        using var source = new ReadCounterStream([2, 3], seekable: false);
        var noStore = AsyncImageLoaderCacheMetadata.Create(UriFor("a"));
        noStore.NoStore = true;
        await cache.SetAsync(UriFor("a"), source, noStore, CancellationToken.None);
        Assert.That(source.BytesRead, Is.Zero);
        Assert.That(source.Disposed, Is.False);
        Assert.That(await Bytes(cache, "a"), Is.Null);

        await Put(cache, "a", [1]);
        await cache.TouchAsync(UriFor("a"), noStore, CancellationToken.None);
        Assert.That(cache.CachedByteCount, Is.Zero);
        Assert.That(await Bytes(cache, "a"), Is.Null);
    }

    [Test]
    public async Task Oversized_seekable_source_is_rejected_before_reading_and_invalidates_an_old_entry()
    {
        RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 4;
        var cache = new RamBasedAsyncImageLoaderCache();
        await Put(cache, "a", [1]);
        using var source = new ReadCounterStream(new byte[64 * 1024], seekable: true);
        await cache.SetAsync(UriFor("a"), source, AsyncImageLoaderCacheMetadata.Create(UriFor("a")), CancellationToken.None);
        Assert.That(source.BytesRead, Is.Zero);
        Assert.That(source.Disposed, Is.False);
        Assert.That(await Bytes(cache, "a"), Is.Null);
        Assert.That(cache.CachedByteCount, Is.Zero);
    }

    [Test]
    public async Task Unknown_length_source_reads_at_most_one_byte_past_the_limit()
    {
        RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 4;
        var cache = new RamBasedAsyncImageLoaderCache();
        using var source = new ReadCounterStream(new byte[64 * 1024], seekable: false);
        await cache.SetAsync(UriFor("a"), source, AsyncImageLoaderCacheMetadata.Create(UriFor("a")), CancellationToken.None);
        Assert.That(source.BytesRead, Is.EqualTo(5));
        Assert.That(source.Disposed, Is.False);
        Assert.That(await Bytes(cache, "a"), Is.Null);

        using var exact = new ReadCounterStream([1, 2, 3, 4], seekable: false);
        await cache.SetAsync(UriFor("a"), exact, AsyncImageLoaderCacheMetadata.Create(UriFor("a")), CancellationToken.None);
        Assert.That(exact.BytesRead, Is.EqualTo(4));
        Assert.That(await Bytes(cache, "a"), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public async Task Seekable_source_limit_applies_to_the_remaining_bytes_not_the_total_length()
    {
        RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 2;
        var cache = new RamBasedAsyncImageLoaderCache();
        using var source = new ReadCounterStream([1, 2, 3, 4], seekable: true) { Position = 2 };
        await cache.SetAsync(UriFor("a"), source, AsyncImageLoaderCacheMetadata.Create(UriFor("a")), CancellationToken.None);
        Assert.That(source.BytesRead, Is.EqualTo(2));
        Assert.That(await Bytes(cache, "a"), Is.EqualTo(new byte[] { 3, 4 }));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task Disabled_per_entry_limit_still_observes_the_instance_aggregate_budget(long limit)
    {
        RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = limit;
        var cache = new RamBasedAsyncImageLoaderCache { MaxCacheSizeBytes = 4 };
        await Put(cache, "a", [1, 2, 3]);
        Assert.That(await Bytes(cache, "a"), Is.Not.Null);
        using var source = new ReadCounterStream(new byte[100], seekable: false);
        await cache.SetAsync(UriFor("b"), source, AsyncImageLoaderCacheMetadata.Create(UriFor("b")), CancellationToken.None);
        Assert.That(source.BytesRead, Is.EqualTo(5));
        Assert.That(await Bytes(cache, "b"), Is.Null);
        Assert.That(cache.CachedByteCount, Is.EqualTo(3));
    }

    [Test]
    public async Task In_flight_write_rechecks_a_reduced_budget_before_publishing()
    {
        var cache = new RamBasedAsyncImageLoaderCache { MaxCacheSizeBytes = 8 };
        using var source = new GatedStream([1, 2, 3, 4]);
        var write = cache.SetAsync(UriFor("a"), source, AsyncImageLoaderCacheMetadata.Create(UriFor("a")), CancellationToken.None);
        await source.Started.Task;
        cache.MaxCacheSizeBytes = 2;
        source.Release.TrySetResult();
        await write;
        Assert.That(cache.CachedByteCount, Is.Zero);
        Assert.That(await Bytes(cache, "a"), Is.Null);
    }

    [Test]
    public async Task Cancellation_during_a_read_preserves_the_old_entry_and_does_not_dispose_the_input()
    {
        var cache = new RamBasedAsyncImageLoaderCache();
        await Put(cache, "a", [9]);
        using var source = new GatedStream([1, 2, 3, 4]);
        using var cancellation = new CancellationTokenSource();
        var write = cache.SetAsync(UriFor("a"), source, AsyncImageLoaderCacheMetadata.Create(UriFor("a")), cancellation.Token);
        await source.Started.Task;
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await write);
        Assert.That(source.Disposed, Is.False);
        Assert.That(await Bytes(cache, "a"), Is.EqualTo(new byte[] { 9 }));
    }

    private static Uri UriFor(string id) => new("https://example.invalid/" + id + ".png");

    private static async Task Put(RamBasedAsyncImageLoaderCache cache, string id, byte[] bytes)
    {
        using var source = new MemoryStream(bytes);
        await cache.SetAsync(UriFor(id), source, AsyncImageLoaderCacheMetadata.Create(UriFor(id)), CancellationToken.None);
    }

    private static async Task<byte[]?> Bytes(RamBasedAsyncImageLoaderCache cache, string id)
    {
        await using var entry = await cache.GetAsync(UriFor(id), CancellationToken.None);
        if (entry is null) return null;
        using var copy = new MemoryStream();
        await entry.Stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private class ReadCounterStream(byte[] bytes, bool seekable) : Stream
    {
        private readonly MemoryStream _source = new(bytes);
        public long BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? _source.Length : throw new NotSupportedException();
        public override long Position { get => seekable ? _source.Position : throw new NotSupportedException(); set { if (!seekable) throw new NotSupportedException(); _source.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) { var read = _source.Read(buffer, offset, count); BytesRead += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _source.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => seekable ? _source.Seek(offset, origin) : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) _source.Dispose(); base.Dispose(disposing); }
    }

    private sealed class GatedStream(byte[] bytes) : ReadCounterStream(bytes, seekable: false)
    {
        private bool _first = true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
