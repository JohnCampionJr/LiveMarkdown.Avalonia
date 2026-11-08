namespace LiveMarkdown.Avalonia;

/// <summary>
/// Handler for loading images from specific URI schemes.
/// </summary>
public abstract class AsyncImageLoaderHandler
{
    /// <summary>
    /// Gets whether concurrent cold loads may share one fetch and encoded-byte buffer. HTTP and
    /// custom handlers default to true. Local/resource/data handlers disable this to preserve their
    /// direct seekable-stream decoding path. Disable it for target-specific or unbuffered sources.
    /// </summary>
    public virtual bool CoalesceConcurrentLoads => true;

    /// <summary>
    /// Supported URI schemes (e.g., "http", "https", "file").
    /// </summary>
    public abstract IEnumerable<string> SupportedSchemes { get; }

    /// <summary>
    /// Loads image bytes for the specified URI.
    /// </summary>
    /// <param name="uri">The URI to load.</param>
    /// <param name="cancellationToken">A token that cancels the load operation.</param>
    /// <returns>A readable stream owned by the caller, who must dispose it.</returns>
    public abstract Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Loads image bytes for the specified URI, optionally using cached metadata
    /// for handler-specific validation.
    /// </summary>
    /// <param name="uri">The URI to load.</param>
    /// <param name="cachedMetadata">Metadata from a stale cache entry, or null when no entry exists.</param>
    /// <param name="cancellationToken">A token that cancels the load operation.</param>
    /// <returns>A response object owned by the caller, who must dispose it.</returns>
    public virtual async Task<AsyncImageLoaderResponse> LoadAsync(
        Uri uri,
        AsyncImageLoaderCacheMetadata? cachedMetadata,
        CancellationToken cancellationToken)
    {
        var stream = await LoadAsync(uri, cancellationToken);
        return AsyncImageLoaderResponse.FromStream(stream, AsyncImageLoaderCacheMetadata.Create(uri));
    }
}
