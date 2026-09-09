using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Documents;
using Markdig.Syntax;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Base class for nodes that render Markdig inline objects as Avalonia inlines.
/// </summary>
public abstract class InlineNode : MarkdownNode
{
    /// <summary>
    /// Gets the Avalonia inline rendered by this node.
    /// </summary>
    public abstract Inline Inline { get; }

    /// <summary>
    /// Determines whether a registered inline node factory can handle the supplied Markdig type.
    /// </summary>
    /// <param name="inlineType">The runtime Markdig inline type.</param>
    /// <returns><see langword="true"/> when a compatible inline factory is registered.</returns>
    public static bool HasRegisteredInlineNodeFactory(Type inlineType)
    {
        var lookup = Lookup();
        return lookup.Registered.GetOrAdd(
            inlineType,
            static (type, factories) => factories
                .OfType<IMarkdownNodeFactory<InlineNode>>()
                .Any(factory => factory.MarkdownType.IsAssignableFrom(type)),
            lookup.Factories);
    }

    /// <summary>
    /// The answers the registered factories give, kept per set of factories. See the same type on
    /// <see cref="BlockNode"/> for why it is per set rather than cleared.
    /// </summary>
    private sealed class FactoryLookup(ImmutableHashSet<IMarkdownNodeFactory> factories)
    {
        public ImmutableHashSet<IMarkdownNodeFactory> Factories { get; } = factories;

        public ConcurrentDictionary<Type, IMarkdownNodeFactory<InlineNode>?> ByType { get; } = new();

        public ConcurrentDictionary<Type, bool> Registered { get; } = new();
    }

    private static FactoryLookup? lookup;

    private static FactoryLookup Lookup()
    {
        var factories = NodeFactories;
        var current = lookup;
        return current is not null && ReferenceEquals(current.Factories, factories)
            ? current
            : lookup = new FactoryLookup(factories);
    }

    /// <summary>
    /// Creates and initializes a node for the specified Markdig inline.
    /// </summary>
    /// <param name="documentNode">The document that owns the inline.</param>
    /// <param name="inline">The Markdig inline to render.</param>
    /// <param name="change">The change that caused the update.</param>
    /// <param name="cancellationToken">A token that cancels node creation.</param>
    /// <returns>A node suitable for rendering <paramref name="inline"/>.</returns>
    protected static InlineNode CreateInlineNode(
        DocumentNode documentNode,
        Markdig.Syntax.Inlines.Inline inline,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var type = inline.GetType();

        // First the exact match, then the most specific compatible one. Which factory that is depends
        // only on the type, so it is resolved once per type rather than per inline.
        var lookup = Lookup();
        var factory = lookup.ByType.GetOrAdd(
            type,
            static (inlineType, factories) => factories
                .OfType<IMarkdownNodeFactory<InlineNode>>()
                .Where(f => f.MarkdownType.IsAssignableFrom(inlineType))
                .OrderBy(f => f)
                .FirstOrDefault(),
            lookup.Factories);

        var node = factory?.CreateNode() ?? new NotImplementedInlineNode(type);

        node.Update(documentNode, inline, change, cancellationToken);
        return node;
    }
}

/// <summary>
/// Base class for inline nodes that handle a specific Markdig inline type.
/// </summary>
/// <typeparam name="TInline">The Markdig inline type handled by the node.</typeparam>
public abstract class InlineNode<TInline> : InlineNode where TInline : Markdig.Syntax.Inlines.Inline
{
    /// <inheritdoc/>
    protected override bool IsDirty(MarkdownObject markdownObject, in ObservableStringBuilderChangedEventArgs change)
    {
        return base.IsDirty(markdownObject, in change) ||
            markdownObject is not TInline inline ||
            !MatchesInline(inline);
    }

    /// <summary>
    /// Determines whether the given inline can be handled by this node.
    /// The default implementation requires an exact type match.
    /// </summary>
    /// <param name="inline">The inline to test.</param>
    /// <returns><see langword="true"/> when the inline can be handled; otherwise, <see langword="false"/>.</returns>
    protected virtual bool MatchesInline(TInline inline) => inline.GetType() == typeof(TInline);

    /// <inheritdoc/>
    protected sealed override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        return markdownObject is TInline inline &&
            MatchesInline(inline) &&
            UpdateCore(documentNode, Unsafe.As<TInline>(markdownObject), change, cancellationToken);
    }

    /// <summary>
    /// Updates the rendered inline from a typed Markdig inline.
    /// </summary>
    /// <param name="documentNode">The document that owns the inline.</param>
    /// <param name="inline">The Markdig inline to render.</param>
    /// <param name="change">The change that caused the update.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns><see langword="true"/> when the inline was handled successfully.</returns>
    protected abstract bool UpdateCore(
        DocumentNode documentNode,
        TInline inline,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken);
}