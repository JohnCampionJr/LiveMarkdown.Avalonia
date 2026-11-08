using Avalonia.Controls;
using Avalonia.Layout;
using Markdig.Syntax;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Base class for block nodes that contain and render child block nodes.
/// </summary>
/// <typeparam name="TContainerBlock">The Markdig container block type.</typeparam>
public abstract class ContainerBlockNode<TContainerBlock> : BlockNode<TContainerBlock> where TContainerBlock : ContainerBlock
{
    /// <summary>
    /// Gets the child container control.
    /// </summary>
    public override Control Control => container;

    /// <summary>
    /// The container that holds the child block nodes.
    /// </summary>
    protected readonly StackPanel container;

    /// <summary>
    /// The proxy that manages the child block nodes.
    /// </summary>
    protected readonly MarkdownRenderer.BlocksProxy proxy;

    private int skippablePrefixBlocks;
    private object? synchronizedFactories;

    /// <summary>
    /// Initializes the child control collection and synchronization proxy.
    /// </summary>
    protected ContainerBlockNode()
    {
        container = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        proxy = new MarkdownRenderer.BlocksProxy(container.Children);
    }

    /// <summary>
    /// Synchronizes child block nodes with the Markdig container.
    /// </summary>
    /// <param name="documentNode">The owning document node.</param>
    /// <param name="containerBlock">The Markdig container block.</param>
    /// <param name="change">The source change being applied.</param>
    /// <param name="cancellationToken">The token used to cancel the update.</param>
    /// <returns><see langword="true"/> when the container remains valid.</returns>
    protected override bool UpdateCore(
        DocumentNode documentNode,
        TContainerBlock containerBlock,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (containerBlock.Count == 0)
        {
            skippablePrefixBlocks = 0;
            if (ReferenceEquals(this, documentNode)) documentNode.LastPrefixBlocksSkipped = 0;
            return false;
        }

        var i = ReferenceEquals(this, documentNode) &&
            documentNode.UnchangedPrefixBlockCount is { } retained && ReferenceEquals(synchronizedFactories, NodeFactories)
                ? Math.Min(retained, Math.Min(skippablePrefixBlocks, Math.Min(proxy.Count, containerBlock.Count)))
                : 0;
        if (ReferenceEquals(this, documentNode)) documentNode.LastPrefixBlocksSkipped = i;
        skippablePrefixBlocks = i;
        for (; i < containerBlock.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = containerBlock[i];

            BlockNode current;
            if (i < proxy.Count)
            {
                current = proxy[i];
                if (current.Update(documentNode, block, change, cancellationToken) is false)
                {
                    // if Update returned false, it means the block needs to be replaced
                    current = CreateBlockNode(documentNode, block, change, cancellationToken);
                    proxy[i] = current;
                }
            }
            else
            {
                current = CreateBlockNode(documentNode, block, change, cancellationToken);
                proxy.Add(current);
            }

            // Custom nodes can deliberately override dirtiness/matching, even for unchanged AST
            // objects. Never suppress those calls. Factory edits invalidate this cached boundary.
            if (skippablePrefixBlocks == i && HasDefaultDirtiness(current)) skippablePrefixBlocks++;
        }

        for (var j = proxy.Count - 1; j >= i; j--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            proxy.RemoveAt(j);
        }

        synchronizedFactories = NodeFactories;
        return true;
    }

    private static bool HasDefaultDirtiness(BlockNode node)
    {
        var type = node.GetType();
        return type == typeof(ParagraphBlockNode) || type == typeof(HeadingBlockNode) ||
            type == typeof(CodeBlockNode) || type == typeof(QuoteBlockNode) ||
            type == typeof(ListBlockNode) || type == typeof(ListItemBlockNode) ||
            type == typeof(TableNode) || type == typeof(TableCellNode) ||
            type == typeof(ThematicBreakBlockNode) || type == typeof(AlertBlockNode);
    }
}
