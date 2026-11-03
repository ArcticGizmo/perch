using Avalonia.Threading;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Finding commits in the graph (Ctrl+F while the graph shows, or Ctrl+G): every loaded row matching the text is washed,
// Enter / Shift+Enter walk them, and when nothing loaded matches, "Search older history" keeps loading pages until
// something does or the history runs out.
internal sealed partial class GitTreeWindow
{
    private List<int> _findRows = [];
    private bool _searchingOlder;

    /// <summary>For the headless render: the commit search with <paramref name="query"/> typed.</summary>
    internal void PreviewFind(string? query)
    {
        if (query is null) { CloseGoTo(); return; }
        OpenCommitFind();
        _gotoBox.Text = query;
    }

    /// <summary>For the headless render: the busy cover over the graph, with the files pane collapsed.</summary>
    internal void PreviewBusyCollapsed(bool on)
    {
        SetFilesCollapsed(on, persist: false);
        if (on) SetGraphBusy("Checking out feeds…"); else ClearGraphBusy();
    }

    private void OpenCommitFind()
    {
        _gotoBar.IsVisible = true;
        _gotoBox.Focus();
        _gotoBox.SelectAll();
        RunFind();
    }

    // The text changed: recompute the matches and land on the first one at or below the selection.
    private void RunFind()
    {
        RefreshFind();
        if (_findRows.Count == 0) return;
        int sel = _graph.Canvas.SelectedRow;
        int first = _findRows.FirstOrDefault(r => r >= sel, _findRows[0]);
        _graph.Canvas.Flash(first);
        UpdateFindLabel();
    }

    // The matches against what's loaded now (a refresh or a new page changes it), without moving the selection.
    private void RefreshFind()
    {
        if (!_gotoBar.IsVisible || _snap is null) return;
        _findRows = _snap.FindAll(_gotoBox.Text ?? "");
        _graph.Canvas.SetMatches(_findRows.ToHashSet());
        UpdateFindLabel();
    }

    private void StepFind(int direction)
    {
        if (_findRows.Count == 0) { RunFind(); return; }
        int sel = _graph.Canvas.SelectedRow;
        int next = direction > 0
            ? _findRows.FirstOrDefault(r => r > sel, _findRows[0])
            : _findRows.LastOrDefault(r => r < sel, _findRows[^1]);
        _graph.Canvas.Flash(next);
        UpdateFindLabel();
    }

    private void UpdateFindLabel()
    {
        bool blank = string.IsNullOrWhiteSpace(_gotoBox.Text);
        bool more = _snap?.HasMore == true;
        _findOlderBtn.IsVisible = !blank && _findRows.Count == 0 && more && !_searchingOlder;
        if (_searchingOlder) { _gotoLabel.Text = $"Searching older history… ({_snap?.RealCommitCount:N0} commits)"; return; }
        if (blank) { _gotoLabel.Text = ""; return; }
        if (_findRows.Count == 0)
        {
            _gotoLabel.Text = more ? $"No match in the newest {_snap!.RealCommitCount:N0} commits" : "No match";
            return;
        }
        int at = _findRows.IndexOf(_graph.Canvas.SelectedRow);
        _gotoLabel.Text = (at >= 0 ? $"{at + 1} / {_findRows.Count}" : $"{_findRows.Count} matches") + (more ? " loaded" : "");
    }

    // Loads page after page (off the UI thread) until the text matches something or the history ends.
    private async void SearchOlder()
    {
        if (_searchingOlder) return;
        _searchingOlder = true;
        UpdateFindLabel();
        try
        {
            var query = _gotoBox.Text ?? "";
            while (IsVisible && _gotoBar.IsVisible && _snap is { HasMore: true } before && before.FindAll(query).Count == 0)
            {
                GraphSnapshot next;
                try { next = await System.Threading.Tasks.Task.Run(() => GraphLoader.LoadMore(before)); }
                catch (InvalidOperationException) { break; }   // the graph's own paging took this page first
                if (_snap != before) break;   // a refresh replaced it meanwhile
                _snap = next;
                _suppressSelect = true;
                _graph.Canvas.SetSnapshot(next, keepPlace: true);
                _suppressSelect = false;
                _rail.SetSnapshot(next);
                UpdateFindLabel();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);   // let it paint
            }
        }
        finally
        {
            _searchingOlder = false;
        }
        RunFind();
    }
}
