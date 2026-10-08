using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Feeds;

namespace Perch.Avalonia.Services;

/// <summary>
/// Decoded feed icons for the overlay row, keyed by the cached file's path and timestamp. A miss starts a decode on
/// the thread pool and returns what it had (or null → the initials fallback); <see cref="Loaded"/> fires on the UI
/// thread when one lands, so the row is re-pushed. The bytes are re-vetted by <see cref="FeedIcon.Validate"/>
/// (size-capped raster only) before decoding, at a small target width — the icons are drawn ~17 DIP across.
/// </summary>
internal sealed class FeedIconCache
{
    private const int DecodeWidth = 48;

    private readonly Dictionary<string, (DateTime Stamp, Bitmap? Bitmap)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Loaded;

    /// <summary>The decoded icon at <paramref name="path"/>, or null while it loads / when it can't be used.</summary>
    public Bitmap? Get(string? path)
    {
        if (path is null) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch { return null; }

        _cache.TryGetValue(path, out var hit);
        if (hit.Stamp == stamp && _cache.ContainsKey(path)) return hit.Bitmap;
        if (!_loading.Add(path)) return hit.Bitmap;

        Task.Run(() => Decode(path)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _loading.Remove(path);
            _cache[path] = (stamp, t.IsCompletedSuccessfully ? t.Result : null);
            Loaded?.Invoke();
        }));
        return hit.Bitmap;
    }

    private static Bitmap? Decode(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > FeedFetcher.MaxIconBytes) return null;
            var bytes = File.ReadAllBytes(path);
            if (FeedIcon.Validate(bytes) is null) return null;
            return Bitmap.DecodeToWidth(new MemoryStream(bytes), DecodeWidth);
        }
        catch
        {
            return null;
        }
    }
}
