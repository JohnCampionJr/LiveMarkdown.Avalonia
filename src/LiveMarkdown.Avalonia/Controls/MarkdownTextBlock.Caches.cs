using Avalonia.Controls.Documents;
using Avalonia.Media.TextFormatting;

namespace LiveMarkdown.Avalonia;

public partial class MarkdownTextBlock
{
    private InlineCollection? observedInlines;
    private readonly HashSet<InlineCollection> observedNestedInlines = [];
    private bool inlineObservationsDirty;
    private bool inlineCachesDetached;
    private bool inlineCachesSupported = true;
    private int[] indexedRunLengths = [];
    private bool hasRunIndex;
    private (int Start, int RunIndex)[] cachedRunOffsets = [];
    private CodeInlineSpan[]? cachedCodeSpans;
    private DotInlineSpan[] cachedDotSpans = [];

    internal int LayoutTextBuilds { get; private set; }
    internal int RunIndexBuilds { get; private set; }
    internal int InlineSpanBuilds { get; private set; }

    private string GetLayoutText()
    {
        // Detached collections have no subscription. Preserve correct off-tree reads without
        // retaining external collections just to cache text that is not being rendered.
        EnsureInlineObservations();
        if (inlineCachesDetached || !inlineCachesSupported) _layoutText = null;
        if (_layoutText is not null) return _layoutText;
        LayoutTextBuilds++;
        return _layoutText = Inlines is { Count: > 0 } inlines ? inlines.LayoutText : Text ?? string.Empty;
    }

    private void ObserveInlineContent(InlineCollection? inlines)
    {
        if (ReferenceEquals(observedInlines, inlines))
        {
            inlineObservationsDirty = true;
            return;
        }
        if (observedInlines is not null) observedInlines.Invalidated -= HandleInlineContentInvalidated;
        foreach (var nested in observedNestedInlines) nested.Invalidated -= HandleInlineContentInvalidated;
        observedNestedInlines.Clear();
        observedInlines = inlines;
        inlineObservationsDirty = true;
        if (inlines is not null) inlines.Invalidated += HandleInlineContentInvalidated;
    }

    private void HandleInlineContentInvalidated(object? sender, EventArgs args)
    {
        // Release removed subtrees immediately; discover new nested collections lazily before
        // caching their content, rather than rescanning the tree for every streamed insertion.
        foreach (var nested in observedNestedInlines) nested.Invalidated -= HandleInlineContentInvalidated;
        observedNestedInlines.Clear();
        inlineObservationsDirty = true;
        InvalidateInlineContent();
    }

