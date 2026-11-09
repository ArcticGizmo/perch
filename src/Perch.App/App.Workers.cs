using Avalonia.Controls;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Remote;

namespace Perch.Avalonia;

// Remote workers (docs/remote-workers-plan.md): the app-side prompts the WorkerHost needs a window for, and what this
// machine shows while paired machines control its sessions (W5).
public partial class App
{
    // Shown in the tray menu only while a controller is connected.
    private NativeMenuItem? _disconnectControllersItem;
    private NativeMenu? _trayMenu;

    /// <summary>Marks each of this machine's own Perch sessions with the paired machines following it, for the overlay
    /// row and the Roost pane. Returns the same list when nothing changes.</summary>
    private IReadOnlyList<ClaudeSession> WithControlledMarks(IReadOnlyList<ClaudeSession> sessions)
    {
        var following = _perchSessions
            .Where(s => s.ControlledFrom.Count > 0 && s.SessionId is not null)
            .ToDictionary(s => s.SessionId!, s => string.Join(", ", s.ControlledFrom), StringComparer.Ordinal);
        if (following.Count == 0 && sessions.All(s => s.ControlledFrom is null)) return sessions;
        return [.. sessions.Select(s =>
        {
            var mark = following.GetValueOrDefault(s.SessionId);
            return s.ControlledFrom == mark ? s : s with { ControlledFrom = mark };
        })];
    }

    // A controller connected, left, or started or stopped following a session (UI thread).
    private void OnControlledChanged()
    {
        _lastSessions = WithControlledMarks(_lastSessions);
        PushOverlayRows();
        RefoldRoost();
        RefreshDisconnectControllersItem();
    }

    private void RefreshDisconnectControllersItem()
    {
        if (_trayMenu is not { } menu) return;
        int n = _workerHost?.ConnectedControllerNames.Count ?? 0;
        _disconnectControllersItem ??= NewDisconnectControllersItem();
        bool shown = menu.Items.Contains(_disconnectControllersItem);
        if (n == 0)
        {
            if (shown) menu.Items.Remove(_disconnectControllersItem);
            return;
        }
        _disconnectControllersItem.Header = n == 1
            ? $"Disconnect {_workerHost!.ConnectedControllerNames[0]}"
            : $"Disconnect {n} remote controllers";
        // Just above Exit's separator, where the session items end.
        if (!shown) menu.Items.Insert(Math.Max(0, menu.Items.Count - 2), _disconnectControllersItem);
    }

    private NativeMenuItem NewDisconnectControllersItem()
    {
        var item = new NativeMenuItem("Disconnect remote controllers");
        item.Click += (_, _) => _workerHost?.DisconnectControllers();
        return item;
    }

    // A machine asked to pair while pairing was open here. The code is what makes it safe: a machine in the middle
    // would show a different one on each side, so the user only allows when both screens match.
    private async Task<bool> AskToPairController(PairingRequest request)
    {
        Window? owner = (Window?)_settings ?? _overlay;
        if (owner is null) return false;
        return await ConfirmDialog.ShowAsync(owner,
            $"Let {request.Name} control this machine?",
            $"{request.Name} should be showing the code {request.Code}. Allow only if it matches exactly. Once paired, it " +
            "can send prompts to this machine's Perch sessions, answer their permission prompts and interrupt them. " +
            "You can remove it any time in Settings → Workers.",
            "Allow", "Don't allow");
    }
}
