namespace Perch.Plugins;

using System.Net.Http;
using System.Net.Http.Headers;

/// <summary>
/// The live <see cref="IPluginDownloader"/> over <c>HttpClient</c>. Sends the GitHub API <c>User-Agent</c>
/// and Accept headers (the API rejects requests without a UA) and honours <c>GITHUB_TOKEN</c> for the
/// metadata call only, never on asset downloads — asset URLs redirect to a pre-signed objects host that
/// rejects an <c>Authorization</c> header (the same rule install.ps1 documents).
/// </summary>
internal sealed class HttpPluginDownloader : IPluginDownloader
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("perch-plugin-installer");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("perch-plugin-installer"); // no Authorization: pre-signed asset host
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
