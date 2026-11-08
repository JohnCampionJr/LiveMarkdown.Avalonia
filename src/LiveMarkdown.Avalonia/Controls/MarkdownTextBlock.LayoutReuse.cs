using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace LiveMarkdown.Avalonia;

public partial class MarkdownTextBlock
{
    // Avalonia owns the current layout. Transfer it out before its unconditional arrange
    // disposal, and keep at most one spare across the transient old/new-width arrange passes.
    // Glyph runs are reference-counted by Avalonia; each retained layout is disposed once.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_textLayout")]
    private extern static ref TextLayout? TextLayoutOf(TextBlock block);

    private DeferredLayoutDisposal? spareLayout;
    private Size spareLayoutConstraint;
    private TextLayout? arrangeLayout;
    private Size arrangeLayoutConstraint;
    private Size activeLayoutConstraint;
    internal int RetainedLayoutCount => spareLayout?.Layout is null ? 0 : 1;
    internal static int TextLayoutsReused;

    private static bool FitsLayout(TextLayout layout, Size original, Size requested) =>
        original == requested || original.Width.Equals(requested.Width) &&
        double.IsPositiveInfinity(original.Height) && requested.Height >= layout.Height;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Embedded/custom drawable runs can carry control ownership and arrangement state.
        // Keep Avalonia's normal lifetime for those rather than queuing their layout for reuse.
        if (_textRuns is { } runs && runs.Any(static run => run is DrawableTextRun and not ShapedTextRun))
        {
            ReleaseArrangeLayouts();
            return base.ArrangeOverride(finalSize);
        }
        var padding = Padding;
        if (UseLayoutRounding) padding = LayoutHelper.RoundLayoutThickness(padding, LayoutHelper.GetLayoutScale(this));
        var requested = finalSize.Deflate(padding);
        ref var current = ref TextLayoutOf(this);
        var previous = current;
        current = null; // ownership is transferred below, before base arrange can dispose it
        if (previous is not null && FitsLayout(previous, activeLayoutConstraint, requested))
        {
            arrangeLayout = previous;
            arrangeLayoutConstraint = activeLayoutConstraint;
        }
        else
        {
            if (spareLayout?.Layout is { } spare && FitsLayout(spare, spareLayoutConstraint, requested))
            {
                arrangeLayout = spareLayout.Take();
                arrangeLayoutConstraint = spareLayoutConstraint;
                spareLayout = null;
            }
            if (previous is not null)
            {
                ReleaseSpareLayout();
                spareLayout = new DeferredLayoutDisposal(previous);
                spareLayoutConstraint = activeLayoutConstraint;
                // The queued callback owns only a disposal ticket, never the control/view.
                // A promoted layout is removed from its ticket before this callback runs.
                Dispatcher.UIThread.Post(spareLayout.Dispose, DispatcherPriority.ContextIdle);
            }
        }
        try { return base.ArrangeOverride(finalSize); }
        finally
        {
            // Includes a failed/reentrant arrange that never consumed the candidate.
            arrangeLayout?.Dispose();
            arrangeLayout = null;
        }
    }

    private TextLayout? TakeArrangeLayout(Size requested)
    {
        var candidate = arrangeLayout;
        arrangeLayout = null;
        if (candidate is null) return null;
        if (FitsLayout(candidate, arrangeLayoutConstraint, requested))
        {
            TextLayoutsReused++;
            return candidate;
        }
        candidate.Dispose();
        return null;
    }

    private void ReleaseSpareLayout()
    {
        spareLayout?.Dispose();
        spareLayout = null;
    }

    private sealed class DeferredLayoutDisposal(TextLayout layout) : IDisposable
    {
        internal TextLayout? Layout { get; private set; } = layout;
        internal TextLayout? Take() { var value = Layout; Layout = null; return value; }
        public void Dispose() => Take()?.Dispose();
    }

    private void ReleaseArrangeLayouts()
    {
        ReleaseSpareLayout();
        arrangeLayout?.Dispose();
        arrangeLayout = null;
    }
}
