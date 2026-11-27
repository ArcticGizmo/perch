using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Perch.Feeds;

namespace Perch.Data;

/// <summary>
/// Site icons for web quick links: the site's <c>/favicon.ico</c>, fetched through the Feeds fetcher (fenced against
/// private networks except for the link's own host, size-capped, and vetted by <see cref="FeedIcon.Validate"/>), then
/// cached on disk per site so the strip doesn't hit the network on every reload. A cached icon is refetched after
/// <see cref="Refresh"/>; a failed fetch isn't retried until then either, so a site with no favicon isn't asked again
/// every time Settings changes. Never throws.
/// </summary>
internal sealed class QuickLinkFavicons
{
    public static readonly TimeSpan Refresh = TimeSpan.FromDays(7);

    private static readonly string[] Extensions = [".png", ".ico", ".jpg", ".gif", ".bmp", ".webp"];

    private readonly Func<Uri, string?, CancellationToken, Task<byte[]?>> _fetch;
    private readonly string _dir;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, DateTime> _failedAtUtc = new();

    public QuickLinkFavicons(FeedFetcher fetcher)
        : this(fetcher.FetchIconAsync, DefaultDirectory, () => DateTime.UtcNow) { }

    internal QuickLinkFavicons(Func<Uri, string?, CancellationToken, Task<byte[]?>> fetch, string dir, Func<DateTime> clock)
    {
        _fetch = fetch;
        _dir = dir;
        _clock = clock;
    }

    // Local, not roaming: these are a cache, rebuilt from the network whenever they're missing.
    private static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataFolderName, "favicons");

    /// <summary>The cached icon file for <paramref name="url"/>'s site, fetching or refreshing it first when it's
    /// missing or stale. Returns whatever is cached (possibly a stale icon, possibly null) when a fetch fails.</summary>
    public async Task<string?> GetAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (QuickLinkRouting.NormalizeUrl(url) is not { } normal) return null;
            var site = new Uri(normal);
            string stem = Stem(site);
            var existing = Find(stem);
            var now = _clock();

            if (existing is not null && now - File.GetLastWriteTimeUtc(existing) < Refresh) return existing;
            if (_failedAtUtc.TryGetValue(stem, out var failedAt) && now - failedAt < Refresh) return existing;

            // The link's own host may be on a private network (an intranet page the user chose), so its same-host
            // favicon may be too; a redirect anywhere else stays fenced.
            var bytes = await _fetch(new Uri(site, "/favicon.ico"), site.IdnHost, ct).ConfigureAwait(false);
            var ext = bytes is null ? null : FeedIcon.Validate(bytes);
            if (bytes is null || ext is null)
            {
                _failedAtUtc[stem] = now;
                return existing;
            }

            var file = Path.Combine(_dir, stem + ext);
            AtomicFile.Write(file, s => s.Write(bytes, 0, bytes.Length));
            foreach (var other in Extensions)
                if (other != ext) TryDelete(Path.Combine(_dir, stem + other));
            _failedAtUtc.TryRemove(stem, out _);
            return file;
        }
        catch
        {
            return null;
        }
    }

    // One icon per site (scheme + host + port): every link into the same site shares it.
    internal static string Stem(Uri site)
    {
        var key = $"{site.Scheme}://{site.IdnHost}:{site.Port}".ToLowerInvariant();
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private string? Find(string stem)
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_dir, stem + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
