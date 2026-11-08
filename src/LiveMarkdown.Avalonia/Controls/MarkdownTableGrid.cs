using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// A robust, crash-free panel designed for Markdown tables.
/// Automatically infers rows, columns, and spans from children's Grid attached properties.
/// Content dictates the width of the columns and the height of the rows, except that a table wider than the
/// space it is given narrows its widest columns and wraps their cells to fit.
/// Completely immune to IndexOutOfRangeException.
/// </summary>
public class MarkdownTableGrid : Panel
{
    private readonly Dictionary<int, double> _rowHeights = new();
    private readonly Dictionary<int, double> _colWidths = new();

    /// <summary>
    /// Defines the <see cref="ColumnSpacing"/> property.
    /// </summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<MarkdownTableGrid, double>(nameof(ColumnSpacing));

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
        AvaloniaProperty.Register<MarkdownTableGrid, double>(nameof(RowSpacing));

    /// <summary>
    /// Gets or sets the spacing between rows.
    /// </summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="MinColumnWidth"/> property.
    /// </summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<MarkdownTableGrid, double>(nameof(MinColumnWidth), 80);

    /// <summary>
    /// Gets or sets the narrowest a column is squeezed to when the table is fitted to its available width.
    /// A column whose content is narrower than this keeps its content width. When even these minimums do not
    /// fit, the table is wider than the space it was given and its host scrolls.
    /// </summary>
    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="MaxUnbreakableWidth"/> property.
    /// </summary>
    public static readonly StyledProperty<double> MaxUnbreakableWidthProperty =
        AvaloniaProperty.Register<MarkdownTableGrid, double>(nameof(MaxUnbreakableWidth), 160);

    /// <summary>
    /// Gets or sets how wide an unbreakable run (a word, an inline-code chip) may be and still always hold its
    /// column open when the table is fitted. One up to this width is never split — if such runs cannot all fit,
    /// the table comes out wider than its space and its host scrolls. A longer one (a 60-character identifier)
    /// holds its column only up to an even share of the width, and breaks past that rather than squeezing every
    /// other column down to <see cref="MinColumnWidth"/>.
    /// </summary>
    public double MaxUnbreakableWidth
    {
        get => GetValue(MaxUnbreakableWidthProperty);
        set => SetValue(MaxUnbreakableWidthProperty, value);
    }

    /// <summary>
    /// The width the table should fit into when its parent measures it at infinity — a horizontally scrolling
    /// host does, so it passes its viewport width here instead (see <see cref="MarkdownTableScrollViewer"/>).
    /// </summary>
    internal double FitWidth
    {
        get => _fitWidth;
        set
        {
            // The host measures this grid at infinity every time, so a cached measure would never see a resize.
            if (_fitWidth.Equals(value)) return;
            _fitWidth = value;
            InvalidateMeasure();
        }
    }

    private double _fitWidth = double.PositiveInfinity;

