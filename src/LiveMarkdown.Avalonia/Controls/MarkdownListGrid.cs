using Avalonia;
using System.Collections.Specialized;
using Avalonia.Controls;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// A highly robust, crash-free panel designed specifically for two-column Markdown lists.
/// Column 0 (Index/Bullet) is Auto-sized.
/// Column 1 (Content) fills the remaining width.
/// Unlimited rows, automatically inferred safely from Grid.Row properties.
/// </summary>
public class MarkdownListGrid : Panel
{
    private readonly Dictionary<int, double> _rowHeights = new();
    private readonly Dictionary<int, double> _rowOffsets = new();
    private readonly List<int> _sortedRows = [];
    private readonly HashSet<Control> _observedChildren = [];
    internal int RowOffsetBuilds { get; private set; }
    private double _col0Width;

    /// <summary>
    /// Defines the <see cref="ColumnSpacing"/> property.
    /// </summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<MarkdownListGrid, double>(nameof(ColumnSpacing));

    /// <summary>
    /// Gets or sets the spacing between columns.
    /// </summary>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="RowSpacing"/> property.
    /// </summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<MarkdownListGrid, double>(nameof(RowSpacing));

    /// <summary>
    /// Gets or sets the spacing between rows.
    /// </summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    static MarkdownListGrid()
    {
        AffectsMeasure<MarkdownListGrid>(ColumnSpacingProperty, RowSpacingProperty);
    }

    /// <inheritdoc/>
    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (Control child in e.OldItems)
                if (_observedChildren.Remove(child)) child.PropertyChanged -= HandleChildPlacementChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var child in _observedChildren.ToArray())
                if (!Children.Contains(child))
                {
                    _observedChildren.Remove(child);
                    child.PropertyChanged -= HandleChildPlacementChanged;
                }
        if (e.NewItems is not null)
            foreach (Control child in e.NewItems)
                if (_observedChildren.Add(child)) child.PropertyChanged += HandleChildPlacementChanged;
        base.ChildrenChanged(sender, e);
    }

    private void HandleChildPlacementChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Grid.RowProperty || args.Property == Grid.ColumnProperty) InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        _rowHeights.Clear();
        _rowOffsets.Clear();
        _sortedRows.Clear();
        _col0Width = 0;

        // Children is a get only property, so we don't need to cache it in local variable
        if (Children.Count == 0) return new Size();

        // Pass 1: Measure all Column 0 children to determine the maximum Auto width.
        // We treat any Grid.Column <= 0 as the first column.
        foreach (var child in Children)
        {
            if (child == null) continue;

            var col = Grid.GetColumn(child);
            if (col > 0) continue;

            // Measure with infinite space to get true Auto desired size
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _col0Width = Math.Max(_col0Width, child.DesiredSize.Width);
        }

        // Calculate remaining width for Column 1
        var col1AvailableWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - _col0Width - ColumnSpacing);

        double totalCol1Width = 0;

        // Pass 2: Measure Column 1 children and calculate maximum row heights.
        foreach (var child in Children)
        {
            if (child == null) continue;

            var row = Grid.GetRow(child);
            var col = Grid.GetColumn(child);

            if (col > 0)
            {
                // Column 1 gets the remaining width
                child.Measure(new Size(col1AvailableWidth, double.PositiveInfinity));
                totalCol1Width = Math.Max(totalCol1Width, child.DesiredSize.Width);
            }

            // Update max height for this specific row using Dictionary to avoid IndexOutOfRange
            var childHeight = child.DesiredSize.Height;
            if (_rowHeights.TryGetValue(row, out var currentMaxHeight))
            {
                _rowHeights[row] = Math.Max(currentMaxHeight, childHeight);
            }
            else
            {
                _rowHeights[row] = childHeight;
            }
        }

        var totalHeight = _rowHeights.Values.Sum() + Math.Max(0, _rowHeights.Count - 1) * RowSpacing;
        var totalWidth = _col0Width + totalCol1Width + (totalCol1Width > 0 ? ColumnSpacing : 0);

        // Row heights and spacing are measurement inputs. Rebuild ordering/offsets here once,
        // then width-only arrange passes can reuse them without sorting or allocating maps.
        _sortedRows.AddRange(_rowHeights.Keys);
        _sortedRows.Sort();
        var currentY = 0d;
        foreach (var row in _sortedRows)
        {
            _rowOffsets[row] = currentY;
            currentY += _rowHeights[row] + RowSpacing;
        }
        RowOffsetBuilds++;

        // Return the total requested size, expanding to available width if possible.
        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : availableSize.Width,
            totalHeight);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        var col1Width = Math.Max(0, finalSize.Width - _col0Width - ColumnSpacing);

        // Arrange each child in its safe bounding box
        foreach (var child in Children)
        {
            if (child == null) continue;

            var row = Grid.GetRow(child);
            var col = Grid.GetColumn(child);

            // Failsafe checks
            if (!_rowOffsets.TryGetValue(row, out var yOffset)) continue;
            if (!_rowHeights.TryGetValue(row, out var height)) continue;

            var xOffset = col <= 0 ? 0 : _col0Width + ColumnSpacing;
            var width = col <= 0 ? _col0Width : col1Width;

            child.Arrange(new Rect(xOffset, yOffset, width, height));
        }

        return finalSize;
    }
}