    // Run.Text uses IInlineHost.Invalidate directly, not InlineCollection.Invalidated.
    // Static class handlers add no per-run subscriptions or retained block references.
    private static void InvalidateParentInlineContent(Inline inline, bool collectionChanged = false)
    {
        for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not MarkdownTextBlock block) continue;
            if (collectionChanged) block.HandleInlineContentInvalidated(null, EventArgs.Empty);
            else block.InvalidateInlineContent();
            break;
        }
    }

    private void EnsureInlineObservations()
    {
        if (!inlineObservationsDirty || inlineCachesDetached) return;
        foreach (var nested in observedNestedInlines) nested.Invalidated -= HandleInlineContentInvalidated;
        observedNestedInlines.Clear();
        inlineCachesSupported = true;
        if (observedInlines is not null) Visit(observedInlines, this);
        inlineObservationsDirty = false;

        void Visit(InlineCollection inlines, global::Avalonia.StyledElement parent)
        {
            foreach (var inline in inlines)
            {
                // Some framework versions leave replacement Span collections unparented.
                // Run.Text notifications then cannot find this host. Keep those trees on the
                // uncached path rather than returning stale text or pointer coordinates.
                if (!ReferenceEquals(inline.Parent, parent)) inlineCachesSupported = false;
                if (inline is not Span span || !observedNestedInlines.Add(span.Inlines)) continue;
                span.Inlines.Invalidated += HandleInlineContentInvalidated;
                Visit(span.Inlines, span);
            }
        }
    }

    internal event Action? InlineContentInvalidated;

    private void InvalidateInlineContent()
    {
        ReleaseSpareLayout();
        _layoutText = null;
        hasRunIndex = false;
        indexedRunLengths = [];
        cachedRunOffsets = [];
        _paintSnapshotDirty = true;
        _paintSnapshot = TextPaintSnapshot.Empty;
        _paintSnapshotTextRuns = null;
        InvalidateInlineSpanCache();
        InlineContentInvalidated?.Invoke();
    }

    // Two reused maps retain only keys consulted while building the latest paint snapshot.
    // No full-cache flush on a normal append: unchanged neighbors still hit the previous map.
    private sealed class CodeInlineShapeCache
    {
        private Dictionary<CodeInlineShapeKey, ShapedCodeInline?> current = [];
        private Dictionary<CodeInlineShapeKey, ShapedCodeInline?>? next;
        internal int Count => current.Count;
        internal int Misses { get; private set; }
        internal void BeginSnapshot() => next?.Clear();
        internal void CompleteSnapshot()
        {
            if (next is null) { current.Clear(); return; }
            (current, next) = (next, current);
            next.Clear(); // drop references to retired text and glyph arrays
        }
        internal void Clear()
        {
            current.Clear();
            next?.Clear();
        }
        internal bool TryGetValue(CodeInlineShapeKey key, out ShapedCodeInline? shaped)
        {
            if (next is not null && next.TryGetValue(key, out shaped)) return true;
            if (current.TryGetValue(key, out shaped))
            {
                (next ??= [])[key] = shaped;
                return true;
            }
            Misses++;
            return false;
        }
        internal ShapedCodeInline? this[CodeInlineShapeKey key]
        {
            set => (next ??= [])[key] = value;
        }
    }

    private void InvalidateInlineSpanCache()
    {
        cachedCodeSpans = null;
        cachedDotSpans = [];
        ClearCodeInlineGeometry();
    }

    private (int Start, int RunIndex)[] GetRunOffsets(IReadOnlyList<TextRun> textRuns)
    {
        // Avalonia may rebuild run objects for a width change. Only their lengths determine
        // these offsets; compare them to preserve correctness even for custom/embedded runs.
        if (!inlineCachesDetached && hasRunIndex && indexedRunLengths.Length == textRuns.Count)
        {
            var same = true;
            for (var i = 0; i < textRuns.Count; i++)
                if (indexedRunLengths[i] != textRuns[i].Length) { same = false; break; }
            if (same) return cachedRunOffsets;
        }
        RunIndexBuilds++;
        var lengths = new int[textRuns.Count];
        var count = 0;
        for (var i = 0; i < textRuns.Count; i++)
        {
            lengths[i] = textRuns[i].Length;
            if (lengths[i] > 0) count++;
        }
        var offsets = count == 0 ? [] : new (int Start, int RunIndex)[count];
        var position = 0;
        var next = 0;
        for (var i = 0; i < textRuns.Count; i++)
        {
            var length = textRuns[i].Length;
            if (length <= 0) continue;
            offsets[next++] = (position, i);
            position += length;
        }
        indexedRunLengths = lengths;
        hasRunIndex = true;
        return cachedRunOffsets = offsets;
    }

    private CodeInlineSpan[] GetCachedCodeInlineSpans()
    {
        EnsureInlineSpans();
        return cachedCodeSpans!;
    }

    private DotInlineSpan[] GetDotInlineSpans()
    {
        EnsureInlineSpans();
        return cachedDotSpans;
    }

    private void EnsureInlineSpans()
    {
        EnsureInlineObservations();
        if (inlineCachesDetached || !inlineCachesSupported) InvalidateInlineSpanCache();
        if (cachedCodeSpans is not null) return;
        InlineSpanBuilds++;
        List<CodeInlineSpan>? codes = null;
        List<DotInlineSpan>? dots = null;
        var position = 0;
        if (Inlines is { } inlines)
            foreach (var inline in inlines) Append(inline);
        cachedCodeSpans = codes?.ToArray() ?? [];
        cachedDotSpans = dots?.ToArray() ?? [];

        void Append(Inline inline)
        {
            switch (inline)
            {
                case CodeInline code:
                    var length = code.Text?.Length ?? 0;
                    (codes ??= []).Add(new CodeInlineSpan(position, length, code, code.Background,
                        code.CornerRadius, code.Padding, code.Margin));
                    position += length;
                    break;
                case DotInline dot:
                    (dots ??= []).Add(new DotInlineSpan(position, dot.Text?.Length ?? 0, dot));
                    position += dot.Text?.Length ?? 0;
                    break;
                case Run run:
                    position += run.Text?.Length ?? 0;
                    break;
                case Span span:
                    foreach (var child in span.Inlines) Append(child);
                    break;
                case LineBreak:
                    position += Environment.NewLine.Length;
                    break;
                case InlineUIContainer:
                    position += TextRun.DefaultTextSourceLength;
                    break;
            }
        }
    }
}
