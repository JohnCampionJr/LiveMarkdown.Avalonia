using Avalonia.Controls;
using Avalonia.VisualTree;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// FORK: keeps an inline chip's <see cref="TextBlock.BaselineOffsetProperty"/> in
/// sync with the real baseline of the text inside it.
///
/// <para>An <see cref="Avalonia.Controls.Documents.InlineUIContainer"/> becomes an <c>EmbeddedControlRun</c>
/// whose <c>Baseline</c> defaults to the control's FULL HEIGHT — i.e. "my baseline is my bottom edge". The host
/// line's baseline is the max ascent of its runs, so one chip drags the whole line's baseline down to the chip's
/// bottom: the surrounding prose drops by several pixels, the chip's own text rides high, and with a fixed
/// LineHeight the line BOX doesn't grow — the dropped glyphs render below the box, outside the selection
/// highlight (the "selected text sits under its highlight" glitch).</para>
///
/// <para>Avalonia's escape hatch is the <c>TextBlock.BaselineOffset</c> attached property, which the run reads
/// live. The correct value depends on the chip's font metrics, so it cannot be a static style setter — this
/// helper recomputes it after each layout of the chip and, when it changes, tells the hosting block to reshape
/// (its layout cache would otherwise resurrect lines shaped under the stale baseline).</para>
/// </summary>
public static class ChipBaseline
{
    /// <summary>Wire a chip (a Border wrapping a text block) to report its inner text baseline.</summary>
    public static void Track(Border chip, MarkdownTextBlock inner)
    {
        inner.LayoutUpdated += (_, _) => Sync(chip, inner);
    }

    private static void Sync(Border chip, MarkdownTextBlock inner)
    {
        if (inner.TextLayout is not { TextLines.Count: > 0 } layout) return;

        var baseline = chip.Padding.Top + chip.BorderThickness.Top + inner.Margin.Top
                       + layout.TextLines[0].Baseline;

        if (Math.Abs(chip.GetValue(TextBlock.BaselineOffsetProperty) - baseline) < 0.05) return;

        chip.SetValue(TextBlock.BaselineOffsetProperty, baseline);
        // The hosting block has already shaped its lines against the old baseline, so it has to re-measure.
        //
        // This used to call a fork-only InvalidateEmbeddedBaseline(), which existed solely to defeat the fork's
        // own per-font-size layout cache — a plain InvalidateMeasure would have been handed the stale lines
        // straight back. That cache is gone as of the v2.3.2 merge (upstream's OnMeasureInvalidated nulls
        // _layoutText itself), so the ordinary invalidation is now sufficient AND correct.
        chip.FindAncestorOfType<MarkdownTextBlock>()?.InvalidateMeasure();
    }
}
