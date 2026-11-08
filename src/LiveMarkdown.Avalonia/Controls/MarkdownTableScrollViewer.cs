using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// The horizontally scrolling host of a <see cref="MarkdownTableGrid"/>. A scroll viewer measures its content at
/// infinite width, which would leave the grid no width to wrap its cells into; this one hands the grid its
/// viewport width, so a table only scrolls when its columns cannot be narrowed enough to fit.
/// </summary>
public class MarkdownTableScrollViewer : ScrollViewer
{
    private readonly MarkdownTableGrid grid;

    /// <summary>
    /// Initializes a host for <paramref name="grid"/>, which must be inside <see cref="ScrollViewer.Content"/>.
    /// </summary>
    public MarkdownTableScrollViewer(MarkdownTableGrid grid)
    {
        this.grid = grid;
    }

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Everything between the viewport and the grid (the table border's chrome) comes off its width.
        var width = availableSize.Width - Padding.Left - Padding.Right;
        for (var element = grid.Parent as Layoutable; element != null && element != this; element = element.Parent as Layoutable)
        {
            width -= element.Margin.Left + element.Margin.Right;
            if (element is Decorator { Padding: var padding }) width -= padding.Left + padding.Right;
            if (element is Border { BorderThickness: var thickness }) width -= thickness.Left + thickness.Right;
        }

        grid.FitWidth = Math.Max(0, width);
        return base.MeasureOverride(availableSize);
    }
}
