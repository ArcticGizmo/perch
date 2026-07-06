using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IImageClipboard"/>: will write the poster to the general <c>NSPasteboard</c> as a
/// PNG/TIFF representation via AppKit P/Invoke. Stubbed for now (returns false, so the Wrapped card's
/// "Copy image" is a no-op on macOS) pending the port plan's interop pass; "Save PNG…" still works
/// everywhere since it goes through Avalonia's cross-platform storage provider.
/// </summary>
public sealed class ImageClipboard : IImageClipboard
{
    public bool TryCopyBgra(byte[] bgra, int width, int height, int stride) => false;
}
