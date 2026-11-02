using System.Diagnostics;
using System.Text.Json;

namespace Perch.Data.GitGraph;

/// <summary>
/// The IO half of <see cref="GraphAvatars"/>: finds each author's picture and returns its image bytes. A noreply email
/// names its avatar outright; any other is looked up once through <c>gh api graphql</c> (the user's own GitHub sign-in,
/// so private repos work) and the answer, picture or none, is kept on disk for <see cref="Fresh"/>. Images come only
/// from <see cref="GraphAvatars.AvatarHost"/>, capped at <see cref="MaxBytes"/>, and are cached beside the index. The
/// cache is keyed by a hash of the email, never the email. Blocking: call off the UI thread. Never throws.
/// </summary>
internal sealed class AvatarFetcher
{
    public const int MaxBytes = 512 * 1024;
    public static readonly TimeSpan Fresh = TimeSpan.FromDays(7);
    private const int GhTimeoutMs = 20_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly object IndexLock = new();

    private readonly string _dir;
    private readonly Func<string, string?> _graphql;
    private readonly Func<string, byte[]?> _download;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="dir">The cache folder; null is <c>%LocalAppData%/Perch/avatars</c>.</param>
    /// <param name="graphql">Runs a GraphQL body, returning gh's stdout or null; null runs gh.</param>
    /// <param name="download">Fetches an avatar URL's bytes or null; null uses HTTP.</param>
    public AvatarFetcher(string? dir = null, Func<string, string?>? graphql = null, Func<string, byte[]?>? download = null,
        Func<DateTimeOffset>? now = null)
    {
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppProfile.DataFolderName, "avatars");
        _graphql = graphql ?? RunGhGraphql;
        _download = download ?? Download;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>One cached answer: the picture's URL ("" = GitHub has no account for this email) and when it was asked.</summary>
    internal sealed record Entry(string Url, long Checked);

    /// <summary>
    /// The image bytes for each author in <paramref name="authors"/> (email, a commit of theirs) that has a picture.
    /// <paramref name="repoRoot"/> is the checkout whose GitHub remote the lookup asks.
    /// </summary>
    public Dictionary<string, byte[]> Fetch(string repoRoot, IReadOnlyList<(string Email, string Oid)> authors)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            long now = _now().ToUnixTimeSeconds();
            var index = LoadIndex();
            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ask = new List<(string Email, string Oid)>();
            foreach (var (email, oid) in authors)
            {
                if (GraphAvatars.NoReplyUrl(email) is { } direct) urls[email] = direct;
                else if (index.TryGetValue(GraphAvatars.CacheKey(email), out var e) && now - e.Checked < Fresh.TotalSeconds)
                {
                    if (e.Url.Length > 0) urls[email] = e.Url;
                }
                else ask.Add((email, oid));
            }

            if (ask.Count > 0 && GraphAvatars.GitHubRepo(GitCheckoutScanner.ReadRemotes(repoRoot)) is { } repo
                && _graphql(GraphAvatars.BuildQuery(repo, ask)) is { } response)
            {
                foreach (var (email, url) in GraphAvatars.ParseResponse(response, ask))
                {
                    index[GraphAvatars.CacheKey(email)] = new Entry(url ?? "", now);
                    if (url is not null) urls[email] = url;
                }
                SaveIndex(index);
            }

            foreach (var (email, url) in urls)
                if (Image(GraphAvatars.CacheKey(email), url, now) is { } bytes)
                    images[email] = bytes;
        }
        catch
        {
            // Best effort: whatever was found so far still shows; the rest keep their initials.
        }
        return images;
    }

    // The cached image when it's fresh and from the same URL (a changed avatar has a new URL), else a new download.
    private byte[]? Image(string key, string url, long now)
    {
        if (!GraphAvatars.IsAvatarUrl(url)) return null;
        string file = Path.Combine(_dir, key + ".img"), from = file + ".url";
        try
        {
            if (File.Exists(file) && File.Exists(from) && File.ReadAllText(from) == url
                && now - new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() < Fresh.TotalSeconds)
                return File.ReadAllBytes(file);
        }
        catch
        {
        }
        if (_download(url) is not { Length: > 0 and <= MaxBytes } bytes) return null;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(file, bytes);
            File.WriteAllText(from, url);
        }
        catch
        {
        }
        return bytes;
    }

    private string IndexPath => Path.Combine(_dir, "index.json");

    internal Dictionary<string, Entry> LoadIndex()
    {
        lock (IndexLock)
        {
            try
            {
                if (File.Exists(IndexPath)
                    && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexPath)) is { } d)
                    return d;
            }
            catch
            {
            }
            return new Dictionary<string, Entry>();
        }
    }

    // Written whole to a temp file and moved over, so a crash or a second window never leaves half an index.
    private void SaveIndex(Dictionary<string, Entry> index)
    {
        lock (IndexLock)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(index));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch
            {
            }
        }
    }

    private static byte[]? Download(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxBytes) return null;
            using var stream = resp.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxBytes) return null;
            }
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // gh as GitHubAlertsClient runs it: resolved to an absolute path, stdin its own pipe, UTF-8 both ways.
    private static string? RunGhGraphql(string body)
    {
        if (ExecutableResolver.Find("gh") is null) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutableResolver.Resolve("gh"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
            };
            foreach (var a in (ReadOnlySpan<string>)["api", "graphql", "--input", "-"]) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            proc.StandardInput.Write(body);
            proc.StandardInput.Close();
            if (!proc.WaitForExit(GhTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            // gh exits non-zero when GraphQL reports errors but still prints the partial data, which is usable.
            return stdout.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
