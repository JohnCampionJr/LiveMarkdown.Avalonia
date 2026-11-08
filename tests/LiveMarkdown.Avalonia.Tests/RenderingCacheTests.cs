using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using NUnit.Framework;
using Markdig;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture, NonParallelizable]
public class RenderingCacheTests
{
    [Test]
    public Task Width_and_selection_changes_reuse_text_and_run_indices() => Run(() =>
    {
        using var host = new Host();
        var text = host.Block.LayoutText;
        var textBuilds = host.Block.LayoutTextBuilds;
        var indices = host.Block.RunIndexBuilds;
        Assert.That(indices, Is.GreaterThan(0));
        host.Resize(320);
        host.Resize(800);
        host.Block.SelectionStart = 1;
        host.Block.SelectionEnd = 5;
        host.Window.UpdateLayout();
        Assert.That(host.Block.LayoutText, Is.SameAs(text));
        Assert.That(host.Block.LayoutTextBuilds, Is.EqualTo(textBuilds));
        Assert.That(host.Block.RunIndexBuilds, Is.EqualTo(indices));
    });

    [Test]
    public Task Nested_content_changes_refresh_caches_even_while_measure_is_already_invalid() => Run(() =>
    {
        using var host = new Host();
        var original = host.Block.LayoutText;
        var spans = host.Block.GetCodeInlineSpans();
        var indices = host.Block.RunIndexBuilds;
        host.Block.InvalidateMeasure();
        host.Prefix.Text = "changed prefix ";
        host.Chip.Text = "changed code";
        Assert.That(host.Block.LayoutText, Is.Not.EqualTo(original));
        Assert.That(host.Block.LayoutText, Does.Contain("changed code"));
        var changed = host.Block.GetCodeInlineSpans().Single();
        Assert.That(changed.Start, Is.EqualTo("changed prefix ".Length));
        Assert.That(changed.Length, Is.EqualTo("changed code".Length));
        Assert.That(changed.Start, Is.Not.EqualTo(spans[0].Start));
        host.Window.UpdateLayout();
        Assert.That(host.Block.RunIndexBuilds, Is.GreaterThan(indices));
    });

    [Test]
    public Task Replacing_inlines_unsubscribes_old_content_and_observes_the_replacement() => Run(() =>
    {
        using var host = new Host();
        var old = host.Block.Inlines!;
        var replacement = new Run("replacement source");
        host.Block.Inlines = new InlineCollection { replacement };
        var text = host.Block.LayoutText;
        host.Prefix.Text = "retired prefix";
        old.Add(new Run("retired addition"));
        Assert.That(host.Block.LayoutText, Is.SameAs(text));
        replacement.Text = "current source";
        Assert.That(host.Block.LayoutText, Is.EqualTo("current source"));
        Assert.That(host.Block.GetCodeInlineSpans(), Is.Empty);
    });

    [Test]
    public Task Inline_span_walks_and_pointer_geometry_are_reused_and_public_results_cannot_corrupt_them() => Run(() =>
    {
        using var host = new Host();
        var rects = host.Block.GetCodeInlineRects(host.Chip);
        var spans = host.Block.InlineSpanBuilds;
        var geometry = host.Block.CodeInlineGeometryBuilds;
        Assert.That(rects, Is.Not.Empty);
        for (var i = 0; i < 20; i++)
        {
            Assert.That(host.Block.GetCodeInlineRects(host.Chip), Is.SameAs(rects));
            Assert.That(host.Block.GetCodeInlineAt(rects[0].Center), Is.SameAs(host.Chip));
        }
        var exposed = host.Block.GetCodeInlineSpans();
        exposed[0] = default;
        Assert.That(host.Block.GetCodeInlineSpans()[0].Source, Is.SameAs(host.Chip));
        Assert.That(host.Block.GetCodeInlineAt(rects[0].Center), Is.SameAs(host.Chip));
        Assert.That(host.Block.InlineSpanBuilds, Is.EqualTo(spans));
        Assert.That(host.Block.CodeInlineGeometryBuilds, Is.EqualTo(geometry));
        Assert.That(((IList<Rect>)rects).IsReadOnly, Is.True);
        Assert.Throws<NotSupportedException>(() => ((IList<Rect>)rects)[0] = default);
    });

    [Test]
    public Task Wrapped_geometry_refreshes_on_width_padding_and_nested_content_changes() => Run(() =>
    {
        using var host = new Host();
        var original = host.Block.GetCodeInlineRects(host.Chip);
        var spanBuilds = host.Block.InlineSpanBuilds;
        host.Resize(160);
        var wrapped = host.Block.GetCodeInlineRects(host.Chip);
        Assert.That(wrapped, Is.Not.SameAs(original));
        Assert.That(wrapped.Count, Is.GreaterThan(original.Count));
        Assert.That(host.Block.InlineSpanBuilds, Is.EqualTo(spanBuilds), "width does not change inline metadata");
        host.Chip.Padding = new Thickness(8, 2);
        host.Window.UpdateLayout();
        var padded = host.Block.GetCodeInlineRects(host.Chip);
        Assert.That(padded, Is.Not.SameAs(wrapped));
        Assert.That(host.Block.GetCodeInlineSpans().Single().Padding, Is.EqualTo(host.Chip.Padding));
        host.Prefix.Text = "different text before code ";
        host.Window.UpdateLayout();
        var moved = host.Block.GetCodeInlineRects(host.Chip);
        Assert.That(moved, Is.Not.SameAs(padded));
        Assert.That(host.Block.GetCodeInlineAt(moved[0].Center), Is.SameAs(host.Chip));
    });

