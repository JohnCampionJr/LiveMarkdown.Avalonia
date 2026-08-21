using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Markdig.Extensions.Mathematics;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Tweed fork addition: a node for display math (<c>$$…$$</c>).
///
/// Unlike <see cref="MathInlineNode"/> this is not fixing an invisible hole: Markdig's <see cref="MathBlock"/>
/// derives from <c>FencedCodeBlock</c> → <c>CodeBlock</c>, so upstream's <c>CodeBlockNode</c> already claimed it
/// (its <c>MatchesBlock</c> accepts any CodeBlock subtype that no more specific factory handles) and display
/// math rendered as a code block. Registering this node makes <c>HasMoreSpecificBlockNodeFactory</c> true for
/// MathBlock, so CodeBlockNode now declines it and math lands here.
///
/// Same two modes as the inline node — typeset when <see cref="MathRendering.ViewFactory"/> is set and the
/// LaTeX parses, TeX source in a code block otherwise (which is also the streaming mode). The host is a plain
/// <see cref="Border"/> because the node's <c>Control</c> is fixed at construction while the CONTENT has to
/// swap; the source path keeps the real <c>CodeBlock</c> control inside it, so the app's <c>md|CodeBlock</c>
/// styling still applies.
/// </summary>
public class MathBlockNode : BlockNode<MathBlock>
{
    public override Control Control => host;

    private readonly Border host;
    private readonly CodeBlock codeBlock;

    private IMathView? mathView;
    private bool mathViewUnavailable;

    public MathBlockNode()
    {
        codeBlock = new CodeBlock
        {
            // "CodeBlock" keeps the shared block chrome (mono face, copy button, wrap toggle) that
            // MarkdownStyles.axaml hangs off md|CodeBlock; "MathBlock" is the restyling hook.
            Classes = { "CodeBlock", "MathBlock" },
            // No language to highlight. This also unsubscribes CodeBlock's Inlines.CollectionChanged →
            // HighlightSyntax handler, so rebuilding the runs below costs nothing extra.
            AutoSyntaxHighlight = false
        };
        codeBlock.ApplyTemplate(); // match CodeBlockNode: initialize the CodeTextBlock before first update

        host = new Border
        {
            Classes = { "MathBlockHost" },
            Child = codeBlock
        };
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MathBlock mathBlock,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (mathBlock.Lines.Lines is null) return false;

        var latex = SourceText(mathBlock, cancellationToken);

        if (TryTypeset(latex))
        {
            host.Child = mathView!.Control;
            // Styling hook: a typeset equation is centered like a LaTeX display; the source fallback is a code
            // block and must stay full-width and left-aligned, or it would jump around as a message streams.
            host.Classes.Set("Typeset", true);
            return true;
        }

        // Full rebuild rather than CodeBlockNode's index-juggling incremental update. That loop exists so a
        // 500-line fenced block doesn't re-run the syntax highlighter on every streaming token; a display
        // equation is a handful of lines with no highlighting, so the simple version is the right trade.
        var inlines = codeBlock.Inlines;
        inlines.Clear();

        for (var i = 0; i < mathBlock.Lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0) inlines.Add(new LineBreak());
            inlines.Add(new Run(mathBlock.Lines.Lines[i].Slice.ToString()));
        }

        host.Child = codeBlock;
        host.Classes.Set("Typeset", false);
        return true;
    }

    private static string SourceText(MathBlock mathBlock, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < mathBlock.Lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0) builder.Append('\n');
            builder.Append(mathBlock.Lines.Lines[i].Slice.AsSpan());
        }

        return builder.ToString();
    }

    private bool TryTypeset(string latex)
    {
        if (mathViewUnavailable) return false;

        if (mathView is null)
        {
            if (MathRendering.ViewFactory is not { } factory)
            {
                mathViewUnavailable = true;
                return false;
            }

            mathView = factory();
        }

        return mathView.TrySetLaTeX(latex, isDisplay: true);
    }
}
