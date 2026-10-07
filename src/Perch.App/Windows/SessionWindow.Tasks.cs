using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

// Background shells and Monitors (docs/background-tasks-plan.md), shown beside the sub-agent chips in the RUNNING row
// above the composer: one amber chip per running task with its glyph, name and elapsed time (a Monitor also counts its
// events). A click brings the card that launched it into view, expanded to its live output; the chip's ■ stops it
// while the session is live. Async agents aren't repeated here: the sub-agent chips already show them. Fed by the
// conversation's own task tracker, so it follows the stream directly, with a 1s tick for the elapsed labels.
internal sealed partial class SessionWindow
{
    private WrapPanel _taskChips = null!;
    private readonly Dictionary<string, TaskChip> _taskChipById = new();
    private DispatcherTimer? _taskTick;
    private bool _renderLive;   // render-only: a process-less sample poses as a live session

    private sealed record TaskChip(Border Root, TextBlock Label, TextBlock Status, Border Stop);

    private void BuildTaskChips()
    {
        _taskChips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    }

    // The tasks a chip is shown for: running shells and Monitors, oldest first.
    private IEnumerable<BackgroundTask> ChipTasks() =>
        Conv.BackgroundTasks.Running.Where(t => t.Kind != BackgroundTaskKind.Agent);

    private void OnBackgroundTaskChanged(BackgroundTask _) => RenderTaskChips();

    private void RenderTaskChips()
    {
        var tasks = ChipTasks().ToList();
        var ids = tasks.Select(t => t.TaskId).ToHashSet();
        foreach (var stale in _taskChipById.Keys.Where(k => !ids.Contains(k)).ToList()) _taskChipById.Remove(stale);
        var roots = new List<Control>(tasks.Count);
        foreach (var t in tasks)
        {
            if (!_taskChipById.TryGetValue(t.TaskId, out var chip)) _taskChipById[t.TaskId] = chip = BuildTaskChip(t.TaskId);
            UpdateTaskChip(chip, t);
            roots.Add(chip.Root);
        }
        if (!_taskChips.Children.SequenceEqual(roots))
        {
            _taskChips.Children.Clear();
            foreach (var r in roots) _taskChips.Children.Add(r);
        }
        RenderAgents();   // the RUNNING row shows while either set has something

        if (tasks.Count > 0)
        {
            if (_taskTick is null)
            {
                _taskTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _taskTick.Tick += (_, _) => TickTaskChips();
            }
            _taskTick.Start();
        }
        else _taskTick?.Stop();
    }

    private void TickTaskChips()
    {
        if (_closed || _taskChipById.Count == 0) { _taskTick?.Stop(); return; }
        foreach (var t in ChipTasks())
            if (_taskChipById.TryGetValue(t.TaskId, out var chip)) chip.Status.Text = BackgroundTaskText.Status(t, DateTime.UtcNow);
    }

    private TaskChip BuildTaskChip(string taskId)
    {
        var glyph = new TextBlock
        {
            FontSize = 12, Foreground = _p.Await, VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = _p.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var status = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
        };
        var stop = new Border
        {
            Padding = new Thickness(5, 0, 1, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center, [ToolTip.TipProperty] = "Stop this background task",
            Child = new TextBlock { Text = "■", FontSize = 10, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center },
        };
        stop.PointerEntered += (_, _) => ((TextBlock)stop.Child!).Foreground = _p.Err;
        stop.PointerExited += (_, _) => ((TextBlock)stop.Child!).Foreground = _p.Faint;
        stop.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;   // not the chip's own click
            if (Conv.BackgroundTasks.Get(taskId) is { } t) _session?.StopTask(t);
        };
        var chip = Pill(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
            Children = { glyph, label, status, stop },
        }, _p.AwaitWash, _p.AwaitLine);
        chip.Margin = new Thickness(0, 2, 8, 2);
        chip.Cursor = new Cursor(StandardCursorType.Hand);
        chip.PointerEntered += (_, _) => chip.BorderBrush = _p.Await;
        chip.PointerExited += (_, _) => chip.BorderBrush = _p.AwaitLine;
        chip.OnLeftClick(() => { CloseAgentView(); _thread.RevealBackgroundTask(taskId); });
        chip.Tag = glyph;
        return new TaskChip(chip, label, status, stop);
    }

    private void UpdateTaskChip(TaskChip chip, BackgroundTask t)
    {
        ((TextBlock)chip.Root.Tag!).Text = t.Kind == BackgroundTaskKind.Monitor ? "◉" : "❯";
        var name = t.Description.Length > 0 ? t.Description : t.TaskId;
        chip.Label.Text = ClipChip(name, 34);
        chip.Status.Text = BackgroundTaskText.Status(t, DateTime.UtcNow);
        chip.Stop.IsVisible = _session is { IsRunning: true } || _renderLive;
        var noun = BackgroundTaskText.Noun(t.Kind);
        chip.Root[ToolTip.TipProperty] = $"Background {noun}: {name}"
            + (t.LastEvent is { } e ? $"\nLast event: {e}" : "")
            + "\nClick to see its output";
    }

    // The window's session changed or went away: its tasks go with it.
    private void ClearTaskChips()
    {
        _taskChipById.Clear();
        _taskChips.Children.Clear();
        _taskTick?.Stop();
    }

    /// <summary>Render-only: shows the RUNNING row for the attached conversation's tasks without waiting on events.</summary>
    internal void ShowTasksForRender() => RenderTaskChips();

    /// <summary>Render-only: the attached conversation, so a scene can pose its tasks' timing.</summary>
    internal SessionConversation ConversationForRender => Conv;

    /// <summary>Render-only: what a RUNNING chip's click does.</summary>
    internal void RevealBackgroundTaskForRender(string taskId) => _thread.RevealBackgroundTask(taskId);
}
