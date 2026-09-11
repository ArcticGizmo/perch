using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Perch.Data.Control;

/// <summary>
/// Recovers the images from a resumed user message so the rich UI can show them as thumbnails instead of the
/// bare <c>[Image #N]</c> placeholder the CLI leaves in the text. Claude Code records a pasted image two ways
/// in the transcript: a <c>[Image #N]</c> token in a text block <em>and</em> an <c>image</c> content block,
/// and it caches the file at <c>~/.claude/image-cache/{sessionId}/{N}.{ext}</c>.
///
/// So resolution is: for the k-th <c>image</c> block, take N from the k-th <c>[Image #N]</c> token and look
/// for that cached file (the common case — no work, we just point a chip at the file on disk). Only if the
/// cache file is gone do we fall back to decoding the block's own base64 <c>source.data</c> to a stable temp
/// file (hashed, so a re-resume reuses it). All managed code — this mirrors the send-side base64 encode in
/// <see cref="Perch.Avalonia.Services"/>' <c>PerchSession.BuildImageContents</c>, in reverse.
/// </summary>
internal static partial class TranscriptImages
{
    private static readonly string[] CacheExtensions = ["png", "jpeg", "jpg", "gif", "webp"];

    [GeneratedRegex(@"\[Image #(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ImageTokenRegex();

    /// <summary>Removes the <c>[Image #N]</c> placeholder tokens (and any trailing space) from a display
    /// string once the images are shown as chips instead.</summary>
    public static string StripTokens(string text) =>
        ImageTokenRegex().Replace(text, "").Replace("  ", " ").Trim();

    /// <summary>The image attachments for a user message's <c>content</c> array. <paramref name="text"/> is the
    /// message's joined text (for the <c>[Image #N]</c> numbers), <paramref name="sessionId"/> the id whose
    /// image-cache directory to probe. Empty when there are no image blocks. Never throws.</summary>
    public static IReadOnlyList<MessageAttachment> Extract(JsonArray? content, string? text, string? sessionId)
    {
        if (content is null) return [];

        var numbers = new List<int>();
        if (!string.IsNullOrEmpty(text))
            foreach (Match m in ImageTokenRegex().Matches(text))
                if (int.TryParse(m.Groups[1].Value, out var n)) numbers.Add(n);

        var result = new List<MessageAttachment>();
        int k = 0;
        foreach (var block in content)
        {
            if (TranscriptJson.BlockType(block) != "image") continue;
            int number = k < numbers.Count ? numbers[k] : k + 1;   // token order matches block order
            k++;
            if (Resolve(block, number, sessionId) is { } att) result.Add(att);
        }
        return result;
    }

    private static MessageAttachment? Resolve(JsonNode? block, int number, string? sessionId)
    {
        try
        {
            var source = block?["source"];
            var mediaType = TranscriptJson.AsString(source?["media_type"]);

            // Primary: the file Claude already cached on disk. No decode, no write — just point a chip at
            // it. Probe each config dir's image-cache (a non-primary session's cache lives under its own
            // dir); under a pinned CLAUDE_CONFIG_DIR this is just the primary.
            if (!string.IsNullOrEmpty(sessionId))
            {
                foreach (var cfg in ClaudeConfigSet.Instance.All)
                {
                    var dir = Path.Combine(cfg.ImageCacheDir, sessionId);
                    foreach (var ext in ExtensionOrder(mediaType))
                    {
                        var candidate = Path.Combine(dir, $"{number}.{ext}");
                        if (File.Exists(candidate))
                            return new MessageAttachment { Kind = AttachmentKind.Image, Path = candidate, MediaType = mediaType ?? MediaOf(ext) };
                    }
                }
            }

            // Fallback: decode the transcript's own base64 to a stable temp file (hashed, so re-resume reuses).
            var data = TranscriptJson.AsString(source?["data"]);
            if (!string.IsNullOrEmpty(data))
                return FromBase64(data, mediaType);
        }
        catch { /* best-effort — a chip that can't resolve just doesn't appear */ }
        return null;
    }

    // Decode base64 image bytes to %TEMP%/perch-images/{sha1}.{ext}, written once. Mirrors the send-side
    // encode in reverse; managed only.
    private static MessageAttachment? FromBase64(string base64, string? mediaType)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch { return null; }
        if (bytes.Length == 0) return null;

        var ext = ExtensionOrder(mediaType).First();
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "perch-images");
        var path = Path.Combine(dir, $"{hash}.{ext}");
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
            }
        }
        catch { return null; }
        return new MessageAttachment { Kind = AttachmentKind.Image, Path = path, MediaType = mediaType ?? MediaOf(ext) };
    }

    // The extension(s) to try for a media type, most-likely first. When unknown, try every cache extension.
    private static IEnumerable<string> ExtensionOrder(string? mediaType) => mediaType switch
    {
        "image/png" => ["png"],
        "image/jpeg" => ["jpeg", "jpg"],
        "image/gif" => ["gif"],
        "image/webp" => ["webp"],
        _ => CacheExtensions,
    };

    private static string MediaOf(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        _ => "image/png",
    };
}
