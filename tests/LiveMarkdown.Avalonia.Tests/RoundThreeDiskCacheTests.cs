using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture, NonParallelizable]
public class RoundThreeDiskCacheTests
{
    private string oldDirectory = null!;
    private long oldSize;
    private long oldEntrySize;
    private TimeSpan oldInterval;
    private TimeProvider oldClock = null!;
    private readonly List<DirectoryInfo> directories = [];
    private ManualClock clock = null!;
    private FileBasedAsyncImageLoaderCache cache = null!;

    [SetUp]
    public void SetUp()
    {
        oldDirectory = FileBasedAsyncImageLoaderCache.CacheDirectory;
        oldSize = FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes;
        oldEntrySize = FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes;
        oldInterval = FileBasedAsyncImageLoaderCache.CleanupInterval;
        oldClock = FileBasedAsyncImageLoaderCache.Clock;
        FileBasedAsyncImageLoaderCache.CacheDirectory = NewDirectory();
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 4096;
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 1024;
        FileBasedAsyncImageLoaderCache.CleanupInterval = TimeSpan.FromHours(1);
        FileBasedAsyncImageLoaderCache.Clock = clock = new ManualClock();
        cache = new FileBasedAsyncImageLoaderCache();
    }

    [TearDown]
    public void TearDown()
    {
        FileBasedAsyncImageLoaderCache.BeforeMissingEntryAccounting = null;
        FileBasedAsyncImageLoaderCache.CacheDirectory = oldDirectory;
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = oldSize;
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = oldEntrySize;
        FileBasedAsyncImageLoaderCache.CleanupInterval = oldInterval;
        FileBasedAsyncImageLoaderCache.Clock = oldClock;
        foreach (var directory in directories)
            if (Directory.Exists(directory.FullName)) Directory.Delete(directory.FullName, recursive: true);
        directories.Clear();
    }

    [Test]
    public async Task Writes_scan_once_then_update_local_accounting_without_full_scans()
    {
        var before = FileBasedAsyncImageLoaderCache.DirectoryScanCount;
        for (var i = 0; i < 48; i++) await Store(i, 64);
        Assert.That(FileBasedAsyncImageLoaderCache.DirectoryScanCount - before, Is.EqualTo(1));
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
        Assert.That(await TotalBytes(Enumerable.Range(0, 48)), Is.EqualTo(48 * 64));
    }

