using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace LiveMarkdown.Avalonia;

/// <summary>Intrinsic sizes for one Markdown cell, independent of its fitted column width.
/// Arbitrary custom layouts keep the conservative measure path: their private layout inputs cannot
/// be observed reliably after Avalonia has independently remeasured them.</summary>
internal sealed class TableCellMeasureCache(MarkdownTableGrid owner, Control cell) : IDisposable
{
    private readonly List<Control> _observed = [];
    private readonly List<Panel> _panels = [];
    private readonly List<MarkdownTextBlock> _blocks = [];
    private bool _dirty = true;
    private bool _supported;
    private double? _minContentWidth;

    internal Size NaturalSize { get; private set; }
    internal Size LastMeasuredSize { get; set; }
    internal double MinContentWidth => _minContentWidth ??= MeasureMinContentWidth();

    internal void MeasureNatural()
    {
        if (_supported && !_dirty && cell.IsMeasureValid && cell.DesiredSize == LastMeasuredSize) return;

        ObserveSubtree();
        cell.Measure(Size.Infinity);
        NaturalSize = cell.DesiredSize;
        LastMeasuredSize = cell.DesiredSize;
        _minContentWidth = null;
        _dirty = false;
    }

    private double MeasureMinContentWidth()
    {
        var minimum = 0.0;
        // The custom-control fallback still uses the original visual-tree word-floor calculation.
        var blocks = cell.GetVisualDescendants().OfType<MarkdownTextBlock>();
        if (cell is MarkdownTextBlock root) blocks = blocks.Prepend(root);
        foreach (var block in blocks)
        {
            var chrome = Math.Max(0, NaturalSize.Width - block.DesiredSize.Width);
            minimum = Math.Max(minimum, Math.Min(NaturalSize.Width, block.MeasureLongestWord() + chrome));
        }
        return minimum;
    }

    private void ObserveSubtree()
    {
        Dispose();
        _supported = Observe(cell);
        foreach (var control in _observed) control.PropertyChanged += OnPropertyChanged;
        foreach (var panel in _panels) panel.Children.CollectionChanged += OnChildrenChanged;
        foreach (var block in _blocks) block.IntrinsicMeasureInvalidated += Invalidate;
    }

    private bool Observe(Control control)
    {
        _observed.Add(control);
        if (control is MarkdownTextBlock block)
        {
            _blocks.Add(block);
            // An embedded control can remeasure from private state without changing a property.
            // Keep that cell on the conservative path instead of caching an unobservable width.
            return block.Inlines is null || !HasEmbeddedControl(block.Inlines);
        }
        if (control.GetType() == typeof(Border) && control is Border border)
            return border.Child is null || Observe(border.Child);
        if (control.GetType() == typeof(StackPanel) && control is StackPanel panel)
        {
            _panels.Add(panel);
            var supported = true;
            foreach (var child in panel.Children) supported &= Observe(child);
            return supported;
        }
        return false;
    }

    private static bool HasEmbeddedControl(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer) return true;
            if (inline is Span span && HasEmbeddedControl(span.Inlines)) return true;
        }
        return false;
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        // Text/inline/typography changes arrive through the text block's real measure invalidation.
        // Chrome and attached placement properties need explicit observation, including a change
        // whose fitted DesiredSize is unchanged and would otherwise never notify the table.
        if (change.Property == Layoutable.WidthProperty || change.Property == Layoutable.HeightProperty ||
            change.Property == Layoutable.MinWidthProperty || change.Property == Layoutable.MinHeightProperty ||
            change.Property == Layoutable.MaxWidthProperty || change.Property == Layoutable.MaxHeightProperty ||
            change.Property == Layoutable.MarginProperty || change.Property == Layoutable.IsVisibleProperty ||
            change.Property == Layoutable.UseLayoutRoundingProperty ||
            change.Property == Layoutable.HorizontalAlignmentProperty || change.Property == Layoutable.VerticalAlignmentProperty ||
            change.Property == Decorator.PaddingProperty || change.Property == Decorator.ChildProperty ||
            change.Property == Border.BorderThicknessProperty || change.Property == StackPanel.SpacingProperty ||
            change.Property == StackPanel.OrientationProperty || change.Property == Grid.RowProperty ||
            change.Property == Grid.ColumnProperty || change.Property == Grid.RowSpanProperty || change.Property == Grid.ColumnSpanProperty)
            Invalidate();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs change) => Invalidate();

    private void Invalidate()
    {
        _dirty = true;
        owner.InvalidateMeasure();
    }

    public void Dispose()
    {
        foreach (var control in _observed) control.PropertyChanged -= OnPropertyChanged;
        foreach (var panel in _panels) panel.Children.CollectionChanged -= OnChildrenChanged;
        foreach (var block in _blocks) block.IntrinsicMeasureInvalidated -= Invalidate;
        _observed.Clear();
        _panels.Clear();
        _blocks.Clear();
    }
}
