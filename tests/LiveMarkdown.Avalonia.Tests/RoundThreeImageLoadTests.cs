using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture, NonParallelizable]
public class RoundThreeImageLoadTests
{
    [Test]
    public Task Cold_consumers_share_fetch_and_cache_fill_but_decode_independently() => Run(async () =>
    {
        using var joined = new JoinSignal(8);
        var release = Signal();
        var handler = new Handler(async (_, cancellation) => { await release.Task.WaitAsync(cancellation); return new MemoryStream([1, 2, 3]); });
        var cache = new MissCache();
        var images = Enumerable.Range(0, 8).Select(_ => Start(handler, cache, new OwnedDecoder())).ToArray();
        try
        {
            await joined.Ready.Task;
            Assert.That(handler.Calls, Is.EqualTo(1));
            release.TrySetResult();
            await Task.WhenAll(images.Select(item => item.Task));
            Assert.That(cache.Writes, Is.EqualTo(1));
            Assert.That(images.Select(item => item.Image.Source).Distinct().Count(), Is.EqualTo(8));
            Assert.That(images.Select(item => ((OwnedDecoder)item.Decoder).ReadBytes), Is.All.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
        }
        finally { release.TrySetResult(); await Finish(images); }
    });

    [Test]
    public Task Cancelling_one_consumer_keeps_its_peer_and_network_request_alive() => Run(async () =>
    {
        using var joined = new JoinSignal(2);
        var release = Signal();
        var networkCancellation = CancellationToken.None;
        var handler = new Handler(async (_, cancellation) =>
        {
            networkCancellation = cancellation;
            await release.Task.WaitAsync(cancellation);
            return new MemoryStream([7]);
        });
        var cache = new MissCache();
        var first = Start(handler, cache, new OwnedDecoder());
        var second = Start(handler, cache, new OwnedDecoder());
        try
        {
            await joined.Ready.Task;
            AsyncImageLoader.SetSource(first.Image, null);
            await first.Task;
            Assert.That(networkCancellation.IsCancellationRequested, Is.False);
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.EqualTo(1));
            release.TrySetResult();
            await second.Task;
            Assert.That(first.Image.Source, Is.Null);
            Assert.That(second.Image.Source, Is.TypeOf<DisposableImage>());
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
        }
        finally { release.TrySetResult(); await Finish(first, second); }
    });

    [Test]
    public Task Cancelling_every_consumer_cancels_fetch_and_removes_the_flight() => Run(async () =>
    {
        TestContext.Progress.WriteLine("cancel-all: preparing two consumers");
        using var joined = new JoinSignal(2);
        var cancellationObserved = Signal();
        var release = Signal();
        var handler = new Handler(async (_, cancellation) =>
        {
            TestContext.Progress.WriteLine("cancel-all: handler entered");
            try
            {
                await release.Task.WaitAsync(cancellation);
                return new MemoryStream([1]);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Observe the cancelled operation itself. A separate token registration can
                // be disposed by this continuation before CTS.Cancel reaches that callback.
                TestContext.Progress.WriteLine("cancel-all: shared fetch cancellation observed");
                cancellationObserved.TrySetResult();
                throw;
            }
        });
        var cache = new MissCache();
        var first = Start(handler, cache, new OwnedDecoder());
        var second = Start(handler, cache, new OwnedDecoder());
        try
        {
            TestContext.Progress.WriteLine("cancel-all: waiting for joined consumers");
            await joined.Ready.Task;
            TestContext.Progress.WriteLine("cancel-all: joined; cancelling first source");
            AsyncImageLoader.SetSource(first.Image, null);
            TestContext.Progress.WriteLine("cancel-all: cancelling second source");
            AsyncImageLoader.SetSource(second.Image, null);
            TestContext.Progress.WriteLine("cancel-all: awaiting both UI apply tasks");
            await Task.WhenAll(first.Task, second.Task);
            TestContext.Progress.WriteLine($"cancel-all: both tasks completed; active flights={AsyncImageLoader.ActiveSharedLoadCount}");
            await cancellationObserved.Task;
            TestContext.Progress.WriteLine("cancel-all: cancellation signal completed");
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
        }
        finally { release.TrySetResult(); await Finish(first, second); }
    });

