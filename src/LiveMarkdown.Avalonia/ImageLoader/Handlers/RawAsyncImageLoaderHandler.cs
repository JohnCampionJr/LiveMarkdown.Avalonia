using System.Text;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Treats a relative or scheme-less source string as raw image bytes.
/// </summary>
public class RawAsyncImageLoaderHandler : AsyncImageLoaderHandler
{
    /// <summary>
    /// Gets the shared raw source image loader handler.
    /// </summary>
    public static RawAsyncImageLoaderHandler Shared { get; } = new();

    /// <inheritdoc/>
    public override bool CoalesceConcurrentLoads => false;

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedSchemes => [string.Empty];

    private RawAsyncImageLoaderHandler() { }

    /// <inheritdoc/>
    public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(uri.OriginalString)));
    }
}
