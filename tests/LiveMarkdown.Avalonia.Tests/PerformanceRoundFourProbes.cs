using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>Explicit .12 audit probes. Work counts establish behavior; timings never gate a test.</summary>
[TestFixture, NonParallelizable, Explicit("Round-four audit; run this fixture explicitly")]
public class PerformanceRoundFourProbes
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(32)]
    [TestCase(512)]
    [TestCase(4096)]
    public Task Open_fence_tail_snapshot_and_parse(int lines) => OnUi(async () =>
    {
        using var host = new ProducerHost(render: false);
        const string prefix = "unchanged paragraph\n\n";
        var initial = prefix + "```text\n" + string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i:D5} with a fixed payload")) + "\n";
        await host.Append(initial);
        var restart = (int)typeof(MarkdownUpdateProducer).GetField("restartOffset", Private)!.GetValue(host.Producer)!;
        Assert.That(restart, Is.EqualTo(prefix.Length));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = host.Source.CaptureSnapshot(restart);
        var snapshotBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.That(snapshot.Text, Is.EqualTo(initial[prefix.Length..]));
        var parsedBefore = OptionalCounter(host.Producer, "ParsedCharacters");
        var fenceBefore = OptionalCounter(host.Producer, "FenceAppendCount");
        var before = GC.GetTotalAllocatedBytes(false);
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 8; i++) await host.Append($"new line {i}\n");
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(false) - before;
        Assert.That(host.Producer.SpliceCount, Is.EqualTo(8));
        Publish($"open-fence-{lines}", new { lines, snapshotCharacters = snapshot.Text.Length, snapshotBytes,
            appends = 8, milliseconds, processAllocatedBytes = bytes, rendererIncluded = false,
            parsedCharacters = OptionalCounter(host.Producer, "ParsedCharacters") - parsedBefore,
            fenceAppends = OptionalCounter(host.Producer, "FenceAppendCount") - fenceBefore });
    });

    [Test]
    public Task Unknown_fence_labels_remain_in_the_static_language_cache() => OnUi(() =>
    {
        var cache = (IDictionary)typeof(SyntaxHighlighting).GetField("LanguageCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var names = Enumerable.Range(0, 256).Select(i => $"round4-unknown-language-{i:D4}").ToArray();
        var before = cache.Count;
        try
        {
            foreach (var name in names)
            {
                Assert.That(cache.Contains(name), Is.False);
                Assert.That(SyntaxHighlighting.Create(name).HasGrammar, Is.False);
            }
            Assert.That(cache.Count - before, Is.InRange(0, names.Length));
            Publish("unknown-languages", new { labels = names.Length, retainedLanguageEntries = cache.Count - before });
        }
        finally { foreach (var name in names) cache.Remove(name); }
        return Task.CompletedTask;
    });

    [Test]
    public Task Detached_renderers_keep_their_producer_subscriptions_and_receive_updates() => OnUi(async () =>
    {
        var source = new ObservableStringBuilder();
        var producer = new MarkdownUpdateProducer { MarkdownBuilder = source };
        var renderers = Enumerable.Range(0, 8).Select(_ => new MarkdownRenderer { UpdateProducer = producer }).ToArray();
        var panel = new StackPanel();
        foreach (var renderer in renderers) panel.Children.Add(renderer);
        var window = new Window { Width = 600, Height = 400, Content = panel };
        try
        {
            window.Show(); window.UpdateLayout();
            window.Content = null; window.UpdateLayout();
            var retained = ((IEnumerable)typeof(MarkdownUpdateProducer).GetField("observers", Private)!.GetValue(producer)!).Cast<object>().Count();
            Assert.That(retained, Is.InRange(0, 8));
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var witness = producer.Subscribe(new Callback(update => { if (update.Version == 1) delivered.TrySetResult(); }));
            source.Append("published after detaching every renderer");
            await delivered.Task;
            var updated = renderers.Count(renderer => renderer.DocumentUpdate?.Version == 1);
            Publish("detached-renderers", new { detachedRenderers = 8, retainedSubscriptions = retained, updatedWhileDetached = updated });
        }
        finally
        {
            window.Close();
            foreach (var renderer in renderers) renderer.UpdateProducer = new MarkdownUpdateProducer();
        }
    });

    [Test]
    public Task Oversized_prewarm_repeats_all_tokenization_because_the_result_cannot_be_cached() => OnUi(() =>
    {
        const int lines = 1024;
        var text = string.Join("\n", Enumerable.Range(0, lines).Select(i => $"// round4 oversized {i:D5} " + new string('x', 256)));
        var highlighter = new SyntaxHighlighting("csharp");
        var before = highlighter.GrammarLockAcquisitions;
        highlighter.Prewarm(text);
        var first = highlighter.GrammarLockAcquisitions - before;
        before = highlighter.GrammarLockAcquisitions;
        highlighter.Prewarm(text);
        var second = highlighter.GrammarLockAcquisitions - before;
        Assert.That(first, Is.InRange(0, lines));
        Assert.That(second, Is.InRange(0, lines));
        Publish("oversized-prewarm", new { lines, characters = text.Length, firstTokenizedLines = first, repeatedTokenizedLines = second });
        return Task.CompletedTask;
    });

    [TestCase(false)]
    [TestCase(true)]
    public Task Reading_code_views_changes_the_cost_of_unchanged_highlighting(bool readViews) => OnUi(() =>
    {
        const int lines = 512;
        var code = new CodeBlock { AutoSyntaxHighlight = false, Language = "csharp" };
        for (var i = 0; i < lines; i++) code.SetLine(i, $"var round4Value{i} = {i};");
        code.HighlightSyntax();
        if (readViews) Assert.That(code.CodeTextBlocks.Count, Is.EqualTo(lines / CodeBlock.LinesPerChunk));
        code.HighlightSyntax();
        Assert.That(code.LastHighlightLineVisits, Is.InRange(0, lines));
        Publish($"code-view-read-{readViews}", new { lines, readViews, unchangedFormattingLineVisits = code.LastHighlightLineVisits });
        return Task.CompletedTask;
    });

    [TestCase(32)]
    [TestCase(256)]
    public Task Search_active_tail_appends_rebuild_aggregates_over_unchanged_prefixes(int paragraphs) => OnUi(async () =>
    {
        using var host = new ProducerHost(render: true);
        await host.Append(string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(i => $"fixed paragraph {i}")));
        var window = Host(host.Renderer!);
        try
        {
            window.Show(); window.UpdateLayout();
            var calls = 0;
            host.Renderer!.ApplyTextSearch((_, _) => { calls++; return new[] { new TextHighlightRange(0, 5) }; });
            window.UpdateLayout();
            calls = 0;
            var collections = host.Renderer.RenderedTextBlockCollections;
            var replacements = 0;
            var allocated = GC.GetTotalAllocatedBytes(false);
            for (var i = 0; i < 8; i++)
            {
                var matches = host.Renderer.TextSearchMatches;
                await host.Append(" tail");
                window.UpdateLayout();
                if (!ReferenceEquals(matches, host.Renderer.TextSearchMatches)) replacements++;
                Assert.That(host.Renderer.TextSearchMatches.Count, Is.EqualTo(paragraphs));
            }
            allocated = GC.GetTotalAllocatedBytes(false) - allocated;
            Assert.That(calls, Is.EqualTo(8));
            Assert.That(replacements, Is.InRange(0, 8));
            Publish($"search-appends-{paragraphs}", new { paragraphs, appends = 8, matcherCalls = calls,
                aggregateReplacements = replacements, blockCollections = host.Renderer.RenderedTextBlockCollections - collections,
                processAllocatedBytes = allocated });
        }
        finally { window.Close(); }
    });

    [TestCase(false)]
    [TestCase(true)]
    public Task Unchanged_width_code_appends_and_returning_to_a_previous_width(bool wrap) => OnUi(async () =>
    {
        using var host = new ProducerHost(render: true);
        var source = "```text\n" + string.Join("\n", Enumerable.Range(0, 128).Select(i => $"line {i:D4} " + new string('w', 140))) + "\n";
        await host.Append(source);
        var window = Host(host.Renderer!);
        try
        {
            window.Show(); window.UpdateLayout();
            var code = host.Renderer!.GetVisualDescendants().OfType<CodeBlock>().Single();
            code.IsCodeWrapped = wrap;
            window.UpdateLayout();
            var chunks = host.Renderer!.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray();
            var prior = chunks.Select(chunk => chunk.TextLayout).ToArray();
            var start = MarkdownTextBlock.TextLayoutsCreated;
            await host.Append("new tail line\n");
            window.UpdateLayout();
            var appendLayouts = MarkdownTextBlock.TextLayoutsCreated - start;
            var unchanged = chunks.Where((chunk, i) => ReferenceEquals(chunk.TextLayout, prior[i])).Count();
            start = MarkdownTextBlock.TextLayoutsCreated;
            window.Width = 400; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.That(window.Bounds.Width, Is.EqualTo(400));
            var narrowerLayouts = MarkdownTextBlock.TextLayoutsCreated - start;
            start = MarkdownTextBlock.TextLayoutsCreated;
            window.Width = 600; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.That(window.Bounds.Width, Is.EqualTo(600));
            var returnLayouts = MarkdownTextBlock.TextLayoutsCreated - start;
            Assert.That(code.Code, Does.EndWith("new tail line"));
            Publish($"code-layout-{wrap}", new { wrap, originalChunks = chunks.Length, oldLayoutsPreserved = unchanged,
                appendLayouts, narrowerLayouts, returnLayouts });
        }
        finally { window.Close(); }
    });

    private static Window Host(Control renderer) => new()
    {
        Width = 600, Height = 400,
        Content = new ScrollViewer { Content = renderer, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
    };

    [TestCase(false)]
    [TestCase(true)]
    public Task Layout_creation_constraints_across_measure_and_arrange(bool wrap) => OnUi(() =>
    {
        var block = new LayoutProbeText
        {
            Text = string.Join(" ", Enumerable.Repeat("fixed long code text", 64)),
            TextWrapping = wrap ? global::Avalonia.Media.TextWrapping.Wrap : global::Avalonia.Media.TextWrapping.NoWrap
        };
        var window = Host(new StackPanel { Children = { block } });
        try
        {
            window.Show(); window.UpdateLayout();
            block.Requests.Clear();
            var before = MarkdownTextBlock.TextLayoutsCreated;
            window.Width = 400; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.That(window.Bounds.Width, Is.EqualTo(400));
            var narrow = block.Requests.ToArray();
            var narrowCreated = MarkdownTextBlock.TextLayoutsCreated - before;
            before = MarkdownTextBlock.TextLayoutsCreated;
            block.Requests.Clear();
            window.Width = 600; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.That(window.Bounds.Width, Is.EqualTo(600));
            Publish($"layout-constraints-{wrap}", new { wrap, narrow, narrowCreated, restored = block.Requests.ToArray(), restoredCreated = MarkdownTextBlock.TextLayoutsCreated - before });
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private sealed class LayoutProbeText : MarkdownTextBlock
    {
        private string phase = "other";
        internal List<object> Requests { get; } = [];
        protected override Size MeasureOverride(Size availableSize)
        {
            var previous = phase; phase = "measure";
            try { return base.MeasureOverride(availableSize); }
            finally { phase = previous; }
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var previous = phase; phase = "arrange";
            try { return base.ArrangeOverride(finalSize); }
            finally { phase = previous; }
        }
        protected override global::Avalonia.Media.TextFormatting.TextLayout CreateTextLayout(string? text)
        {
            var size = GetMaxSizeFromConstraint();
            Requests.Add(new { phase, width = size.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                height = size.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
            return base.CreateTextLayout(text);
        }
    }

    private static async Task OnUi(Func<Task> work) => await HeadlessSession.Current.Dispatch(async () =>
    {
        await work(); return true;
    }, CancellationToken.None);

    private static long? OptionalCounter(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) is { } value
            ? Convert.ToInt64(value) : null;

    private static void Publish(string id, object metrics)
    {
        var json = JsonSerializer.Serialize(new { id, baseline = "3acb29892c3dbffc45efcffeb564d9d7c74136d2", metrics },
            new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("LM_ROUND4_OUTPUT_DIR") is { Length: > 0 } path)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, id + ".json"), json);
        }
    }

    private sealed class Callback(Action<MarkdownDocumentUpdate> receive) : IObserver<MarkdownDocumentUpdate>
    {
        public void OnNext(MarkdownDocumentUpdate value) => receive(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }

    private sealed class ProducerHost : IDisposable
    {
        internal ObservableStringBuilder Source { get; } = new();
        internal MarkdownUpdateProducer Producer { get; }
        internal MarkdownRenderer? Renderer { get; }
        private readonly IDisposable subscription;
        private TaskCompletionSource<MarkdownDocumentUpdate>? pending;
        private long expected;
        internal ProducerHost(bool render)
        {
            if (render) Renderer = new MarkdownRenderer();
            Producer = new MarkdownUpdateProducer { MarkdownBuilder = Source };
            subscription = Producer.Subscribe(new Callback(value =>
            {
                try
                {
                    if (Renderer is not null) Renderer.DocumentUpdate = value;
                    if (value.Version >= expected) pending?.TrySetResult(value);
                }
                catch (Exception error) { pending?.TrySetException(error); throw; }
            }));
        }
        internal Task<MarkdownDocumentUpdate> Append(string text)
        {
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            expected = Source.Version + 1;
            Source.Append(text);
            return pending.Task;
        }
        public void Dispose() => subscription.Dispose();
    }
}
