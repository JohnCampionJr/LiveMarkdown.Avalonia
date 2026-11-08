using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
public class AsyncImageLoaderCacheTests
{
    private string? _cacheDirectory;
    private string? _originalCacheDirectory;
    private long _originalMaxCacheSizeBytes;
    private long _originalMaxEntrySizeBytes;
    private TimeSpan _originalDefaultFreshnessLifetime;
    private TimeSpan _originalCleanupInterval;
    private string[] _originalCacheableSchemes = [];

    [SetUp]
    public void SetUp()
    {
        _originalCacheDirectory = FileBasedAsyncImageLoaderCache.CacheDirectory;
        _originalMaxCacheSizeBytes = FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes;
        _originalMaxEntrySizeBytes = FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes;
        _originalDefaultFreshnessLifetime = FileBasedAsyncImageLoaderCache.DefaultFreshnessLifetime;
        _originalCleanupInterval = FileBasedAsyncImageLoaderCache.CleanupInterval;
        _originalCacheableSchemes = FileBasedAsyncImageLoaderCache.CacheableSchemes.ToArray();

        _cacheDirectory = Path.Combine(Path.GetTempPath(), "LiveMarkdown.Avalonia.Tests", Guid.NewGuid().ToString("N"));
        FileBasedAsyncImageLoaderCache.CacheDirectory = _cacheDirectory;
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 256L * 1024L * 1024L;
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 32L * 1024L * 1024L;
        FileBasedAsyncImageLoaderCache.DefaultFreshnessLifetime = TimeSpan.FromDays(7);
        FileBasedAsyncImageLoaderCache.CleanupInterval = TimeSpan.Zero;
        FileBasedAsyncImageLoaderCache.CacheableSchemes = ImmutableHashSet.Create<string>("http", "https");
    }