    [Test]
    public Task Detach_and_reattach_refresh_content_and_detached_queries_remain_correct() => Run(() =>
    {
        using var host = new Host();
        _ = host.Block.LayoutText;
        _ = host.Block.GetCodeInlineRects(host.Chip);
        host.Window.Content = null;
        host.Chip.Text = "off tree";
        Assert.That(host.Block.LayoutText, Does.Contain("off tree"));
        Assert.That(host.Block.GetCodeInlineSpans().Single().Length, Is.EqualTo(8));
        host.Chip.Text = "another off tree edit";
        Assert.That(host.Block.LayoutText, Does.Contain("another off tree edit"));
        Assert.That(host.Block.GetCodeInlineSpans().Single().Length, Is.EqualTo(host.Chip.Text.Length));
        host.Window.Content = host.Block;
        host.Window.UpdateLayout();
        host.Chip.Text = "attached again";
        host.Window.UpdateLayout();
        Assert.That(host.Block.LayoutText, Does.Contain("attached again"));
        Assert.That(host.Block.GetCodeInlineRects(host.Chip), Is.Not.Empty);
    });

    [Test]
    public Task Nested_collection_add_remove_and_replacement_refresh_metadata_and_text() => Run(() =>
    {
        using var host = new Host();
        var span = (Span)host.Block.Inlines![1];
        _ = host.Block.LayoutText;
        var added = new CodeInline { Text = "new chip" };
        span.Inlines.Add(added);
        Assert.That(host.Block.GetCodeInlineSpans(), Has.Length.EqualTo(2));
        Assert.That(host.Block.LayoutText, Does.Contain("new chip"));
        span.Inlines.Remove(added);
        Assert.That(host.Block.GetCodeInlineSpans(), Has.Length.EqualTo(1));
        Assert.That(host.Block.LayoutText, Does.Not.Contain("new chip"));
        var replacement = new CodeInline { Text = "replacement chip" };
        span.Inlines = new InlineCollection { replacement };
        Assert.That(host.Block.GetCodeInlineSpans().Single().Source, Is.SameAs(replacement));
        Assert.That(host.Block.LayoutText, Does.Contain("replacement chip"));
        replacement.Text = "edited replacement";
        Assert.That(host.Block.LayoutText, Does.Contain("edited replacement"));
        Assert.That(host.Block.GetCodeInlineSpans().Single().Length, Is.EqualTo(replacement.Text.Length));
    });

    [Test]
    public Task Width_changes_do_not_rematch_unchanged_rendered_search_text() => Run(() =>
    {
        var renderer = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(
            Markdown.Parse("needle **strong** and `code` with more text to wrap in a small window")) };
        var window = new Window { Width = 800, Height = 600, Content = renderer };
        try
        {
            window.Show();
            window.UpdateLayout();
            var calls = 0;
            renderer.ApplyTextSearch((_, _) => { calls++; return new[] { new TextHighlightRange(0, 6) }; });
            renderer.InvalidateRenderedTextState();
            renderer.InvalidateMeasure();
            window.UpdateLayout();
            var matches = renderer.TextSearchMatches;
            Assert.That(matches, Has.Count.EqualTo(1));
            calls = 0;
            renderer.InvalidateRenderedTextState();
            window.Width = 240;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.That(calls, Is.Zero);
            Assert.That(renderer.TextSearchMatches, Is.SameAs(matches));
        }
        finally { window.Close(); }
    });

    [Test]
    public Task Shape_cache_retires_streaming_revisions_but_reuses_unchanged_neighbors() => Run(() =>
    {
        using var host = new Host();
        var stable = new CodeInline { Text = "unchanged neighbor" };
        host.Block.Inlines!.Add(stable);
        host.Window.UpdateLayout();
        Assert.That(host.Block.CodeInlineShapeCount, Is.EqualTo(3), "two code chips and a dot");
        var misses = host.Block.CodeInlineShapeMisses;
        host.Prefix.Text += "more prose ";
        host.Window.UpdateLayout();
        Assert.That(host.Block.CodeInlineShapeMisses, Is.EqualTo(misses), "prose changes must not reshape surviving chips");
        for (var i = 0; i < 24; i++)
        {
            host.Chip.Text = $"stream revision {i:D2}";
            host.Window.UpdateLayout();
            Assert.That(host.Block.CodeInlineShapeCount, Is.EqualTo(3), "historical text must not accumulate in the shape cache");
        }
        host.Block.Inlines.Clear();
        host.Window.UpdateLayout();
        Assert.That(host.Block.CodeInlineShapeCount, Is.Zero);
    });

    private static Task Run(Action action) => HeadlessSession.Current.Dispatch(action, CancellationToken.None);

    private sealed class Host : IDisposable
    {
        internal Run Prefix { get; } = new("A longer prefix before the code ");
        internal CodeInline Chip { get; } = new() { Text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi" };
        internal MarkdownTextBlock Block { get; }
        internal Window Window { get; }
        internal Host()
        {
            Block = new MarkdownTextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap,
                Inlines = new InlineCollection { Prefix, new Span { Inlines = { Chip, new DotInline() } }, new Run(" suffix") } };
            Window = new Window { Width = 800, Height = 600, Content = Block };
            try { Window.Show(); Window.UpdateLayout(); }
            catch { Window.Close(); throw; }
        }
        internal void Resize(double width)
        {
            Window.Width = width;
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }
        public void Dispose() => Window.Close();
    }
}
