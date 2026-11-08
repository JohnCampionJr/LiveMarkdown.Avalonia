using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>Fixed workloads usable unchanged on the pre-round-three source. No timing gates.</summary>
[TestFixture, NonParallelizable, Explicit("Run tools/rendering-benchmark.py run --suite round3")]
public class RoundThreeBenchmarks
{
    private const int Steps = 16;
    private const int Samples = 7;
    private const int Warmups = 2;
    private const string Corpus = "round3-v1:256-paragraph-tail;256-line-code-tail;200-projections;128-sparse-list-rows;32+16x64-byte-disk-entries;8-cold-images;1-stale-decode";
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Test]
    public async Task Fixed_round_three_workloads()
    {
        var scenarios = new List<object>();
        await HeadlessSession.Current.Dispatch(async () =>
        {
            foreach (var (id, run) in new (string, Func<Task<Sample>>)[]
            {
                ("producer-paragraph-tail", () => Stream(code: false)),
                ("producer-code-tail", () => Stream(code: true)),
                ("projection-unchanged", () => Task.FromResult(Projection())),
                ("list-width-arrange", () => Task.FromResult(ListArrange())),
                ("disk-cache-writes", DiskWrites),
                ("image-cold-batch", Images),
                ("image-stale-decode", StaleImage),
            })
            {
                for (var i = 0; i < Warmups; i++) await run();
                var samples = new List<Sample>();
                for (var i = 0; i < Samples; i++) samples.Add(await run());
                scenarios.Add(new { id, samples });
            }
            return true;
        }, CancellationToken.None);
        var report = new
        {
            schema = 1, workload = "round3-v1",
            corpusSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Corpus))),
            harnessSha256 = Environment.GetEnvironmentVariable("LM_BENCHMARK_HARNESS_SHA256"),
            revision = Environment.GetEnvironmentVariable("LM_BENCHMARK_REVISION"),
            environment = new
            {
                machine = Environment.MachineName, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount, avalonia = typeof(Window).Assembly.GetName().Version?.ToString(),
                markdig = typeof(Markdown).Assembly.GetName().Version?.ToString(),
#if DEBUG
                configuration = "Debug",
#else
                configuration = "Release",
#endif
                backend = "Avalonia.Headless + Skia + Inter", serverGc = System.Runtime.GCSettings.IsServerGC,
                runtimeOptions = new[] { "TieredCompilation", "TieredPGO", "ReadyToRun", "gcServer", "GCHeapHardLimit", "GCConserveMemory" }
                    .SelectMany(option => new[] { "DOTNET_" + option, "COMPlus_" + option })
                    .ToDictionary(option => option, Environment.GetEnvironmentVariable),
            },
            parameters = new { warmups = Warmups, samples = Samples, steps = Steps, streamingPrefix = 256, projectionParagraphs = 200,
                listRows = 128, diskSeed = 32, diskWrites = Steps, imageConsumers = 8,
                streamAllocationScope = "UI render callbacks only; parsing excluded", syncAllocationScope = "current UI thread",
                asyncAllocationScope = "process-wide for disk/image; includes unrelated host allocations" },
            scenarios,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("LM_BENCHMARK_OUTPUT") is { Length: > 0 } path) File.WriteAllText(path, json);
    }

    private static async Task<Sample> Stream(bool code)
    {
        var initial = code ? "```csharp\n" + string.Join("\n", Enumerable.Range(0, 256).Select(i => $"var prefix{i:D3} = {i};")) + "\n"
            : string.Join("\n\n", Enumerable.Range(0, 256).Select(i => $"paragraph {i:D3}"));
        var source = new ObservableStringBuilder(initial);
        var producer = new MarkdownUpdateProducer { MarkdownBuilder = source };
        var renderer = new MarkdownRenderer();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new Reports();
        using var diagnostics = MarkdownRenderDiagnostics.Listener.Subscribe(reports, name => name == MarkdownRenderDiagnostics.UpdateEvent);
        var measuring = false;
        var milliseconds = 0d;
        long bytes = 0;
        using var subscription = producer.Subscribe(new Observer(update =>
        {
            try
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                renderer.DocumentUpdate = update;
                if (measuring)
                {
                    milliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
                signal.TrySetResult();
            }
            catch (Exception error) { signal.TrySetException(error); }
        }));
        await signal.Task;
        reports.Values.Clear();
        var inspections = StaticCounter(typeof(CodeBlockNode), "LineInspections");
        var searches = StaticCounter(typeof(CodeBlockNode), "LineSearchComparisons");
        measuring = true;
        for (var i = 0; i < Steps; i++)
        {
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Append(code ? $"var appended{i:D2} = {i};\n" : " tail");
            await signal.Task;
        }
        Assert.That(reports.Values, Has.Count.EqualTo(Steps));
        var work = new Dictionary<string, long?> { ["updates"] = Steps,
            ["skippedNodes"] = reports.Values.Sum(report => report.SkippedNodes),
            ["constructedNodes"] = reports.Values.Sum(report => report.ConstructedNodes),
            ["lineInspections"] = StaticCounter(typeof(CodeBlockNode), "LineInspections") - inspections,
            ["lineSearchComparisons"] = StaticCounter(typeof(CodeBlockNode), "LineSearchComparisons") - searches };
        var finalText = code ? renderer.GetVisualDescendants().OfType<CodeBlock>().Single().Code
            : renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Last().LayoutText;
        Assert.That(finalText, Does.Contain(code ? "appended15" : " tail"));
        return new(milliseconds, bytes, work);
    }

    private static Sample Projection()
    {
        var renderer = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(
            string.Join("\n\n", Enumerable.Range(0, 200).Select(i => $"paragraph {i}")), MarkdownUpdateProducer.DefaultPipeline)) };
        var window = new Window { Width = 400, Height = 300, Content = renderer };
        try
        {
            window.Show(); window.UpdateLayout();
            var original = renderer.RenderedTextProjection;
            var builds = renderer.RenderedTextProjectionBuilds;
            var bytes = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < Steps; i++) { renderer.InvalidateRenderedTextState(); renderer.InvalidateMeasure(); window.UpdateLayout(); }
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds; bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Assert.That(renderer.RenderedTextProjection!.Buffers, Has.Count.EqualTo(200));
            return new(ms, bytes, new() { ["refreshes"] = Steps, ["projectionBuilds"] = renderer.RenderedTextProjectionBuilds - builds,
                ["projectionReplaced"] = ReferenceEquals(original, renderer.RenderedTextProjection) ? 0 : 1 });
        }
        finally { window.Close(); }
    }

    private static Sample ListArrange()
    {
        var panel = new MarkdownListGrid();
        for (var row = 0; row < 128; row++)
            for (var col = 0; col < 2; col++)
            {
                var child = new Cell(); Grid.SetRow(child, row * 2); Grid.SetColumn(child, col); panel.Children.Add(child);
            }
        panel.Measure(new Size(400, 10000)); panel.Arrange(new Rect(0, 0, 400, 10000));
        var builds = InstanceCounter(panel, "RowOffsetBuilds");
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < Steps; i++) panel.Arrange(new Rect(0, 0, i % 2 == 0 ? 401 : 400, 10000));
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds; bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.That(panel.Children[^1].Bounds.Y, Is.EqualTo(1270));
        return new(ms, bytes, new() { ["arranges"] = Steps, ["rowOffsetBuilds"] = InstanceCounter(panel, "RowOffsetBuilds") - builds });
    }

    private static async Task<Sample> DiskWrites()
    {
        var root = Directory.CreateTempSubdirectory("lm-round3-benchmark-");
        var oldDirectory = FileBasedAsyncImageLoaderCache.CacheDirectory;
        var oldLimit = FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes;
        var oldInterval = FileBasedAsyncImageLoaderCache.CleanupInterval;
        try
        {
            FileBasedAsyncImageLoaderCache.CacheDirectory = root.FullName;
            FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = 256 * 1024;
            FileBasedAsyncImageLoaderCache.CleanupInterval = TimeSpan.FromDays(1);
            var cache = new FileBasedAsyncImageLoaderCache();
            for (var i = 0; i < 32; i++) await Write(i);
            var scans = StaticCounter(typeof(FileBasedAsyncImageLoaderCache), "DirectoryScanCount");
            var bytes = GC.GetTotalAllocatedBytes(precise: true); var started = Stopwatch.GetTimestamp();
            for (var i = 32; i < 32 + Steps; i++) await Write(i);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds; bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            var retained = ActiveLocks();
            Assert.That(Directory.EnumerateFiles(root.FullName, "*.entry", SearchOption.AllDirectories).Count(), Is.EqualTo(48));
            var scanDelta = StaticCounter(typeof(FileBasedAsyncImageLoaderCache), "DirectoryScanCount") - scans;
            await cache.ClearAsync(CancellationToken.None);
            return new(ms, bytes, new() { ["writes"] = Steps, ["directoryScans"] = scanDelta, ["activeLocksAfterWrites"] = retained });
            async Task Write(int i)
            {
                var uri = new Uri($"https://example.invalid/benchmark/{i:D2}.png");
                using var stream = new MemoryStream(new byte[64]);
                await cache.SetAsync(uri, stream, AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);
            }
        }
        finally
        {
            FileBasedAsyncImageLoaderCache.CacheDirectory = oldDirectory;
            FileBasedAsyncImageLoaderCache.MaxCacheSizeBytes = oldLimit;
            FileBasedAsyncImageLoaderCache.CleanupInterval = oldInterval;
            CleanupOldLocks(root.FullName);
            if (Directory.Exists(root.FullName)) Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static async Task<Sample> Images()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var joinField = typeof(AsyncImageLoader).GetField("SharedLoadJoinedObserver", PrivateStatic);
        var oldObserver = joinField?.GetValue(null);
        var uri = new Uri("audit://round3/batch");
        joinField?.SetValue(null, (Action<Uri, int>)((joinedUri, count) => { if (joinedUri == uri && count == 8) ready.TrySetResult(); }));
        var handler = new Handler(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 8) ready.TrySetResult();
            await gate.Task;
            return new MemoryStream(new byte[64]);
        });
        var images = Enumerable.Range(0, 8).Select(_ => new Image()).ToArray();
        var cache = new RamBasedAsyncImageLoaderCache();
        var decoded = new List<OwnedImage>();
        var decoder = new Decoder((_, _) => { var image = new OwnedImage(); lock (decoded) decoded.Add(image); return Task.FromResult<IImage?>(image); });
        foreach (var image in images) { AsyncImageLoader.SetHandlers(image, [handler]); AsyncImageLoader.SetDecoders(image, [decoder]); AsyncImageLoader.SetCache(image, cache); }
        var pending = new List<Task>();
        var bytes = GC.GetTotalAllocatedBytes(precise: true); var started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var image in images) { AsyncImageLoader.SetSource(image, uri.AbsoluteUri); pending.Add(Pending(image)); }
            await ready.Task;
            gate.TrySetResult();
            await Task.WhenAll(pending);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds; bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            Assert.That(images.All(image => image.Source is OwnedImage), Is.True);
            Assert.That(decoded, Has.Count.EqualTo(8));
            return new(ms, bytes, new() { ["consumers"] = 8, ["handlerCalls"] = calls, ["decodes"] = decoded.Count });
        }
        finally
        {
            gate.TrySetResult(); await Task.WhenAll(pending); joinField?.SetValue(null, oldObserver);
            foreach (var image in decoded) if (image.DisposeCalls == 0) image.Dispose();
        }
    }

    private static async Task<Sample> StaleImage()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoded = new TaskCompletionSource<IImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCache = AsyncImageLoader.DefaultCache; AsyncImageLoader.DefaultCache = null;
        var owned = new OwnedImage(); var image = new Image(); Task pending = Task.CompletedTask;
        AsyncImageLoader.SetHandlers(image, [new Handler(_ => Task.FromResult<Stream>(new MemoryStream(new byte[1])))]);
        AsyncImageLoader.SetDecoders(image, [new Decoder((_, _) => { entered.TrySetResult(); return decoded.Task; })]);
        var bytes = GC.GetTotalAllocatedBytes(precise: true); var started = Stopwatch.GetTimestamp();
        try
        {
            AsyncImageLoader.SetSource(image, "audit://round3/stale"); pending = Pending(image);
            await Task.WhenAny(entered.Task, pending);
            Assert.That(entered.Task.IsCompleted, Is.True);
            AsyncImageLoader.SetSource(image, null); decoded.TrySetResult(owned); await pending;
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds; bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            Assert.That(image.Source, Is.Null);
            return new(ms, bytes, new() { ["decodes"] = 1, ["discardedImageDisposals"] = owned.DisposeCalls });
        }
        finally { decoded.TrySetResult(owned); await pending; if (owned.DisposeCalls == 0) owned.Dispose(); AsyncImageLoader.DefaultCache = oldCache; }
    }

    private static long? StaticCounter(Type type, string name) => Number(type.GetField(name, PrivateStatic)?.GetValue(null) ?? type.GetProperty(name, PrivateStatic)?.GetValue(null));
    private static long? InstanceCounter(object instance, string name) => Number(instance.GetType().GetProperty(name, PrivateInstance)?.GetValue(instance));
    private static long? Number(object? value) => value is null ? null : Convert.ToInt64(value);
    private static long? ActiveLocks() => StaticCounter(typeof(FileBasedAsyncImageLoaderCache), "ActiveEntryLockCount") ?? OldLocks()?.Count;
    private static IDictionary? OldLocks() => typeof(FileBasedAsyncImageLoaderCache).GetField("EntryLocks", PrivateStatic)?.GetValue(null) as IDictionary;
    private static void CleanupOldLocks(string root)
    {
        if (OldLocks() is not { } locks) return;
        foreach (var key in locks.Keys.Cast<string>().Where(key => key.StartsWith(root, StringComparison.Ordinal)).ToArray()) { ((IDisposable)locks[key]!).Dispose(); locks.Remove(key); }
    }
    private static Task Pending(Image image) => ((Dictionary<Image, (Task task, CancellationTokenSource cts)>)typeof(AsyncImageLoader).GetField("ImageLoadTasks", PrivateStatic)!.GetValue(null)!)[image].task;
    private sealed record Sample(double milliseconds, long allocatedBytes, Dictionary<string, long?> work);
    private sealed class Cell : Control { protected override Size MeasureOverride(Size availableSize) => new(10, 10); }
    private sealed class Observer(Action<MarkdownDocumentUpdate> accept) : IObserver<MarkdownDocumentUpdate>
    { public void OnNext(MarkdownDocumentUpdate update) => accept(update); public void OnCompleted() { } public void OnError(Exception error) => throw error; }
    private sealed class Reports : IObserver<KeyValuePair<string, object?>>
    {
        internal List<MarkdownRenderReport> Values { get; } = [];
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is MarkdownRenderReport report) Values.Add(report); }
        public void OnCompleted() { } public void OnError(Exception error) => throw error;
    }
    private sealed class Handler(Func<Uri, Task<Stream>> load) : AsyncImageLoaderHandler
    { public override IEnumerable<string> SupportedSchemes => ["audit"]; public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken) => load(uri); }
    private sealed class Decoder(Func<Image, Stream, Task<IImage?>> decode) : IImageDecoder
    {
        public bool OwnsDecodedImage(IImage image) => true; // also compiles against the baseline interface
        public Task<IImage?> TryDecodeAsync(Image image, Stream stream, Uri uri, CancellationToken token) => decode(image, stream);
    }
    private sealed class OwnedImage : IImage, IDisposable
    { public int DisposeCalls; public Size Size => new(10, 10); public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { } public void Dispose() => DisposeCalls++; }
}
