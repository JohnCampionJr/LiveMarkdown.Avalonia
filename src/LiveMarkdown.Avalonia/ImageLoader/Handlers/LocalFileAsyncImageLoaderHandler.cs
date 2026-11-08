namespace LiveMarkdown.Avalonia;

/// <summary>
/// Handler for loading images from local file URIs (filed://)
/// </summary>
public class LocalFileAsyncImageLoaderHandler : AsyncImageLoaderHandler
{
    /// <summary>
    /// Gets the shared local-file image loader handler.
    /// </summary>
    public static LocalFileAsyncImageLoaderHandler Shared { get; } = new();

    /// <inheritdoc/>
    public override bool CoalesceConcurrentLoads => false;

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedSchemes => ["file"];

    private LocalFileAsyncImageLoaderHandler() { }

    /// <inheritdoc/>
    public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsFile) throw new NotSupportedException("Only file URIs are supported.");

        Stream stream = File.OpenRead(uri.LocalPath);
        return Task.FromResult(stream);
    }
}
