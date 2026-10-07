namespace Perch.Feeds;

/// <summary>An image lifted out of an entry for a feed with images on: a vetted http(s) source, its alt text and its
/// <c>title</c> (shown as a caption — XKCD's hover text is the punchline). Both strings are cleaned.</summary>
internal sealed record FeedImage(Uri Src, string? Alt, string? Caption);

/// <summary>One piece of a card body, in order: sanitized Markdown, or an image. Exactly one is set.</summary>
internal sealed record FeedCardPart(string? Markdown, FeedImage? Image);

/// <summary>
/// What a story card's body shows (docs/feeds-plan.md §1, "Card"): the entry's content run through
/// <see cref="HtmlToMarkdown"/>, else its plain-text summary, escaped so it renders literally, else nothing (the
/// window shows a "No preview" stub). Pure and off-UI-thread safe, so the window converts here on the thread pool
/// and only hands finished Markdown to <c>MarkdownView</c>.
///
/// <para>The card is rendered <b>without</b> a <c>FileRefContext</c>, so no path-like text or link can ever open a
/// local file. Every link this emits has already passed <see cref="FeedUrl.Safe"/>; <c>UiConventionTests</c> pins
/// the window to the no-file-refs overload.</para>
/// </summary>
internal static class FeedCard
{
    public static string BodyMarkdown(FeedEntry entry)
    {
        var md = HtmlToMarkdown.Convert(entry.ContentHtml, Base(entry));
        if (!string.IsNullOrWhiteSpace(md)) return md;
        return Summary(entry);
    }

    /// <summary>The body as ordered parts. With <paramref name="images"/> off (the default for every feed) it's the
    /// single Markdown part of <see cref="BodyMarkdown"/>; on, block-level images come out as their own parts
    /// (<see cref="HtmlToMarkdown.ConvertParts"/>) for the window to load. Empty when there's nothing to show.</summary>
    public static IReadOnlyList<FeedCardPart> Body(FeedEntry entry, bool images)
    {
        if (images && HtmlToMarkdown.ConvertParts(entry.ContentHtml, Base(entry)) is { Count: > 0 } parts) return parts;
        var md = images ? Summary(entry) : BodyMarkdown(entry);
        return md.Length == 0 ? [] : [new FeedCardPart(md, null)];
    }

    private static Uri? Base(FeedEntry entry) =>
        entry.ContentBase is { } b && Uri.TryCreate(b, UriKind.Absolute, out var u) ? u : null;

    private static string Summary(FeedEntry entry) =>
        string.IsNullOrWhiteSpace(entry.SummaryText) ? "" : HtmlToMarkdown.Escape(entry.SummaryText);
}
