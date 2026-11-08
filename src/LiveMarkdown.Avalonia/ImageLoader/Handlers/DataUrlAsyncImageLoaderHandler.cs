using System.Text;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Loads image bytes from data URLs.
/// </summary>
public class DataUrlAsyncImageLoaderHandler : AsyncImageLoaderHandler
{
    /// <summary>
    /// Gets the shared data URL image loader handler.
    /// </summary>
    public static DataUrlAsyncImageLoaderHandler Shared { get; } = new();

    /// <inheritdoc/>
    public override bool CoalesceConcurrentLoads => false;

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedSchemes => ["data"];

    private DataUrlAsyncImageLoaderHandler() { }

    /// <inheritdoc/>
    public override Task<Stream> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "data") throw new NotSupportedException("Only data URIs are supported.");

        var dataPart = uri.OriginalString.AsSpan(5);
        var commaIndex = dataPart.IndexOf(',');
        if (commaIndex == -1) throw new FormatException("Invalid Data URL: missing comma.");

        var header = dataPart[..commaIndex];
        var payload = dataPart[(commaIndex + 1)..];

        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
#if NETSTANDARD2_0
            var bytes = Convert.FromBase64CharArray(payload.ToArray(), 0, payload.Length);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
#else
            var bufferSize = payload.Length * 3 / 4;
            var buffer = new byte[bufferSize];

            return Convert.TryFromBase64Chars(payload, buffer, out var bytesWritten) ?
                Task.FromResult<Stream>(new MemoryStream(buffer, 0, bytesWritten)) :
                throw new FormatException("Invalid Base64 data in Data URL.");
#endif
        }

        {
            // fallback to normal URL decoding for non-base64 data URIs
            var decoded = Uri.UnescapeDataString(payload.ToString());
            var bytes = Encoding.UTF8.GetBytes(decoded);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }
    }
}
