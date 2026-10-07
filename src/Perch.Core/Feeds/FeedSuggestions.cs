namespace Perch.Feeds;

/// <summary>A feed Perch offers on the Feeds page as a one-click add.</summary>
internal sealed record FeedSuggestion(string Title, string Url, string Blurb, bool ShowImages);

/// <summary>The feeds offered on the Feeds settings page. Each is added like any other subscription (quiet start, the
/// normal checks); one already followed isn't offered.</summary>
internal static class FeedSuggestions
{
    public static readonly IReadOnlyList<FeedSuggestion> All =
    [
        // The comic is the image (and its hover text the punchline), so images come on with it.
        new("xkcd", "https://xkcd.com/atom.xml", "A webcomic of romance, sarcasm, math and language. Images on.", ShowImages: true),
    ];

    /// <summary>The suggestions not already among <paramref name="existing"/>.</summary>
    public static IEnumerable<FeedSuggestion> NotFollowed(IReadOnlyList<FeedSubscription> existing) =>
        All.Where(s => FeedUrl.Safe(s.Url, null) is { } u && !FeedAddress.IsDuplicate(u, existing, exceptId: null));

    public static FeedSubscription Subscribe(FeedSuggestion s, DateTime nowUtc) => new()
    {
        Url = s.Url, ShowImages = s.ShowImages, Enabled = true, AddedUtc = nowUtc,
    };
}
