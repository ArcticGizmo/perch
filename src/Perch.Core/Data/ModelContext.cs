using System.Globalization;
using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>How a session's context window was worked out, best evidence first. Surfaced in the
/// overlay's thermometer tooltip so a window that reads wrong can be diagnosed on the spot.</summary>
public enum ContextWindowSource
{
    /// <summary>Nothing named the model — the standard window, assumed.</summary>
    Assumed,
    /// <summary>A <c>/model</c> confirmation line in the transcript named the variant outright.</summary>
    ModelLine,
    /// <summary>The configured model id carried the <c>[1m]</c> opt-in marker.</summary>
    ConfiguredOptIn,
    /// <summary>The running model id, mapped by family + generation.</summary>
    ModelId,
    /// <summary>A prompt the session actually sent was too big for the window we'd assumed.</summary>
    Observed,
}

/// <summary>A resolved context window: its size, the model it belongs to, and where the size came
/// from. See <see cref="ModelContext.Resolve"/>.</summary>
public readonly record struct ContextWindowInfo(
    int Tokens,                  // the window size to measure fill against
    string? Model,               // best label for the model — a display name if we have one, else its id
    ContextWindowSource Source   // which signal decided Tokens
);

/// <summary>
/// Everything a session's files can tell us about the model it's running, gathered by
/// <see cref="TranscriptReader"/> and ranked by <see cref="ModelContext.Resolve"/>. All optional.
/// </summary>
public readonly record struct ContextEvidence(
    string? ModelLineName = null,      // display name from the newest /model confirmation line
    string? ModelIdSinceLine = null,   // newest message.model recorded *after* that line, if any
    string? RunningModelId = null,     // newest message.model anywhere in the transcript
    string? ConfiguredModelId = null,  // the "model" field from settings.json
    long MaxObservedPrompt = 0         // largest prompt the session has sent, all input buckets summed
);

/// <summary>
/// Resolves the context-window size (in tokens) of the model a session is running.
///
/// This is harder than it looks, because nothing in <c>~/.claude</c> states the window outright. The
/// transcript's <c>message.model</c> is <b>variant-stripped</b>: a session on the 1M beta records a bare
/// <c>claude-opus-5</c>, exactly like a session on the 200k variant. So we layer the evidence, strongest
/// first (see <see cref="Resolve"/>):
///
/// <list type="number">
/// <item>The <c>/model</c> confirmation line in the transcript — the one place the variant is spelled out
///   in words (<c>"Kept model as ESC[1mOpus 5 (1M context)ESC[22m"</c>).</item>
/// <item><c>settings.json</c>'s <c>model</c> id, which unlike the transcript <b>keeps</b> the opt-in
///   marker (<c>"opus[1m]"</c>) — the only persisted record that the 1M variant was chosen.</item>
/// <item>The running model id, mapped by <b>family + generation</b> rather than a table of exact names,
///   so a new release lands on a sensible window without a code change.</item>
/// <item>The tokens the session has actually sent. The API rejects a prompt larger than the window, so a
///   260k prompt is <i>proof</i> the window isn't 200k — this ratchet corrects everything above it and is
///   what stops a brand-new model from reading as 200k until someone updates this file.</item>
/// </list>
/// </summary>
internal static class ModelContext
{
    /// <summary>Standard context window. Fallback for any unrecognised model.</summary>
    public const int DefaultWindow = 200_000;

    /// <summary>Extended (1M-token) context window — the "(1M context)" / "[1m]" variant.</summary>
    public const int ExtendedWindow = 1_000_000;

    // The verbs Claude Code uses when reporting the session's model: "Set model to …" on a switch,
    // "Kept model as …" when /model closes on the model already running. Both state the current model,
    // so both are authoritative.
    private static readonly string[] ModelLineVerbs = ["Set model to ", "Kept model as "];

    // Matches the human display name between the ANSI bold-on (ESC[1m) and bold-off (ESC[22m) markers.
    //  is the ESC character; JSON parsing turns the transcript's  escapes into real ESC bytes
    // before we see the content string.
    private static readonly Regex ModelLineRegex = new(
        "(?:Set model to|Kept model as) \\[1m(?<name>.*?)\\[22m",
        RegexOptions.Compiled);

