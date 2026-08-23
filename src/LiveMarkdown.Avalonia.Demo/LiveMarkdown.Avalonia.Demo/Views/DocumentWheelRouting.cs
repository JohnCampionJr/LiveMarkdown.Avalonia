using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace LiveMarkdown.Avalonia.Demo.Views;

/// <summary>
/// PROTOTYPE — dominant-axis wheel routing applied ONCE at the document scroller, entirely from the host.
/// Uses no LiveMarkdown API and touches no library type: everything here is public <see cref="ScrollViewer"/>
/// and routed-event surface.
///
/// <para>The problem: a <see cref="ScrollViewer"/> that can only move horizontally still consumes a wheel
/// gesture whose delta carries any horizontal component. A trackpad two-finger scroll always carries a
/// little, so scrolling the document with the pointer over a wide table pans the table sideways and the page
/// stands still. A mouse wheel sends a pure vertical delta and never hits it.</para>
///
/// <para>The approach: attach ONE handler in the TUNNEL phase on the document scroller. Tunnel runs
/// root-to-target, so this sees every gesture before any nested scroller does. A vertical-dominant gesture is
/// the reader scrolling the document — take it here and mark it handled, so nothing nested gets the chance to
/// claim it. Anything else is left alone and reaches whatever is under the pointer, so deliberate horizontal
/// panning and shift+wheel keep working.</para>
///
/// <para>Why this beats attaching per scroller: it needs no knowledge of which controls the document happens
/// to contain, so it covers tables, code blocks, diagrams and any host-embedded scroller alike, and nothing
/// breaks if the library renames a style class or a template part.</para>
/// </summary>
public static class DocumentWheelRouting
{
    /// <summary>Pixels per unit of delta — the step ScrollViewer itself uses for one mouse notch.</summary>
    private const double StepPx = 48;

    /// <summary>A gap this long (ms) ends a gesture. Trackpad events arrive every frame or so while a
    /// gesture is live, so anything beyond this is a new one. Kept SHORT: macOS keeps sending inertial
    /// events after the fingers lift, so a long gap makes the lock outlive the gesture a person thinks
    /// they finished, and the next scroll feels stuck until they pause deliberately.</summary>
    private const ulong GestureGapMs = 80;

    /// <summary>How decisively the other axis has to win before it BREAKS an established lock. A lock that
    /// can only expire by timeout means changing direction requires a deliberate pause; a lock that breaks
    /// on any wobble is not a lock at all. This sits far above trackpad drift — drift is a fraction of the
    /// dominant axis, never several times it — so noise cannot flip it, but a real turn re-latches at once.</summary>
    private const double BreakoutRatio = 2.5;

    // ── Axis LOCK ────────────────────────────────────────────────────────────────────────────────────
    // Deciding per EVENT is not enough. A two-finger scroll is not a clean vector: individual events
    // within one gesture flip horizontal-dominant on noise, and each one that does slips through and
    // nudges the table sideways. The drift is small per event and cumulative over a read.
    //
    // So latch the axis at the START of a gesture and hold it until the gesture ends, which is what
    // platforms mean by directional locking. Mid-gesture noise cannot change the answer any more.
    private static ulong _lastTimestamp;
    private static bool _lockedVertical;

    private static bool IsVerticalGesture(PointerWheelEventArgs e)
    {
        var newGesture = e.Timestamp - _lastTimestamp > GestureGapMs || e.Timestamp < _lastTimestamp;
        _lastTimestamp = e.Timestamp;

        var x = Math.Abs(e.Delta.X);
        var y = Math.Abs(e.Delta.Y);

        if (newGesture)
        {
            _lockedVertical = y >= x;
        }
        else if (_lockedVertical ? x > y * BreakoutRatio : y > x * BreakoutRatio)
        {
            // Decisively the other way mid-gesture: the reader turned, rather than wobbled.
            _lockedVertical = !_lockedVertical;
        }

        return _lockedVertical;
    }

    /// <summary>Opt a subtree out, for something nested that legitimately wants its own vertical wheel
    /// (a scrollable popup, an embedded editor). Nothing in this demo sets it; it exists because a
    /// document-wide interception has to have an escape hatch.</summary>
    public static readonly AttachedProperty<bool> ExemptProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Exempt", typeof(DocumentWheelRouting));

    public static void SetExempt(Control element, bool value) => element.SetValue(ExemptProperty, value);
    public static bool GetExempt(Control element) => element.GetValue(ExemptProperty);

    public static void Attach(ScrollViewer document)
    {
        document.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        document.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer document) return;

        // Explicit horizontal intent belongs to whatever is under the pointer.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (e.Delta is { X: 0, Y: 0 }) return;

        // An opted-out subtree keeps its own wheel.
        for (var v = e.Source as Visual; v is not null && v != document; v = v.GetVisualParent())
        {
            if (v is Control c && GetExempt(c)) return;
        }

        // A locked gesture is OURS for its whole life, on BOTH axes. Handling only the vertical case and
        // letting horizontal gestures fall through is not symmetric: the nested scroller pans on the
        // horizontal component, the leftover vertical component reaches the document, and a sideways
        // gesture drifts the page down — the same bug as before with the axes swapped.
        if (IsVerticalGesture(e))
        {
            ScrollBy(document, -e.Delta.Y * StepPx, vertical: true);
        }
        else if (NearestHorizontal(e.Source as Visual, document) is { } pannable)
        {
            ScrollBy(pannable, -e.Delta.X * StepPx, vertical: false);
        }

        // Handled either way, including when a drift-only event carried nothing to apply, and when there
        // was nothing to pan. Inside a locked gesture, passing anything on is how the other axis creeps.
        e.Handled = true;
    }

    /// <summary>The nearest scroller between the pointer and the document that can actually move
    /// horizontally — the thing a deliberate sideways gesture is asking for.</summary>
    private static ScrollViewer? NearestHorizontal(Visual? from, ScrollViewer document)
    {
        for (var v = from; v is not null && v != document; v = v.GetVisualParent())
        {
            if (v is ScrollViewer sv && sv.Extent.Width > sv.Viewport.Width + 0.5) return sv;
        }
        return null;
    }

    private static void ScrollBy(ScrollViewer target, double delta, bool vertical)
    {
        var max = vertical
            ? Math.Max(0, target.Extent.Height - target.Viewport.Height)
            : Math.Max(0, target.Extent.Width - target.Viewport.Width);
        if (max <= 0) return;

        target.Offset = vertical
            ? new Vector(target.Offset.X, Math.Clamp(target.Offset.Y + delta, 0, max))
            : new Vector(Math.Clamp(target.Offset.X + delta, 0, max), target.Offset.Y);
    }
}