    [Test]
    public Task Failed_shared_fetch_is_removed_so_a_later_load_retries() => Run(async () =>
    {
        using var joined = new JoinSignal(2);
        var firstResponse = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((call, _) => call == 1 ? firstResponse.Task : Task.FromResult<Stream>(new MemoryStream([9])));
        var cache = new MissCache();
        var first = Start(handler, cache, new OwnedDecoder());
        var second = Start(handler, cache, new OwnedDecoder());
        try
        {
            await joined.Ready.Task;
            firstResponse.TrySetException(new IOException("controlled fetch failure"));
            await Task.WhenAll(first.Task, second.Task);
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
            Assert.That(first.Image.Source, Is.Null);
            Assert.That(second.Image.Source, Is.Null);
            var retry = Start(handler, cache, new OwnedDecoder());
            try
            {
                await retry.Task;
                Assert.That(handler.Calls, Is.EqualTo(2));
                Assert.That(retry.Image.Source, Is.TypeOf<DisposableImage>());
            }
            finally { await Finish(retry); }
        }
        finally { firstResponse.TrySetException(new IOException("probe cleanup")); await Finish(first, second); }
    });

    [Test]
    public Task Different_handlers_and_caches_do_not_share_fetches() => Run(async () =>
    {
        using var joined = new JoinSignal(4, countAll: true);
        var release = Signal();
        async Task<Stream> Fetch(int _, CancellationToken cancellation) { await release.Task.WaitAsync(cancellation); return new MemoryStream([1]); }
        var handlerA = new Handler(Fetch);
        var handlerB = new Handler(Fetch);
        var cacheA = new MissCache();
        var cacheB = new MissCache();
        var loads = new[] { Start(handlerA, cacheA, new OwnedDecoder()), Start(handlerA, cacheA, new OwnedDecoder()),
            Start(handlerB, cacheA, new OwnedDecoder()), Start(handlerA, cacheB, new OwnedDecoder()) };
        try
        {
            await joined.Ready.Task;
            Assert.That(handlerA.Calls, Is.EqualTo(2));
            Assert.That(handlerB.Calls, Is.EqualTo(1));
            release.TrySetResult();
            await Task.WhenAll(loads.Select(load => load.Task));
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
        }
        finally { release.TrySetResult(); await Finish(loads); }
    });

    [TestCase(true)]
    [TestCase(false)]
    public Task Stale_decode_releases_owned_results_once_and_preserves_borrowed_results(bool owned) => Run(async () =>
    {
        var entered = Signal();
        var decoded = new TaskCompletionSource<IImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new DisposableImage();
        Task<IImage?> Decode(Image _, Stream __) { entered.TrySetResult(); return decoded.Task; }
        IImageDecoder decoder = owned ? new ControlledDecoder(true, Decode) : new BorrowedDecoder(Decode);
        var load = Start(new Handler((_, _) => Task.FromResult<Stream>(new MemoryStream([1]))), new MissCache(), decoder);
        try
        {
            await entered.Task;
            AsyncImageLoader.SetSource(load.Image, null);
            decoded.TrySetResult(resource); // simulate a decode completing just after cancellation
            await load.Task;
            Assert.That(load.Image.Source, Is.Null);
            Assert.That(resource.DisposeCalls, Is.EqualTo(owned ? 1 : 0));
            Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
        }
        finally { decoded.TrySetResult(resource); await Finish(load); if (!owned) resource.Dispose(); }
    });

    [Test]
    public Task Cache_publication_failure_releases_the_unapplied_owned_image_exactly_once() => Run(async () =>
    {
        var resource = new DisposableImage();
        var cache = new MissCache { WriteFailure = new IOException("controlled cache failure") };
        var decoder = new ControlledDecoder(true, (_, _) => Task.FromResult<IImage?>(resource));
        var load = Start(new Handler((_, _) => Task.FromResult<Stream>(new MemoryStream([1]))), cache, decoder);
        await load.Task;
        Assert.That(load.Image.Source, Is.Null);
        Assert.That(resource.DisposeCalls, Is.EqualTo(1));
        Assert.That(AsyncImageLoader.ActiveSharedLoadCount, Is.Zero);
    });

    [Test]
    public Task A_displayed_owned_image_is_transferred_to_the_consumer_without_disposal() => Run(async () =>
    {
        var decoder = new OwnedDecoder();
        var load = Start(new Handler((_, _) => Task.FromResult<Stream>(new MemoryStream([1]))), new MissCache(), decoder);
        try
        {
            await load.Task;
            var image = (DisposableImage)load.Image.Source!;
            Assert.That(image.DisposeCalls, Is.Zero);
            AsyncImageLoader.SetSource(load.Image, null);
            Assert.That(image.DisposeCalls, Is.Zero, "a successfully transferred image is not owned by the pending loader");
            image.Dispose();
        }
        finally { await Finish(load); }
    });

