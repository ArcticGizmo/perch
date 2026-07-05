using Avalonia;
using Avalonia.Controls;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-stats dashboard (the Avalonia port of <c>StatsForm</c>). A shell for now — the
/// owner-drawn dashboard body lands in step 5.5. Sized to match the WinForms window's minimum.
/// </summary>
public sealed class StatsWindow : Window
{
    public StatsWindow()
    {
        Title = "Session stats";
        Width = 620;
        Height = 720;
        MinWidth = 560;
        MinHeight = 520;
        Background = Palette.FormBgBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Placeholder.For("Session stats");
    }
}
