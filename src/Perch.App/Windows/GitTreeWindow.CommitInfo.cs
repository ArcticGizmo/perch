using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// The details above a commit's (or stash's) files: who wrote it and when, who committed it if that differs, its
// co-authors, and the whole message, which the graph row only shows the subject of. The row's own author, date and
// subject show at once; the full details (one `git show -s`) replace them when they arrive.
internal sealed partial class GitTreeWindow
{
    private const double CommitMessageMaxHeight = 160;

    private Border _commitInfo = null!;
    private TextBlock _ciAuthor = null!, _ciEmail = null!, _ciWhen = null!, _ciCommitter = null!, _ciCoAuthors = null!;
    private SelectableTextBlock _ciMessage = null!;
    private Border _ciRule = null!;
    private Button _ciHash = null!;
    private string _ciHashValue = "";

    private Control BuildCommitInfo()
    {
        _ciAuthor = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _ciHash = new Button
        {
            FontFamily = Mono, FontSize = 11.5, Padding = new Thickness(6, 1), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), [DockPanel.DockProperty] = Dock.Right,
        };
        ToolTip.SetTip(_ciHash, "Copy the full commit SHA");
        _ciHash.Click += (_, _) =>
        {
            if (_ciHashValue.Length == 0) return;
            Clipboard?.SetTextAsync(_ciHashValue);
            ShowToast($"Copied {Short(_ciHashValue)}", "", null);
        };
        _ciEmail = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        _ciWhen = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        _ciCommitter = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        _ciCoAuthors = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        _ciRule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 6) };
        _ciMessage = new SelectableTextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        _commitInfo = new Border
        {
            IsVisible = false, Padding = new Thickness(12, 8, 12, 10), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Children =
                {
                    new DockPanel { LastChildFill = true, Children = { _ciHash, _ciAuthor } },
                    _ciEmail, _ciWhen, _ciCommitter, _ciCoAuthors, _ciRule,
                    // A long message scrolls inside its own box, so the files below stay in reach.
                    new ScrollViewer
                    {
                        MaxHeight = CommitMessageMaxHeight, Content = _ciMessage,
                        HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    },
                },
            },
        };
        return _commitInfo;
    }

    private void ApplyCommitInfoPalette()
    {
        _commitInfo.Background = _pal.WindowBg;
        _commitInfo.BorderBrush = _pal.Separator;
        _ciAuthor.Foreground = _pal.Title;
        _ciEmail.Foreground = _pal.Muted;
        _ciWhen.Foreground = _pal.Muted;
        _ciCommitter.Foreground = _pal.Muted;
        _ciCoAuthors.Foreground = _pal.Fg;
        _ciRule.Background = _pal.Separator;
        _ciMessage.Foreground = _pal.Fg;
        _ciHash.Foreground = _pal.Muted;
    }

    private void HideCommitInfo() => _commitInfo.IsVisible = false;

    // What the graph row already knows, shown straight away while the full details load.
    private void ShowCommitInfoFrom(GitCommit c)
    {
        _ciHashValue = c.Hash;
        _ciHash.Content = c.ShortHash.Length > 0 ? c.ShortHash : Short(c.Hash);
        _ciAuthor.Text = c.Author.Length > 0 ? c.Author : "Stash";
        _ciEmail.IsVisible = false;
        _ciWhen.Text = Exact(c.Date);
        _ciCommitter.IsVisible = false;
        _ciCoAuthors.IsVisible = false;
        _ciMessage.Text = c.Subject;
        _commitInfo.IsVisible = true;
    }

    private void ShowCommitInfo(CommitInfo info)
    {
        _ciHashValue = info.Hash;
        _ciHash.Content = Short(info.Hash);
        _ciAuthor.Text = info.Author.Name;
        _ciEmail.Text = info.Author.Email;
        _ciEmail.IsVisible = info.Author.Email.Length > 0;
        _ciWhen.Text = $"Authored {Exact(info.Authored)}";
        _ciCommitter.Text = string.Equals(info.Committer.Email, info.Author.Email, StringComparison.OrdinalIgnoreCase)
            ? $"Committed {Exact(info.Committed)}"
            : $"Committed by {info.Committer.Name} <{info.Committer.Email}>, {Exact(info.Committed)}";
        _ciCommitter.IsVisible = info.CommittedSeparately;
        _ciCoAuthors.Text = "Co-authored by\n" + string.Join("\n", info.CoAuthors.Select(p => $"  {p.Name} <{p.Email}>"));
        _ciCoAuthors.IsVisible = info.CoAuthors.Count > 0;
        _ciMessage.Text = CommitInfoReader.Reflow(info.Message);
        _commitInfo.IsVisible = true;
    }

    // Off the UI thread; dropped if another row was picked meanwhile.
    private void LoadCommitInfo(string cwd, string hash)
    {
        System.Threading.Tasks.Task.Run(() => CommitInfoReader.Load(cwd, hash)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result is not { } info) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || _selectedCommitHash != hash) return;
                ShowCommitInfo(info);
            });
        });
    }

    // The machine's own long date and time format, as the graph's When tooltip uses.
    private static string Exact(DateTimeOffset at) =>
        at.LocalDateTime.ToString("F", System.Globalization.CultureInfo.CurrentCulture);
}
