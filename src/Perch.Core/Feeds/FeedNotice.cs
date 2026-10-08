namespace Perch.Feeds;

/// <summary>One toast to show. <see cref="SubId"/> is the feed whose story a click plays, or null for a summary
/// across several feeds (a click plays the first feed with news).</summary>
internal sealed record FeedToast(string Title, string Body, string? SubId);

/// <summary>
/// Turns one check's arrivals into toasts (docs/feeds-plan.md §4.2): one per feed, batched ("3 new posts in .NET
/// Blog", the newest title underneath), and a single summary toast once more than <see cref="MaxSeparate"/> feeds
/// have news at once (a catch-up after unlock mustn't stack a toast per feed). Priming never produces arrivals, so
/// never toasts. Every string was cleaned by the parser; it's re-capped here for the toast's short lines. Pure; the
/// app does the gating (the notify setting, Quiet mode, the master switch, Do Not Disturb).
/// </summary>
internal static class FeedNotice
{
    public const int MaxSeparate = 3;
    private const int TitleMax = 80, BodyMax = 140;

    public static IReadOnlyList<FeedToast> Build(IReadOnlyList<FeedArrivals> arrivals)
    {
        var withNews = arrivals.Where(a => a.Entries.Count > 0).ToList();
        if (withNews.Count == 0) return [];

        if (withNews.Count > MaxSeparate)
        {
            int total = withNews.Sum(a => a.Entries.Count);
            string list = string.Join(", ", withNews.Select(a => $"{a.FeedTitle} ({a.Entries.Count})"));
            return [new FeedToast($"{total} new posts in {withNews.Count} feeds", FeedText.Clean(list, BodyMax), null)];
        }

        return withNews.Select(a =>
        {
            var newest = a.Entries.MaxBy(e => e.Updated)!;
            string feed = FeedText.Clean(a.FeedTitle, TitleMax);
            string latest = newest.Title.Length > 0 ? newest.Title : "(untitled)";
            return a.Entries.Count == 1
                ? new FeedToast(FeedText.Clean("New in " + feed, TitleMax), FeedText.Clean(latest, BodyMax), a.SubId)
                : new FeedToast(FeedText.Clean($"{a.Entries.Count} new posts in {feed}", TitleMax),
                    FeedText.Clean("Latest: " + latest, BodyMax), a.SubId);
        }).ToList();
    }
}
