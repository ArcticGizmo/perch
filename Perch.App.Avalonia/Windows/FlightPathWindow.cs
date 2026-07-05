using Avalonia.Controls;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The flight-path (session-timeline) window (the Avalonia port of <c>FlightPathForm</c>). A shell for
/// now — the owner-drawn body lands in step 5.6. Sized to match the WinForms window's minimum.
/// </summary>
public sealed class FlightPathWindow : Window
{
    public FlightPathWindow()
    {
        Title = "Flight path";
        Width = 900;
        Height = 620;
        MinWidth = 620;
        MinHeight = 460;
        Background = Palette.FormBgBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Placeholder.For("Flight path");
    }
}
