using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Perch.Avalonia.ViewModels;

namespace Perch.Avalonia.Views;

/// <summary>
/// The live overlay body. Hosted by <c>LiveOverlayWindow</c> at runtime and render-tested in isolation
/// (headless) via the app's <c>render</c> mode. Header drags the host window; a row click focuses that
/// session's terminal through the platform window-activator.
/// </summary>
public partial class OverlayView : UserControl
{
    public OverlayView() => AvaloniaXamlLoader.Load(this);

    // Drag the borderless host window from the header (BeginMoveDrag is on Window, reached via the
    // visual root).
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && this.VisualRoot is Window w)
        {
            w.BeginMoveDrag(e);
        }
    }

    // Click a session row -> bring its terminal/IDE window to the foreground.
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SessionRowViewModel row }
            && int.TryParse(row.Pid, out int pid))
        {
            PlatformServices.WindowActivator.FocusTerminalForProcess(pid, row.ProjectName);
        }
    }
}
