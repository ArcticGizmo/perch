using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Perch.Data;

namespace Perch.Avalonia.ViewModels;

/// <summary>
/// Backing state for the live overlay: the header summary and the current session rows. Updated on the
/// UI thread from each SessionMonitor scan. The list is rebuilt wholesale per scan (small N), which is
/// the simplest faithful port of the WinForms overlay's "rebuild the render list each update" model.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = "Perch — no sessions";

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    public void Update(IReadOnlyList<ClaudeSession> sessions)
    {
        Sessions.Clear();
        foreach (var s in sessions)
            Sessions.Add(new SessionRowViewModel(s));

        Header = sessions.Count switch
        {
            0 => "Perch — no sessions",
            1 => "Perch — 1 session",
            var n => $"Perch — {n} sessions",
        };
    }
}
