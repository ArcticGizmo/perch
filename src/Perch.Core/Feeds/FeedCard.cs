namespace Perch.Feeds;

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
        Uri? @base = entry.ContentBase is { } b && Uri.TryCreate(b, UriKind.Absolute, out var u) ? u : null;
        var md = HtmlToMarkdown.Convert(entry.ContentHtml, @base);
        if (!string.IsNullOrWhiteSpace(md)) return md;
        return string.IsNullOrWhiteSpace(entry.SummaryText) ? "" : HtmlToMarkdown.Escape(entry.SummaryText);
    }
}