    [Test]
    public Task Fresh_cached_bytes_decode_directly_without_fetch_or_shared_flight() => Run(async () =>
    {
        var handler = new Handler((_, _) => throw new AssertionException("fresh hits must not fetch"));
        var cache = new ReadCache(stale: false);
        var starts = AsyncImageLoader.SharedLoadStarts;
        var loads = new[] { Start(handler, cache, new OwnedDecoder()), Start(handler, cache, new OwnedDecoder()) };
        try
        {
            await Task.WhenAll(loads.Select(load => load.Task));
            Assert.That(handler.Calls, Is.Zero);
            Assert.That(AsyncImageLoader.SharedLoadStarts, Is.EqualTo(starts));
            Assert.That(loads.Select(load => ((OwnedDecoder)load.Decoder).ReadBytes), Is.All.EqualTo(new byte[] { 4, 5 }));
            Assert.That(loads[0].Image.Source, Is.Not.SameAs(loads[1].Image.Source));
        }
        finally { await Finish(loads); }
    });

    [Test]
    public Task Stale_validators_remain_consumer_local_and_not_modified_responses_keep_cached_bytes() => Run(async () =>
    {
        var handler = new ValidationHandler();
        var cache = new ReadCache(stale: true);
        var starts = AsyncImageLoader.SharedLoadStarts;
        var loads = new[] { Start(handler, cache, new OwnedDecoder()), Start(handler, cache, new OwnedDecoder()) };
        try
        {
            await Task.WhenAll(loads.Select(load => load.Task));
            Assert.That(handler.Calls, Is.EqualTo(2));
            Assert.That(cache.Touches, Is.EqualTo(2));
            Assert.That(AsyncImageLoader.SharedLoadStarts, Is.EqualTo(starts));
            Assert.That(loads.Select(load => load.Image.Source), Is.All.TypeOf<DisposableImage>());
        }
        finally { await Finish(loads); }
    });

