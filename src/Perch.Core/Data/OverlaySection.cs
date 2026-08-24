namespace Perch.Data;

using System.Text.Json.Serialization;

/// <summary>
/// A reorderable vertical section of the floating overlay panel. The user controls the order these appear
/// in (via the Settings live preview's "Rearrange" mode); it's persisted as <see cref="AppSettings.SectionOrder"/>.
///
/// <para>The header (always top) and the outage status bar (always bottom) are deliberately <em>not</em>
/// members — they are fixed chrome that anchor the panel. The daemon-worker strip rides inside
/// <see cref="Sessions"/> (it draws directly under the session rows) and the "sign in to Social" prompt rides
/// inside <see cref="Friends"/>, so each stays a single movable unit.</para>
///
/// <para>Persisted by <em>name</em> (see the converter) so the member order can change without breaking an
/// older settings file, and an unknown/missing name is dropped by <see cref="OverlaySectionOrder.Normalize"/>.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlaySection
{
    /// <summary>The whole-machine CPU + RAM strip, just under the header.</summary>
    SystemInfo,
    /// <summary>The rate-limit + monthly-spend usage bars.</summary>
    ClaudeMetrics,
    /// <summary>The launcher icon strip (quick links + the scratch-pad note button).</summary>
    QuickLinks,
    /// <summary>The Hypertree branch stack.</summary>
    Hypertree,
    /// <summary>The user's own to-do section.</summary>
    Todo,
    /// <summary>The session rows (and, directly beneath them, the daemon-worker strip).</summary>
    Sessions,
    /// <summary>The Social friends region (or the "sign in to Social" prompt when signed out).</summary>
    Friends,
    /// <summary>The now-playing media transport strip.</summary>
    Media,
    /// <summary>The microphone-presence ("on a call") strip.</summary>
    Call,
}

/// <summary>
/// The canonical default order of the overlay's movable sections, plus <see cref="Normalize"/>, which reconciles
/// a persisted (possibly stale or partial) order back to a complete, de-duplicated list. Pure and UI-free so the
/// head and the tests share one source of truth.
/// </summary>
public static class OverlaySectionOrder
{
    // The default top-to-bottom order (header sits above these, the outage bar below them).
    private static readonly OverlaySection[] DefaultArr =
    {
        OverlaySection.SystemInfo,
        OverlaySection.ClaudeMetrics,
        OverlaySection.QuickLinks,
        OverlaySection.Hypertree,
        OverlaySection.Todo,
        OverlaySection.Sessions,
        OverlaySection.Friends,
        OverlaySection.Media,
        OverlaySection.Call,
    };

    /// <summary>The default section order — every movable section, once, top to bottom.</summary>
    public static IReadOnlyList<OverlaySection> Default => DefaultArr;

    /// <summary>
    /// Turns a persisted order into a complete, valid one: keeps the saved order but drops unknown values and
    /// duplicates, then splices in any section the saved list is missing at its natural spot (right after the
    /// last of its default-predecessors that's already present). A <c>null</c>/empty input yields
    /// <see cref="Default"/>. This self-heals a settings file written by an older/newer version — a section
    /// added later simply appears in a sensible place rather than being lost or dumped at the end.
    /// </summary>
    public static IReadOnlyList<OverlaySection> Normalize(IEnumerable<OverlaySection>? saved)
    {
        var known = new HashSet<OverlaySection>(DefaultArr);
        var result = new List<OverlaySection>(DefaultArr.Length);
        var seen = new HashSet<OverlaySection>();

        if (saved != null)
            foreach (var s in saved)
                if (known.Contains(s) && seen.Add(s))
                    result.Add(s);

        // Splice each still-missing default member in at its natural position.
        foreach (var s in DefaultArr)
        {
            if (seen.Contains(s)) continue;
            result.Insert(InsertionIndexFor(s, result, seen), s);
            seen.Add(s);
        }

        return result;
    }

    /// <summary>Whether <paramref name="order"/> is exactly the default order.</summary>
    public static bool IsDefault(IEnumerable<OverlaySection>? order)
        => Normalize(order).SequenceEqual(DefaultArr);

    // Where a missing section belongs: just after the last of its earlier-in-default siblings that's already
    // in the list (or the front, if none are present).
    private static int InsertionIndexFor(OverlaySection s, List<OverlaySection> result, HashSet<OverlaySection> seen)
    {
        int di = Array.IndexOf(DefaultArr, s);
        int insertAfter = -1;
        for (int i = 0; i < di; i++)
            if (seen.Contains(DefaultArr[i]))
            {
                int pos = result.IndexOf(DefaultArr[i]);
                if (pos > insertAfter) insertAfter = pos;
            }
        return insertAfter + 1;
    }
}
