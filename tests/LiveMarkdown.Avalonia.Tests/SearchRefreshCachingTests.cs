using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class SearchRefreshCachingTests
{
    [Test]
    public Task Unchanged_refresh_preserves_matches_and_highlights_without_notifications_or_rematching() => OnSession(() =>
    {
        using var host = new Host("needle first\n\nneedle second\n\nno result here");
        var calls = 0;
        host.Renderer.ApplyTextSearch((_, text) =>
        {
            calls++;
            return text.StartsWith("needle", StringComparison.Ordinal) ? [new TextHighlightRange(0, 6)] : [];
        });
        host.Refresh();
        var matches = host.Renderer.TextSearchMatches;
        var highlights = matches.Select(match => match.Block.Highlights.Values.Single()).ToArray();
        var notifications = 0;
        host.Renderer.PropertyChanged += (_, e) => { if (e.Property == MarkdownRenderer.TextSearchMatchesProperty) notifications++; };
        calls = 0;
        for (var i = 0; i < 4; i++) host.Refresh();
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Zero);
            Assert.That(notifications, Is.Zero);
            Assert.That(host.Renderer.TextSearchMatches, Is.SameAs(matches));
            Assert.That(matches.Select(match => match.Block.Highlights.Values.Single()), Is.EqualTo(highlights));
        });
    });

    [Test]
    public Task Changed_text_reorders_and_visibility_refresh_results_but_unchanged_blocks_keep_their_memos() => OnSession(() =>
    {
        using var host = new Host("needle one\n\nneedle two");
        host.Renderer.ApplyTextSearch("needle");
        host.Refresh();
        var original = host.Renderer.TextSearchMatches;
        var first = original[0].Block;
        var second = original[1].Block;
        var panel = (Panel)host.Renderer.GetVisualChildren().Single();
        panel.Children.Move(0, 1);
        host.Refresh();
        Assert.That(host.Renderer.TextSearchMatches.Select(match => match.Block), Is.EqualTo(new[] { second, first }));

        second.IsVisible = false;
        host.Refresh();
        Assert.That(host.Renderer.TextSearchMatches.Single().Block, Is.SameAs(first));
        second.IsVisible = true;
        host.Refresh();
        Assert.That(host.Renderer.TextSearchMatches, Has.Count.EqualTo(2));

        host.Renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse("gone now\n\nneedle new"));
        host.Refresh();
        Assert.That(host.Renderer.TextSearchMatches, Has.Count.EqualTo(1));
        Assert.That(host.Renderer.TextSearchMatches[0].Block.LayoutText, Is.EqualTo("needle new"));
    });

    [Test]
    public Task Externally_removed_or_replaced_highlights_are_repaired_and_search_changes_invalidate_the_snapshot() => OnSession(() =>
    {
        using var host = new Host("one two");
        host.Renderer.ApplyTextSearch("one", "custom", priority: 3);
        host.Refresh();
        var block = host.Renderer.TextSearchMatches.Single().Block;
        block.Highlights.Remove("custom");
        host.Refresh();
        Assert.That(block.Highlights.TryGetValue("custom", out var restored), Is.True);
        Assert.That(restored!.Ranges.Single(), Is.EqualTo(new TextHighlightRange(0, 3)));

        block.Highlights.Set("custom", [new TextHighlightRange(4, 3)], priority: 9);
        host.Refresh();
        Assert.That(block.Highlights.TryGetValue("custom", out var repaired), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(repaired!.Ranges.Single(), Is.EqualTo(new TextHighlightRange(0, 3)));
            Assert.That(repaired.Priority, Is.EqualTo(3));
        });

        host.Renderer.ApplyTextSearch("two", "second", priority: 5);
        host.Refresh();
        Assert.That(block.Highlights.TryGetValue("custom", out _), Is.False);
        Assert.That(host.Renderer.TextSearchMatches.Single().Range, Is.EqualTo(new TextHighlightRange(4, 3)));
        host.Renderer.ClearTextSearch();
        host.Refresh();
        Assert.That(host.Renderer.TextSearchMatches, Is.Empty);
        Assert.That(block.Highlights.Count, Is.Zero);
    });

    [TestCase("one")]
    [TestCase("two")]
    public Task Moving_a_block_to_another_renderer_keeps_memos_and_highlight_ownership_separate(string nextQuery) => OnSession(() =>
    {
        using var first = new Host("one two");
        using var second = new Host("");
        first.Renderer.ApplyTextSearch("one");
        first.Refresh();
        var block = first.Renderer.TextSearchMatches.Single().Block;
        var oldPanel = (Panel)first.Renderer.GetVisualChildren().Single();
        var newPanel = (Panel)second.Renderer.GetVisualChildren().Single();
        var control = oldPanel.Children.Single();
        oldPanel.Children.Remove(control);
        newPanel.Children.Add(control);
        second.Renderer.ApplyTextSearch(nextQuery);
        second.Refresh();
        Assert.That(second.Renderer.TextSearchMatches.Single().Block, Is.SameAs(block));
        var expected = new TextHighlightRange("one two".IndexOf(nextQuery, StringComparison.Ordinal), 3);
        Assert.That(second.Renderer.TextSearchMatches.Single().Range, Is.EqualTo(expected));
        first.Refresh();
        Assert.That(first.Renderer.TextSearchMatches, Is.Empty);
        first.Renderer.ClearTextSearch();
        Assert.That(block.Highlights.Values.Single().Ranges.Single(), Is.EqualTo(expected),
            "retiring the old renderer's result must not remove the new owner's highlight");
    });

    private static Task OnSession(Action action) => HeadlessSession.Current.Dispatch(action, CancellationToken.None);

    private sealed class Host : IDisposable
    {
        public MarkdownRenderer Renderer { get; }
        private readonly Window _window;
        public Host(string markdown)
        {
            Renderer = new() { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(markdown)) };
            _window = new() { Width = 400, Height = 300, Content = new ScrollViewer { Content = Renderer } };
            _window.Show();
            _window.UpdateLayout();
        }
        public void Refresh()
        {
            Renderer.InvalidateRenderedTextState();
            Renderer.InvalidateMeasure();
            _window.UpdateLayout();
        }
        public void Dispose() => _window.Close();
    }
}
