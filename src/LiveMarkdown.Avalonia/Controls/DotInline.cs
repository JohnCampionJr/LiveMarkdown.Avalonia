using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// A small circle in the text flow — a status marker, say — drawn by the containing
/// <see cref="MarkdownTextBlock"/> into space this run reserves in the line.
/// </summary>
/// <remarks>
/// <para>Unlike a control in an <see cref="InlineUIContainer"/>, the dot is part of the text: it is a
/// run, so inside a <see cref="Link"/> it hit-tests as that link — the hand cursor and the click cover
/// the dot and the gap after it, not only the pixels the circle paints. It also never shifts the line
/// when its colour changes: the reserved width depends only on the font size.</para>
///
/// <para>Its text is a single WORD JOINER (U+2060), which forbids a line break on either side, so the
/// dot always stays with what follows it. The block shapes that character to exactly
/// <see cref="Size"/> + <see cref="Spacing"/> em wide. The character is left out of
/// <see cref="MarkdownTextBlock.ActualText"/> and copied selections.</para>
///
/// <para>Sizes are in em, so the dot follows the font size it inherits.</para>
/// </remarks>
public sealed class DotInline : Run
{
    /// <summary>The text every dot carries: a zero-width, non-breaking WORD JOINER.</summary>
    public const string DotText = "⁠";

    /// <summary>Defines the <see cref="Fill"/> property.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<DotInline, IBrush?>(nameof(Fill));

    /// <summary>Defines the <see cref="Stroke"/> property.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<DotInline, IBrush?>(nameof(Stroke));

    /// <summary>Defines the <see cref="StrokeThickness"/> property.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<DotInline, double>(nameof(StrokeThickness), 1);

    /// <summary>Defines the <see cref="Size"/> property.</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<DotInline, double>(nameof(Size), 0.55);

    /// <summary>Defines the <see cref="Spacing"/> property.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<DotInline, double>(nameof(Spacing), 0.2);

    /// <summary>Defines the <see cref="Elevation"/> property.</summary>
    public static readonly StyledProperty<double> ElevationProperty =
        AvaloniaProperty.Register<DotInline, double>(nameof(Elevation), 0.35);

    /// <summary>Gets or sets the brush the dot is filled with. Null draws no fill.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Gets or sets the brush of the dot's outline — a hollow ring when there is no
    /// <see cref="Fill"/>. Null draws no outline.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Gets or sets the outline's thickness, in pixels. It is drawn inside the dot's size.</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Gets or sets the dot's diameter, in em.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Gets or sets the gap reserved after the dot, before the text that follows, in em.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Gets or sets how far above the baseline the dot's centre sits, in em.</summary>
    public double Elevation
    {
        get => GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    /// <summary>Initializes a new dot.</summary>
    public DotInline()
    {
        Text = DotText;
    }

    static DotInline()
    {
        FillProperty.Changed.AddClassHandler<DotInline>(static (s, _) => s.InvalidateParentTextBlock(affectsLayout: false));
        StrokeProperty.Changed.AddClassHandler<DotInline>(static (s, _) => s.InvalidateParentTextBlock(affectsLayout: false));
        StrokeThicknessProperty.Changed.AddClassHandler<DotInline>(static (s, _) => s.InvalidateParentTextBlock(affectsLayout: false));
        ElevationProperty.Changed.AddClassHandler<DotInline>(static (s, _) => s.InvalidateParentTextBlock(affectsLayout: false));
        SizeProperty.Changed.AddClassHandler<DotInline>(static (s, _) => s.InvalidateParentTextBlock(affectsLayout: true));
        SpacingProperty.Changed.AddClassHandler<DotInline>(static (s, _) => s.InvalidateParentTextBlock(affectsLayout: true));
    }

    /// <summary>The width the dot reserves in the line, in pixels, at <paramref name="fontSize"/>.</summary>
    internal double GetAdvance(double fontSize) => Math.Round(fontSize * (Math.Max(0, Size) + Math.Max(0, Spacing)));

    /// <summary>The dot's diameter in pixels at <paramref name="fontSize"/>.</summary>
    internal double GetDiameter(double fontSize) => Math.Round(fontSize * Math.Max(0, Size));

    private void InvalidateParentTextBlock(bool affectsLayout)
    {
        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is MarkdownTextBlock textBlock)
            {
                textBlock.InvalidateInlineDecorations(affectsLayout);
                break;
            }
        }
    }
}
