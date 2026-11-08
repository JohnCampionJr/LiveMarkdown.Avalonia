using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NUnit.Framework;
using RenderedCode = LiveMarkdown.Avalonia.CodeBlock;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture, NonParallelizable]
public class RoundFourPerformanceTests
{
    [TestCase("", "\r", "\n")]
    [TestCase("prefix\n\n", "\r", "\n")]
    [TestCase("prefix\n\n", "\r\n", "\n")]
    public Task Mixed_line_endings_do_not_duplicate_the_open_fence_body(string prefix, string openingNewline, string bodyNewline) => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append(prefix + "```text" + openingNewline + "first" + bodyNewline);
        await host.Append("second\n");
        Assert.That(host.Producer.FenceAppendCount, Is.EqualTo(1));
        AssertParity(host, host.Source.ToString());
        await host.Append("third");
        await host.Append(" partial\n");
        AssertParity(host, host.Source.ToString());
        await host.Append("```\n");
        AssertParity(host, host.Source.ToString());
    });

    [Test]
    public Task Attaching_and_reattaching_populated_code_keeps_dirty_tail_highlighting() => OnUi(() =>
    {
        var code = new RenderedCode { AutoSyntaxHighlight = false, Language = "csharp" };
        for (var i = 0; i < 512; i++) code.SetLine(i, $"var reviewItem{i} = {i};");
        code.HighlightSyntax();
        var window = WindowFor(code);
        var parent = window.Content;
        try
        {
            window.Show(); window.UpdateLayout();
            code.SetLine(512, "var firstTail = 1;"); code.HighlightSyntax();
            Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1), "first attachment is not a source edit");
            window.Content = null; window.UpdateLayout();
            window.Content = parent; window.UpdateLayout();
            code.SetLine(513, "var secondTail = 2;"); code.HighlightSyntax();
            Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1), "an unchanged reattachment keeps authoritative lines");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [TestCase(false)]
    [TestCase(true)]
    public Task Nested_edits_while_detached_are_validated_before_highlighting(bool highlightWhileDetached) => OnUi(() =>
    {
        var code = new RenderedCode { AutoSyntaxHighlight = false, Language = "csharp", Code = "var reviewValue = 1;" };
        code.HighlightSyntax();
        var window = WindowFor(code);
        var parent = window.Content;
        try
        {
            window.Show(); window.UpdateLayout();
            window.Content = null; window.UpdateLayout();
            var span = (Span)code.CodeTextBlocks[0].Inlines![0];
            span.Inlines.Clear();
            span.Inlines.Add(new Run("// external detached edit"));
            if (highlightWhileDetached) code.HighlightSyntax();
            window.Content = parent; window.UpdateLayout();
            code.HighlightSyntax();
            Assert.That(code.Code, Is.EqualTo("// external detached edit"));
            Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1), "detached nested edits cannot reuse stale authoritative lines");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [TestCase("```", "\n", "")]
    [TestCase("~~~~", "\r\n", "intro\n\n# Heading\n\n")]
    [TestCase("````", "\n", "intro\n\n")]
    public Task Open_fence_fragments_keep_ast_rendering_and_source_spans_equal_to_a_full_parse(string fence, string newline, string prefix) => OnUi(async () =>
    {
        using var host = new ProducerHost();
        var initial = prefix + fence + "csharp" + newline + string.Join(newline, Enumerable.Range(0, 32).Select(i => $"var item{i} = {i};")) + newline;
        var source = new StringBuilder(initial);
        await host.Append(initial);
        foreach (var fragment in new[] { "more", " text", "\r", "\n", "\tindent\n", "\n", fence[..2], fence[2..] + "\n", "after *emphasis*\n" })
        {
            source.Append(fragment);
            await host.Append(fragment);
            AssertParity(host, source.ToString());
        }
        Assert.That(host.Producer.FenceAppendCount, Is.GreaterThan(0));
        Assert.That(host.Latest!.Document.Descendants().OfType<FencedCodeBlock>().Single().ClosingFencedCharCount, Is.EqualTo(fence.Length));
    });

    [TestCase(32)]
    [TestCase(4096)]
    public Task Complete_line_appends_parse_a_bounded_suffix_independent_of_fence_history(int lines) => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append("intro\n\n```text\n" + string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i}")) + "\n");
        var before = host.Producer.ParsedCharacters;
        for (var i = 0; i < 8; i++) await host.Append($"tail {i}\n");
        Assert.That(host.Producer.ParsedCharacters - before, Is.EqualTo(8 * ("```text\n".Length + "tail 0\n".Length)));
        Assert.That(host.Producer.FenceAppendCount, Is.EqualTo(8));
        AssertParity(host, host.Source.ToString());
    });

    [Test]
    public Task Custom_pipelines_indentation_closing_fences_and_clear_keep_the_conservative_path() => OnUi(async () =>
    {
        using var custom = new ProducerHost(new MarkdownPipelineBuilder().UseAdvancedExtensions().UseCodeBlockSpanFixer().Build());
        await custom.Append("intro\n\n```text\none\n");
        await custom.Append("two\n");
        Assert.That(custom.Producer.FenceAppendCount, Is.Zero);
        AssertParity(custom, custom.Source.ToString());
        using var indented = new ProducerHost();
        await indented.Append("intro\n\n   ```text\n   one\n");
        await indented.Append("   two\n");
        Assert.That(indented.Producer.FenceAppendCount, Is.Zero);
        AssertParity(indented, indented.Source.ToString());
        using var ordinary = new ProducerHost();
        await ordinary.Append("intro\n\n```text\none\n");
        await ordinary.Append("two\n");
        await ordinary.Append("```\n\n[ref]\n\n[ref]: https://example.invalid\n");
        AssertParity(ordinary, ordinary.Source.ToString());
        ordinary.Source.Clear();
        await ordinary.Append("replacement\n\n~~~text\nfresh\n");
        await ordinary.Append("last\n");
        AssertParity(ordinary, ordinary.Source.ToString());
    });

    [Test]
    public Task Detached_renderers_release_shared_subscriptions_and_replay_on_reattachment() => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append("before");
        var views = Enumerable.Range(0, 8).Select(_ => new MarkdownRenderer { UpdateProducer = host.Producer }).ToArray();
        var panel = new StackPanel();
        foreach (var view in views) panel.Children.Add(view);
        var window = new Window { Width = 600, Height = 400, Content = panel };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.That(SubscriberCount(host.Producer), Is.EqualTo(9)); // includes the test witness
            window.Content = null; window.UpdateLayout();
            Assert.That(SubscriberCount(host.Producer), Is.EqualTo(1));
            var previous = views[0].DocumentUpdate;
            await host.Append(" after");
            Assert.That(views.Select(view => view.DocumentUpdate), Is.All.SameAs(previous));
            window.Content = panel; window.UpdateLayout();
            Assert.That(SubscriberCount(host.Producer), Is.EqualTo(9));
            Assert.That(views.Select(view => view.DocumentUpdate), Is.All.SameAs(host.Latest));
            window.Content = null; window.UpdateLayout();
            var other = new MarkdownUpdateProducer();
            views[0].UpdateProducer = other;
            Assert.That(SubscriberCount(other), Is.Zero);
            panel.Children.Remove(views[0]);
            window.Content = views[0]; window.UpdateLayout();
            Assert.That(SubscriberCount(other), Is.EqualTo(1));
        }
        finally { window.Close(); }
    });

    [Test]
    public Task Initial_off_tree_renderers_and_repeated_attach_cycles_remain_supported() => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append("initial");
        var view = new MarkdownRenderer { UpdateProducer = host.Producer };
        Assert.That(view.DocumentUpdate, Is.SameAs(host.Latest));
        var window = new Window { Width = 600, Height = 400 };
        try
        {
            window.Show();
            for (var i = 0; i < 3; i++)
            {
                window.Content = view; window.UpdateLayout();
                Assert.That(SubscriberCount(host.Producer), Is.EqualTo(2));
                window.Content = null; window.UpdateLayout();
                Assert.That(SubscriberCount(host.Producer), Is.EqualTo(1));
                await host.Append(" tail");
            }
            window.Content = view; window.UpdateLayout();
            Assert.That(view.DocumentUpdate, Is.SameAs(host.Latest));
        }
        finally { window.Close(); }
    });

    [Test]
    public Task A_default_producer_stops_without_subscribers_and_catches_up_on_attach() => OnUi(async () =>
    {
        var source = new ObservableStringBuilder("initial");
        var view = new MarkdownRenderer { MarkdownBuilder = source };
        var producer = (MarkdownUpdateProducer)view.UpdateProducer;
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (producer.Subscribe(new Observer(update => { if (update.Version == 0) initial.TrySetResult(); }))) await initial.Task;
        var window = new Window { Width = 600, Height = 400, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            window.Content = null; window.UpdateLayout();
            Assert.That(SubscriberCount(producer), Is.Zero);
            var previous = view.DocumentUpdate;
            source.Append(" while detached");
            Assert.That(view.DocumentUpdate, Is.SameAs(previous));
            window.Content = view; window.UpdateLayout();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (producer.Subscribe(new Observer(update => { if (update.Version == source.Version) ready.TrySetResult(); }))) await ready.Task;
            Assert.That(view.DocumentUpdate!.Version, Is.EqualTo(source.Version));
            Assert.That(SubscriberCount(producer), Is.EqualTo(1));
        }
        finally { window.Close(); }
    });

    [Test]
    public Task Unsupported_languages_share_a_stateless_fallback_without_retaining_labels() => OnUi(() =>
    {
        var cache = (IDictionary)typeof(SyntaxHighlighting).GetField("LanguageCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var before = cache.Count;
        var fallback = SyntaxHighlighting.Create("round4-unsupported-fallback");
        for (var i = 0; i < 256; i++) Assert.That(SyntaxHighlighting.Create($"round4-unsupported-{i}"), Is.SameAs(fallback));
        Assert.That(fallback.HasGrammar, Is.False);
        Assert.That(cache.Count, Is.EqualTo(before));
        Assert.That(SyntaxHighlighting.Create("CSHARP"), Is.SameAs(SyntaxHighlighting.Create("csharp")));
        return Task.CompletedTask;
    });

    [Test]
    public Task Oversized_warming_skips_tokenization_but_foreground_formatting_still_works() => OnUi(() =>
    {
        var highlighter = new SyntaxHighlighting("csharp");
        var text = string.Join("\n", Enumerable.Range(0, 1024).Select(i => $"// round4 large {i} " + new string('x', 256)));
        highlighter.Prewarm(text); highlighter.Prewarm(text);
        Assert.That(highlighter.GrammarLockAcquisitions, Is.Zero);
        var code = new RenderedCode { AutoSyntaxHighlight = false, Language = "csharp", Code = text };
        code.HighlightSyntax();
        Assert.That(code.Code, Is.EqualTo(text.Replace("\n", Environment.NewLine)));
        Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1024));
        return Task.CompletedTask;
    });

    [Test]
    public Task Read_only_code_inspection_preserves_the_fast_path_and_actual_mutation_invalidates_it() => OnUi(() =>
    {
        var code = new RenderedCode { AutoSyntaxHighlight = false, Language = "csharp" };
        for (var i = 0; i < 512; i++) code.SetLine(i, $"var round4Item{i} = {i};");
        code.HighlightSyntax();
        var chunks = code.CodeTextBlocks;
        Assert.That(code.CodeTextBlock, Is.SameAs(chunks[0]));
        Assert.That(chunks, Is.Not.InstanceOf<List<MarkdownTextBlock>>());
        code.HighlightSyntax();
        Assert.That(code.LastHighlightLineVisits, Is.Zero);
        code.SetLine(512, "var tail = 1;"); code.HighlightSyntax();
        Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1));
        chunks[0].Inlines![0] = new Run("// externally replaced");
        code.HighlightSyntax();
        Assert.That(code.Code, Does.StartWith("// externally replaced"));
        Assert.That(code.LastHighlightLineVisits, Is.EqualTo(513));
        return Task.CompletedTask;
    });

    [Test]
    public Task Editing_an_exposed_run_invalidates_authoritative_code_lines() => OnUi(() =>
    {
        var code = new RenderedCode { AutoSyntaxHighlight = false, Language = "csharp", Code = "// original" };
        code.HighlightSyntax();
        var run = (Run)code.CodeTextBlocks[0].Inlines![0];
        run.Text = "// external edit";
        code.HighlightSyntax();
        Assert.That(code.Code, Is.EqualTo("// external edit"));
        Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1));
        return Task.CompletedTask;
    });

    [Test]
    public Task Search_keeps_matches_for_unchanged_ranges_but_rebuilds_for_new_ranges_and_external_highlights() => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append("needle first\n\nneedle last");
        var window = WindowFor(host.Renderer);
        try
        {
            window.Show(); window.UpdateLayout();
            host.Renderer.ApplyTextSearch("needle");
            var matches = host.Renderer.TextSearchMatches;
            var highlight = matches[1].Block.Highlights.Values.Single();
            await host.Append(" unchanged ranges"); window.UpdateLayout();
            Assert.That(host.Renderer.TextSearchMatches, Is.SameAs(matches));
            Assert.That(matches[1].Block.Highlights.Values.Single(), Is.SameAs(highlight));
            await host.Append(" needle"); window.UpdateLayout();
            Assert.That(host.Renderer.TextSearchMatches, Has.Count.EqualTo(3));
            Assert.That(host.Renderer.TextSearchMatches, Is.Not.SameAs(matches));
            var block = host.Renderer.TextSearchMatches[0].Block;
            block.Highlights.Remove(MarkdownRenderer.DefaultTextSearchHighlightName);
            host.Renderer.InvalidateRenderedTextState(); host.Renderer.InvalidateMeasure(); window.UpdateLayout();
            Assert.That(block.Highlights.TryGetValue(MarkdownRenderer.DefaultTextSearchHighlightName, out _), Is.True);
        }
        finally { window.Close(); }
    });

    [TestCase(false)]
    [TestCase(true)]
    public Task Resize_reuses_measured_layouts_and_releases_spares_after_dispatcher_jobs(bool wrap) => OnUi(() =>
    {
        var block = new MarkdownTextBlock { Text = string.Join(" ", Enumerable.Repeat("long code reply", 64)),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        var window = WindowFor(new StackPanel { Children = { block } });
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var start = MarkdownTextBlock.TextLayoutsCreated;
            window.Width = 400; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.That(window.Bounds.Width, Is.EqualTo(400));
            Assert.That(MarkdownTextBlock.TextLayoutsCreated - start, Is.LessThan(3));
            var height = block.TextLayout.Height;
            block.Text += " more content"; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.That(block.RetainedLayoutCount, Is.Zero);
            Assert.That(block.LayoutText, Does.EndWith("more content"));
            block.FontSize *= 1.5; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.That(block.TextLayout.Height, Is.GreaterThan(height));
            window.Content = null; window.UpdateLayout();
            Assert.That(block.RetainedLayoutCount, Is.Zero);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [TestCase(0, false)]
    [TestCase(2, true)]
    public Task Layout_reuse_preserves_height_limits_and_trimming(int maxLines, bool trim) => OnUi(() =>
    {
        MarkdownTextBlock Create() => new()
        {
            Text = string.Join(" ", Enumerable.Repeat("height constrained content", 128)),
            TextWrapping = TextWrapping.Wrap, MaxLines = maxLines,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None
        };
        var block = Create();
        block.Measure(new Size(400, double.PositiveInfinity));
        block.Arrange(new Rect(0, 0, 600, 100));
        block.Arrange(new Rect(0, 0, 400, 30));
        var reference = Create();
        reference.Measure(new Size(400, double.PositiveInfinity));
        reference.Arrange(new Rect(0, 0, 400, 30));
        Assert.That(block.TextLayout.TextLines.Select(line => (line.FirstTextSourceIndex, line.Length, line.Width, line.Height)),
            Is.EqualTo(reference.TextLayout.TextLines.Select(line => (line.FirstTextSourceIndex, line.Length, line.Width, line.Height))));
        Dispatcher.UIThread.RunJobs();
        Assert.That(block.RetainedLayoutCount, Is.Zero);
        return Task.CompletedTask;
    });

    [Test]
    public Task Embedded_controls_keep_native_arrangement_and_are_not_retained_in_spare_layouts() => OnUi(() =>
    {
        var child = new Border { Width = 24, Height = 18 };
        var block = new MarkdownTextBlock { Inlines = new InlineCollection
            { new Run("before "), new InlineUIContainer { Child = child }, new Run(" after") } };
        var window = WindowFor(block);
        try
        {
            window.Show(); window.UpdateLayout();
            window.Width = 400; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.That(child.GetVisualParent(), Is.SameAs(block));
            Assert.That(child.Bounds.Size, Is.EqualTo(new Size(24, 18)));
            Assert.That(block.RetainedLayoutCount, Is.Zero);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static int SubscriberCount(MarkdownUpdateProducer producer) =>
        ((IEnumerable)typeof(MarkdownUpdateProducer).GetField("observers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!).Cast<object>().Count();
    private static Window WindowFor(Control content) => new() { Width = 600, Height = 400,
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Visible } };
    private static void AssertParity(ProducerHost host, string source)
    {
        var whole = Markdown.Parse(source, host.Producer.Pipeline);
        Assert.That(Describe(host.Latest!.Document), Is.EqualTo(Describe(whole)));
        var fresh = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(whole) };
        Assert.That(host.Renderer.GetVisualDescendants().OfType<RenderedCode>().Select(code => code.Code),
            Is.EqualTo(fresh.GetVisualDescendants().OfType<RenderedCode>().Select(code => code.Code)));
    }
    private static string Describe(MarkdownDocument document)
    {
        var result = new StringBuilder();
        foreach (var node in document.Descendants())
        {
            result.Append(node.GetType().Name).Append(node.Span);
            if (node is Markdig.Syntax.CodeBlock code)
                for (var i = 0; i < code.Lines.Count; i++) result.Append('|').Append(code.Lines.Lines[i].Slice.ToString()).Append(':').Append(code.Lines.Lines[i].NewLine);
            if (node is LiteralInline literal) result.Append(literal.Content.ToString());
            if (node is LinkInline link) result.Append(link.Url);
            result.AppendLine();
        }
        return result.ToString();
    }
    private static async Task OnUi(Func<Task> work) => await HeadlessSession.Current.Dispatch(async () => { await work(); return true; }, CancellationToken.None);
    private sealed class Observer(Action<MarkdownDocumentUpdate> receive) : IObserver<MarkdownDocumentUpdate>
    {
        public void OnNext(MarkdownDocumentUpdate value) => receive(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
    private sealed class ProducerHost : IObserver<MarkdownDocumentUpdate>, IDisposable
    {
        internal ObservableStringBuilder Source { get; } = new();
        internal MarkdownRenderer Renderer { get; } = new();
        internal MarkdownUpdateProducer Producer { get; }
        internal MarkdownDocumentUpdate? Latest;
        private readonly IDisposable subscription;
        private TaskCompletionSource<MarkdownDocumentUpdate>? pending;
        private long expected;
        internal ProducerHost(MarkdownPipeline? pipeline = null)
        {
            Producer = new MarkdownUpdateProducer { MarkdownBuilder = Source };
            if (pipeline is not null) Producer.Pipeline = pipeline;
            subscription = Producer.Subscribe(this);
        }
        internal Task<MarkdownDocumentUpdate> Append(string text)
        {
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously); expected = Source.Version + 1;
            Source.Append(text); return pending.Task;
        }
        public void OnNext(MarkdownDocumentUpdate value)
        {
            try { Renderer.DocumentUpdate = value; Latest = value; if (value.Version >= expected) pending?.TrySetResult(value); }
            catch (Exception error) { pending?.TrySetException(error); throw; }
        }
        public void OnError(Exception error) => pending?.TrySetException(error);
        public void OnCompleted() { }
        public void Dispose() => subscription.Dispose();
    }
}
