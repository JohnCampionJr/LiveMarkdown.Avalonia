using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NUnit.Framework;
using Code = Markdig.Syntax.CodeBlock;
using RenderedCode = LiveMarkdown.Avalonia.CodeBlock;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture, NonParallelizable]
public class RoundThreeStreamingTests
{
    [TestCase(32)]
    [TestCase(256)]
    public Task Producer_proves_an_unchanged_prefix_without_visiting_its_nodes(int count) => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append(string.Join("\n\n", Enumerable.Range(0, count).Select(i => $"paragraph {i}")));
        var oldBlocks = host.Latest!.Document.Take(count - 1).ToArray();
        var oldViews = TextBlocks(host.Renderer).Take(count - 1).ToArray();
        var oldSpans = oldViews.Select(view => view.SourceSpan).ToArray();
        host.Reports.Clear();
        var update = await host.Append(" tail");
        Assert.That(update, Is.TypeOf<MarkdownDocumentUpdate.Incremental>());
        Assert.That(((MarkdownDocumentUpdate.Incremental)update).UnchangedPrefixBlockCount, Is.EqualTo(count - 1));
        Assert.That(Root(host.Renderer).LastPrefixBlocksSkipped, Is.EqualTo(count - 1));
        Assert.That(host.Reports.Single().SkippedNodes, Is.Zero);
        Assert.That(host.Reports.Single().ConstructedNodes, Is.Zero);
        Assert.That(TextBlocks(host.Renderer).Last().LayoutText, Does.EndWith(" tail"));
        Assert.That(host.Latest.Document.Take(count - 1), Is.EqualTo(oldBlocks));
        Assert.That(TextBlocks(host.Renderer).Take(count - 1), Is.EqualTo(oldViews));
        Assert.That(oldViews.Select(view => view.SourceSpan), Is.EqualTo(oldSpans));
    });

    [Test]
    public Task Public_deltas_reorders_and_unrelated_update_identities_keep_the_conservative_path() => OnUi(() =>
    {
        const string source = "first\n\nsecond\n\nlast";
        var initial = Full(source);
        var renderer = new MarkdownRenderer { DocumentUpdate = initial };
        using var reports = new Capture();
        var changed = source + " tail";
        var delta = new MarkdownDocumentUpdate.Incremental(initial, Parse(changed),
            new ObservableStringBuilderChangedEventArgs(source.Length, 5, changed.Length, 1));
        renderer.DocumentUpdate = delta;
        Assert.That(delta.UnchangedPrefixBlockCount, Is.Null);
        Assert.That(Root(renderer).LastPrefixBlocksSkipped, Is.Zero);
        Assert.That(reports.Values.Last().SkippedNodes, Is.GreaterThan(0));
        Assert.That(TextBlocks(renderer).Last().LayoutText, Is.EqualTo("last tail"));

        const string reordered = "second\n\nfirst\n\nlast tail";
        var reorder = new MarkdownDocumentUpdate.Incremental(delta, Parse(reordered),
            new ObservableStringBuilderChangedEventArgs(0, reordered.Length, reordered.Length, 2));
        renderer.DocumentUpdate = reorder;
        Assert.That(Root(renderer).LastPrefixBlocksSkipped, Is.Zero);
        Assert.That(TextBlocks(renderer).Select(view => view.LayoutText), Is.EqualTo(new[] { "second", "first", "last tail" }));

        // Even a producer-style proof cannot apply when this renderer did not receive its base.
        var foreign = Full(reordered);
        const string replacement = "replacement\n\ncontent";
        renderer.DocumentUpdate = new MarkdownDocumentUpdate.Incremental(foreign, Parse(replacement),
            new ObservableStringBuilderChangedEventArgs(reordered.Length, 1, replacement.Length, 3), 2);
        Assert.That(Root(renderer).LastPrefixBlocksSkipped, Is.Zero);
        Assert.That(TextBlocks(renderer).Select(view => view.LayoutText), Is.EqualTo(new[] { "replacement", "content" }));
        renderer.DocumentUpdate = null;
        renderer.DocumentUpdate = Full("fresh tree");
        Assert.That(TextBlocks(renderer).Single().LayoutText, Is.EqualTo("fresh tree"));
    });

    [Test]
    public Task Custom_node_dirtiness_and_factory_edits_are_not_skipped_by_a_prefix_proof() => OnUi(async () =>
    {
        MarkdownNode.Edit(builder => builder.Unregister<ParagraphBlockNode>().Register<AlwaysDirtyParagraphNode>());
        try
        {
            using var host = new ProducerHost();
            await host.Append("first\n\nsecond\n\nlast");
            var before = AlwaysDirtyParagraphNode.Updates;
            await host.Append(" tail");
            Assert.That(Root(host.Renderer).LastPrefixBlocksSkipped, Is.Zero);
            Assert.That(AlwaysDirtyParagraphNode.Updates - before, Is.EqualTo(3));
            Assert.That(TextBlocks(host.Renderer).Last().LayoutText, Is.EqualTo("last tail"));
        }
        finally { MarkdownNode.Edit(builder => builder.Unregister<AlwaysDirtyParagraphNode>().Register<ParagraphBlockNode>()); }

        using var ordinary = new ProducerHost();
        await ordinary.Append("first\n\nsecond\n\nlast");
        MarkdownNode.Edit(builder => builder.Unregister<ThematicBreakBlockNode>());
        try
        {
            await ordinary.Append(" tail");
            Assert.That(Root(ordinary.Renderer).LastPrefixBlocksSkipped, Is.Zero, "factory-set edits invalidate prefix eligibility");
        }
        finally { MarkdownNode.Register<ThematicBreakBlockNode>(); }
    });

    [Test]
    public Task Definition_and_heading_reference_fallbacks_render_changed_prefix_meanings() => OnUi(async () =>
    {
        using var definitions = new ProducerHost();
        await definitions.Append("[target]\n\nlast paragraph");
        var update = await definitions.Append("\n\n[target]: https://example.invalid/path\n");
        Assert.That(update, Is.TypeOf<MarkdownDocumentUpdate.Full>());
        Assert.That(Root(definitions.Renderer).LastPrefixBlocksSkipped, Is.Zero);
        Assert.That(TextBlocks(definitions.Renderer).First().LayoutText, Is.EqualTo("target"));

        using var headings = new ProducerHost();
        await headings.Append("[new heading]\n\nlast paragraph");
        var oldPrefix = TextBlocks(headings.Renderer).First().LayoutText;
        update = await headings.Append("\n\n# New heading\n");
        Assert.That(update, Is.TypeOf<MarkdownDocumentUpdate.Full>());
        Assert.That(Root(headings.Renderer).LastPrefixBlocksSkipped, Is.Zero);
        Assert.That(update.Document.Descendants().OfType<LinkInline>(), Is.Not.Empty, "the newly defined heading changed the earlier reference's meaning");
        var fresh = new MarkdownRenderer { DocumentUpdate = Full("[new heading]\n\nlast paragraph\n\n# New heading\n") };
        Assert.That(TextBlocks(headings.Renderer).Select(view => view.LayoutText),
            Is.EqualTo(TextBlocks(fresh).Select(view => view.LayoutText)), "global-state fallback must render the same end state as a fresh document");
        Assert.That(TextBlocks(headings.Renderer).First().LayoutText, Is.Not.EqualTo(oldPrefix), "the old literal must not survive the fallback");
    });

    [TestCase(32)]
    [TestCase(512)]
    public Task Producer_code_appends_inspect_only_new_lines_and_keep_formatting_incremental(int count) => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append("```csharp\n" + string.Join("\n", Enumerable.Range(0, count).Select(i => $"var value{i} = {i};")) + "\n");
        var before = CodeBlockNode.LineInspections;
        await host.Append("var appended = 42;\n");
        Assert.That(CodeBlockNode.LineInspections - before, Is.EqualTo(1));
        var code = host.Renderer.GetVisualDescendants().OfType<RenderedCode>().Single();
        Assert.That(code.Code, Does.EndWith("var appended = 42;"));
        Assert.That(code.LastHighlightLineVisits, Is.EqualTo(1));
        before = CodeBlockNode.LineInspections;
        await host.Append("var next = 43;\n");
        Assert.That(CodeBlockNode.LineInspections - before, Is.EqualTo(1));
        Assert.That(code.Code, Does.EndWith("var next = 43;"));
    });

    [Test]
    public Task A_spliced_code_tail_normalizes_source_positions_for_edits_to_an_existing_line() => OnUi(async () =>
    {
        using var host = new ProducerHost();
        await host.Append("a paragraph before code\n\n```csharp\nvar result = ");
        Assert.That(host.Producer.SpliceCount, Is.Zero);
        await host.Append("42;");
        Assert.That(host.Producer.SpliceCount, Is.GreaterThan(0));
        var code = host.Renderer.GetVisualDescendants().OfType<RenderedCode>().Single();
        Assert.That(code.Code, Is.EqualTo("var result = 42;"));
        await host.Append("\n\nvar next = 7;\n");
        Assert.That(code.Code, Is.EqualTo(string.Join(Environment.NewLine, "var result = 42;", "", "var next = 7;")));
        Assert.That(code.SourceSpan.Start, Is.GreaterThan(0));
    });

    [Test]
    public Task Editing_the_last_unterminated_line_uses_a_bounded_position_search() => OnUi(() =>
    {
        var source = "```text\n" + string.Join("\n", Enumerable.Range(0, 512).Select(i => $"line {i}"));
        var document = Parse(source);
        IncrementalParse.CanSplice(document);
        var root = new DocumentNode(new MarkdownRenderer());
        var node = new CodeBlockNode();
        node.Update(root, document.OfType<Code>().Single(), new(0, source.Length, source.Length, 0), CancellationToken.None);
        var after = source + " changed";
        var edited = Parse(after);
        IncrementalParse.CanSplice(edited);
        var inspections = CodeBlockNode.LineInspections;
        var searches = CodeBlockNode.LineSearchComparisons;
        node.Update(root, edited.OfType<Code>().Single(), new(source.Length, 8, after.Length, 1), CancellationToken.None);
        Assert.That(CodeBlockNode.LineInspections - inspections, Is.EqualTo(1));
        Assert.That(CodeBlockNode.LineSearchComparisons - searches, Is.InRange(1, 10));
        Assert.That(((RenderedCode)node.Control).Code, Does.EndWith("line 511 changed"));
    });

    [TestCase("```csharp\nvar one = 1;\n\nvar two = 2;\n", "```csharp\nvar one = 1;\nfilled blank\nvar two = 2;\n")]
    [TestCase("```csharp\nvar one = 1;\nvar two = 2;\n", "```csharp\nvar one = 9;\nvar two = 2;\n")]
    [TestCase("```csharp\nvar one = 1;\nvar two = 2;\n", "```csharp\nvar one = 1;\n")]
    [TestCase("    first\n    second\n", "    first\n    replacement\n")]
    [TestCase("  ```text\n  alpha\n  beta\n", "```text\n  alpha\n  beta\n")]
    [TestCase("```text\nalpha\nbeta\n", "```text\nalpha\n```\nbeta\n")]
    public Task Changed_code_ranges_match_a_fresh_node_after_edits_truncation_indentation_and_fence_changes(string before, string after) => OnUi(() =>
    {
        var renderer = new MarkdownRenderer();
        var root = new DocumentNode(renderer);
        var node = new CodeBlockNode();
        var oldDocument = Parse(before);
        IncrementalParse.CanSplice(oldDocument);
        var oldBlock = oldDocument.OfType<Code>().First();
        node.Update(root, oldBlock, new(0, before.Length, before.Length, 0), CancellationToken.None);
        var nextDocument = Parse(after);
        IncrementalParse.CanSplice(nextDocument);
        var nextBlock = nextDocument.OfType<Code>().First();
        var change = Change(before, after);
        node.Update(root, nextBlock, change, CancellationToken.None);
        var fresh = new CodeBlockNode();
        fresh.Update(root, nextBlock, new(0, after.Length, after.Length, 1), CancellationToken.None);
        Assert.That(((RenderedCode)node.Control).Code, Is.EqualTo(((RenderedCode)fresh.Control).Code));
        Assert.That(((RenderedCode)node.Control).SourceSpan, Is.EqualTo(nextBlock.Span));
    });

    [Test]
    public Task Public_code_asts_keep_conservative_inspection_and_cancellation_is_observed_before_skipping() => OnUi(() =>
    {
        var owner = new MarkdownRenderer();
        var root = new DocumentNode(owner);
        var node = new CodeBlockNode();
        var source = "```text\nfirst\nsecond\n";
        node.Update(root, Parse(source).OfType<Code>().Single(), new(0, source.Length, source.Length, 0), CancellationToken.None);
        var after = source + "third\n";
        var before = CodeBlockNode.LineInspections;
        node.Update(root, Parse(after).OfType<Code>().Single(), new(source.Length, 6, after.Length, 1), CancellationToken.None);
        Assert.That(CodeBlockNode.LineInspections - before, Is.EqualTo(3));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledSource = after + "fourth\n";
        var canceledDocument = Parse(canceledSource);
        IncrementalParse.CanSplice(canceledDocument);
        before = CodeBlockNode.LineInspections;
        Assert.Throws<OperationCanceledException>(() => node.Update(root, canceledDocument.OfType<Code>().Single(), new(after.Length, 7, canceledSource.Length, 2), cancellation.Token));
        Assert.That(CodeBlockNode.LineInspections, Is.EqualTo(before), "canceled work must not inspect any code line");
        Assert.Throws<OperationCanceledException>(() => root.UpdateWithPrefix(Parse("body"), new(0, 4, 4, 1), 1, cancellation.Token));
        Assert.That(root.UnchangedPrefixBlockCount, Is.Null, "proof is cleared even on cancellation");
    });

    private static MarkdownDocument Parse(string source) => Markdown.Parse(source, MarkdownUpdateProducer.DefaultPipeline);
    private static MarkdownDocumentUpdate.Full Full(string source) => new(Parse(source));
    private static MarkdownTextBlock[] TextBlocks(MarkdownRenderer renderer) => renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray();
    private static DocumentNode Root(MarkdownRenderer renderer) => (DocumentNode)typeof(MarkdownRenderer).GetField("documentNode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
    private static Task OnUi(Action action) => HeadlessSession.Current.Dispatch(action, CancellationToken.None);
    private static Task OnUi(Func<Task> action) => HeadlessSession.Current.Dispatch(async () => { await action(); return true; }, CancellationToken.None);
    private static ObservableStringBuilderChangedEventArgs Change(string before, string after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var end = 0;
        while (end < before.Length - start && end < after.Length - start && before[before.Length - end - 1] == after[after.Length - end - 1]) end++;
        return new(start, Math.Max(before.Length, after.Length) - start - end, after.Length, 1);
    }

    public sealed class AlwaysDirtyParagraphNode : BlockNode<ParagraphBlock>
    {
        public static int Updates;
        public override Control Control { get; } = new MarkdownTextBlock();
        protected override bool IsDirty(MarkdownObject markdownObject, in ObservableStringBuilderChangedEventArgs change) => true;
        protected override bool UpdateCore(DocumentNode documentNode, ParagraphBlock block, in ObservableStringBuilderChangedEventArgs change, CancellationToken cancellationToken)
        {
            Updates++;
            ((MarkdownTextBlock)Control).Text = ((LiteralInline)block.Inline!.FirstChild!).Content.ToString();
            return true;
        }
    }

    private sealed class Capture : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        internal List<MarkdownRenderReport> Values { get; } = [];
        private readonly IDisposable subscription;
        internal Capture() => subscription = MarkdownRenderDiagnostics.Listener.Subscribe(this, name => name == MarkdownRenderDiagnostics.UpdateEvent);
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is MarkdownRenderReport report) Values.Add(report); }
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
        public void Dispose() => subscription.Dispose();
    }

    private sealed class ProducerHost : IObserver<MarkdownDocumentUpdate>, IDisposable
    {
        private readonly ObservableStringBuilder source = new();
        private readonly IDisposable subscription;
        private readonly Capture capture = new();
        private TaskCompletionSource<MarkdownDocumentUpdate>? pending;
        private long awaitedVersion;
        internal MarkdownRenderer Renderer { get; } = new();
        internal MarkdownUpdateProducer Producer { get; }
        internal MarkdownDocumentUpdate? Latest;
        internal List<MarkdownRenderReport> Reports => capture.Values;
        internal ProducerHost()
        {
            Producer = new MarkdownUpdateProducer { MarkdownBuilder = source };
            subscription = Producer.Subscribe(this);
        }
        internal Task<MarkdownDocumentUpdate> Append(string text)
        {
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            awaitedVersion = source.Version + 1;
            source.Append(text);
            return pending.Task;
        }
        public void OnNext(MarkdownDocumentUpdate value)
        {
            try
            {
                Renderer.DocumentUpdate = value;
                Latest = value;
                if (value.Version >= awaitedVersion) pending?.TrySetResult(value);
            }
            catch (Exception error) { pending?.TrySetException(error); throw; }
        }
        public void OnError(Exception error) => pending?.TrySetException(error);
        public void OnCompleted() { }
        public void Dispose() { subscription.Dispose(); capture.Dispose(); }
    }
}
