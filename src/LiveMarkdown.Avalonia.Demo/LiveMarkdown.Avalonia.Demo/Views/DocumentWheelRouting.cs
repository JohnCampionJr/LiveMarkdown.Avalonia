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
/// <summary>Which axes a subtree keeps for itself.</summary>
[Flags]
public enum WheelAxes
{
    /// <summary>The router owns both axes here — the default.</summary>
    None = 0,

    /// <summary>Vertical gestures stay in this subtree. The useful one: a nested list or editor that
    /// scrolls itself.</summary>
    Vertical = 1,

    /// <summary>Horizontal gestures stay in this subtree, rather than being handed to the nearest
    /// horizontally scrollable child. Rarely needed, because that is already close to what the router does
    /// with a horizontal gesture — worth having only when a control pans itself in some other way.</summary>
    Horizontal = 2,

    /// <summary>Neither axis is the router's. What a zoomable diagram wants: it uses the wheel for
    /// something that is not scrolling at all.</summary>
    Both = Vertical | Horizontal,
}

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
    private sealed class GestureState
    {
        public ulong LastTimestamp;
        public bool LockedVertical;

        /// <summary>Who owns this gesture, decided once at its start — see the note on latching below.</summary>
        public WheelAxes Exempt;

        /// <summary>What a horizontal gesture pans, also decided at its start. Weak because the target can
        /// be recycled out from under a live gesture — a virtualized list does exactly that — and a gesture
        /// has no business keeping a discarded control alive.</summary>
        public WeakReference<ScrollViewer>? PanTarget;
    }

    /// <summary>Per-scroller, not static: an app can have several documents live at once — a second
    /// transcript, a tool-output well — and one shared lock would let a gesture in one decide the axis in
    /// another. Weak so a closed view is collectable.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, GestureState> States = new();

    /// <summary>Decide the axis AND the owner for this gesture, latching both at its start.
    ///
    /// <para>Ownership has to latch for the same reason the axis does. Evaluated per event, a scroll already
    /// in flight changes hands the moment the pointer crosses something exempt — you are reading the
    /// document, the text moves a diagram under your cursor, and the page stops dead while the diagram
    /// starts zooming. The pointer is not aiming at the thing it happens to be over; it is sitting still
    /// while content moves beneath it.</para>
    ///
    /// <para>So a gesture belongs to whatever it STARTED over, for its whole life. A scroll begun on prose
    /// keeps scrolling the document across anything it passes; a zoom begun on a diagram keeps zooming even
    /// if the diagram slides out from under the pointer.</para></summary>
    private static (bool Vertical, WheelAxes Exempt, ScrollViewer? PanTarget) ResolveGesture(
        ScrollViewer document, PointerWheelEventArgs e)
    {
        var state = States.GetOrCreateValue(document);
        var newGesture = e.Timestamp - state.LastTimestamp > GestureGapMs || e.Timestamp < state.LastTimestamp;
        state.LastTimestamp = e.Timestamp;

        var x = Math.Abs(e.Delta.X);
        var y = Math.Abs(e.Delta.Y);

        if (newGesture)
        {
            state.LockedVertical = y >= x;
            state.Exempt = ExemptionsFor(e.Source as Visual, document);

            var target = NearestHorizontal(e.Source as Visual, document);
            state.PanTarget = target is null ? null : new WeakReference<ScrollViewer>(target);
        }
        else if (state.LockedVertical ? x > y * BreakoutRatio : y > x * BreakoutRatio)
        {
            // Decisively the other way mid-gesture: the reader turned, rather than wobbled.
            state.LockedVertical = !state.LockedVertical;
        }

        ScrollViewer? pan = null;
        // Recycled, detached, or no longer able to move: the gesture has nothing left to pan, and picking a
        // replacement mid-gesture is the per-event behaviour this exists to avoid.
        if (state.PanTarget is not null
            && state.PanTarget.TryGetTarget(out var latched)
            && latched.GetVisualParent() is not null
            && latched.Extent.Width > latched.Viewport.Width + 0.5)
        {
            pan = latched;
        }

        return (state.LockedVertical, state.Exempt, pan);
    }

    /// <summary>Opt a subtree out, per axis. A document-wide router owns every gesture by default, so
    /// anything nested that legitimately wants one has to say which.
    ///
    /// <para>Per axis rather than all-or-nothing because the two are not symmetric. Keeping VERTICAL is the
    /// common case — a nested list or editor that scrolls itself — and it is the one the default takes away.
    /// Keeping HORIZONTAL is rarely needed, since a horizontal gesture is already handed to the nearest
    /// horizontally scrollable child. Wanting BOTH means the control is not scrolling at all: a zoomable
    /// diagram uses the wheel for zoom.</para>
    ///
    /// <para>Exemptions accumulate down the tree, so a subtree cannot un-exempt what an ancestor claimed.</para></summary>
    public static readonly AttachedProperty<WheelAxes> ExemptProperty =
        AvaloniaProperty.RegisterAttached<Control, WheelAxes>("Exempt", typeof(DocumentWheelRouting));

    public static void SetExempt(Control element, WheelAxes value) => element.SetValue(ExemptProperty, value);
    public static WheelAxes GetExempt(Control element) => element.GetValue(ExemptProperty);

    /// <summary>Wire this up from XAML. Declared HERE, in the app — an attached property can be defined by
    /// any assembly and set on any AvaloniaObject, and the document scroller is the host's own control, so
    /// none of this needs anything from the markdown library.</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Enabled", typeof(DocumentWheelRouting));

    public static void SetEnabled(ScrollViewer element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(ScrollViewer element) => element.GetValue(EnabledProperty);

    static DocumentWheelRouting() =>
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>((scrollViewer, e) =>
        {
            if (e.GetNewValue<bool>()) Attach(scrollViewer);
            else Detach(scrollViewer);
        });

    public static void Detach(ScrollViewer document) =>
        document.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);

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

        // A locked gesture is OURS for its whole life, on BOTH axes. Handling only the vertical case and
        // letting horizontal gestures fall through is not symmetric: the nested scroller pans on the
        // horizontal component, the leftover vertical component reaches the document, and a sideways
        // gesture drifts the page down — the same bug as before with the axes swapped.
        var (vertical, exempt, panTarget) = ResolveGesture(document, e);

        if (vertical)
        {
            if (exempt.HasFlag(WheelAxes.Vertical)) return;   // this subtree scrolls itself
            ScrollBy(document, -e.Delta.Y * StepPx, vertical: true);
        }
        else
        {
            if (exempt.HasFlag(WheelAxes.Horizontal)) return;
            if (panTarget is not null)
            {
                ScrollBy(panTarget, -e.Delta.X * StepPx, vertical: false);
            }
        }

        // Handled either way, including when a drift-only event carried nothing to apply, and when there
        // was nothing to pan. Inside a locked gesture, passing anything on is how the other axis creeps.
        e.Handled = true;
    }

    /// <summary>Everything the subtree under the pointer has claimed, from the pointer up to the document.
    /// Accumulated rather than first-wins so an ancestor's claim cannot be dropped by a descendant.</summary>
    private static WheelAxes ExemptionsFor(Visual? from, ScrollViewer document)
    {
        var axes = WheelAxes.None;
        for (var v = from; v is not null && v != document; v = v.GetVisualParent())
        {
            if (v is Control c) axes |= GetExempt(c);
        }
        return axes;
    }

    /// <summary>The nearest scroller between the pointer and the document that can actually move
    /// horizontally — the thing a deliberate sideways gesture is asking for. Resolved once per gesture,
    /// never per event, so a sideways drag cannot switch tables halfway through.</summary>
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
