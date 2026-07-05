using Avalonia.Controls;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-history viewer (the Avalonia port of <c>HistoryViewerForm</c>). A shell for now — the
/// session list + transcript pane land in step 5.7. Sized to match the WinForms window's minimum.
/// </summary>
public sealed class HistoryWindow : Window
{
    public HistoryWindow()
    {
        Title = "Session history";
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 400;
        Background = Palette.FormBgBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Placeholder.For("Session history");
    }

    /// <summary>Opens the viewer on a specific session (from the overlay's "View history" / a plugin
    /// jump). No-op until the list is built in 5.7; kept so the wiring can land now.</summary>
    public void ShowSession(string? sessionId) { /* 5.7: select + scroll to the session */ }

    /// <summary>Feeds the current live sessions so the viewer can mark/refresh them. No-op until 5.7.</summary>
    public void SetActiveSessions(IReadOnlyList<ClaudeSession> sessions) { /* 5.7 */ }
}
