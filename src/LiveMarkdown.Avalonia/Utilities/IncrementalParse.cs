using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Splices a freshly parsed trailing region onto an already parsed document, so that a streaming
/// append costs the block it touched rather than the whole document.
/// </summary>
/// <remarks>
/// <para>The restart point is the first character of the line the last content block starts on. Text
/// appended at the end of a document can only extend that block, so every construct that reaches
/// back one block — a lazy paragraph continuation, a setext heading underlining the line above, a
/// pipe table taking the line above as its header, a definition list taking it as its term, a fence
/// or HTML block still open — is inside the region that gets re-parsed.</para>
///
/// <para>The constructs that reach back FURTHER are the ones a definition anywhere can change: a
/// link reference, a footnote or an abbreviation appended at the end rewrites matching text at the
/// top. <see cref="CanSplice"/> refuses those documents and the producer parses them whole.</para>
///
/// <para>Headings are the same kind of thing in disguise. Markdig's auto-identifier extension emits
/// a <see cref="HeadingLinkReferenceDefinition"/> for every heading into a group block at the end of
/// the document, so a heading is a definition that <c>[Some Heading]</c> anywhere can resolve
/// against, and its identifier carries a uniqueness suffix that depends on every heading before it.
/// That group is spliced as document state rather than as content, and <see cref="TrySplice"/>
/// refuses — for this update only, not for good — in the two cases where the tail cannot see enough
/// to get it right: when the identifiers it produces are not exactly the ones it replaces, which is
/// what a new heading or a moved suffix looks like; and when it leaves a bracket sitting in literal
/// text, which is what a reference to a heading defined further up looks like once it has failed to
/// resolve against a tail that does not contain it.</para>
///
/// <para>The result is indistinguishable from parsing the whole source: same block and inline types,
/// same source spans, same text. <c>IncrementalParseTests</c> asserts that over a matrix of
/// documents and appends, and <c>RasterEqualityTests</c> asserts that the two draw the same
/// pixels.</para>
/// </remarks>
internal static class IncrementalParse
{
    /// <summary>
    /// Determines whether a parsed region is free of constructs that can change text outside it.
    /// </summary>
    /// <remarks>
    /// An extension that keeps document-global state of its own has to be added here.
    /// </remarks>
    public static bool CanSplice(MarkdownObject root)
    {
        foreach (var descendant in root.Descendants())
        {
            switch (descendant)
            {
                // A heading's definition is state, but it is state this file knows how to carry.
                case HeadingLinkReferenceDefinition:
                case LinkReferenceDefinitionGroup:
                    continue;

                case LinkReferenceDefinition:
                case Footnote:
                case FootnoteGroup:
                case FootnoteLink:
                case Abbreviation:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the offset to re-parse from after <paramref name="document"/> changes again.
    /// </summary>
    /// <param name="document">The document just parsed or spliced.</param>
    /// <param name="text">The text the trailing blocks were parsed from.</param>
    /// <param name="textOffset">The offset of <paramref name="text"/> within the whole source.</param>
    /// <param name="fallback">The offset to keep when the last block predates <paramref name="text"/>.</param>
    /// <returns>The absolute offset to re-parse from, or -1 when there is no usable restart point.</returns>
    public static int RestartOffset(MarkdownDocument document, string text, int textOffset, int fallback)
    {
        if (LastContentBlock(document) is not { } last || last.Span.Start > last.Span.End) return -1;

        // An append that produced no block of its own — trailing whitespace, say — leaves the last
        // block where it was, in text this call no longer holds. The point it gave last time still
        // stands, because it is that same block's.
        if (last.Span.Start < textOffset) return fallback;

        var start = last.Span.Start - textOffset;
        if (start > text.Length || text.Length == 0) return -1;

        var lineBreak = text.LastIndexOf('\n', Math.Min(start, text.Length - 1));
        return textOffset + (lineBreak < 0 ? 0 : lineBreak + 1);
    }

    /// <summary>
    /// Moves every source span in a region parsed on its own to where the region sits in the source.
    /// </summary>
    public static void Shift(MarkdownDocument tail, int offset)
    {
        foreach (var descendant in tail.Descendants())
        {
            // An unset span is left alone: parsing the whole source would not have given it one
            // either, and the node sync compares spans for equality.
            if (descendant.Span.Start > descendant.Span.End) continue;
            descendant.Span = new SourceSpan(descendant.Span.Start + offset, descendant.Span.End + offset);
        }
    }

    /// <summary>
    /// Replaces everything from <paramref name="restartOffset"/> onwards with the re-parsed tail.
    /// </summary>
    /// <param name="document">The document to splice into. Untouched when this returns false.</param>
    /// <param name="restartOffset">The offset the tail was parsed from.</param>
    /// <param name="tail">The re-parsed tail, already shifted. Emptied when this returns true.</param>
    /// <returns>
    /// <see langword="false"/> when the tail cannot be trusted on its own — see the remarks on this
    /// class — and the caller has to parse the whole source for this update instead.
    /// </returns>
    /// <remarks>
    /// The document is mutated rather than rebuilt: the blocks before the restart point are the same
    /// objects, with the same spans, which is exactly what lets the node sync skip them.
    /// </remarks>
    public static bool TrySplice(MarkdownDocument document, int restartOffset, MarkdownDocument tail)
    {
        var documentGroup = document.LastChild as LinkReferenceDefinitionGroup;
        var tailGroup = tail.LastChild as LinkReferenceDefinitionGroup;

        if (!HeadingsMatch(documentGroup, tailGroup, restartOffset)) return false;
        if (HasUnresolvedReference(tail)) return false;

        if (documentGroup is not null) document.RemoveAt(document.Count - 1);
        if (tailGroup is not null) tail.RemoveAt(tail.Count - 1);

        while (document.Count > 0 && document[^1].Span.Start >= restartOffset)
        {
            document.RemoveAt(document.Count - 1);
        }

        // Taken from the end so each removal is O(1), then added back in source order. Markdig
        // refuses to add a block that still has a parent, which is what the detach is for.
        var blocks = new Block[tail.Count];
        for (var i = blocks.Length - 1; i >= 0; i--)
        {
            blocks[i] = tail[i];
            tail.RemoveAt(i);
        }

        foreach (var block in blocks)
        {
            document.Add(block);
        }

        if (MergeDefinitions(documentGroup, tailGroup, restartOffset) is { } group)
        {
            document.Add(group);
        }

        return true;
    }

    /// <summary>The last block that came from the source, ignoring the definitions group.</summary>
    private static Block? LastContentBlock(MarkdownDocument document)
    {
        for (var i = document.Count - 1; i >= 0; i--)
        {
            if (document[i] is not LinkReferenceDefinitionGroup) return document[i];
        }

        return null;
    }

    /// <summary>
    /// Determines whether the tail defines exactly the headings the region it replaces defined.
    /// </summary>
    /// <remarks>
    /// A heading is identified by the identifier the auto-identifier extension gave it, which is
    /// what a reference resolves against and what carries the uniqueness suffix. A tail that
    /// produces a different set has seen a heading the document does not know about, or would
    /// number one differently than the whole document does.
    /// </remarks>
    private static bool HeadingsMatch(
        LinkReferenceDefinitionGroup? documentGroup,
        LinkReferenceDefinitionGroup? tailGroup,
        int restartOffset)
    {
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        if (documentGroup is not null)
        {
            foreach (var block in documentGroup)
            {
                // Only headings put definitions here in a document CanSplice accepted, so anything
                // else means the two disagree about what this document is. Take the whole thing.
                if (Identifier(block) is not { } identifier) return false;
                if (((HeadingLinkReferenceDefinition)block).Heading.Span.Start >= restartOffset)
                {
                    replaced.Add(identifier);
                }
            }
        }

        var count = 0;
        if (tailGroup is not null)
        {
            foreach (var block in tailGroup)
            {
                if (Identifier(block) is not { } identifier || !replaced.Contains(identifier)) return false;
                count++;
            }
        }

        return count == replaced.Count;
    }

    private static string? Identifier(Block block) =>
        block is HeadingLinkReferenceDefinition { Heading: { } heading }
            ? heading.TryGetAttributes()?.Id
            : null;

    /// <summary>
    /// Determines whether the tail left a bracket in literal text, which is what a reference to
    /// something defined outside the tail looks like after failing to resolve.
    /// </summary>
    /// <remarks>
    /// Resolved links do not reach here — their brackets are structure, not text — so this costs a
    /// whole-document parse only for a tail that really does carry an unmatched bracket.
    /// </remarks>
    private static bool HasUnresolvedReference(MarkdownDocument tail)
    {
        foreach (var descendant in tail.Descendants())
        {
            if (descendant is LiteralInline literal && literal.Content.IndexOf('[') >= 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Drops the definitions of the headings that were replaced and takes the tail's in their place.
    /// </summary>
    private static LinkReferenceDefinitionGroup? MergeDefinitions(
        LinkReferenceDefinitionGroup? documentGroup,
        LinkReferenceDefinitionGroup? tailGroup,
        int restartOffset)
    {
        if (documentGroup is null) return tailGroup;

        for (var i = documentGroup.Count - 1; i >= 0; i--)
        {
            if (documentGroup[i] is HeadingLinkReferenceDefinition { Heading: { } heading }
                && heading.Span.Start >= restartOffset)
            {
                documentGroup.RemoveAt(i);
            }
        }

        if (tailGroup is not null)
        {
            var definitions = new Block[tailGroup.Count];
            for (var i = definitions.Length - 1; i >= 0; i--)
            {
                definitions[i] = tailGroup[i];
                tailGroup.RemoveAt(i);
            }

            foreach (var definition in definitions)
            {
                documentGroup.Add(definition);
            }
        }

        return documentGroup;
    }
}
