using Avalonia.Controls;
using Avalonia.Controls.Documents;
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

        _codeBlock.SourceSpan = codeBlock.Span;

        // One highlight pass for the whole rewrite, below, rather than one per mutation: the control
        // re-highlights on every change to its inlines, and highlighting walks the entire block.
        using var highlightScope = _codeBlock.SuspendSyntaxHighlighting();

        var lineCount = codeBlock.Lines.Count;
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slice = codeBlock.Lines.Lines[lineIndex].Slice;

            // A line the change cannot have reached keeps what it has, which for a streaming block
            // is every line but the last. The control decides which text block holds it.
            if (lineIndex < _codeBlock.LineCount &&
                (slice.End < change.StartIndex || change.StartIndex + change.Length <= slice.Start)) continue;

            _codeBlock.SetLine(lineIndex, slice.ToString());
        }

        _codeBlock.TrimLines(lineCount);

        // Highlighting only works for closed FencedCodeBlock with Info
        if (codeBlock is not FencedCodeBlock fencedCodeBlock) return true;

        _codeBlock.Language = fencedCodeBlock.Info?.Trim();
        _codeBlock.HighlightSyntax();

        return true;
    }
}