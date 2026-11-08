using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>Retained audit probes: correctness checks and work counts, never timing verdicts.</summary>
[TestFixture, NonParallelizable, Explicit("Round-three audit; run this fixture explicitly")]
public class PerformanceRoundThreeProbes
{
    [Test]
    public async Task Rendering_work()
    {
        var report = await HeadlessSession.Current.Dispatch(() =>
        {
            var prefix = new[] { 32, 256 }.Select(count =>
            {
                var source = string.Join("\n\n", Enumerable.Range(0, count).Select(i => $"paragraph {i}"));
                var initial = new MarkdownDocumentUpdate.Full(Markdown.Parse(source, MarkdownUpdateProducer.DefaultPipeline));
                var renderer = new MarkdownRenderer { DocumentUpdate = initial };
                var reports = new Reports();
                using var subscription = MarkdownRenderDiagnostics.Listener.Subscribe(reports,
                    name => name == MarkdownRenderDiagnostics.UpdateEvent);
                renderer.DocumentUpdate = new MarkdownDocumentUpdate.Incremental(initial, Markdown.Parse(source + " tail", MarkdownUpdateProducer.DefaultPipeline),
                    new ObservableStringBuilderChangedEventArgs(source.Length, 5, source.Length + 5, 1));
                var last = reports.Values.Single();
                Assert.That(last.Completed, Is.True);
                Assert.That(renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Last().LayoutText, Does.EndWith(" tail"));
                return new { paragraphs = count, last.SkippedNodes, last.BlockUpdates, last.InlineUpdates, last.ConstructedNodes };
            }).ToArray();

            var projected = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(
                string.Join("\n\n", Enumerable.Range(0, 200).Select(i => $"fixed paragraph {i}")))) };
            var window = new Window { Width = 400, Height = 300, Content = projected };
            object projection;
            try
            {
                window.Show(); window.UpdateLayout();
                var before = projected.RenderedTextProjection;
                Assert.That(before, Is.Not.Null);
                var builds = projected.RenderedTextProjectionBuilds;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 5; i++)
                {
                    projected.InvalidateRenderedTextState();
                    projected.InvalidateMeasure();
                    window.UpdateLayout();
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                projection = new { paragraphs = 200, refreshes = 5, builds = projected.RenderedTextProjectionBuilds - builds,
                    replaced = !ReferenceEquals(before, projected.RenderedTextProjection), allocatedBytes = allocated };
            }
            finally { window.Close(); }

            var lists = new[] { 16, 128 }.Select(count =>
            {
                var panel = new MarkdownListGrid();
                for (var i = 0; i < count; i++)
                    for (var column = 0; column < 2; column++)
                    {
                        var child = new FixedCell();
                        Grid.SetRow(child, i * 2); // sparse rows are intentional and supported
                        Grid.SetColumn(child, column);
                        panel.Children.Add(child);
                    }
                panel.Measure(new Size(400, 10000));
                panel.Arrange(new Rect(0, 0, 400, 10000));
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 8; i++) panel.Arrange(new Rect(0, 0, i % 2 == 0 ? 401 : 400, 10000));
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.That(panel.Children[^1].Bounds.Y, Is.EqualTo((count - 1) * 10));
                return new { rows = count, arrangements = 8, allocatedBytes = allocated };
            }).ToArray();
            return new { prefix, projection, lists };
        }, CancellationToken.None);
        Publish("rendering", report);
    }

    [Test]
    public async Task Disk_lock_retention()
    {
        var oldDirectory = FileBasedAsyncImageLoaderCache.CacheDirectory;
        var oldLimit = FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes;
        var oldInterval = FileBasedAsyncImageLoaderCache.CleanupInterval;
        var root = Directory.CreateTempSubdirectory("lm-round3-locks-");
        try
        {
            FileBasedAsyncImageLoaderCache.CacheDirectory = root.FullName;
            FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 0;
            FileBasedAsyncImageLoaderCache.CleanupInterval = TimeSpan.MaxValue;
            var cache = new FileBasedAsyncImageLoaderCache();
            var before = FileBasedAsyncImageLoaderCache.ActiveEntryLockCount;
            for (var i = 0; i < 32; i++)
            {
                var uri = new Uri($"https://example.invalid/round3/{i}.png");
                using var bytes = new MemoryStream(new byte[16]);
                await cache.SetAsync(uri, bytes, AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);
            }
            var committed = Directory.EnumerateFiles(root.FullName, "*.entry", SearchOption.AllDirectories).Count();
            Assert.That(committed, Is.EqualTo(32));
            await cache.ClearAsync(CancellationToken.None);
            Publish("disk-locks", new { writes = 32, committedEntriesBeforeClear = committed, retainedLocksAfterClear = FileBasedAsyncImageLoaderCache.ActiveEntryLockCount - before,
                cacheDirectoryStillExists = Directory.Exists(root.FullName) });
        }
        finally
        {
            FileBasedAsyncImageLoaderCache.CacheDirectory = oldDirectory;
            FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = oldLimit;
            FileBasedAsyncImageLoaderCache.CleanupInterval = oldInterval;
            if (root.Exists) root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Image_load_duplication_and_stale_disposal()
    {
        // Keep the dispatcher pumping for the whole async pipeline, including its final UI apply.
        var report = await HeadlessSession.Current.Dispatch(async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var oldObserver = AsyncImageLoader.SharedLoadJoinedObserver;
            AsyncImageLoader.SharedLoadJoinedObserver = (uri, count) =>
            {
                if (uri.AbsoluteUri == "audit://same/image" && count == 2) entered.TrySetResult();
            };
            var handler = new Handler(async _ =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return new MemoryStream(new byte[1]);
            });
            var shared = new RamBasedAsyncImageLoaderCache();
            var pending = Enumerable.Range(0, 2).Select(_ =>
            {
                var image = new Image();
                AsyncImageLoader.SetHandlers(image, [handler]);
                AsyncImageLoader.SetDecoders(image, []);
                AsyncImageLoader.SetCache(image, shared);
                AsyncImageLoader.SetSource(image, "audit://same/image");
                return Pending(image);
            }).ToArray();
            try
            {
                await Task.WhenAny(entered.Task, pending[0], pending[1]);
                Assert.That(entered.Task.IsCompleted, Is.True, "a load finished before both consumers joined the fetch");
            }
            finally { gate.TrySetResult(); await Task.WhenAll(pending); AsyncImageLoader.SharedLoadJoinedObserver = oldObserver; }

            var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decoded = new TaskCompletionSource<IImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resource = new DisposableImage();
            var oldCache = AsyncImageLoader.DefaultCache;
            AsyncImageLoader.DefaultCache = null;
            var target = new Image();
            Task stale = Task.CompletedTask;
            try
            {
                AsyncImageLoader.SetHandlers(target, [new Handler(_ => Task.FromResult<Stream>(new MemoryStream(new byte[1])))]);
                AsyncImageLoader.SetDecoders(target, [new Decoder((_, _) => { decoding.TrySetResult(); return decoded.Task; })]);
                AsyncImageLoader.SetSource(target, "audit://stale/image");
                stale = Pending(target);
                await Task.WhenAny(decoding.Task, stale);
                Assert.That(decoding.Task.IsCompleted, Is.True, "the load finished without invoking the decoder");
                AsyncImageLoader.SetSource(target, null);
                decoded.TrySetResult(resource); // cancellation can race a successful native decode
                await stale;
                Assert.That(target.Source, Is.Null);
                return new { simultaneousConsumers = 2, handlerCallsForSameUri = calls,
                    staleDecodedImageDisposeCalls = resource.DisposeCalls };
            }
            finally
            {
                decoded.TrySetResult(resource);
                await stale;
                resource.Dispose();
                AsyncImageLoader.DefaultCache = oldCache;
            }
        }, CancellationToken.None);
        Publish("images", report);
    }

    private static Task Pending(Image image) => ((Dictionary<Image, (Task task, CancellationTokenSource cts)>)
        typeof(AsyncImageLoader).GetField("ImageLoadTasks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)[image].task;
    private sealed class Handler(Func<Uri, Task<Stream>> load) : AsyncImageLoaderHandler
    {
        public override IEnumerable<string> SupportedSchemes => ["audit"];
        public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken) => load(uri);
    }
    private sealed class Decoder(Func<Image, Stream, Task<IImage?>> decode) : IImageDecoder
    {
        public bool OwnsDecodedImage(IImage image) => true;
        public Task<IImage?> TryDecodeAsync(Image target, Stream stream, Uri uri, CancellationToken cancellationToken) => decode(target, stream);
    }
    private sealed class DisposableImage : IImage, IDisposable
    {
        public Size Size => new(10, 10);
        public int DisposeCalls;
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
        public void Dispose() => DisposeCalls++;
    }
    private sealed class FixedCell : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(10, 10);
    }
    private sealed class Reports : IObserver<KeyValuePair<string, object?>>
    {
        internal List<MarkdownRenderReport> Values { get; } = [];
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is MarkdownRenderReport report) Values.Add(report); }
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
    private static void Publish(string name, object report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(name + ": " + json);
        if (Environment.GetEnvironmentVariable("LM_ROUND3_OUTPUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        }
    }
}