    [Test]
    public Task Local_files_decode_from_the_original_file_stream_without_whole_body_buffering() => Run(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("lm-direct-image-");
        var path = Path.Combine(directory.FullName, "image.bin");
        var resource = new DisposableImage();
        var seenFile = false;
        var decoder = new ControlledDecoder(true, (target, stream) =>
        {
            Assert.That(stream, Is.TypeOf<FileStream>());
            Assert.That(((FileStream)stream).Name, Is.EqualTo(path));
            Assert.That(stream.Position, Is.Zero);
            seenFile = true;
            return Task.FromResult<IImage?>(resource);
        });
        var starts = AsyncImageLoader.SharedLoadStarts;
        Load? load = null;
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            load = Start(LocalFileAsyncImageLoaderHandler.Shared, new MissCache(), decoder, new Uri(path).AbsoluteUri);
            await load.Task;
            Assert.That(seenFile, Is.True);
            Assert.That(load.Image.Source, Is.SameAs(resource));
            Assert.That(AsyncImageLoader.SharedLoadStarts, Is.EqualTo(starts));
        }
        finally
        {
            if (load is not null) await Finish(load);
            resource.Dispose();
            directory.Delete(recursive: true);
        }
    });

    private static Task Run(Func<Task> action) => HeadlessSession.Current.Dispatch(async () => { await action(); return true; }, CancellationToken.None);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Load(Image Image, Task Task, IImageDecoder Decoder);
    private static Load Start(AsyncImageLoaderHandler handler, AsyncImageLoaderCache cache, IImageDecoder decoder,
        string source = "round3://same/image")
    {
        var image = new Image();
        AsyncImageLoader.SetHandlers(image, [handler]);
        AsyncImageLoader.SetDecoders(image, [decoder]);
        AsyncImageLoader.SetCache(image, cache);
        AsyncImageLoader.SetSource(image, source);
        return new(image, AsyncImageLoader.PendingLoadFor(image), decoder);
    }
    private static async Task Finish(params Load[] loads)
    {
        foreach (var load in loads) AsyncImageLoader.SetSource(load.Image, null);
        await Task.WhenAll(loads.Select(load => load.Task));
        foreach (var load in loads)
            if (load.Decoder is OwnedDecoder owned)
                foreach (var image in owned.Created.Where(image => image.DisposeCalls == 0)) image.Dispose();
    }
    private sealed class JoinSignal : IDisposable
    {
        private readonly Action<Uri, int>? previous = AsyncImageLoader.SharedLoadJoinedObserver;
        internal TaskCompletionSource Ready { get; } = Signal();
        private int observations;
        internal JoinSignal(int expected, bool countAll = false)
        {
            AsyncImageLoader.SharedLoadJoinedObserver = (_, count) =>
            {
                TestContext.Progress.WriteLine($"shared-load: joined consumer count={count}");
                if ((countAll ? Interlocked.Increment(ref observations) : count) >= expected) Ready.TrySetResult();
            };
        }
        public void Dispose() => AsyncImageLoader.SharedLoadJoinedObserver = previous;
    }
    private sealed class Handler(Func<int, CancellationToken, Task<Stream>> load) : AsyncImageLoaderHandler
    {
        internal int Calls;
        public override IEnumerable<string> SupportedSchemes => ["round3"];
        public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken) => load(Interlocked.Increment(ref Calls), cancellationToken);
    }
    private sealed class ValidationHandler : AsyncImageLoaderHandler
    {
        internal int Calls;
        public override IEnumerable<string> SupportedSchemes => ["round3"];
        public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken) => throw new AssertionException("missing validators");
        public override Task<AsyncImageLoaderResponse> LoadAsync(Uri uri, AsyncImageLoaderCacheMetadata? metadata, CancellationToken cancellationToken)
        {
            Assert.That(metadata?.GetHttpETag(), Is.EqualTo("\"known\""));
            Interlocked.Increment(ref Calls);
            return Task.FromResult(AsyncImageLoaderResponse.NotModified(AsyncImageLoaderCacheMetadata.Create(uri)));
        }
    }
    private sealed class MissCache : AsyncImageLoaderCache
    {
        internal int Writes;
        internal Exception? WriteFailure;
        public override Task<AsyncImageLoaderCacheEntry?> GetAsync(Uri uri, CancellationToken cancellationToken) => Task.FromResult<AsyncImageLoaderCacheEntry?>(null);
        public override Task SetAsync(Uri uri, Stream stream, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Writes);
            return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
        }
    }
    private sealed class ReadCache(bool stale) : AsyncImageLoaderCache
    {
        internal int Touches;
        public override Task<AsyncImageLoaderCacheEntry?> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            var metadata = AsyncImageLoaderCacheMetadata.Create(uri);
            metadata.NoCache = stale;
            metadata.SetHttpETag("\"known\"");
            return Task.FromResult<AsyncImageLoaderCacheEntry?>(new AsyncImageLoaderCacheEntry(new MemoryStream([4, 5]), metadata));
        }
        public override Task SetAsync(Uri uri, Stream stream, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken) => throw new AssertionException("unexpected cache replacement");
        public override Task TouchAsync(Uri uri, AsyncImageLoaderCacheMetadata metadata, CancellationToken cancellationToken) { Interlocked.Increment(ref Touches); return Task.CompletedTask; }
    }
    private sealed class OwnedDecoder : IImageDecoder
    {
        internal List<DisposableImage> Created { get; } = [];
        internal byte[]? ReadBytes;
        public bool OwnsDecodedImage(IImage image) => true;
        public async Task<IImage?> TryDecodeAsync(Image target, Stream stream, Uri uri, CancellationToken cancellationToken)
        {
            Assert.That(stream.Position, Is.Zero);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken);
            ReadBytes = copy.ToArray();
            var image = new DisposableImage();
            Created.Add(image);
            return image;
        }
    }
    private sealed class ControlledDecoder(bool owned, Func<Image, Stream, Task<IImage?>> decode) : IImageDecoder
    {
        public bool OwnsDecodedImage(IImage image) => owned;
        public Task<IImage?> TryDecodeAsync(Image target, Stream stream, Uri uri, CancellationToken cancellationToken) => decode(target, stream);
    }
    // No ownership member: exercises the default interface implementation for existing decoders.
    private sealed class BorrowedDecoder(Func<Image, Stream, Task<IImage?>> decode) : IImageDecoder
    {
        public Task<IImage?> TryDecodeAsync(Image target, Stream stream, Uri uri, CancellationToken cancellationToken) => decode(target, stream);
    }
    private sealed class DisposableImage : IImage, IDisposable
    {
        public Size Size => new(10, 10);
        internal int DisposeCalls;
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
        public void Dispose() => DisposeCalls++;
    }
}
