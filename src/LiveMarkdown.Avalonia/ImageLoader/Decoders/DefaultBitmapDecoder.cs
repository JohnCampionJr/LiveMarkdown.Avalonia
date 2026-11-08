using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// The default decoder that attempts to load the stream as a standard Avalonia Bitmap.
/// </summary>
public class DefaultBitmapDecoder : IImageDecoder
{
    /// <summary>
    /// Gets the shared default bitmap decoder.
    /// </summary>
    public static DefaultBitmapDecoder Shared { get; } = new();

    /// <inheritdoc/>
    public bool OwnsDecodedImage(IImage image) => true;

    /// <inheritdoc/>
    public Task<IImage?> TryDecodeAsync(Image target, Stream stream, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = new Bitmap(stream);
            return Task.FromResult<IImage?>(bitmap);
        }
        catch
        {
            return Task.FromResult<IImage?>(null);
        }
    }
}
