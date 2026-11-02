using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Views;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Author pictures on the graph's nodes: a header toggle (persisted as AppSettings.GitTreeAvatars) and a lookup per
// loaded page. Off, the graph paints its plain dots and nothing is looked up.
internal sealed partial class GitTreeWindow
{
    private readonly AvatarImages _avatarImages = new();
    private Button _avatarBtn = null!;

    private Button BuildAvatarToggle()
    {
        _avatarBtn = new Button
        {
            Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), [DockPanel.DockProperty] = Dock.Right,
            Content = ActionIcon("M8,2.2 A2.9,2.9 0 1 1 7.99,2.2 Z M2.4,14.2 C2.4,10.6 5,9.2 8,9.2 C11,9.2 13.6,10.6 13.6,14.2", 16, 16),
        };
        _avatarBtn.Click += (_, _) =>
        {
            _settings.GitTreeAvatars = !_settings.GitTreeAvatars;
            _settings.Save();
            ApplyAvatars();
        };
        return _avatarBtn;
    }

    private void ApplyAvatars()
    {
        bool on = _settings.GitTreeAvatars;
        _avatarBtn.Opacity = on ? 1 : 0.45;
        ToolTip.SetTip(_avatarBtn, on
            ? "Author pictures on the graph: on. Found through your GitHub sign-in (gh); initials until then. Click to turn off."
            : "Author pictures on the graph: off. Click to show each commit's author on its node.");
        _graph.Canvas.SetAvatars(on ? _avatarImages : null);
        RequestAvatars(_snap);
    }

    /// <summary>For the headless render: whether pictures are on, and how many authors have one.</summary>
    internal string PreviewAvatars => $"on = {_settings.GitTreeAvatars}, pictures = {_avatarImages.Count}";

    // Each page asks about the authors it brought that haven't been asked about yet; nothing while it's off.
    private void RequestAvatars(GraphSnapshot? snap)
    {
        if (snap is not null && _settings.GitTreeAvatars) _avatarImages.Request(snap);
    }
}
