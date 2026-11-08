using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class TableMeasureCacheTests
{
    private const string LongText = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega";

    [Test]
    public Task Wide_tables_skip_min_content_layout_and_width_changes_reuse_it_when_needed() => Run(() =>
    {
        using var fixture = new Fixture(width: 4000);
        Assert.That(fixture.Blocks.Select(block => block.LongestWordMeasureCount), Is.All.EqualTo(0),
            "a naturally fitting table needs no extra unwrapped layouts for word floors");
        fixture.Resize(320);
        var wordMeasures = fixture.Blocks.Select(block => block.LongestWordMeasureCount).ToArray();
        Assert.That(wordMeasures, Is.All.EqualTo(1));
        fixture.Resize(4000);
        fixture.Resize(320);
        Assert.That(fixture.Blocks.Select(block => block.LongestWordMeasureCount), Is.EqualTo(wordMeasures));
    });

    [Test]
    public Task Growing_one_cell_does_not_remeasure_stable_markdown_cells() => Run(() =>
    {
        using var fixture = new Fixture();
        foreach (var block in fixture.Blocks) block.Measures = 0;
        fixture.Blocks[0].Height = fixture.Blocks[0].Bounds.Height + 30;
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Blocks[0].Measures, Is.GreaterThan(0));
        Assert.That(fixture.Blocks.Skip(1).Select(block => block.Measures), Is.All.EqualTo(0),
            "unchanged cells should keep the same fitted constraint and hit Avalonia's cached measure");
    });

    [Test]
    public Task Changing_viewport_width_rewraps_without_reshaping_natural_measurements() => Run(() =>
    {
        using var fixture = new Fixture();
        var heights = fixture.Blocks.Select(block => block.Bounds.Height).ToArray();
        foreach (var block in fixture.Blocks) block.Measures = 0;
        fixture.Resize(900);
        Assert.That(fixture.Grid.Bounds.Width, Is.GreaterThan(320), "the platform resize must have reached arrange");
        Assert.That(fixture.Blocks.Select(block => block.Measures), Is.All.EqualTo(1),
            "a resize requests its new final constraint once, not an infinite/fitted pair");
        Assert.That(fixture.Blocks[0].Bounds.Height, Is.LessThan(heights[0]));
        fixture.Resize(320);
        Assert.That(fixture.Blocks.Select(block => block.Bounds.Height), Is.EqualTo(heights));
    });

    [Test]
    public Task Content_typography_and_chrome_changes_refresh_intrinsic_metrics() => Run(() =>
    {
        using var fixture = new Fixture(width: 4000, text: "small");
        var initial = fixture.Cells[0].Bounds.Width;
        fixture.Blocks[0].Text = "a much longer natural cell width";
        fixture.Window.UpdateLayout();
        var textWidth = fixture.Cells[0].Bounds.Width;
        Assert.That(textWidth, Is.GreaterThan(initial));

        fixture.Blocks[0].FontSize *= 2;
        fixture.Window.UpdateLayout();
        var fontWidth = fixture.Cells[0].Bounds.Width;
        Assert.That(fontWidth, Is.GreaterThan(textWidth));

        fixture.Cells[0].Padding = new Thickness(30, 0);
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Cells[0].Bounds.Width, Is.EqualTo(fontWidth + 60).Within(1));
        fixture.Cells[0].Padding = default;
        fixture.Cells[0].BorderThickness = new Thickness(4);
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Cells[0].Bounds.Width, Is.EqualTo(fontWidth + 8).Within(1));
    });

    [Test]
    public Task Nested_children_and_replaced_content_refresh_metrics_and_stop_observing_old_controls() => Run(() =>
    {
        using var fixture = new Fixture(width: 4000, text: "small");
        var old = fixture.Blocks[0];
        fixture.Cells[0].Child = null;
        var panel = new StackPanel { Children = { old } };
        fixture.Cells[0].Child = panel;
        fixture.Window.UpdateLayout();
        var initial = fixture.Cells[0].Bounds.Width;
        var added = new MarkdownTextBlock { Text = LongText, FontSize = old.FontSize };
        panel.Children.Add(added);
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Cells[0].Bounds.Width, Is.GreaterThan(initial));

        panel.Children.Remove(added);
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Cells[0].Bounds.Width, Is.EqualTo(initial));
        added.Text = "discarded text cannot invalidate its former table";
        Assert.That(fixture.Grid.IsMeasureValid, Is.True);

        fixture.Grid.Children.Remove(fixture.Cells[0]);
        fixture.Window.UpdateLayout();
        old.Text = "neither can a removed cell";
        Assert.That(fixture.Grid.IsMeasureValid, Is.True);
        fixture.Grid.Children.Clear();
        fixture.Window.UpdateLayout();
        fixture.Blocks[1].Text = "nor cells discarded by a collection reset";
        Assert.That(fixture.Grid.IsMeasureValid, Is.True);
    });

    [Test]
    public Task Detaching_releases_observers_and_reattaching_refreshes_and_observes_the_current_content() => Run(() =>
    {
        using var fixture = new Fixture(text: "small");
        var oldWidth = fixture.Cells[0].Bounds.Width;
        Assert.That(fixture.Grid.CachedCellMeasureCount, Is.EqualTo(fixture.Cells.Length));
        fixture.Window.Content = null;
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Grid.CachedCellMeasureCount, Is.Zero);
        fixture.Blocks[0].Text = LongText;
        Assert.That(fixture.Grid.CachedCellMeasureCount, Is.Zero, "off-tree changes must not recreate cache subscriptions");
        fixture.Window.Content = fixture.Grid;
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Grid.CachedCellMeasureCount, Is.EqualTo(fixture.Cells.Length));
        Assert.That(fixture.Cells[0].Bounds.Width, Is.GreaterThan(oldWidth), "reattachment measures the changed content");
        fixture.Blocks[0].FontSize += 1;
        Assert.That(fixture.Grid.IsMeasureValid, Is.False, "reattachment restores intrinsic-metric observation");
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Grid.IsMeasureValid, Is.True);
    });

    [Test]
    public Task Disposing_a_cell_cache_unsubscribes_its_measure_invalidation_callback() => Run(() =>
    {
        // This owner is deliberately empty and the text is unparented. A text change cannot
        // invalidate the owner through Avalonia's normal parent/child layout notifications.
        var owner = new MarkdownTableGrid();
        var block = new MarkdownTextBlock { Text = "original", FontSize = 14 };
        using var cache = new TableCellMeasureCache(owner, block);
        cache.MeasureNatural();
        owner.Measure(Size.Infinity);
        block.Text = "changed while observed";
        Assert.That(owner.IsMeasureValid, Is.False, "the live cache callback must actually be connected");
        cache.MeasureNatural();
        owner.Measure(Size.Infinity);
        Assert.That(block.IsMeasureValid, Is.True);
        cache.Dispose();
        block.Text = "changed after disposal";
        Assert.That(owner.IsMeasureValid, Is.True, "disposing the cache removes the independent callback");
        Assert.That(owner.CachedCellMeasureCount, Is.Zero);
    });

    [Test]
    public Task Visibility_and_column_span_changes_keep_layout_current() => Run(() =>
    {
        using var fixture = new Fixture(width: 4000, text: "small");
        var initial = fixture.Grid.DesiredSize;
        fixture.Cells[1].IsVisible = false;
        fixture.Cells[3].IsVisible = false;
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Grid.DesiredSize.Width, Is.LessThan(initial.Width));
        fixture.Cells[1].IsVisible = true;
        fixture.Cells[3].IsVisible = true;
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Grid.DesiredSize, Is.EqualTo(initial));
        Grid.SetColumnSpan(fixture.Cells[0], 2);
        fixture.Window.UpdateLayout();
        Assert.That(fixture.Cells[0].Bounds.Width, Is.GreaterThan(fixture.Cells[2].Bounds.Width));
    });

    [Test]
    public Task Custom_cells_without_observable_intrinsic_inputs_keep_the_conservative_path() => Run(() =>
    {
        var first = new PrivateStateCell();
        var second = new PrivateStateCell();
        Grid.SetColumn(second, 1);
        var grid = new MarkdownTableGrid { Children = { first, second } };
        var window = new Window { Width = 2000, Height = 200, Content = grid };
        try
        {
            window.Show();
            window.UpdateLayout();
            var before = grid.DesiredSize.Width;
            first.ContentWidth = 700;
            first.InvalidateMeasure();
            grid.InvalidateMeasure();
            window.UpdateLayout();
            Assert.That(grid.DesiredSize.Width, Is.GreaterThan(before));
        }
        finally { window.Close(); }
    });

    private static Task Run(Action body) => HeadlessSession.Current.Dispatch(body, CancellationToken.None);

    private sealed class CountedMarkdown : MarkdownTextBlock
    {
        public int Measures;
        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    private sealed class PrivateStateCell : Control
    {
        public double ContentWidth = 200;
        protected override Size MeasureOverride(Size availableSize) => new(Math.Min(ContentWidth, availableSize.Width), 30);
    }

    private sealed class Fixture : IDisposable
    {
        public Window Window { get; }
        public MarkdownTableGrid Grid { get; } = new();
        public CountedMarkdown[] Blocks { get; } = new CountedMarkdown[4];
        public Border[] Cells { get; } = new Border[4];
        public Fixture(double width = 320, string text = LongText)
        {
            for (var i = 0; i < Blocks.Length; i++)
            {
                Blocks[i] = new() { Text = text, FontSize = 14, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
                Cells[i] = new() { Child = Blocks[i] };
                global::Avalonia.Controls.Grid.SetColumn(Cells[i], i % 2);
                global::Avalonia.Controls.Grid.SetRow(Cells[i], i / 2);
                Grid.Children.Add(Cells[i]);
            }
            Window = new() { Width = width, Height = 1000, Content = Grid };
            Window.Show();
            Window.UpdateLayout();
        }
        public void Dispose() => Window.Close();
        public void Resize(double width)
        {
            Window.Width = width;
            // The headless platform delivers the native client-size change through the dispatcher.
            // Measuring before that notification can use the new Width with the old arrange bounds.
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }
    }
}
