using Avalonia.Controls;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>Key handling the session composers (the session window and the Roost's panes) share.</summary>
internal static class ComposerKeys
{
    /// <summary>The terminal's <c>\</c>+Enter: when the caret follows a backslash, select it and report true. The
    /// caller then leaves the Enter unhandled so the TextBox's own Enter (it accepts returns) types its newline
    /// over the selection, which keeps the caret, undo and the highlight layer in step, unlike setting
    /// <c>Text</c> from code. See <see cref="ComposerNewline"/>.</summary>
    public static bool TryContinueLine(this TextBox box)
    {
        var caret = box.CaretIndex;
        if (box.IsReadOnly || !ComposerNewline.IsContinuation(box.Text, caret, box.SelectionStart, box.SelectionEnd))
            return false;
        box.SelectionStart = caret - 1;
        box.SelectionEnd = caret;
        return true;
    }

    /// <summary>Backspace/Delete against an <c>[Image #N]</c> marker: select the whole marker and report true. As
    /// with <see cref="TryContinueLine"/>, the caller leaves the key unhandled so the TextBox's own Backspace/Delete
    /// removes the selection (keeping undo and the caret in step) — the marker goes as one unit, and with it the
    /// image. Does nothing while text is selected (the user's own selection wins).</summary>
    public static bool TrySelectImageMarker(this TextBox box, bool backward)
    {
        if (box.IsReadOnly || box.SelectionStart != box.SelectionEnd) return false;
        if (ImageMarker.SpanToDelete(box.Text, box.CaretIndex, backward) is not { } span) return false;
        box.SelectionStart = span.Start;
        box.SelectionEnd = span.Start + span.Length;
        return true;
    }
}
