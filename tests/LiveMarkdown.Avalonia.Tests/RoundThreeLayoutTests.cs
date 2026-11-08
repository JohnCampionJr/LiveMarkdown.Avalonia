using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Syntax;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture, NonParallelizable]
public class RoundThreeLayoutTests
{
    [Test]
    public Task Unchanged_projection_refreshes_preserve_identity_and_do_not_notify_or_rebuild() => Run(() =>
    {
        using var host = new Host("first\n\nsecond");
        var projection = host.Renderer.RenderedTextProjection;
        var count = host.Renderer.RenderedTextProjectionBuilds;
        var changes = 0;
        host.Renderer.PropertyChanged += (_, e) => { if (e.Property == MarkdownRenderer.RenderedTextProjectionProperty) changes++; };
        for (var i = 0; i < 8; i++) host.Refresh();
        Assert.That(host.Renderer.RenderedTextProjection, Is.SameAs(projection));
        Assert.That(host.Renderer.RenderedTextProjectionBuilds, Is.EqualTo(count));
        Assert.That(changes, Is.Zero);
    });

    [Test]
    public Task Projection_refreshes_for_text_spans_order_visibility_and_same_version_replacement() => Run(() =>
    {
        using var host = new Host("first\n\nsecond");
        var before = host.Renderer.RenderedTextProjection!;
        var panel = (Panel)host.Renderer.GetVisualChildren().Single();
        var blocks = host.Renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray();
        blocks[0].SourceSpan = new SourceSpan(10, 20);
        host.Refresh();
        Assert.That(host.Renderer.RenderedTextProjection, Is.Not.SameAs(before));
        Assert.That(host.Renderer.RenderedTextProjection!.Buffers[0].SourceSpan, Is.EqualTo(new SourceSpan(10, 20)));
        panel.Children.Move(0, 1);
        host.Refresh();
        Assert.That(Text(host)[0], Is.EqualTo("second"));
        blocks[1].IsVisible = false;
        host.Refresh();
        Assert.That(Text(host), Is.EqualTo(new[] { "first" }));
        blocks[1].IsVisible = true;
        host.Refresh();
        Assert.That(Text(host), Has.Length.EqualTo(2));
        host.Renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse("replacement", MarkdownUpdateProducer.DefaultPipeline));
        host.Refresh();
        Assert.That(Text(host), Is.EqualTo(new[] { "replacement" }));
    });

    [Test]
    public Task Projection_repairs_externally_mutated_buffers_and_tracks_embedded_text_independently() => Run(() =>
    {
        using var host = new Host("");
        var panel = (Panel)host.Renderer.GetVisualChildren().Single();
        var child = new MarkdownTextBlock { Text = "child" };
        var parent = new MarkdownTextBlock { Inlines = new InlineCollection
            { new Run("before"), new InlineUIContainer { Child = child }, new Run("after") } };
        panel.Children.Add(parent);
        host.Refresh();
        Assert.That(Text(host), Is.EqualTo(new[] { "before\ufffcafter", "child" }));
        var original = host.Renderer.RenderedTextProjection!;
        ((MarkdownTextBuffer[])original.Buffers)[0] = default;
        host.Refresh();
        Assert.That(Text(host)[0], Is.EqualTo("before\ufffcafter"));
        var repaired = host.Renderer.RenderedTextProjection!;
        child.Text = "changed child";
        host.Refresh();
        Assert.That(host.Renderer.RenderedTextProjection, Is.Not.SameAs(repaired));
        Assert.That(Text(host), Is.EqualTo(new[] { "before\ufffcafter", "changed child" }));
    });

    [Test]
    public Task New_source_versions_produce_new_projections_even_when_text_is_equal() => Run(() =>
    {
        using var host = new Host("same");
        var first = host.Renderer.RenderedTextProjection;
        host.Renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse("same", MarkdownUpdateProducer.DefaultPipeline), version: 9);
        host.Refresh();
        Assert.That(host.Renderer.RenderedTextProjection, Is.Not.SameAs(first));
        Assert.That(host.Renderer.RenderedTextProjection!.SourceVersion, Is.EqualTo(9));
        host.Renderer.DocumentUpdate = null;
        Assert.That(host.Renderer.RenderedTextProjection, Is.Null);
    });

    [Test]
    public Task Width_only_list_arrange_reuses_row_offsets_and_preserves_sparse_order() => Run(() =>
    {
        var panel = new MarkdownListGrid { RowSpacing = 3 };
        var last = Add(panel, 900, 1, 20);
        var first = Add(panel, 10, 1, 10);
        Add(panel, 10, 0, 8);
        Layout(panel);
        var builds = panel.RowOffsetBuilds;
        for (var i = 0; i < 8; i++) panel.Arrange(new Rect(0, 0, i % 2 == 0 ? 401 : 400, 1000));
        Assert.That(panel.RowOffsetBuilds, Is.EqualTo(builds));
        Assert.That(first.Bounds.Y, Is.Zero);
        Assert.That(last.Bounds.Y, Is.EqualTo(13));
    });

    [Test]
    public Task Row_column_spacing_and_child_removal_invalidate_list_measurement() => Run(() =>
    {
        var panel = new MarkdownListGrid { RowSpacing = 3, ColumnSpacing = 2 };
        var first = Add(panel, 10, 0, 10);
        var second = Add(panel, 20, 1, 20);
        Layout(panel);
        Grid.SetRow(second, 5);
        Assert.That(panel.IsMeasureValid, Is.False);
        Layout(panel);
        Assert.That(second.Bounds.Y, Is.Zero);
        Assert.That(first.Bounds.Y, Is.EqualTo(23));
        Grid.SetColumn(first, 1);
        Assert.That(panel.IsMeasureValid, Is.False);
        panel.RowSpacing = 7;
        panel.ColumnSpacing = 9;
        Layout(panel);
        Assert.That(first.Bounds.Y, Is.EqualTo(27));
        panel.Children.Clear();
        Layout(panel);
        Grid.SetRow(first, 100);
        Assert.That(panel.IsMeasureValid, Is.True, "removed children must not keep placement handlers");
        var replacement = Add(panel, 1000, 1, 15);
        Layout(panel);
        Assert.That(replacement.Bounds.Y, Is.Zero);
    });

    private static string[] Text(Host host) => host.Renderer.RenderedTextProjection!.Buffers.Select(buffer => buffer.Text.ToString()).ToArray();
    private static Task Run(Action action) => HeadlessSession.Current.Dispatch(action, CancellationToken.None);
    private static Control Add(Panel panel, int row, int column, double height)
    {
        var child = new Cell(height);
        Grid.SetRow(child, row); Grid.SetColumn(child, column);
        panel.Children.Add(child);
        return child;
    }
    private static void Layout(MarkdownListGrid panel)
    {
        panel.Measure(new Size(400, 1000));
        panel.Arrange(new Rect(0, 0, 400, 1000));
    }
    private sealed class Cell(double height) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(10, height);
    }
    private sealed class Host : IDisposable
    {
        internal MarkdownRenderer Renderer { get; }
        internal Window Window { get; }
        internal Host(string source)
        {
            Renderer = new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(source, MarkdownUpdateProducer.DefaultPipeline)) };
            Window = new Window { Width = 500, Height = 400, Content = Renderer };
            try { Window.Show(); Window.UpdateLayout(); _ = Renderer.RenderedTextProjection; }
            catch { Window.Close(); throw; }
        }
        internal void Refresh() { Renderer.InvalidateRenderedTextState(); Renderer.InvalidateMeasure(); Window.UpdateLayout(); }
        public void Dispose() => Window.Close();
    }
}
