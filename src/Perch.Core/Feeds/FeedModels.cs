namespace Perch.Feeds;

/// <summary>
/// One feed the user subscribes to (persisted in <c>AppSettings.Feeds</c>, edited on the Feeds settings page).
/// <see cref="Id"/> is the stable key for the on-disk cache and read state, so editing the URL keeps both.
/// </summary>
internal sealed class FeedSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string? TitleOverride { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime AddedUtc { get; set; }

    public FeedSubscription Clone() => new()
    {
        Id = Id, Url = Url, TitleOverride = TitleOverride, Enabled = Enabled, AddedUtc = AddedUtc,
    };
}

/// <summary>
/// A parsed feed, normalized so every format (Atom now, RSS later) lands in the same shape. Every string field is
/// already cleaned by <see cref="FeedText"/> and every URL already vetted by <see cref="FeedUrl"/> — the parser
/// is the one place untrusted text is admitted. See docs/feeds-plan.md §2.1.
/// </summary>
internal sealed record FeedDoc(
    string Title,
    string? SiteUrl,
    string? IconUrl,
    DateTime? Updated,
    IReadOnlyList<FeedEntry> Entries);

/// <summary>
/// One entry. <see cref="ContentHtml"/> is the only raw field: it stays untrusted HTML in the model and the cache
/// and is sanitized at render time by <see cref="HtmlToMarkdown"/> (so a sanitizer fix applies without a
/// refetch), resolving relative links against <see cref="ContentBase"/> — an http(s) URL or null.
/// </summary>
internal sealed record FeedEntry(
    string Id,
    string Title,
    string? Url,
    string? Author,
    DateTime? Published,
    DateTime Updated,
    string? ContentHtml,
    string? ContentBase,
    string? SummaryText);

/// <summary>What <see cref="FeedParser"/> returns: a document, or a short user-facing reason it isn't one. Never
/// both, never an exception.</summary>
internal sealed record FeedParseResult(FeedDoc? Doc, string? Error)
{
    public static FeedParseResult Ok(FeedDoc doc) => new(doc, null);
    public static FeedParseResult Fail(string error) => new(null, error);
}