    [Test]
    public async Task Concurrent_instances_enforce_the_local_cap_without_repeated_scans()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 32;
        var before = FileBasedAsyncImageLoaderCache.DirectoryScanCount;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            using var source = new MemoryStream(new byte[8]);
            await new FileBasedAsyncImageLoaderCache().SetAsync(UriFor(i), source, Metadata(i), CancellationToken.None);
        }));
        Assert.That(await TotalBytes(Enumerable.Range(0, 16)), Is.EqualTo(32));
        Assert.That(FileBasedAsyncImageLoaderCache.DirectoryScanCount - before, Is.EqualTo(1));
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
    }

    [Test]
    public async Task Replacements_removal_and_no_store_touch_adjust_body_bytes_exactly()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 8;
        await Store(1, 6);
        await Store(1, 2);
        await Store(2, 6);
        Assert.That(await TotalBytes([1, 2]), Is.EqualTo(8));
        await cache.RemoveAsync(UriFor(1), CancellationToken.None);
        await Store(3, 2);
        Assert.That(await TotalBytes([1, 2, 3]), Is.EqualTo(8));
        var validation = Metadata(2);
        validation.NoStore = true;
        await cache.TouchAsync(UriFor(2), validation, CancellationToken.None);
        await Store(4, 6);
        Assert.That(await TotalBytes([1, 2, 3, 4]), Is.EqualTo(8));
        Assert.That(await cache.GetAsync(UriFor(2), CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Accesses_preserve_live_lru_order_without_sleeping_or_scanning()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 12;
        await Store(1, 6);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Store(2, 6);
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var first = await cache.GetAsync(UriFor(1), CancellationToken.None)) Assert.That(first, Is.Not.Null);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Store(3, 6);
        Assert.That(await cache.GetAsync(UriFor(2), CancellationToken.None), Is.Null);
        Assert.That(await TotalBytes([1, 3]), Is.EqualTo(12));
    }

    [Test]
    public async Task External_writes_are_reconciled_at_the_configured_interval()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 8;
        await Store(99, 6);
        var externalPath = EntryPath(99);
        var externalBytes = await File.ReadAllBytesAsync(externalPath);
        await cache.RemoveAsync(UriFor(99), CancellationToken.None);
        await Store(1, 4);
        Directory.CreateDirectory(Path.GetDirectoryName(externalPath)!);
        await File.WriteAllBytesAsync(externalPath, externalBytes); // a writer outside this cache instance
        var scans = FileBasedAsyncImageLoaderCache.DirectoryScanCount;
        await Store(1, 4);
        Assert.That(FileBasedAsyncImageLoaderCache.DirectoryScanCount, Is.EqualTo(scans));
        clock.Advance(TimeSpan.FromHours(1));
        await Store(2, 1);
        Assert.That(FileBasedAsyncImageLoaderCache.DirectoryScanCount, Is.EqualTo(scans + 1));
        Assert.That(await TotalBytes([1, 2, 99]), Is.LessThanOrEqualTo(8));
    }

    [Test]
    public async Task Directory_and_budget_changes_force_reconciliation_and_clear_resets_accounting()
    {
        await Store(1, 16);
        var scans = FileBasedAsyncImageLoaderCache.DirectoryScanCount;
        FileBasedAsyncImageLoaderCache.CacheDirectory = NewDirectory();
        await Store(2, 4);
        Assert.That(FileBasedAsyncImageLoaderCache.DirectoryScanCount, Is.EqualTo(scans + 1));
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 6;
        await Store(3, 4);
        Assert.That(FileBasedAsyncImageLoaderCache.DirectoryScanCount, Is.EqualTo(scans + 2));
        Assert.That(await TotalBytes([2, 3]), Is.LessThanOrEqualTo(6));
        await cache.ClearAsync(CancellationToken.None);
        await Store(4, 6);
        Assert.That(await TotalBytes([2, 3, 4]), Is.EqualTo(6));
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
    }

    [Test]
    public async Task Failed_cross_process_lock_acquisition_releases_its_registry_reservation()
    {
        var key = FileBasedAsyncImageLoaderCache.GetCacheKey(UriFor(1));
        var path = Path.Combine(FileBasedAsyncImageLoaderCache.CacheDirectory, ".locks", key + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var otherProcess = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Store(1, 16);
        Assert.That(File.Exists(EntryPath(1)), Is.False);
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
        await Store(1, 16);
        Assert.That(await TotalBytes([1]), Is.EqualTo(16));
    }

    [Test]
    public async Task Cancelled_waiters_do_not_retire_a_gate_still_owned_or_reserved()
    {
        var registry = new ImageCacheEntryLockRegistry();
        using var first = await registry.AcquireAsync("entry", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ReservationObserver = _ => reserved.TrySetResult();
        var waiting = registry.AcquireAsync("entry", cancellation.Token).AsTask();
        await reserved.Task;
        Assert.That(waiting.IsCompleted, Is.False);
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await waiting);
        Assert.That(registry.Count, Is.EqualTo(1));
        first.Dispose();
        Assert.That(registry.Count, Is.Zero);
    }

    [Test]
    public async Task Queued_holders_share_one_gate_until_all_reservations_are_released()
    {
        var registry = new ImageCacheEntryLockRegistry();
        var first = await registry.AcquireAsync("entry", CancellationToken.None);
        var second = registry.AcquireAsync("entry", CancellationToken.None).AsTask();
        Assert.That(second.IsCompleted, Is.False);
        Assert.That(registry.Count, Is.EqualTo(1));
        first.Dispose();
        var secondLease = await second;
        var third = registry.AcquireAsync("entry", CancellationToken.None).AsTask();
        Assert.That(third.IsCompleted, Is.False);
        Assert.That(registry.Count, Is.EqualTo(1));
        secondLease.Dispose();
        using (await third) Assert.That(registry.Count, Is.EqualTo(1));
        Assert.That(registry.Count, Is.Zero);
        for (var i = 0; i < 64; i++) using (await registry.AcquireAsync($"other-{i}", CancellationToken.None)) { }
        Assert.That(registry.Count, Is.Zero);
    }

    [Test]
    public async Task Cancelled_writes_leave_no_lock_reservations_or_accounting_changes()
    {
        await Store(1, 8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var body = new MemoryStream(new byte[16]);
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cache.SetAsync(UriFor(1), body, Metadata(1), cancellation.Token));
        Assert.That(await TotalBytes([1]), Is.EqualTo(8));
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
    }

    [Test]
    public async Task A_completed_old_streaming_transaction_is_not_removed_by_its_own_reconciliation()
    {
        using var source = new AgedTransactionStream(() =>
        {
            var transaction = Directory.EnumerateDirectories(Path.Combine(FileBasedAsyncImageLoaderCache.CacheDirectory, ".tmp")).Single();
            Directory.SetLastWriteTimeUtc(transaction, DateTime.UnixEpoch);
        });
        await cache.SetAsync(UriFor(1), source, Metadata(1), CancellationToken.None);
        Assert.That(await TotalBytes([1]), Is.EqualTo(16));
    }

    [Test]
    public async Task A_paused_miss_cannot_forget_a_concurrent_commit_and_exceed_the_cap()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 8;
        var missed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeMiss = new ManualResetEventSlim();
        FileBasedAsyncImageLoaderCache.BeforeMissingEntryAccounting = path =>
        {
            if (path != EntryPath(1)) return;
            FileBasedAsyncImageLoaderCache.BeforeMissingEntryAccounting = null;
            missed.TrySetResult();
            resumeMiss.Wait();
        };
        var reader = Task.Run(() => cache.GetAsync(UriFor(1), CancellationToken.None));
        try
        {
            await missed.Task;
            await Store(1, 8);
        }
        finally { resumeMiss.Set(); }
        await using var missing = await reader;
        Assert.That(missing, Is.Null);
        await Store(2, 8);
        Assert.That(await TotalBytes([1, 2]), Is.EqualTo(8));
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Bulk_eviction_sorts_one_batch_and_preserves_expiration_then_lru_order(bool reduceBudget)
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 48 * 8;
        for (var i = 0; i < 48; i++)
        {
            // The newest entries are expired, so they must go before older live entries.
            var metadata = Metadata(i);
            metadata.ExpiresAt = clock.GetUtcNow().AddDays(i < 32 ? 1 : -1);
            using var source = new MemoryStream(new byte[8]);
            await cache.SetAsync(UriFor(i), source, metadata, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var batches = FileBasedAsyncImageLoaderCache.EvictionBatchCount;
        var candidates = FileBasedAsyncImageLoaderCache.EvictionCandidateCount;
        if (reduceBudget)
        {
            FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 4 * 8;
            // Touch triggers reconciliation for the changed budget without adding an entry.
            await cache.TouchAsync(UriFor(100), Metadata(100), CancellationToken.None);
        }
        else await Store(99, 44 * 8);
        Assert.That(FileBasedAsyncImageLoaderCache.EvictionBatchCount - batches, Is.EqualTo(1));
        Assert.That(FileBasedAsyncImageLoaderCache.EvictionCandidateCount - candidates, Is.EqualTo(reduceBudget ? 48 : 49));
        for (var i = 32; i < 48; i++) Assert.That(File.Exists(EntryPath(i)), Is.False, $"expired entry {i}");
        // Reconciliation uses filesystem access times; only local accounting uses the injected clock.
        if (!reduceBudget)
            for (var i = 0; i < 32; i++)
                Assert.That(File.Exists(EntryPath(i)), Is.EqualTo(i >= 28), $"live entry {i}");
        Assert.That(await TotalBytes(Enumerable.Range(0, 48)), Is.EqualTo(32));
        Assert.That(await TotalBytes([99]), Is.EqualTo(reduceBudget ? 0 : 44 * 8));
        Assert.That(FileBasedAsyncImageLoaderCache.ActiveEntryLockCount, Is.Zero);
    }

    [Test]
    public async Task Equal_access_times_preserve_the_original_eviction_order()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 16 * 8;
        for (var i = 0; i < 16; i++) await Store(i, 8);
        await Store(99, 8 * 8);
        for (var i = 0; i < 16; i++)
            Assert.That(File.Exists(EntryPath(i)), Is.EqualTo(i >= 8), $"entry {i}");
        Assert.That(await TotalBytes(Enumerable.Range(0, 16).Append(99)), Is.EqualTo(16 * 8));
    }

    private string NewDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("lm-round3-disk-");
        directories.Add(directory);
        return directory.FullName;
    }
    private static Uri UriFor(int id) => new($"https://example.invalid/round3/{id}.png");
    private static AsyncImageLoaderCacheMetadata Metadata(int id) => AsyncImageLoaderCacheMetadata.Create(UriFor(id));
    private string EntryPath(int id)
    {
        var key = FileBasedAsyncImageLoaderCache.GetCacheKey(UriFor(id));
        return Path.Combine(FileBasedAsyncImageLoaderCache.CacheDirectory, key[..2], key + ".entry");
    }
    private async Task Store(int id, int length)
    {
        using var source = new MemoryStream(new byte[length]);
        await cache.SetAsync(UriFor(id), source, Metadata(id), CancellationToken.None);
    }
    private async Task<long> TotalBytes(IEnumerable<int> ids)
    {
        var total = 0L;
        foreach (var id in ids)
        {
            await using var entry = await cache.GetAsync(UriFor(id), CancellationToken.None);
            if (entry is not null) total += entry.Stream.Length;
        }
        return total;
    }
    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan amount) => now += amount;
    }
    private sealed class AgedTransactionStream(Action age) : MemoryStream(new byte[16])
    {
        private bool aged;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!aged) { age(); aged = true; }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
