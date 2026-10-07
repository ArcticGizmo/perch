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
}
