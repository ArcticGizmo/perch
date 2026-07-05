using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.ViewModels;

/// <summary>
/// One session row in the live overlay. Immutable snapshot built from a <see cref="ClaudeSession"/>
/// each scan (the list is rebuilt wholesale, mirroring the WinForms overlay's render-list approach).
/// Maps the session's <see cref="SessionStatus"/> onto the shared palette's status colours.
/// </summary>
public sealed class SessionRowViewModel
{
    public string Pid { get; }
    public string ProjectName { get; }
    public string DisplayName { get; }
    public string StatusText { get; }
    public IBrush StatusBrush { get; }

    public SessionRowViewModel(ClaudeSession s)
    {
        Pid = s.Pid;
        ProjectName = s.ProjectName;
        DisplayName = s.DisplayName;
        (var label, var color) = Describe(s);
        StatusText = label;
        StatusBrush = new SolidColorBrush(color);
    }

    private static (string label, Color color) Describe(ClaudeSession s) => s.Status switch
    {
        SessionStatus.Running        => (s.Activity is { Length: > 0 } a ? a : "running", Palette.Green),
        SessionStatus.NeedsAttention => ("needs attention", Palette.Orange),
        SessionStatus.AwaitingInput  => ("awaiting input", Palette.Yellow),
        _                            => ("idle", Color.FromRgb(100, 116, 139)),
    };
}