    // The 1M marker in either of its two spellings: "(1M context)" in a display name, "[1m]" in a model
    // id. One word-bounded "1m" covers both, and won't fire on a family/generation id like
    // "claude-opus-5" or on a stray unstripped bold marker ("1mSonnet" has no boundary after the m).
    private static readonly Regex ExtendedMarkerRegex = new(
        @"\b1m\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Family and (optional) generation out of any model string, id or display name:
    //   "claude-opus-5[1m]" → (opus, 5)    "Opus 4.8" → (opus, 4.8)
    //   "claude-haiku-4-5-20251001" → (haiku, 4.5)     "sonnet" → (sonnet, null)
    private static readonly Regex FamilyRegex = new(
        @"\b(?<family>opus|sonnet|haiku|fable)\b[\s\-]*(?<gen>\d+(?:[.\-]\d+)?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Strips ANSI SGR escape sequences (ESC[…m) and trims whitespace.
    private static readonly Regex AnsiRegex = new("\\[[0-9;]*m", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the model display name from a <c>/model</c> confirmation line, or returns null if the
    /// text is not one. Handles both the ANSI-marked form and a plain-text fallback, stripping the
    /// known trailing clauses ("for this session…", "and saved…", "· Draws from usage credits").
    /// </summary>
    public static string? ParseDisplayName(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        // Primary path: ANSI bold markers present (the common case from real terminals).
        var m = ModelLineRegex.Match(content);
        if (m.Success)
            return Clean(m.Groups["name"].Value);

        // Fallback: strip ANSI codes and parse the plain text after whichever verb appears.
        foreach (var prefix in ModelLineVerbs)
        {
            var i = content.IndexOf(prefix, StringComparison.Ordinal);
            if (i < 0)
                continue;

            var rest = Clean(content[(i + prefix.Length)..]);
            foreach (var stop in new[] { " for this session", " and saved", " ·", "·" })
            {
                var s = rest.IndexOf(stop, StringComparison.Ordinal);
                if (s >= 0)
                    rest = rest[..s];
            }
            rest = rest.Trim();
            if (rest.Length > 0)
                return rest;
        }

        return null;
    }

    /// <summary>True when a line could be a <c>/model</c> confirmation — the cheap substring pre-filter
    /// that lets the transcript scan skip parsing almost every line.</summary>
    public static bool LooksLikeModelLine(string line) =>
        line.Contains("Set model to", StringComparison.Ordinal) ||
        line.Contains("Kept model as", StringComparison.Ordinal);

    /// <summary>
    /// Maps any model string — a display name ("Opus 5 (1M context)"), a full id
    /// ("claude-opus-5[1m]"), or a short alias ("opus") — to its context window in tokens.
    ///
    /// An explicit 1M marker decides it outright. Otherwise the answer comes from the family and
    /// generation, deliberately as a <b>rule</b> rather than a list of known names, so an unreleased
    /// model still resolves sensibly. Where a family's 1M window is an opt-in the marker can't confirm
    /// (a variant-stripped id looks identical either way), we prefer the larger window: over-sizing
    /// under-reports pressure, whereas under-sizing screams that a healthy session is nearly full — and
    /// the <see cref="ContextWindowSource.Observed"/> ratchet in <see cref="Resolve"/> can only correct
    /// upwards anyway.
    /// </summary>
    public static int WindowFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return DefaultWindow;

        if (HasExtendedMarker(model))
            return ExtendedWindow;

        var (family, gen) = ParseFamily(model);
        if (family is null)
            return DefaultWindow;

        // No generation at all means a bare alias ("opus", "sonnet"), which Claude Code resolves to the
        // current model of that family — the newest, so the family's present-day window.
        if (gen is not { } g)
            return family is "opus" or "sonnet" ? ExtendedWindow : DefaultWindow;

        return family switch
        {
            "opus"   => g >= 4 ? ExtendedWindow : DefaultWindow,  // Opus 4.x onwards ships the 1M window
            "sonnet" => g >= 5 ? ExtendedWindow : DefaultWindow,  // Sonnet 5 is 1M by default; 4.x is 200k
            _        => DefaultWindow,                            // no Haiku/Fable ships 1M unmarked
        };
    }

    /// <summary>Resolves the window from every signal a session offers, strongest evidence first.</summary>
    public static ContextWindowInfo Resolve(ContextEvidence e)
    {
        // 1. The /model line spells the variant out in words, so it wins — unless it's stale. Claude Code
        //    can move a session to another family on its own (the Opus weekly limit falling back to
        //    Sonnet, say) without writing a line, so if a *later* record shows another family answering,
        //    the line no longer describes this session.
        if (!string.IsNullOrWhiteSpace(e.ModelLineName))
        {
            var (lineFamily, _) = ParseFamily(e.ModelLineName);
            var (sinceFamily, _) = ParseFamily(e.ModelIdSinceLine);
            if (lineFamily is null || sinceFamily is null || lineFamily == sinceFamily)
                return Promote(
                    new ContextWindowInfo(WindowFor(e.ModelLineName), e.ModelLineName, ContextWindowSource.ModelLine),
                    e.MaxObservedPrompt);
        }

        // 2. settings.json keeps the opt-in marker the transcript throws away, so when it names the family
        //    that's answering, its marker decides. (Different family = the session moved off the
        //    configured default, and the marker says nothing about where it landed.)
        var (runFamily, _) = ParseFamily(e.RunningModelId);
        if (!string.IsNullOrWhiteSpace(e.ConfiguredModelId) && HasExtendedMarker(e.ConfiguredModelId) &&
            (runFamily is null || runFamily == ParseFamily(e.ConfiguredModelId).Family))
            return Promote(
                new ContextWindowInfo(ExtendedWindow, e.RunningModelId ?? e.ConfiguredModelId, ContextWindowSource.ConfiguredOptIn),
                e.MaxObservedPrompt);

        // 3. Family + generation of whichever id we have.
        var id = e.RunningModelId ?? e.ConfiguredModelId;
        if (!string.IsNullOrWhiteSpace(id))
            return Promote(new ContextWindowInfo(WindowFor(id), id, ContextWindowSource.ModelId), e.MaxObservedPrompt);

        return Promote(new ContextWindowInfo(DefaultWindow, null, ContextWindowSource.Assumed), e.MaxObservedPrompt);
    }

    /// <summary>Short human label for a window's provenance, for the thermometer tooltip.</summary>
    public static string SourceLabel(ContextWindowSource source) => source switch
    {
        ContextWindowSource.ModelLine       => "from /model line",
        ContextWindowSource.ConfiguredOptIn => "from settings.json [1m]",
        ContextWindowSource.ModelId         => "from model id",
        ContextWindowSource.Observed        => "from observed usage",
        _                                   => "assumed default",
    };

    /// <summary>True when a model string carries the explicit 1M marker ("(1M context)" or "[1m]").</summary>
    private static bool HasExtendedMarker(string model) => ExtendedMarkerRegex.IsMatch(model);

    /// <summary>
    /// Raises a window to fit a prompt the session actually sent. The rules above are an educated guess
    /// that goes stale with every model release; the token counts in the transcript are ground truth,
    /// because the API refuses a prompt bigger than the window. Rounds up to the next whole million so a
    /// future 2M model lands on 2M rather than on "exactly full".
    /// </summary>
    private static ContextWindowInfo Promote(ContextWindowInfo info, long maxObservedPrompt)
    {
        if (maxObservedPrompt <= info.Tokens)
            return info;

        long fitted = (long)Math.Ceiling(maxObservedPrompt / (double)ExtendedWindow) * ExtendedWindow;
        return info with
        {
            Tokens = (int)Math.Min(fitted, int.MaxValue),
            Source = ContextWindowSource.Observed,
        };
    }

    /// <summary>Pulls the model family and generation out of an id or display name. Either may be null
    /// when the string doesn't carry it.</summary>
    private static (string? Family, double? Gen) ParseFamily(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return (null, null);

        var m = FamilyRegex.Match(model);
        if (!m.Success)
            return (null, null);

        var family = m.Groups["family"].Value.ToLowerInvariant();

        var gen = m.Groups["gen"];
        if (!gen.Success)
            return (family, null);

        // Ids write the generation with hyphens ("opus-4-8"), display names with a dot ("Opus 4.8").
        return double.TryParse(gen.Value.Replace('-', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
            ? (family, g)
            : (family, null);
    }

    private static string Clean(string s) => AnsiRegex.Replace(s, "").Trim();
}
