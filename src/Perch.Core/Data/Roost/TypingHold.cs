namespace Perch.Data.Roost;

/// <summary>
/// Tracks whether the user is mid-reply in one of the Roost's pane composers, so the layout can hold back an
/// auto-expand that would shift the grid under their cursor (UI-free, unit-tested; see
/// <see cref="RoostLayout.ResolveSize"/>'s typing hold).
///
/// <para>"Typing" = a composer is focused and a keystroke landed within <see cref="Pause"/>. The hold releases on
/// send, on the composer losing focus, or once the pause elapses — <see cref="ReleasesAt"/> tells the window when to
/// re-run layout so a held pane then expands. Only one composer can be focused at a time, so one key is tracked.</para>
/// </summary>
public sealed class TypingHold
{
    /// <summary>How long after the last keystroke the user still counts as typing.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    private string? _key;
    private DateTime _lastKeystroke;
    private bool _focused;

    /// <summary>A composer in pane <paramref name="key"/> took focus.</summary>
    public void Focused(string key)
    {
        if (_key != key) _lastKeystroke = DateTime.MinValue;   // focus alone isn't typing
        _key = key;
        _focused = true;
    }

    /// <summary>A keystroke in pane <paramref name="key"/>'s composer (implies focus).</summary>
    public void Keystroke(string key, DateTime now)
    {
        _key = key;
        _focused = true;
        _lastKeystroke = now;
    }

    /// <summary>Pane <paramref name="key"/>'s composer lost focus — release at once.</summary>
    public void Blurred(string key)
    {
        if (_key != key) return;
        _focused = false;
        _lastKeystroke = DateTime.MinValue;
    }

    /// <summary>Pane <paramref name="key"/> sent its draft — release at once (focus stays for the next reply).</summary>
    public void Sent(string key)
    {
        if (_key == key) _lastKeystroke = DateTime.MinValue;
    }

    /// <summary>The pane being typed in right now, or null.</summary>
    public string? TypingIn(DateTime now) =>
        _focused && _key is not null && now - _lastKeystroke < Pause ? _key : null;

    /// <summary>True when the user is typing in some pane <em>other</em> than <paramref name="paneKey"/> — the
    /// resolver's <see cref="RoostSizeInputs.TypingElsewhere"/>.</summary>
    public bool TypingElsewhere(string paneKey, DateTime now) => TypingIn(now) is { } k && k != paneKey;

    /// <summary>When the current hold lapses on its own (re-run layout then), or null when nobody is typing.</summary>
    public DateTime? ReleasesAt(DateTime now) => TypingIn(now) is null ? null : _lastKeystroke + Pause;
}
