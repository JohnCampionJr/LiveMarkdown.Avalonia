using System;
using Avalonia;
using Avalonia.Controls;

namespace LiveMarkdown.Avalonia;

public partial class MarkdownRenderer
{
    /// <summary>
    /// Defines the <see cref="WidgetResolver"/> property.
    /// </summary>
    public static readonly StyledProperty<Func<string, Control?>?> WidgetResolverProperty =
        AvaloniaProperty.Register<MarkdownRenderer, Func<string, Control?>?>(nameof(WidgetResolver));

    /// <summary>
    /// Resolves an app-supplied control to host inline for a <c>:::widget &lt;key&gt;</c> custom container —
    /// the hook that lets a single renderer embed real interactive controls (e.g. an approval card or a
    /// question prompt) in the markdown flow, keyed so any widget type can be plugged in. Return <c>null</c>
    /// for an unknown key (the slot renders empty). The resolver should return a stable instance per key so
    /// state survives the renderer's incremental re-renders.
    /// </summary>
    public Func<string, Control?>? WidgetResolver
    {
        get => GetValue(WidgetResolverProperty);
        set => SetValue(WidgetResolverProperty, value);
    }
}
