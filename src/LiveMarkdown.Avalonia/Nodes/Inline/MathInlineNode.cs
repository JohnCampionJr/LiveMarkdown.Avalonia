using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Markdig.Extensions.Mathematics;
using Inline = Avalonia.Controls.Documents.Inline;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Tweed fork addition: a node for inline math (<c>$…$</c>).
///
/// The pipeline is built with <c>UseAdvancedExtensions()</c>, which switches Markdig's math extension ON — so
/// <c>$E=mc^2$</c> has always parsed to a <see cref="MathInline"/>. Upstream has no node for it, and the
/// fallback for an unhandled inline is <c>NotImplementedInlineNode</c>: an EMPTY <c>Run</c>. The net effect was
/// that inline math vanished from the transcript entirely — delimiters and body both — with nothing to hint
/// that text had gone missing.
///
/// Two rendering modes, chosen per update:
/// <list type="bullet">
/// <item><b>Typeset</b>, when <see cref="MathRendering.ViewFactory"/> is set and the LaTeX parses.</item>
/// <item><b>TeX source in a chip</b> otherwise — no factory registered, or the expression doesn't parse yet.
///   The chip carries the <c>Code</c> class so it inherits the app's inline-code treatment (mono face, warm
///   chip, hover-intent copy).</item>
/// </list>
///
/// The fallback is not just a degraded mode, it is the STREAMING mode: an agent's <c>$\frac{1}{2}$</c> arrives
/// as <c>$\fra</c>, <c>$\frac{1</c>, … and every one of those prefixes fails to parse. The expression shows as
/// source while it lands and swaps to typeset on the delta that completes it.
///
/// The delimiters are dropped and only <see cref="MathInline.Content"/> is shown. That is safe because
/// Markdig's math inline rules are conservative — prose dollars do NOT parse as math (verified against
/// "It costs $5 and $6", "Range $100-$200", "Price: $9.99. Tax: $1.00." and "export FOO=$BAR then $BAZ",
/// none of which produce a MathInline) — so this cannot silently eat a currency amount.
/// </summary>
public class MathInlineNode : InlineNode<MathInline>
{
    public override Inline Inline => inlineUIContainer;

    private readonly InlineUIContainer inlineUIContainer;
    private readonly Border sourceChip;
    private readonly MarkdownTextBlock textBlock;

    private IMathView? mathView;
    private bool mathViewUnavailable;

    public MathInlineNode()
    {
        sourceChip = new Border
        {
            Child = textBlock = new MarkdownTextBlock()
        };

        // Same treatment as CodeInlineNode: the source chip must report its text baseline, not its bottom
        // edge, or the host line's prose drops below the selection highlight — see ChipBaseline.
        ChipBaseline.Track(sourceChip, textBlock);

        inlineUIContainer = new InlineUIContainer
        {
            Classes = { "Code", "Math" },
            Child = sourceChip
        };
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MathInline math,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var latex = math.Content.ToString();

        if (TryTypeset(latex))
        {
            inlineUIContainer.Child = mathView!.Control;
            // Drop the code-chip treatment: typeset math is not code and must not sit on a mono chip.
            inlineUIContainer.Classes.Set("Code", false);
        }
        else
        {
            textBlock.Text = latex;
            inlineUIContainer.Child = sourceChip;
            inlineUIContainer.Classes.Set("Code", true);
        }

        return true;
    }

    private bool TryTypeset(string latex)
    {
        if (mathViewUnavailable) return false;

        if (mathView is null)
        {
            if (MathRendering.ViewFactory is not { } factory)
            {
                // No app-supplied typesetter. Latch it: the factory is set once at startup, so re-checking on
                // every delta of every expression buys nothing.
                mathViewUnavailable = true;
                return false;
            }

            mathView = factory();
        }

        return mathView.TrySetLaTeX(latex, isDisplay: false);
    }
}
