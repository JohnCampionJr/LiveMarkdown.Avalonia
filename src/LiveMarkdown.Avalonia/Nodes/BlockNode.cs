using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Markdig.Syntax;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Base class for nodes that render Markdig block objects as Avalonia controls.
/// </summary>
public abstract class BlockNode : MarkdownNode
{
    /// <summary>
    /// Gets the Avalonia control rendered by this block node.
    /// </summary>
    public abstract Control Control { get; }

    /// <summary>
    /// Determines whether a registered factory handles the block more specifically than a fallback type.
    /// </summary>
    /// <param name="blockType">The runtime Markdig block type.</param>
    /// <param name="fallbackMarkdownType">The fallback Markdig type being considered.</param>
    /// <returns><see langword="true"/> when a more specific compatible block factory is registered.</returns>
    public static bool HasMoreSpecificBlockNodeFactory(Type blockType, Type fallbackMarkdownType)
    {
        var lookup = Lookup();
        return lookup.MoreSpecific.GetOrAdd(
            (blockType, fallbackMarkdownType),
            static (key, factories) => factories
                .OfType<IMarkdownNodeFactory<BlockNode>>()
                .Any(factory =>
                    factory.MarkdownType != key.Fallback &&
                    key.Fallback.IsAssignableFrom(factory.MarkdownType) &&
                    factory.MarkdownType.IsAssignableFrom(key.Block)),
            lookup.Factories);
    }

    /// <summary>
    /// The answers the registered factories give, kept per set of factories.
    /// </summary>
    /// <remarks>
    /// Both questions below are asked once per node built and once per block projected, and neither
    /// depends on anything but the runtime type and the registered set: a document of 856 blocks and
    /// 2,880 inlines asked them several thousand times to receive a few dozen distinct answers, each
    /// time sorting the matching factories afresh.
    ///
    /// <para>The cache belongs to ONE factory set, and a new set gets a new cache rather than a
    /// cleared one, so an answer computed from the old set can never be read back against the new.
    /// <see cref="MarkdownNode.Edit"/> replaces the set wholesale, which is what makes that work.</para>
    /// </remarks>
    private sealed class FactoryLookup(ImmutableHashSet<IMarkdownNodeFactory> factories)
    {
        public ImmutableHashSet<IMarkdownNodeFactory> Factories { get; } = factories;

        public ConcurrentDictionary<Type, IMarkdownNodeFactory<BlockNode>?> ByType { get; } = new();

        public ConcurrentDictionary<(Type Block, Type Fallback), bool> MoreSpecific { get; } = new();
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
    /// Creates the most specific registered node for a Markdig block.
    /// </summary>
    /// <param name="documentNode">The owning document node.</param>
    /// <param name="block">The Markdig block to render.</param>
    /// <param name="change">The source change being applied.</param>
    /// <param name="cancellationToken">The token used to cancel node creation.</param>
    /// <returns>A node capable of rendering the block.</returns>
    protected static BlockNode CreateBlockNode(
        DocumentNode documentNode,
        Block block,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var type = block.GetType();

        // First the exact match, then the most specific compatible one. Which factory that is depends
        // only on the type, so it is resolved once per type rather than per node.
        var lookup = Lookup();
        var factory = lookup.ByType.GetOrAdd(
            type,
            static (blockType, factories) => factories
                .OfType<IMarkdownNodeFactory<BlockNode>>()
                .Where(f => f.MarkdownType.IsAssignableFrom(blockType))
                .OrderBy(f => f)
                .FirstOrDefault(),
            lookup.Factories);

        var node = factory?.CreateNode() ?? new NotImplementedBlockNode(type);

        node.Update(documentNode, block, change, cancellationToken);
        return node;
    }
}

/// <summary>
/// Base class for block nodes that handle a specific Markdig block type.
/// </summary>
/// <typeparam name="TBlock">The Markdig block type handled by the node.</typeparam>
public abstract class BlockNode<TBlock> : BlockNode where TBlock : Block
{
    /// <summary>
    /// Determines whether the block requires synchronization for the source change.
    /// </summary>
    /// <param name="markdownObject">The current Markdown object.</param>
    /// <param name="change">The source change being applied.</param>
    /// <returns><see langword="true"/> when the block is dirty.</returns>
    protected override bool IsDirty(MarkdownObject markdownObject, in ObservableStringBuilderChangedEventArgs change)
    {
        return base.IsDirty(markdownObject, in change) ||
            markdownObject is not TBlock block ||
            !MatchesBlock(block);
    }

    /// <summary>
    /// Determines whether the given block matches the type TBlock.
    /// Default implementation checks for exact type match.
    /// </summary>
    /// <param name="block"></param>
    /// <returns></returns>
    protected virtual bool MatchesBlock(TBlock block) => block.GetType() == typeof(TBlock);

    /// <inheritdoc/>
    protected sealed override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        return markdownObject is TBlock block &&
            MatchesBlock(block) &&
            UpdateCore(documentNode, Unsafe.As<TBlock>(markdownObject), change, cancellationToken);
    }

    /// <summary>
    /// Updates the rendered control from a strongly typed Markdig block.
    /// </summary>
    /// <param name="documentNode">The owning document node.</param>
    /// <param name="block">The Markdig block to render.</param>
    /// <param name="change">The source change being applied.</param>
    /// <param name="cancellationToken">The token used to cancel the update.</param>
    /// <returns><see langword="true"/> when the block remains valid.</returns>
    protected abstract bool UpdateCore(
        DocumentNode documentNode,
        TBlock block,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken);
}