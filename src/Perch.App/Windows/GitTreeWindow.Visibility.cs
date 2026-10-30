using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Hiding branches from the graph (the rail's eye toggles, a pill's right-click), and hiding new remote branches that
// aren't the user's as they arrive. What's hidden is kept per repo (AppSettings.GitTreeRepos, keyed by the shared git
// dir so every worktree agrees) and handed to the loader, which leaves those branches out of the walk.
internal sealed partial class GitTreeWindow
{
    private string? _repoKey;
    private bool _autoHideRunning;

    // The repo's shared git dir, normalised: the key its visibility is kept under. Cheap (no git process), like the
    // watcher setup it sits beside.
    private static string RepoKey(string cwd) =>
        (GitCheckoutScanner.FindRoot(cwd) is { } root && GitWorktreeScanner.CommonDir(root) is { } common ? Path.GetFullPath(common) : cwd)
            .TrimEnd('\\', '/').ToLowerInvariant();

    private GraphRepoPrefs Prefs()
    {
        var key = _repoKey ?? "";
        if (!_settings.GitTreeRepos.TryGetValue(key, out var prefs))
            _settings.GitTreeRepos[key] = prefs = new GraphRepoPrefs();
        return prefs;
    }

    // A copy, so the background load never reads the live list.
    private IReadOnlySet<string> HiddenSet() =>
        _repoKey is not null && _settings.GitTreeRepos.TryGetValue(_repoKey, out var p)
            ? new HashSet<string>(p.Hidden, StringComparer.Ordinal)
            : new HashSet<string>();

    private void SetHidden(IReadOnlyCollection<GraphRef> refs, bool hide)
    {
        if (refs.Count == 0) return;
        var prefs = Prefs();
        foreach (var r in refs)
        {
            prefs.Hidden.Remove(r.FullName);
            if (hide) prefs.Hidden.Add(r.FullName);
        }
        _settings.Save();
        RefreshStatus(preserveSelection: true);
    }

    // The trunk and anything a worktree has checked out stay visible: hiding them would only make the graph lie.
    private string? WhyNotHide(GraphRef r)
    {
        if (_snap is null) return "";
        if (_snap.Worktrees.FirstOrDefault(w => w.Branch == r.Name) is { } wt) return $"{r.Name} is checked out in {wt.Name}.";
        if (BranchVisibility.TrunkRefs(_snap.Refs, GraphLoader.PrimaryBranch(_snap.Refs)).Contains(r.FullName))
            return $"{r.Name} is the main line of the graph.";
        return null;
    }

    private void ToggleHidden(GraphRef r, bool hide)
    {
        if (hide && WhyNotHide(r) is { } why)
        {
            if (why.Length > 0) ShowToast($"Can't hide it: {why}", "", null);
            return;
        }
        SetHidden([r], hide);
        if (hide) ShowToast($"Hid {r.Name} from the graph", "", () => SetHidden([r], false));
    }

    // Right-click on a pill: check it out, or hide it.
    private void ShowRefMenu(GraphRef r)
    {
        if (_snap is not { } snap) return;
        var items = new List<Control>();
        if (r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch or GraphRefKind.Tag)
            items.Add(Item($"Check out {r.Name}", () => BeginCheckout(GraphCheckout.ForRef(snap, r), _graph.Canvas)));
        if (r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch)
            items.Add(WhyNotHide(r) is { Length: > 0 } why ? Note($"Can't hide: {why}") : Item($"Hide {r.Name} from the graph", () => ToggleHidden(r, true)));
        if (items.Count > 0) ShowMenu(_graph.Canvas, atPointer: true, items.ToArray());
    }

    /// <summary>For the headless render (settings persistence is off there): hides the first remote branch.</summary>
    internal void PreviewHideFirstRemote()
    {
        if (_snap?.Refs.FirstOrDefault(r => r.Kind == GraphRefKind.RemoteBranch && WhyNotHide(r) is null) is { } r)
            SetHidden([r], true);
    }

    // The rail's "⋯" on Local / Remotes: the auto-hide setting and a way back for everything hidden.
    private void ShowVisibilityMenu(Control anchor)
    {
        var items = new List<Control>
        {
            Item((_settings.GitTreeAutoHideOthers ? "✓  " : "     ") + "Hide new remote branches that aren't mine", ToggleAutoHide),
            Note("Yours: it has a commit main doesn't, made with your git user.email or user.name"),
        };
        if (_snap is { } snap && snap.Refs.Where(snap.IsHidden).ToList() is { Count: > 0 } hidden)
            items.Add(Item($"Show all {hidden.Count} hidden branch{(hidden.Count == 1 ? "" : "es")}", () => SetHidden(hidden, false)));
        ShowMenu(anchor, atPointer: true, items.ToArray());
    }

    private void ToggleAutoHide()
    {
        _settings.GitTreeAutoHideOthers = !_settings.GitTreeAutoHideOthers;
        // Turning it on counts what's there now as seen: only branches that arrive later are candidates.
        if (_settings.GitTreeAutoHideOthers && _snap is { } snap)
            BranchVisibility.SeedSeen(Prefs(), snap.Refs);
        _settings.Save();
        ShowToast(_settings.GitTreeAutoHideOthers
            ? "New remote branches that aren't yours will be hidden as they arrive"
            : "New remote branches will show as they arrive", "", null);
    }

    // After each load: remote branches Perch hasn't seen before are checked against the user's commits (off the UI
    // thread, one git call), and the ones that aren't theirs are hidden. The first look at a repo only records what's there.
    private void ApplyAutoHide(GraphSnapshot? snap)
    {
        if (snap is null || !_settings.GitTreeAutoHideOthers || _autoHideRunning || _repoKey is null) return;
        var prefs = Prefs();
        if (prefs.Seen is null)
        {
            BranchVisibility.SeedSeen(prefs, snap.Refs);
            _settings.Save();
            return;
        }
        var unseen = BranchVisibility.Unseen(snap.Refs, prefs);
        if (unseen.Count == 0) return;

        _autoHideRunning = true;
        var key = _repoKey;
        var root = snap.Root;
        var trunk = BranchVisibility.TrunkRefs(snap.Refs, GraphLoader.PrimaryBranch(snap.Refs));
        System.Threading.Tasks.Task.Run(() => BranchVisibility.Mine(root, unseen, trunk)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _autoHideRunning = false;
                if (!t.IsCompletedSuccessfully || !IsVisible || key != _repoKey) return;
                var p = Prefs();
                p.Seen ??= [];
                p.Seen.AddRange(unseen.Select(r => r.FullName));
                var others = unseen.Where(r => !t.Result.Contains(r.FullName)).ToList();
                foreach (var r in others) p.Hidden.Add(r.FullName);
                _settings.Save();
                if (others.Count == 0) return;
                ShowToast(others.Count == 1
                        ? $"Hid {others[0].Name}: a new branch that isn't yours"
                        : $"Hid {others.Count} new branches that aren't yours",
                    "", () => SetHidden(others, false));
                RefreshStatus(preserveSelection: true);
            });
        });
    }
}
