using Avalonia.Controls;
using Avalonia.Media;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Represents a decoder that can translate a Stream into an Avalonia IImage.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// Attempts to decode the stream. Returns null if the format is unsupported or invalid.
    /// </summary>
    /// <param name="target">The target Image control, useful for reading attached properties.</param>
    /// <param name="stream">The readable, seekable image stream. It may be read-only; decode
    /// without mutating its encoded bytes. The loader owns and disposes this stream.</param>
    /// <param name="uri">The source URI of the image.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An IImage if decoding is successful, otherwise null.</returns>
    Task<IImage?> TryDecodeAsync(Image target, Stream stream, Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Indicates that a successful result is exclusively owned by this load. The loader disposes
    /// owned disposable results if cancellation/replacement prevents displaying them, or if later
    /// cache publication fails. Once displayed, the result belongs to the image consumer.
    /// Custom decoders default to borrowed results for compatibility; do not opt in for shared images.
    /// </summary>
    /// <param name="image">The successful result returned by this decoder.</param>
    /// <returns>True only when the loader may release an undisplayed result.</returns>
    bool OwnsDecodedImage(IImage image) => false;
}
