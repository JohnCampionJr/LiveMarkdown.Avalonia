using Markdig.Helpers;
using Markdig.Syntax;

namespace LiveMarkdown.Avalonia;

// Only the unmodified default pipeline uses this path. Markdig still parses the opening
// line and changed body suffix, including deciding whether it closes the fence.
internal sealed class OpenFenceAppend(FencedCodeBlock block, string openingLine, int blockIndex)
{
    internal static readonly object SliceOffsetsKey = new();
    private readonly int sourceOrigin = block.GetData(IncrementalParse.CodeLineSourceOffsetKey) as int? ?? 0;

    internal string OpeningLine => openingLine;
    internal int SnapshotOffset(int previousLength) => block.Lines.Count > 0 && block.Lines.Lines[block.Lines.Count - 1].NewLine == NewLine.None
        ? block.Lines.Lines[block.Lines.Count - 1].Position + sourceOrigin : previousLength;

    internal static OpenFenceAppend? Create(MarkdownDocument document, string text, int textOffset)
    {
        var index = document.Count - 1;
        if (index >= 0 && document[index] is LinkReferenceDefinitionGroup) index--;
        if (index < 0 || document[index] is not FencedCodeBlock fence || fence.GetType() != typeof(FencedCodeBlock) ||
            fence.ClosingFencedCharCount != 0 || fence.Column != 0 || fence.IndentCount != 0) return null;
        var start = fence.Span.Start - textOffset;
        if (start < 0 || start >= text.Length) return null;
        var end = text.IndexOf('\n', start);
        if (end < 0 || end - start > 4096) return null;
        return new OpenFenceAppend(fence, text[start..(end + 1)], index);
    }

    internal bool Apply(MarkdownDocument document, MarkdownDocument suffix, int snapshotOffset, out int retained)
    {
        retained = 0;
        if (suffix.Count != 1 || suffix[0] is not FencedCodeBlock parsed || parsed.GetType() != typeof(FencedCodeBlock) ||
            parsed.ClosingFencedCharCount != 0 || parsed.FencedChar != block.FencedChar ||
            parsed.OpeningFencedCharCount != block.OpeningFencedCharCount) return false;
        if (blockIndex >= document.Count || !ReferenceEquals(document[blockIndex], block)) return false;
        retained = blockIndex;
        var count = block.Lines.Count;
        var replacingLast = count > 0 && block.Lines.Lines[count - 1].NewLine == NewLine.None;
        var lineBase = count == 0 ? block.Line + 1 : block.Lines.Lines[count - 1].Line + (replacingLast ? 0 : 1);
        if (replacingLast) block.Lines.RemoveAt(--count);
        var offsets = block.GetData(SliceOffsetsKey) as Dictionary<int, int>;
        if (offsets is null) block.SetData(SliceOffsetsKey, offsets = []);
        var shift = snapshotOffset - openingLine.Length;
        for (var i = 0; i < parsed.Lines.Count; i++)
        {
            var line = parsed.Lines.Lines[i];
            line.Position += shift - sourceOrigin;
            line.Line += lineBase - 1;
            offsets[count + i] = shift;
            block.Lines.Add(ref line);
        }
        block.Span.End = parsed.Span.End + shift;
        document.Span.End = block.Span.End;
        return true;
    }

    internal static int SliceOffset(Markdig.Syntax.CodeBlock code, int index, int fallback) =>
        code.GetData(SliceOffsetsKey) is Dictionary<int, int> offsets && offsets.TryGetValue(index, out var offset) ? offset : fallback;
}
