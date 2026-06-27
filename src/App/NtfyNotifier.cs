namespace Perch.App;

using System.Text;

/// <summary>
/// Sends external push notifications through an <see href="https://ntfy.sh">ntfy</see> server: a
/// plain HTTP POST of the message body to <c>{host}/{topic}</c>, with the title and tags carried as
/// headers. Fire-and-forget from the caller's perspective; failures are returned, never thrown, so a
/// flaky network can't take down a notification path.
/// </summary>
internal static class NtfyNotifier
{
    // One shared client (sockets are pooled); a short timeout keeps a dead host from hanging.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Posts a notification. Returns (true, null) on a 2xx response, else (false, reason). When
    /// <paramref name="actionUrl"/> is set, a single "view" action button (labelled
    /// <paramref name="actionLabel"/>) is attached that opens that URL when tapped.
    /// </summary>
    public static async Task<(bool ok, string? error)> SendAsync(
        string host, string topic, string title, string message, string? tags = null,
        string? actionUrl = null, string? actionLabel = null)
    {
        try
        {
            var baseUrl = host.Trim().TrimEnd('/');
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "https://" + baseUrl;

            var url = $"{baseUrl}/{Uri.EscapeDataString(topic.Trim())}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(message, Encoding.UTF8),
            };
            // ntfy reads the title/tags from headers; values are restricted to ASCII so strip the rest.
            req.Headers.TryAddWithoutValidation("Title", Ascii(title));
            if (!string.IsNullOrEmpty(tags))
                req.Headers.TryAddWithoutValidation("Tags", tags);
            // A "view" action opens the URL when the button is tapped. The Actions header is
            // comma-delimited, so the label can't contain commas; the URL never does here. clear=true
            // dismisses the notification once it's been acted on.
            if (!string.IsNullOrEmpty(actionUrl))
                req.Headers.TryAddWithoutValidation(
                    "Actions", $"view, {Ascii(actionLabel ?? "Open")}, {actionUrl}, clear=true");

            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode
                ? (true, null)
                : (false, $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ntfy header values must be Latin-1; drop anything outside printable ASCII so a project name
    // with emoji or accents can't make the whole request fail.
    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c is >= ' ' and <= '~') sb.Append(c);
        return sb.Length > 0 ? sb.ToString() : "Perch";
    }
}
