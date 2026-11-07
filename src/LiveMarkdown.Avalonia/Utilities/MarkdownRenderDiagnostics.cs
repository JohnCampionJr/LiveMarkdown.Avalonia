using System.Diagnostics;
using System.Globalization;

namespace LiveMarkdown.Avalonia;

/// <summary>Content-free synchronous rendering costs published through the
/// <c>LiveMarkdown.Render</c> DiagnosticListener. Phase durations are exclusive and sum to Total.
/// This does not measure later layout, paint, or asynchronous rendering.</summary>
public sealed record MarkdownRenderReport(
    string Kind, int Characters, bool Completed, TimeSpan Total,
    TimeSpan Blocks, TimeSpan Inlines, TimeSpan Code, TimeSpan Construction,
    TimeSpan TreeChanges, TimeSpan CodeStyling, TimeSpan Other,
    int BlockUpdates, int InlineUpdates, int SkippedNodes, int ConstructedNodes, int CollectionChanges, int StylingCalls)
{
    /// <summary>Formats metadata and timings without including document text or retaining visual controls.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"kind={Kind} characters={Characters} completed={Completed} total={Total.TotalMilliseconds:F2}ms " +
        $"blocks={Blocks.TotalMilliseconds:F2}ms inlines={Inlines.TotalMilliseconds:F2}ms code={Code.TotalMilliseconds:F2}ms " +
        $"construction={Construction.TotalMilliseconds:F2}ms treeChanges={TreeChanges.TotalMilliseconds:F2}ms " +
        $"codeStyling={CodeStyling.TotalMilliseconds:F2}ms other={Other.TotalMilliseconds:F2}ms " +
        $"blockUpdates={BlockUpdates} inlineUpdates={InlineUpdates} skippedNodes={SkippedNodes} " +
        $"constructedNodes={ConstructedNodes} collectionChanges={CollectionChanges} stylingCalls={StylingCalls}");
}

internal enum MarkdownRenderPhase { Other, Blocks, Inlines, Code, Construction, TreeChanges, CodeStyling }

internal sealed class MarkdownRenderMetrics(TimeProvider clock, string kind, int characters)
{
    private readonly TimeSpan[] _times = new TimeSpan[7];
    private MarkdownRenderPhase _phase;
    private long _last = clock.GetTimestamp();
    internal bool Completed;
    internal int BlockUpdates, InlineUpdates, SkippedNodes, ConstructedNodes, CollectionChanges, StylingCalls;

    internal MarkdownRenderPhase Switch(MarkdownRenderPhase phase)
    {
        var now = clock.GetTimestamp();
        _times[(int)_phase] += clock.GetElapsedTime(_last, now);
        _last = now;
        var previous = _phase;
        _phase = phase;
        return previous;
    }

    internal Scope Measure(MarkdownRenderPhase phase) => new(this, Switch(phase));

    internal readonly struct Scope(MarkdownRenderMetrics? metrics, MarkdownRenderPhase previous) : IDisposable
    {
        public void Dispose() => metrics?.Switch(previous);
    }

    internal MarkdownRenderReport Finish()
    {
        Switch(MarkdownRenderPhase.Other);
        return new(kind, characters, Completed, TimeSpan.FromTicks(_times.Sum(time => time.Ticks)),
            _times[1], _times[2], _times[3], _times[4], _times[5], _times[6], _times[0],
            BlockUpdates, InlineUpdates, SkippedNodes, ConstructedNodes, CollectionChanges, StylingCalls);
    }
}

internal static class MarkdownRenderDiagnostics
{
    internal const string ListenerName = "LiveMarkdown.Render";
    internal const string UpdateEvent = "DocumentUpdate";
    internal const string SlowUpdateEvent = "SlowDocumentUpdate";
    internal static readonly DiagnosticListener Listener = new(ListenerName);
    [ThreadStatic] internal static MarkdownRenderMetrics? Current;

    internal static MarkdownRenderMetrics.Scope Measure(MarkdownRenderPhase phase) => Current?.Measure(phase) ?? default;

    internal static Capture Begin(string kind, int characters, TimeProvider? clock = null)
    {
        // Nested renderers contribute to their enclosing update. Do not double-report them or let
        // an inner render replace the active phase accumulator. Disabled tracing takes no timestamps.
        if (Current is not null || (!Listener.IsEnabled(UpdateEvent) && !Listener.IsEnabled(SlowUpdateEvent))) return default;
        Current = new(clock ?? TimeProvider.System, kind, characters);
        return new(Current);
    }

    internal readonly struct Capture(MarkdownRenderMetrics? metrics) : IDisposable
    {
        internal void Complete() { if (metrics is not null) metrics.Completed = true; }
        public void Dispose()
        {
            if (metrics is null) return;
            var report = metrics.Finish();
            Current = null;
            if (Listener.IsEnabled(UpdateEvent)) Listener.Write(UpdateEvent, report);
            if (report.Total.TotalMilliseconds >= 2 && Listener.IsEnabled(SlowUpdateEvent)) Listener.Write(SlowUpdateEvent, report);
        }
    }
}
