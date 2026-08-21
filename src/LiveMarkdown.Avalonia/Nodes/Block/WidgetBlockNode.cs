using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Markdig.Extensions.CustomContainers;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Renders a <c>:::widget &lt;key&gt;</c> custom container as an app-supplied control, resolved by key through
/// <see cref="MarkdownRenderer.WidgetResolver"/>. This is what lets a single renderer host real interactive
/// controls — an approval card, a question prompt, anything — inline in the markdown flow, while keeping the
/// smooth single-scroll surface. Extensible: the key is opaque, so any widget type plugs in.
///
/// A non-<c>widget</c> custom container falls back to showing its info text rather than breaking (we don't want
/// a stray <c>:::</c> block to thrash the incremental updater).
/// </summary>
public sealed class WidgetBlockNode : BlockNode<CustomContainer>
{
    private const string WidgetInfo = "widget";

    private readonly ContentControl host = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    private string? resolvedKey;
    private bool resolved;

    public override Control Control => host;

    protected override bool UpdateCore(
        DocumentNode documentNode,
        CustomContainer block,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(block.Info, WidgetInfo, StringComparison.OrdinalIgnoreCase))
        {
            // Not a widget container — show the info so it isn't silently dropped, and don't churn.
            host.Content ??= new TextBlock { Text = block.Info ?? string.Empty };
            return true;
        }

        var key = block.Arguments?.Trim() ?? string.Empty;

        // Resolve once per key (the resolver returns a stable control), so incremental re-renders while later
        // content streams in don't recreate the hosted control and lose its state.
        if (resolved && string.Equals(resolvedKey, key, StringComparison.Ordinal))
            return true;

        resolved = true;
        resolvedKey = key;
        host.Content = documentNode.Owner.WidgetResolver?.Invoke(key);
        return true;
    }
}
