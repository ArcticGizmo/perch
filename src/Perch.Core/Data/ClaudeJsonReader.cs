using System.Text.Json;

namespace Perch.Data;

/// <summary>How a config dir's credential is signed in, as seen in <c>.claude.json</c>
/// <c>oauthAccount</c> — the observation side of Layer 2.</summary>
internal enum SignInState
{
    /// <summary>No <c>oauthAccount</c> (or no readable <c>.claude.json</c>) — logged out.</summary>
    NotSignedIn,
    /// <summary>Signed in, but the account carries no organization (a personal login).</summary>
    Personal,
    /// <summary>Signed in to an organization — <see cref="ClaudeSignIn.Org"/> is populated.</summary>
    Org,
}

/// <summary>A snapshot of a config dir's live sign-in: the <see cref="State"/>, the
/// <see cref="Org"/> when it is org-bound, and the account <see cref="Email"/> when known. Display and
/// diagnostic only — the org (keyed by uuid) is the identity that matters elsewhere.</summary>
internal readonly record struct ClaudeSignIn(SignInState State, Org? Org, string? Email)
{
    public static readonly ClaudeSignIn None = new(SignInState.NotSignedIn, null, null);
}

/// <summary>
/// Reads the <b>live</b> sign-in / org of a config dir from its <c>.claude.json</c> <c>oauthAccount</c>
/// block — the observation side of Layer 2 ("what is this dir signed into <i>right now</i>").
/// </summary>
/// <remarks>
/// Best-effort and never throws: a missing, locked, malformed, or signed-out file reads as
/// <see cref="ClaudeSignIn.None"/> (and <see cref="ReadLiveOrg(string)"/> as <c>null</c>). Opened with
/// <see cref="FileShare.ReadWrite"/> because Claude Code rewrites <c>.claude.json</c> in place. The
/// result is the state <i>at read time</i>; it must be re-read after a <c>/login</c>, never cached as
/// the dir's identity (a config dir has no fixed org — that is the whole Layer-2 premise).
/// </remarks>
internal static class ClaudeJsonReader
{
    /// <summary>Reads the live org from a config dir, or <c>null</c> if not signed in to one / unreadable.</summary>
    public static Org? ReadLiveOrg(ClaudeConfigDir dir) => ReadSignIn(dir).Org;

    /// <summary>Reads the live org from a <c>.claude.json</c> path, or <c>null</c>.</summary>
    public static Org? ReadLiveOrg(string claudeJsonPath) => ReadSignIn(claudeJsonPath).Org;

    /// <summary>Reads the full sign-in snapshot for a config dir, resolving the right <c>.claude.json</c>.</summary>
    /// <remarks>
    /// Claude Code writes <c>.claude.json</c> <b>inside</b> a <c>CLAUDE_CONFIG_DIR</c>, but the <b>default</b>
    /// <c>~/.claude</c> keeps the real config at the legacy <c>~/.claude.json</c> in the parent (home) dir
    /// and may leave an account-less stub inside. So take the inside file when it is signed in, otherwise
    /// fall back to the parent's file.
    /// </remarks>
    public static ClaudeSignIn ReadSignIn(ClaudeConfigDir dir)
    {
        var inside = ReadSignIn(dir.ClaudeJsonFile);
        if (inside.State != SignInState.NotSignedIn)
            return inside;

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir.Root));
        if (!string.IsNullOrEmpty(parent))
        {
            var up = ReadSignIn(Path.Combine(parent, ".claude.json"));
            if (up.State != SignInState.NotSignedIn)
                return up;
        }
        return inside; // None
    }

    /// <summary>Reads the full sign-in snapshot from a <c>.claude.json</c> path;
    /// <see cref="ClaudeSignIn.None"/> when logged out / unreadable.</summary>
    public static ClaudeSignIn ReadSignIn(string claudeJsonPath)
    {
        var bytes = ReadAllBytes(claudeJsonPath);
        return bytes is { Length: > 0 } ? ParseSignIn(bytes) : ClaudeSignIn.None;
    }

    /// <summary>
    /// Reads the sign-in out of a <c>.claude.json</c>'s bytes. Streams the top level with a
    /// <see cref="Utf8JsonReader"/> and materialises only the <c>oauthAccount</c> value, skipping everything
    /// else — the file also holds every project's history, so building a tree of it all (as a full parse did)
    /// was the costly part (review fixes CP23). Malformed JSON reads as <see cref="ClaudeSignIn.None"/>.
    /// </summary>
    internal static ClaudeSignIn ParseSignIn(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith("﻿"u8))   // the UTF-8 BOM, EF BB BF
            json = json[3..];   // Utf8JsonReader refuses a BOM

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return ClaudeSignIn.None;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isAccount = reader.ValueTextEquals("oauthAccount"u8);
                reader.Read();
                if (!isAccount)
                {
                    reader.Skip();
                    continue;
                }
                return reader.TokenType == JsonTokenType.StartObject
                    ? FromAccount(JsonElement.ParseValue(ref reader))
                    : ClaudeSignIn.None;   // null / not an object: logged out
            }
            return ClaudeSignIn.None;
        }
        catch
        {
            return ClaudeSignIn.None;
        }
    }

    private static ClaudeSignIn FromAccount(JsonElement account)
    {
        var email = Str(account, "emailAddress") ?? Str(account, "email");
        var uuid = Str(account, "organizationUuid");
        if (string.IsNullOrWhiteSpace(uuid))
            return new ClaudeSignIn(SignInState.Personal, null, email); // signed in, no org

        var org = new Org(uuid!)
        {
            Name = Str(account, "organizationName"),
            AccountEmail = email,
        };
        return new ClaudeSignIn(SignInState.Org, org, email);
    }

    // A property as text: a string's value, any other scalar's raw JSON (as the old JsonNode.ToString did),
    // null when absent, null or blank.
    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static byte[]? ReadAllBytes(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
