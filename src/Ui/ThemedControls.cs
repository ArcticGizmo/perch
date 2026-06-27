namespace Perch.Ui;

/// <summary>
/// Shared factories for the dark-themed WinForms controls the app's windows are built from. The
/// settings, history, stats and quick-link windows each hand-rolled the same flat dark button (and
/// the history/stats windows the same selected-state recolour); this is the one place that styling
/// lives, so every window's buttons read as one app. Callers set per-button size / font / margins
/// after construction — only the palette and flat-chrome are shared here.
/// </summary>
internal static class ThemedControls
{
    /// <summary>
    /// A flat dark button in the app palette: themed fore/back, a flat <see cref="Theme.Border"/>
    /// edge, and the standard hover / pressed highlights. Text only — the caller sizes it, picks a
    /// font, and sets margins to taste.
    /// </summary>
    public static Button FlatButton(string text)
    {
        var b = new Button
        {
            Text      = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Fg,
            BackColor = Theme.ButtonBg,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor        = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
        b.FlatAppearance.MouseDownBackColor = Theme.Border;
        return b;
    }

    /// <summary>
    /// Recolours a <see cref="FlatButton"/> to show selected (on) vs unselected state: an accent fill
    /// with dark text when on, the resting dark palette when off. The shared body of the history
    /// viewer's view/follow toggles and the stats scope buttons.
    /// </summary>
    public static void StyleToggle(Button b, bool on)
    {
        b.BackColor = on ? Theme.Accent : Theme.ButtonBg;
        b.ForeColor = on ? Color.FromArgb(18, 18, 24) : Theme.Fg;
        b.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
    }
}
