using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Views;

/// <summary>
/// The author pictures the graph paints on its nodes, by email. <see cref="Request"/> asks about the authors of a
/// snapshot it hasn't asked about yet (a batch at a time), the lookup, download and decode all happen off the UI thread
/// through <see cref="AvatarFetcher"/> and <see cref="BoundedBitmap"/>, and <see cref="Changed"/> fires when new pictures
/// arrive. Until then (or for good, for an author with no GitHub account) the graph paints initials.
/// </summary>
internal sealed class AvatarImages
{
    /// <summary>For the headless render: a fetcher that never touches gh or the network.</summary>
    internal static Func<AvatarFetcher>? FetcherOverride;

    private readonly AvatarFetcher _fetcher = FetcherOverride?.Invoke() ?? new();
    private readonly Dictionary<string, IImage> _images = new(StringComparer.OrdinalIgnoreCase);
    // Asked about per repo: a lookup answers through that repo's commits, so an author missed in one may be found in another.
    private readonly Dictionary<string, HashSet<string>> _asked = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private GraphSnapshot? _next;

    /// <summary>New pictures arrived: repaint.</summary>
    public event Action? Changed;

    public IImage? Get(string email) => _images.GetValueOrDefault(email);

    /// <summary>How many authors have a picture so far.</summary>
    public int Count => _images.Count;

    /// <summary>Starts a lookup for <paramref name="snap"/>'s authors not asked about yet. UI thread. One runs at a time; a
    /// request while one is running is kept (the latest only) and runs after it.</summary>
    public void Request(GraphSnapshot snap)
    {
        if (_busy) { _next = snap; return; }
        var asked = _asked.TryGetValue(snap.Root, out var a) ? a : _asked[snap.Root] = new(StringComparer.OrdinalIgnoreCase);
        var picks = GraphAvatars.PickCommits(snap.Commits, asked);
        if (picks.Count == 0) return;
        foreach (var (email, _) in picks) asked.Add(email);
        _busy = true;
        var root = snap.Root;
        Task.Run(() => _fetcher.Fetch(root, picks)
                .Select(kv => (Email: kv.Key, Image: BoundedBitmap.Load(kv.Value, GraphAvatars.Size, GraphAvatars.Size * GraphAvatars.Size * 64)))
                .Where(x => x.Image is not null)
                .ToList())
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (t.IsCompletedSuccessfully && t.Result.Count > 0)
                {
                    foreach (var (email, image) in t.Result) _images[email] = image!;
                    Changed?.Invoke();
                }
                if (_next is { } more) { _next = null; Request(more); }
            }));
    }
}
