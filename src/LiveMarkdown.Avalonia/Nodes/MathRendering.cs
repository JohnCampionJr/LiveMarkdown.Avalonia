using Avalonia.Controls;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Tweed fork addition: the seam an app plugs a math typesetter into.
///
/// The markdown fork deliberately does NOT depend on a math engine. It knows how to show TeX source (see
/// <see cref="MathInlineNode"/>), and if an app supplies a view factory here it will show typeset math instead.
/// Same shape as the other app seams in this fork — <c>MarkdownRenderer.ConfigurePipeline</c> and
/// <c>MarkdownRenderer.WidgetResolver</c> — and for the same reason: the vendored renderer stays generic, Tweed
/// owns the dependency.
/// </summary>
public static class MathRendering
{
    /// <summary>
    /// Creates a view that can typeset LaTeX. Set once at app startup; null (the default) means every
    /// expression falls back to its TeX source.
    /// </summary>
    public static Func<IMathView>? ViewFactory { get; set; }
}

/// <summary>
/// An app-supplied math view. One instance is created per math node and reused for that node's lifetime, which
/// matters during streaming: an expression is re-set on every delta as it arrives character by character.
/// </summary>
public interface IMathView
{
    /// <summary>The control to place in the visual tree.</summary>
    Control Control { get; }

    /// <summary>
    /// Typeset <paramref name="latex"/>, or report that it can't be.
    /// </summary>
    /// <param name="latex">The expression body, without its <c>$</c> delimiters.</param>
    /// <param name="isDisplay">Display math (<c>$$…$$</c>) rather than inline (<c>$…$</c>). Display style sets
    /// larger operators and puts limits above/below rather than beside.</param>
    /// <returns>
    /// <c>false</c> when the LaTeX doesn't parse — the node then shows the TeX source instead. This is the
    /// NORMAL case while a message streams: <c>\frac{1</c> is not yet valid, and every partial prefix of a real
    /// expression passes through this method. Implementations must return false rather than throw.
    /// </returns>
    bool TrySetLaTeX(string latex, bool isDisplay);
}
