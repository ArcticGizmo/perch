namespace Perch.Data.Control;

/// <summary>
/// The terminal's "<c>\</c> then Enter" line continuation, for the session composers: in the Claude Code TUI a
/// trailing backslash before Enter inserts a newline instead of submitting, so a habit carried over from the
/// terminal shouldn't fire off a half-written prompt here. UI-free so the rule is unit-testable.
/// </summary>
internal static class ComposerNewline
{
    /// <summary>True when the caret sits directly after a <c>\</c> with nothing selected, i.e. Enter should turn
    /// that backslash into a newline rather than submit. Never throws.</summary>
    public static bool IsContinuation(string? text, int caret, int selectionStart, int selectionEnd) =>
        selectionStart == selectionEnd && text is not null && caret > 0 && caret <= text.Length && text[caret - 1] == '\\';
}
