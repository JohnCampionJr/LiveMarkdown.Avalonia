using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Markdig.Helpers;
using Markdig.Syntax;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Renders a Markdig code block through the <see cref="CodeBlock"/> control.
/// </summary>
public class CodeBlockNode : BlockNode<Markdig.Syntax.CodeBlock>
{
    /// <summary>
    /// Gets the code block control.
    /// </summary>
    public override Control Control { get; }

    private readonly CodeBlock _codeBlock;
    private readonly List<int> linePositions = [];
    private bool orderedLinePositions;
    private Type? previousBlockType;
    private int previousBlockStart;
    private int previousBlockColumn;
    private int appendBoundary = int.MaxValue;

    // Work counters for renderer/node-level benchmarks; no elapsed-time assertions are needed.
    internal static long LineInspections;
    internal static long LineSearchComparisons;

    /// <summary>
    /// Initializes a code block node and its control.
    /// </summary>
    public CodeBlockNode()
    {
        Control = _codeBlock = new CodeBlock
        {
            Classes = { "CodeBlock" }
        };
        _codeBlock.ApplyTemplate(); // Ensure the template is applied to initialize the CodeTextBlock
    }

    /// <inheritdoc/>
    protected override bool MatchesBlock(Markdig.Syntax.CodeBlock block)
    {
        var blockType = block.GetType();
        if (blockType == typeof(Markdig.Syntax.CodeBlock) || blockType == typeof(FencedCodeBlock))
        {
            return true;
        }

        return !HasMoreSpecificBlockNodeFactory(blockType, typeof(Markdig.Syntax.CodeBlock));
    }

    /// <summary>
    /// Updates code lines, language metadata, and syntax highlighting.
    /// </summary>
    /// <param name="documentNode">The owning document node.</param>
    /// <param name="codeBlock">The Markdig code block.</param>
    /// <param name="change">The source change being applied.</param>
    /// <param name="cancellationToken">The token used to cancel the update.</param>
    /// <returns><see langword="true"/> when the code block remains valid.</returns>
    protected override bool UpdateCore(
        DocumentNode documentNode,
        Markdig.Syntax.CodeBlock codeBlock,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (codeBlock.Lines.Lines is null) return false;

        cancellationToken.ThrowIfCancellationRequested();
        var sourceOffset = codeBlock.GetData(IncrementalParse.CodeLineSourceOffsetKey) as int?;
        var firstLine = 0;
        var structuralChange = previousBlockType != codeBlock.GetType() || previousBlockStart != codeBlock.Span.Start ||
            previousBlockColumn != codeBlock.Column || linePositions.Count == 0 || change.StartIndex < linePositions[0];
        if (!structuralChange && sourceOffset.HasValue && orderedLinePositions && linePositions.Count == _codeBlock.LineCount &&
            (codeBlock.GetType() == typeof(FencedCodeBlock) || codeBlock.GetType() == typeof(Markdig.Syntax.CodeBlock)))
        {
            firstLine = change.StartIndex >= appendBoundary && codeBlock.Lines.Count >= linePositions.Count
                ? linePositions.Count
                : FindLine(change.StartIndex);
            firstLine = Math.Min(firstLine, codeBlock.Lines.Count);
        }

        _codeBlock.SourceSpan = codeBlock.Span;

        // One highlight pass for the whole rewrite, below, rather than one per mutation: the control
        // re-highlights on every change to its inlines, and highlighting walks the entire block.
        using var highlightScope = _codeBlock.SuspendSyntaxHighlighting();

        var lineCount = codeBlock.Lines.Count;
        if (firstLine == 0) orderedLinePositions = true;
        if (linePositions.Count > firstLine) linePositions.RemoveRange(firstLine, linePositions.Count - firstLine);
        for (var lineIndex = firstLine; lineIndex < lineCount; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LineInspections++;

            var line = codeBlock.Lines.Lines[lineIndex];
            var slice = line.Slice;
            var position = line.Position + (sourceOffset ?? 0);
            if (linePositions.Count > 0 && position <= linePositions[^1]) orderedLinePositions = false;
            linePositions.Add(position);

            // A line the change cannot have reached keeps what it has, which for a streaming block
            // is every line but the last. The control decides which text block holds it.
            if (!structuralChange && lineIndex < _codeBlock.LineCount &&
                (slice.End + (sourceOffset ?? 0) < change.StartIndex || change.StartIndex + change.Length <= slice.Start + (sourceOffset ?? 0))) continue;

            _codeBlock.SetLine(lineIndex, slice.ToString());
        }

        _codeBlock.TrimLines(lineCount);
        previousBlockType = codeBlock.GetType();
        previousBlockStart = codeBlock.Span.Start;
        previousBlockColumn = codeBlock.Column;
        appendBoundary = int.MaxValue;
        if (lineCount > 0)
        {
            var last = codeBlock.Lines.Lines[lineCount - 1];
            var newlineLength = last.NewLine == NewLine.CarriageReturnLineFeed ? 2 : last.NewLine == NewLine.None ? 0 : 1;
            if (newlineLength > 0)
                appendBoundary = Math.Max(last.Position - 1, last.Slice.End) + (sourceOffset ?? 0) + newlineLength + 1;
        }

        // Highlighting only works for closed FencedCodeBlock with Info
        if (codeBlock is not FencedCodeBlock fencedCodeBlock) return true;

        _codeBlock.Language = fencedCodeBlock.Info?.Trim();
        _codeBlock.HighlightSyntax();

        return true;
    }

    private int FindLine(int sourcePosition)
    {
        var low = 0;
        var high = linePositions.Count;
        while (low < high)
        {
            LineSearchComparisons++;
            var middle = low + ((high - low) >> 1);
            if (linePositions[middle] <= sourcePosition) low = middle + 1;
            else high = middle;
        }
        return Math.Max(0, low - 1);
    }
}