    static MarkdownTableGrid()
    {
        AffectsMeasure<MarkdownTableGrid>(ColumnSpacingProperty, RowSpacingProperty, MinColumnWidthProperty, MaxUnbreakableWidthProperty);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        _rowHeights.Clear();
        _colWidths.Clear();

        if (Children.Count == 0) return new Size();

        // Pass 1: Measure all children with infinite space to get their true Auto desired size.
        foreach (var child in Children)
        {
            child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        // Pass 2: Column widths from single-cell elements (Span == 1)
        foreach (var child in Children)
        {
            if (child == null) continue;
            if (Math.Max(1, Grid.GetColumnSpan(child)) != 1) continue;

            var col = Grid.GetColumn(child);
            var width = child.DesiredSize.Width;
            _colWidths[col] = _colWidths.TryGetValue(col, out var w) ? Math.Max(w, width) : width;
        }

        // Pass 3: Distribute extra width required by multi-column elements
        foreach (var child in Children)
        {
            if (child == null) continue;

            var col = Grid.GetColumn(child);
            var colSpan = Math.Max(1, Grid.GetColumnSpan(child));
            if (colSpan == 1) continue;

            var currentSpannedWidth = (colSpan - 1) * ColumnSpacing;
            for (var i = 0; i < colSpan; i++)
            {
                var targetCol = col + i;
#if NETSTANDARD2_0
                if (!_colWidths.ContainsKey(targetCol)) _colWidths[targetCol] = 0;
#else
                _colWidths.TryAdd(targetCol, 0);
#endif
                currentSpannedWidth += _colWidths[targetCol];
            }

            var extraWidth = child.DesiredSize.Width - currentSpannedWidth;
            if (extraWidth > 0)
            {
                // Distribute the extra required width equally across all spanned columns
                var addedWidthPerCol = extraWidth / colSpan;
                for (var i = 0; i < colSpan; i++)
                {
                    _colWidths[col + i] += addedWidthPerCol;
                }
            }
        }

        // Pass 4: Fit the columns into the available width, then let every cell that lost width wrap into it
        var fitWidth = Math.Min(availableSize.Width, FitWidth);
        var budget = fitWidth - Math.Max(0, _colWidths.Count - 1) * ColumnSpacing;
        if (!double.IsInfinity(fitWidth) && _colWidths.Values.Sum() > budget)
        {
            FitColumns(budget, MeasureMinContentWidths());

            foreach (var child in Children)
            {
                if (child == null) continue;

                var width = SpannedWidth(Grid.GetColumn(child), Math.Max(1, Grid.GetColumnSpan(child)));
                if (width < child.DesiredSize.Width)
                {
                    child.Measure(new Size(width, double.PositiveInfinity));
                }
            }
        }

        // Pass 5: Row heights from single-row elements, at their final widths
        foreach (var child in Children)
        {
            if (child == null) continue;
            if (Math.Max(1, Grid.GetRowSpan(child)) != 1) continue;

            var row = Grid.GetRow(child);
            var height = child.DesiredSize.Height;
            _rowHeights[row] = _rowHeights.TryGetValue(row, out var h) ? Math.Max(h, height) : height;
        }

        // Pass 6: Distribute extra height required by multi-row elements
        foreach (var child in Children)
        {
            if (child == null) continue;

            var row = Grid.GetRow(child);
            var rowSpan = Math.Max(1, Grid.GetRowSpan(child));
            if (rowSpan == 1) continue;

            var currentSpannedHeight = (rowSpan - 1) * RowSpacing;
            for (var i = 0; i < rowSpan; i++)
            {
                var targetRow = row + i;
#if NETSTANDARD2_0
                if (!_rowHeights.ContainsKey(targetRow)) _rowHeights[targetRow] = 0;
#else
                _rowHeights.TryAdd(targetRow, 0);
#endif
                currentSpannedHeight += _rowHeights[targetRow];
            }

            var extraHeight = child.DesiredSize.Height - currentSpannedHeight;
            if (extraHeight > 0)
            {
                // Distribute the extra required height equally across all spanned rows
                var addedHeightPerRow = extraHeight / rowSpan;
                for (var i = 0; i < rowSpan; i++)
                {
                    _rowHeights[row + i] += addedHeightPerRow;
                }
            }
        }

        // The total size of the table is the sum of all columns and rows plus inter-cell spacing
        var totalWidth = _colWidths.Values.Sum() + Math.Max(0, _colWidths.Count - 1) * ColumnSpacing;
        var totalHeight = _rowHeights.Values.Sum() + Math.Max(0, _rowHeights.Count - 1) * RowSpacing;

        return new Size(totalWidth, totalHeight);
    }

    /// <summary>
    /// Each column's min-content width: the narrowest its single-column cells can go without breaking a word.
    /// A cell's is its widest text block's longest word (<see cref="MarkdownTextBlock.MeasureLongestWord"/>) plus
    /// whatever the cell puts around that block — its padding and margins, read off the natural measure as the
    /// difference between the cell's width and the block's. Never more than the cell's natural width.
    /// </summary>
    /// <remarks>
    /// Read from the text rather than by measuring the cell narrow: a control's desired size is clamped to the
    /// constraint it is measured at, so a cell measured at zero width reports zero whatever its words.
    /// </remarks>
    private Dictionary<int, double> MeasureMinContentWidths()
    {
        var minContent = new Dictionary<int, double>();
        foreach (var child in Children)
        {
            if (child == null || Math.Max(1, Grid.GetColumnSpan(child)) != 1) continue;

            var cellWidth = child.DesiredSize.Width;
            var cellMin = 0.0;
            foreach (var block in child.GetVisualDescendants().OfType<MarkdownTextBlock>())
            {
                var chrome = Math.Max(0, cellWidth - block.DesiredSize.Width);
                cellMin = Math.Max(cellMin, Math.Min(cellWidth, block.MeasureLongestWord() + chrome));
            }

            var col = Grid.GetColumn(child);
            minContent[col] = minContent.TryGetValue(col, out var w) ? Math.Max(w, cellMin) : cellMin;
        }

        return minContent;
    }

    /// <summary>
    /// Narrows <see cref="_colWidths"/> to fit <paramref name="budget"/>, the way a browser lays out an auto table:
    /// a column that fits within an even share keeps its content width, and the rest split what is left in
    /// proportion to their content widths — no narrower than <see cref="MinColumnWidth"/>, and no narrower than
    /// the column's longest word (<paramref name="minContent"/>), up to the larger of
    /// <see cref="MaxUnbreakableWidth"/> and an even share of the budget. So a short unbreakable token (a hex
    /// colour, a header word) is never split, while a long one (a 60-character identifier) breaks rather than
    /// holding its column open and squeezing every other column to <see cref="MinColumnWidth"/>. When the floors
    /// do not fit, the table comes out wider than the budget and its host scrolls.
    /// </summary>
    private void FitColumns(double budget, IReadOnlyDictionary<int, double> minContent)
    {
        var remaining = Math.Max(0, budget);
        var open = _colWidths.Keys.ToList();
        var wordCap = Math.Max(MaxUnbreakableWidth, remaining / Math.Max(1, open.Count));
        while (open.Count > 0)
        {
            var share = remaining / open.Count;
            var settled = open.Where(c => _colWidths[c] <= share).ToList();
            if (settled.Count == 0) break;

            foreach (var col in settled)
            {
                remaining -= _colWidths[col];
                open.Remove(col);
            }
        }

        double Floor(int col) =>
            Math.Min(_colWidths[col], Math.Max(MinColumnWidth, Math.Min(wordCap, minContent.TryGetValue(col, out var w) ? w : 0)));

        // A column whose proportional share would fall below its floor takes the floor instead, and the others
        // split what that leaves. Pinning one can push another under, so repeat until nothing more pins.
        while (open.Count > 0)
        {
            var openNatural = open.Sum(c => _colWidths[c]);
            if (openNatural <= 0) break;

            var pinned = open.Where(c => remaining * _colWidths[c] / openNatural < Floor(c)).ToList();
            if (pinned.Count == 0)
            {
                foreach (var col in open) _colWidths[col] = remaining * _colWidths[col] / openNatural;
                break;
            }

            foreach (var col in pinned)
            {
                var floor = Floor(col);
                remaining -= floor;
                _colWidths[col] = floor;
                open.Remove(col);
            }
        }
    }

    private double SpannedWidth(int col, int colSpan)
    {
        var width = (colSpan - 1) * ColumnSpacing;
        for (var i = 0;i < colSpan; i++)
        {
            if (_colWidths.TryGetValue(col + i, out var w)) width += w;
        }

        return width;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        // Sort keys to ensure proper spatial layout even if indices are non-sequential
        var sortedRows = _rowHeights.Keys.ToList();
        sortedRows.Sort();

        var sortedCols = _colWidths.Keys.ToList();
        sortedCols.Sort();

        // Pre-calculate X offsets
        var colOffsets = new Dictionary<int, double>();
        double currentX = 0;
        foreach (var col in sortedCols)
        {
            colOffsets[col] = currentX;
            currentX += _colWidths[col] + ColumnSpacing;
        }

        // Pre-calculate Y offsets
        var rowOffsets = new Dictionary<int, double>();
        double currentY = 0;
        foreach (var row in sortedRows)
        {
            rowOffsets[row] = currentY;
            currentY += _rowHeights[row] + RowSpacing;
        }

        // Arrange each child based on its offset and total spanned size
        foreach (var child in Children)
        {
            if (child == null) continue;

            var row = Grid.GetRow(child);
            var col = Grid.GetColumn(child);
            var rowSpan = Math.Max(1, Grid.GetRowSpan(child));
            var colSpan = Math.Max(1, Grid.GetColumnSpan(child));

            // Determine starting coordinates (default to 0 if something is completely missing)
#if NETSTANDARD2_0
            var x = colOffsets.TryGetValue(col, out var startX) ? startX : 0;
            var y = rowOffsets.TryGetValue(row, out var startY) ? startY : 0;
#else
            var x = colOffsets.GetValueOrDefault(col, 0);
            var y = rowOffsets.GetValueOrDefault(row, 0);
#endif

            // Calculate total width across spanned columns
            var w = (colSpan - 1) * ColumnSpacing;
            for (var i = 0; i < colSpan; i++)
            {
                if (_colWidths.TryGetValue(col + i, out var cw)) w += cw;
            }

            // Calculate total height across spanned rows
            var h = (rowSpan - 1) * RowSpacing;
            for (var i = 0; i < rowSpan; i++)
            {
                if (_rowHeights.TryGetValue(row + i, out var rh)) h += rh;
            }

            // Arrange the child within its calculated bounding box
            child.Arrange(new Rect(x, y, w, h));
        }

        return finalSize;
    }
}