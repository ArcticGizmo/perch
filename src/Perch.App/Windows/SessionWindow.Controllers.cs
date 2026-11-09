using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

// The "controlled from" chip (docs/remote-workers-plan.md, W5): while a paired machine follows this session it can send
// prompts, answer permission cards and interrupt, so the bar says who, and a click disconnects them.
internal sealed partial class SessionWindow
{
    private Border _controlledChip = null!;
    private TextBlock _controlledText = null!;

    /// <summary>The user asked to drop the paired machines driving this machine's sessions.</summary>
    public event Action? DisconnectControllersRequested;

    private Border BuildControlledChip()
    {
        _controlledText = new TextBlock { FontSize = 12, Foreground = _p.Brand, VerticalAlignment = VerticalAlignment.Center };
        var chip = Pill(_controlledText, _p.BrandWash, _p.BrandLine);
        chip.IsVisible = false;
        chip.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(chip, "A paired machine is driving this session: it can send prompts, answer permission prompts and " +
                             "interrupt. Click to disconnect it.");
        return chip.OnLeftClick(ShowControlledMenu);
    }

    private void ShowControlledMenu()
    {
        if (_session?.ControlledFrom is not { Count: > 0 } names) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var disconnect = new MenuItem { Header = $"Disconnect {string.Join(", ", names)}" };
        disconnect.Click += (_, _) => DisconnectControllersRequested?.Invoke();
        flyout.Items.Add(disconnect);
        flyout.Items.Add(new MenuItem
        {
            Header = "To keep it out, remove it in Settings → Workers", IsEnabled = false,
        });
        flyout.ShowAt(_controlledChip);
    }

    /// <summary>The headless renderer poses a paired machine driving the sample session: the chip, a prompt it sent, and
    /// the note its answer leaves.</summary>
    internal void PoseControlledForRender(string from)
    {
        if (_session is not { } s) return;
        s.SetControlledFrom([from]);
        s.Conversation.AddUserPrompt("Run the integration tests again.", from: from);
        s.Conversation.AddNote($"{from} allowed Bash");
    }

    private void RefreshControlledChip()
    {
        var names = _session?.ControlledFrom ?? [];
        _controlledChip.IsVisible = names.Count > 0;
        _controlledText.Text = names.Count > 0 ? $"⇄ controlled from {string.Join(", ", names)}" : "";
    }
}