    [TearDown]
    public void TearDown()
    {
        FileBasedAsyncImageLoaderCache.CacheDirectory = _originalCacheDirectory!;
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = _originalMaxCacheSizeBytes;
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = _originalMaxEntrySizeBytes;
        FileBasedAsyncImageLoaderCache.DefaultFreshnessLifetime = _originalDefaultFreshnessLifetime;
        FileBasedAsyncImageLoaderCache.CleanupInterval = _originalCleanupInterval;

        var cacheableSchemesBuilder = ImmutableHashSet.CreateBuilder<string>();
        foreach (var scheme in _originalCacheableSchemes)
        {
            cacheableSchemesBuilder.Add(scheme);
        }
        FileBasedAsyncImageLoaderCache.CacheableSchemes = cacheableSchemesBuilder.ToImmutable();

        if (_cacheDirectory is not null && Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, true);
        }
    }

    [Test]
    public void Metadata_CloneDeepCopiesProperties()
    {
        var uri = new Uri("https://example.com/image.png");
        var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
        metadata.SetHttpETag("\"v1\"");
        metadata.SetContentType("image/png");

        var clone = metadata.Clone();
        clone.SetHttpETag("\"v2\"");

        Assert.That(metadata.GetHttpETag(), Is.EqualTo("\"v1\""));
        Assert.That(clone.GetHttpETag(), Is.EqualTo("\"v2\""));
        Assert.That(clone.GetContentType(), Is.EqualTo("image/png"));
    }

    [Test]
    public void Metadata_UpdateFromValidationMergesProperties()
    {
        var uri = new Uri("https://example.com/image.png");
        var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
        metadata.SetHttpETag("\"old\"");
        metadata.SetContentType("image/png");

        var validationMetadata = AsyncImageLoaderCacheMetadata.Create(uri);
        validationMetadata.SetHttpETag("\"new\"");
        validationMetadata.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        metadata.UpdateFromValidation(validationMetadata);

        Assert.That(metadata.GetHttpETag(), Is.EqualTo("\"new\""));
        Assert.That(metadata.GetContentType(), Is.EqualTo("image/png"));
        Assert.That(metadata.ExpiresAt, Is.EqualTo(validationMetadata.ExpiresAt));
    }

    [Test]
    public async Task RamCache_ReturnsIndependentReadableStreams()
    {
        var cache = new RamBasedAsyncImageLoaderCache();
        var uri = new Uri("https://example.com/image.png");
        var bytes = Encoding.UTF8.GetBytes("image-bytes");

        await cache.SetAsync(uri, new MemoryStream(bytes), AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);

        var first = await cache.GetAsync(uri, CancellationToken.None);
        var second = await cache.GetAsync(uri, CancellationToken.None);

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);

        await using var firstEntry = first!;
        await using var secondEntry = second!;

        firstEntry.Stream.Position = 3;
        Assert.That(await ReadAllAsync(firstEntry.Stream), Is.EqualTo(bytes[3..]));
        Assert.That(await ReadAllAsync(secondEntry.Stream), Is.EqualTo(bytes));
    }

    [Test]
    public void RamCache_ObservesCancellation()
    {
        var cache = new RamBasedAsyncImageLoaderCache();
        var uri = new Uri("https://example.com/image.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await cache.GetAsync(uri, cts.Token));
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await cache.SetAsync(uri, new MemoryStream([1, 2, 3]), AsyncImageLoaderCacheMetadata.Create(uri), cts.Token));
    }

    [Test]
    public async Task FileCache_PersistsEntriesAcrossInstances()
    {
        var uri = new Uri("https://example.com/a.png");
        var bytes = Encoding.UTF8.GetBytes("persisted-image");

        await new FileBasedAsyncImageLoaderCache().SetAsync(uri, new MemoryStream(bytes), AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);
        var entry = await new FileBasedAsyncImageLoaderCache().GetAsync(uri, CancellationToken.None);

        Assert.That(entry, Is.Not.Null);
        await using var cacheEntry = entry!;
        Assert.That(await ReadAllAsync(cacheEntry.Stream), Is.EqualTo(bytes));
        Assert.That(cacheEntry.Metadata.BodyHash, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task FileCache_UsesStableSha256Path()
    {
        var uri = new Uri("https://example.com/a.png?size=large");
        var key = FileBasedAsyncImageLoaderCache.GetCacheKey(uri);

        await new FileBasedAsyncImageLoaderCache().SetAsync(
            uri,
            new MemoryStream(Encoding.UTF8.GetBytes("image")),
            AsyncImageLoaderCacheMetadata.Create(uri),
            CancellationToken.None);

        Assert.That(key, Does.Match("^[a-f0-9]{64}$"));
        Assert.That(File.Exists(Path.Combine(_cacheDirectory!, key[..2], key + ".entry")), Is.True);
        Assert.That(File.Exists(Path.Combine(_cacheDirectory!, key[..2], key + ".data")), Is.False);
        Assert.That(File.Exists(Path.Combine(_cacheDirectory!, key[..2], key + ".json")), Is.False);
    }

    [Test]
    public async Task FileCache_GetMissDoesNotCreateShardDirectory()
    {
        var uri = new Uri("https://example.com/missing.png");

        var entry = await new FileBasedAsyncImageLoaderCache().GetAsync(uri, CancellationToken.None);

        Assert.That(entry, Is.Null);
        Assert.That(Directory.Exists(GetShardDirectory(uri)), Is.False);
    }

    [Test]
    public async Task FileCache_RemoveMissDoesNotCreateShardDirectory()
    {
        var uri = new Uri("https://example.com/remove-missing.png");

        await new FileBasedAsyncImageLoaderCache().RemoveAsync(uri, CancellationToken.None);

        Assert.That(Directory.Exists(GetShardDirectory(uri)), Is.False);
    }

    [Test]
    public async Task FileCache_TouchMissDoesNotCreateShardDirectory()
    {
        var uri = new Uri("https://example.com/touch-missing.png");

        await new FileBasedAsyncImageLoaderCache().TouchAsync(uri, AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);

        Assert.That(Directory.Exists(GetShardDirectory(uri)), Is.False);
    }

    [Test]
    public async Task FileCache_FailedWriteDoesNotCreateShardDirectory()
    {
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 2;
        var uri = new Uri("https://example.com/too-large.png");

        await new FileBasedAsyncImageLoaderCache().SetAsync(
            uri,
            new MemoryStream(Encoding.UTF8.GetBytes("large")),
            AsyncImageLoaderCacheMetadata.Create(uri),
            CancellationToken.None);

        Assert.That(Directory.Exists(GetShardDirectory(uri)), Is.False);
    }

    [Test]
    public async Task FileCache_ThrowingWriteDoesNotCreateShardDirectory()
    {
        var uri = new Uri("https://example.com/throwing-write.png");

        await new FileBasedAsyncImageLoaderCache().SetAsync(
            uri,
            new ThrowingReadStream(),
            AsyncImageLoaderCacheMetadata.Create(uri),
            CancellationToken.None);

        Assert.That(Directory.Exists(GetShardDirectory(uri)), Is.False);
    }

    [Test]
    public void FileCache_CancelledWriteDoesNotCreateShardDirectory()
    {
        var uri = new Uri("https://example.com/cancelled-write.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new FileBasedAsyncImageLoaderCache().SetAsync(
                uri,
                new MemoryStream(Encoding.UTF8.GetBytes("image")),
                AsyncImageLoaderCacheMetadata.Create(uri),
                cts.Token));
        Assert.That(Directory.Exists(GetShardDirectory(uri)), Is.False);
    }

    [Test]
    public async Task FileCache_DoesNotLeaveLockFileAfterWrite()
    {
        var uri = new Uri("https://example.com/no-lock-left-behind.png");

        await new FileBasedAsyncImageLoaderCache().SetAsync(
            uri,
            new MemoryStream(Encoding.UTF8.GetBytes("image")),
            AsyncImageLoaderCacheMetadata.Create(uri),
            CancellationToken.None);

        Assert.That(File.Exists(GetLockPath(uri)), Is.False);
    }

    [Test]
    public async Task FileCache_CleanupDeletesLegacySidecarsAndTemporaryFiles()
    {
        var legacyUri = new Uri("https://example.com/legacy-cache.png");
        var legacyDataPath = GetCachePath(legacyUri, ".data");
        var legacyMetadataPath = GetCachePath(legacyUri, ".json");
        var legacyTempPath = GetCachePath(legacyUri, ".tmp");
        var legacyLockPath = GetCachePath(legacyUri, ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyDataPath)!);
        await File.WriteAllTextAsync(legacyDataPath, "legacy-body", CancellationToken.None);
        await File.WriteAllTextAsync(legacyMetadataPath, "{}", CancellationToken.None);
        await File.WriteAllTextAsync(legacyTempPath, string.Empty, CancellationToken.None);
        await File.WriteAllTextAsync(legacyLockPath, string.Empty, CancellationToken.None);

        var triggerUri = new Uri("https://example.com/trigger-legacy-cleanup.png");
        await new FileBasedAsyncImageLoaderCache().SetAsync(
            triggerUri,
            new MemoryStream(Encoding.UTF8.GetBytes("image")),
            AsyncImageLoaderCacheMetadata.Create(triggerUri),
            CancellationToken.None);

        Assert.That(File.Exists(legacyDataPath), Is.False);
        Assert.That(File.Exists(legacyMetadataPath), Is.False);
        Assert.That(File.Exists(legacyTempPath), Is.False);
        Assert.That(File.Exists(legacyLockPath), Is.False);
    }

    [Test]
    public async Task FileCache_CleanupRemovesStaleLockFilesWhenSizeEvictionIsDisabled()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 0;
        var staleUri = new Uri("https://example.com/stale-lock.png");
        var lockPath = GetLockPath(staleUri);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "stale", CancellationToken.None);

        var triggerUri = new Uri("https://example.com/trigger-cleanup.png");
        await new FileBasedAsyncImageLoaderCache().SetAsync(
            triggerUri,
            new MemoryStream(Encoding.UTF8.GetBytes("image")),
            AsyncImageLoaderCacheMetadata.Create(triggerUri),
            CancellationToken.None);

        Assert.That(File.Exists(lockPath), Is.False);
    }

    [Test]
    public async Task FileCache_CleanupRemovesEmptyShardDirectories()
    {
        var emptyShard = Path.Combine(_cacheDirectory!, "0e");
        Directory.CreateDirectory(emptyShard);

        var triggerUri = new Uri("https://example.com/trigger-empty-shard-cleanup.png");
        await new FileBasedAsyncImageLoaderCache().SetAsync(
            triggerUri,
            new MemoryStream(Encoding.UTF8.GetBytes("image")),
            AsyncImageLoaderCacheMetadata.Create(triggerUri),
            CancellationToken.None);

        Assert.That(Directory.Exists(emptyShard), Is.False);
    }

    [Test]
    public async Task FileCache_TreatsCorruptEntryAsMiss()
    {
        var cache = new FileBasedAsyncImageLoaderCache();
        var uri = new Uri("https://example.com/corrupt.png");
        var entryPath = GetCachePath(uri, ".entry");

        await cache.SetAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes("good-body")), AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);
        await File.WriteAllTextAsync(entryPath, "bad-entry", CancellationToken.None);

        var entry = await cache.GetAsync(uri, CancellationToken.None);

        Assert.That(entry, Is.Null);
        Assert.That(File.Exists(entryPath), Is.False);
    }

    [Test]
    public async Task FileCache_EvictsLeastRecentlyUsedEntriesWhenOverCapacity()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 10;
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 100;
        FileBasedAsyncImageLoaderCache.CleanupInterval = TimeSpan.FromHours(1);
        var oldClock = FileBasedAsyncImageLoaderCache.Clock;
        var clock = new RoundThreeDiskCacheTests.ManualClock();
        FileBasedAsyncImageLoaderCache.Clock = clock;
        try
        {
            var cache = new FileBasedAsyncImageLoaderCache();
            var firstUri = new Uri("https://example.com/first.png");
            var secondUri = new Uri("https://example.com/second.png");
            using var firstBody = new MemoryStream(Encoding.UTF8.GetBytes("123456"));
            await cache.SetAsync(firstUri, firstBody, AsyncImageLoaderCacheMetadata.Create(firstUri), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
            using var secondBody = new MemoryStream(Encoding.UTF8.GetBytes("abcdef"));
            await cache.SetAsync(secondUri, secondBody, AsyncImageLoaderCacheMetadata.Create(secondUri), CancellationToken.None);
            await using var firstEntry = await cache.GetAsync(firstUri, CancellationToken.None);
            await using var secondEntry = await cache.GetAsync(secondUri, CancellationToken.None);
            Assert.That(firstEntry, Is.Null);
            Assert.That(secondEntry, Is.Not.Null);
        }
        finally { FileBasedAsyncImageLoaderCache.Clock = oldClock; }
    }

    [Test]
    public async Task FileCache_DoesNotStoreNoStoreResponses()
    {
        var cache = new FileBasedAsyncImageLoaderCache();
        var uri = new Uri("https://example.com/no-store.png");
        var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
        metadata.NoStore = true;
        metadata.SetHttpCacheControl("no-store");

        await cache.SetAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes("body")), metadata, CancellationToken.None);

        Assert.That(await cache.GetAsync(uri, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task FileCache_TouchUpdatesValidationMetadataAndKeepsBody()
    {
        var cache = new FileBasedAsyncImageLoaderCache();
        var uri = new Uri("https://example.com/validated.png");
        var bytes = Encoding.UTF8.GetBytes("cached-body");
        var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
        metadata.SetHttpETag("\"old\"");
        metadata.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        await cache.SetAsync(uri, new MemoryStream(bytes), metadata, CancellationToken.None);

        var validationMetadata = AsyncImageLoaderCacheMetadata.Create(uri);
        validationMetadata.SetHttpETag("\"new\"");
        validationMetadata.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await cache.TouchAsync(uri, validationMetadata, CancellationToken.None);

        var entry = await cache.GetAsync(uri, CancellationToken.None);

        Assert.That(entry, Is.Not.Null);
        await using var cacheEntry = entry!;
        Assert.That(cacheEntry.Metadata.GetHttpETag(), Is.EqualTo("\"new\""));
        Assert.That(await ReadAllAsync(cacheEntry.Stream), Is.EqualTo(bytes));
    }

    [Test]
    public async Task FileCache_DisablesSizeEvictionWhenMaxCacheSizeIsNotPositive()
    {
        FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 0;
        FileBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 100;

        var cache = new FileBasedAsyncImageLoaderCache();
        var firstUri = new Uri("https://example.com/first.png");
        var secondUri = new Uri("https://example.com/second.png");

        await cache.SetAsync(firstUri, new MemoryStream(Encoding.UTF8.GetBytes("123456")), AsyncImageLoaderCacheMetadata.Create(firstUri), CancellationToken.None);
        await cache.SetAsync(secondUri, new MemoryStream(Encoding.UTF8.GetBytes("abcdef")), AsyncImageLoaderCacheMetadata.Create(secondUri), CancellationToken.None);

        var firstEntry = await cache.GetAsync(firstUri, CancellationToken.None);
        var secondEntry = await cache.GetAsync(secondUri, CancellationToken.None);

        Assert.That(firstEntry, Is.Not.Null);
        Assert.That(secondEntry, Is.Not.Null);

        if (firstEntry is not null) await firstEntry.DisposeAsync();
        if (secondEntry is not null) await secondEntry.DisposeAsync();
    }

    [Test]
    public async Task FileCache_AllowsConcurrentWritesFromMultipleInstances()
    {
        var uri = new Uri("https://example.com/concurrent.png");
        var bodies = Enumerable.Range(0, 8)
            .Select(static index => Encoding.UTF8.GetBytes($"body-{index}"))
            .ToArray();

        await Task.WhenAll(
            bodies.Select(
                body =>
                {
                    var cache = new FileBasedAsyncImageLoaderCache();
                    return cache.SetAsync(
                        uri,
                        new MemoryStream(body),
                        AsyncImageLoaderCacheMetadata.Create(uri),
                        CancellationToken.None);
                }));

        var entry = await new FileBasedAsyncImageLoaderCache().GetAsync(uri, CancellationToken.None);

        Assert.That(entry, Is.Not.Null);
        await using var cacheEntry = entry!;
        var cachedBody = await ReadAllAsync(cacheEntry.Stream);
        Assert.That(bodies.Any(body => body.SequenceEqual(cachedBody)), Is.True);
    }

    [Test]
    public async Task HttpHandler_SendsValidatorsAndReportsNotModified()
    {
        var messageHandler = new RecordingMessageHandler();
        messageHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotModified));
        var handler = new HttpAsyncImageLoaderHandler(new HttpClient(messageHandler));
        var uri = new Uri("https://example.com/image.png");
        var lastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
        metadata.SetHttpETag("\"v1\"");
        metadata.SetHttpLastModified(lastModified);

        await using var response = await handler.LoadAsync(uri, metadata, CancellationToken.None);

        Assert.That(response.IsNotModified, Is.True);
        Assert.That(messageHandler.LastIfNoneMatch, Does.Contain("\"v1\""));
        Assert.That(messageHandler.LastIfModifiedSince, Is.EqualTo(lastModified));
    }

    [Test]
    public async Task HttpHandler_DoesNotSendValidatorsWhenConditionalRequestsAreDisabled()
    {
        var messageHandler = new RecordingMessageHandler();
        messageHandler.Enqueue(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("body"))
            });
        var handler = new HttpAsyncImageLoaderHandler(new HttpClient(messageHandler))
        {
            EnableConditionalRequests = false
        };
        var uri = new Uri("https://example.com/image.png");
        var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
        metadata.SetHttpETag("\"v1\"");
        metadata.SetHttpLastModified(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await using var response = await handler.LoadAsync(uri, metadata, CancellationToken.None);

        Assert.That(messageHandler.LastIfNoneMatch, Is.Empty);
        Assert.That(messageHandler.LastIfModifiedSince, Is.Null);
    }

    [Test]
    public async Task HttpHandler_ExtractsFreshnessHeaders()
    {
        var messageHandler = new RecordingMessageHandler();
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("http-body"))
        };
        httpResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        httpResponse.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
        httpResponse.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        httpResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        messageHandler.Enqueue(httpResponse);
        var handler = new HttpAsyncImageLoaderHandler(new HttpClient(messageHandler));

        await using var response = await handler.LoadAsync(new Uri("https://example.com/image.png"), null, CancellationToken.None);

        Assert.That(response.IsNotModified, Is.False);
        Assert.That(response.Metadata.GetHttpETag(), Is.EqualTo("\"v2\""));
        Assert.That(response.Metadata.GetHttpLastModified(), Is.EqualTo(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        Assert.That(response.Metadata.GetContentType(), Is.EqualTo("image/png"));
        Assert.That(response.Metadata.ExpiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));
        Assert.That(await ReadAllAsync(response.Stream!), Is.EqualTo(Encoding.UTF8.GetBytes("http-body")));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }

    private string GetCachePath(Uri uri, string extension)
    {
        var key = FileBasedAsyncImageLoaderCache.GetCacheKey(uri);
        return Path.Combine(_cacheDirectory!, key[..2], key + extension);
    }

    private string GetShardDirectory(Uri uri)
    {
        var key = FileBasedAsyncImageLoaderCache.GetCacheKey(uri);
        return Path.Combine(_cacheDirectory!, key[..2]);
    }

    private string GetLockPath(Uri uri)
    {
        var key = FileBasedAsyncImageLoaderCache.GetCacheKey(uri);
        return Path.Combine(_cacheDirectory!, ".locks", key + ".lock");
    }

    private sealed class RecordingMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public string[] LastIfNoneMatch { get; private set; } = [];

        public DateTimeOffset? LastIfModifiedSince { get; private set; }

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastIfNoneMatch = request.Headers.IfNoneMatch.Select(static value => value.ToString()).ToArray();
            LastIfModifiedSince = request.Headers.IfModifiedSince;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Read failed.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Read failed."));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
