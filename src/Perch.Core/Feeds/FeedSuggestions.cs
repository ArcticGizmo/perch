namespace Perch.Feeds;

/// <summary>A feed Perch already knows, offered in the Add feed dialog's search.</summary>
internal sealed record FeedSuggestion(string Title, string Url, string Blurb, bool ShowImages);

/// <summary>The known feeds the Add feed dialog's address box searches (all Atom, all checked by hand). Picking one
/// adds it like any other subscription (the same check, a quiet start); one already followed isn't offered.</summary>
internal static class FeedSuggestions
{
    public static readonly IReadOnlyList<FeedSuggestion> All =
    [
        new("Claude Code releases", "https://github.com/anthropics/claude-code/releases.atom",
            "Release notes for each new Claude Code version.", ShowImages: false),
        new("Simon Willison", "https://simonwillison.net/atom/everything/",
            "Near-daily notes on LLMs, AI tools and web development.", ShowImages: false),
        new("Julia Evans", "https://jvns.ca/atom.xml",
            "Friendly deep dives into Linux, networking, git and debugging.", ShowImages: false),
        new("Martin Fowler", "https://martinfowler.com/feed.atom",
            "Software design, architecture and refactoring.", ShowImages: false),
        new("Armin Ronacher", "https://lucumr.pocoo.org/feed.atom",
            "Flask's creator on Python, Rust, open source and agentic coding.", ShowImages: false),
        new(".NET Blog", "https://devblogs.microsoft.com/dotnet/feed/atom/",
            "Official .NET, C# and ASP.NET Core announcements.", ShowImages: false),
        new("The Old New Thing", "https://devblogs.microsoft.com/oldnewthing/feed/atom/",
            "Raymond Chen on Windows history and Win32 quirks.", ShowImages: false),
        new("Andrej Karpathy", "https://karpathy.bearblog.dev/feed/",
            "Occasional long-form posts on neural networks and LLMs.", ShowImages: false),
        // The comic is the image (and its hover text the punchline), so images come on with it.
        new("xkcd", "https://xkcd.com/atom.xml", "A webcomic of romance, sarcasm, math and language. Images on.", ShowImages: true),
    ];

    /// <summary>The suggestions not already among <paramref name="existing"/>.</summary>
    public static IEnumerable<FeedSuggestion> NotFollowed(IReadOnlyList<FeedSubscription> existing) =>
        All.Where(s => FeedUrl.Safe(s.Url, null) is { } u && !FeedAddress.IsDuplicate(u, existing, exceptId: null));

    /// <summary>Whether <paramref name="s"/> matches what's typed in the address box: every word appears in its
    /// title, blurb or address (case-insensitive). An empty box matches everything.</summary>
    public static bool Matches(FeedSuggestion s, string? query) =>
        (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(word =>
            s.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || s.Blurb.Contains(word, StringComparison.OrdinalIgnoreCase)
            || s.Url.Contains(word, StringComparison.OrdinalIgnoreCase));
}
