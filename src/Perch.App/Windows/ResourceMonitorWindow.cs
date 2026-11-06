using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Resources;

namespace Perch.Avalonia.Windows;

/// <summary>
/// "Analyse resource usage" (docs/resource-diagnostics-plan.md): Start/Stop a whole-machine recording, then scrub
/// through it. The window owns the recorder, so closing it ends a running recording (the file is kept). Opens on
/// the newest saved recording, if there is one.
/// </summary>
internal sealed class ResourceMonitorWindow : Window
{
    private readonly ResourceTimelineView _timeline = new();
    private readonly ResourceBreakdownView _breakdown = new();
    private readonly StackPanel _findings = new() { Spacing = 6, Margin = new Thickness(12, 0, 12, 12) };
    private readonly Button _startStop;
    private readonly Button _live;
    private readonly Button _recordings;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _clock;

    private ResourceRecorder? _recorder;
    private ResourceRecording? _recording;
    private ResourceAnalysis? _analysis;
    private int _analysedVersion = -1;
    private ResourceContext _context = ResourceContext.None;
    private ResourceContext? _analysedContext;
    private DateTime _contextLoadedAt;
    private bool _contextLoading;
    private int _cursor = -1;
    private bool _follow = true;
    private ResourceFinding? _selected;
    private IReadOnlyList<ResourceFinding>? _shownFindings;
    private SessionTag[] _sessions = [];

    // The Hooks page: built from the current analysis when shown, and again when the analysis changes while it is.
    private readonly HookActivityView _hookView = new();
    private readonly ScrollViewer _hooksScroll;
    private readonly DockPanel _overviewPage;
    private readonly Button _overviewTab;
    private readonly Button _hooksTab;
    private ResourceAnalysis? _hooksBuiltFor;

    public ResourceMonitorWindow()
    {
        Title = "Resource monitor";
        Width = 1180;
        Height = 880;
        MinWidth = 760;
        MinHeight = 560;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _startStop = ToolbarButton("Start recording", ToggleRecording, accent: true);
        _startStop.MinWidth = 140;
        _status = new TextBlock
        {
            Foreground = Palette.FgBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _live = ToolbarButton("Follow live", () =>
        {
            _follow = true;
            Refresh();
        });
        _recordings = ToolbarButton("Recordings ▾", ShowRecordingsMenu);

        _overviewTab = ToolbarButton("Overview", () => ShowPage(hooks: false));
        _hooksTab = ToolbarButton("Hooks", () => ShowPage(hooks: true));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _overviewTab, _hooksTab } };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { _startStop, _status } };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _live, tabs, _recordings },
        };
        var toolbar = new Grid
        {
            Margin = new Thickness(12, 9),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { left, right },
        };
        Grid.SetColumn(right, 1);
        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };

        var timelinePanel = new Border
        {
            Child = _timeline, BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 1),
            [DockPanel.DockProperty] = Dock.Top,
        };

        var breakdownScroll = new ScrollViewer { Content = _breakdown, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var findingsHeader = new TextBlock
        {
            Text = "Points of interest", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Palette.TitleBrush,
            Margin = new Thickness(12, 14, 12, 8), [DockPanel.DockProperty] = Dock.Top,
        };
        var findingsScroll = new ScrollViewer { Content = _findings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var findingsPanel = new Border
        {
            Background = Palette.SurfaceSunkenBrush, BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new DockPanel { Children = { findingsHeader, findingsScroll } },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,360"), Children = { breakdownScroll, findingsPanel } };
        Grid.SetColumn(findingsPanel, 1);

        _overviewPage = new DockPanel { Children = { timelinePanel, body } };
        _hooksScroll = new ScrollViewer { Content = _hookView, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
        _hookView.OpenFileRequested += path =>
        {
            try { PlatformServices.FileRevealer.OpenInEditor(path); } catch { }
        };
        Content = new DockPanel { Children = { toolbarPanel, new Grid { Children = { _overviewPage, _hooksScroll } } } };
        StyleTabs();

        _timeline.CursorMoved += tick =>
        {
            _cursor = tick;
            _follow = _recorder is { IsRecording: true } && _analysis is { } a && tick == a.TickCount - 1;
            _breakdown.SetData(_analysis, _cursor);
            UpdateChrome();
        };
        _timeline.FindingPicked += SelectFinding;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateChrome();

        UpdateChrome();
        RebuildFindings();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ResourceRecordingStore.List().FirstOrDefault() is { } newest)
            Load(newest.FullName);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        StopRecording("closed");
        base.OnClosed(e);
    }

    /// <summary>The live sessions from the latest scan, so the recorder can keep their trees and label them.</summary>
    public void SetSessions(IEnumerable<ClaudeSession> sessions)
    {
        _sessions = sessions
            .Where(s => int.TryParse(s.Pid, out _))
            .Select(s => new SessionTag(int.Parse(s.Pid), s.SessionId, s.DisplayName, s.Cwd,
                (s.AttributedConfigDir ?? s.ConfigDir)?.Root))
            .ToArray();
        _recorder?.SetSessions(_sessions);
    }

    // ── Recording ───────────────────────────────────────────────────────────

    private void ToggleRecording()
    {
        if (_recorder is { IsRecording: true }) StopRecording("stopped");
        else StartRecording();
    }

    private void StartRecording()
    {
        StopRecording("stopped");
        ResourceRecordingStore.Prune(ResourceRecordingStore.Keep - 1);

        var path = ResourceRecordingStore.NewFilePath(DateTime.Now);
        var recording = new ResourceRecording { FilePath = path };
        var recorder = new ResourceRecorder(PlatformServices.ProcessTable, PlatformServices.SystemMetrics, path, Environment.ProcessId)
        {
            HookSource = ResourceContext.HooksFor,
        };
        recorder.SetSessions(_sessions);
        recorder.RecordWritten += record => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_recording, recording)) return;
            recording.Add(record);
            if (record is not (ResourceTick or ResourceEnd)) return;
            // Transcripts and hooks are re-read every half minute while recording: enough to keep the activity lane and
            // the causes current without re-reading every transcript every 5 s.
            if (DateTime.UtcNow - _contextLoadedAt >= TimeSpan.FromSeconds(30) || record is ResourceEnd) LoadContext();
            Refresh();
        });
        recorder.Stopped += _ => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_recorder, recorder)) _recorder = null;
            recorder.Dispose();
            _clock.Stop();
            UpdateChrome();
        });

        _recording = recording;
        _analysis = null;
        _analysedVersion = -1;
        _context = ResourceContext.None;
        _contextLoadedAt = DateTime.UtcNow;
        _cursor = -1;
        _follow = true;
        _selected = null;
        _recorder = recorder;
        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            _recorder = null;
            recorder.Dispose();
            _recording = null;
            Refresh();
            _status.Text = "Couldn't start recording: " + ex.Message;
            return;
        }
        _clock.Start();
        Refresh();
    }

    private void StopRecording(string reason)
    {
        var recorder = _recorder;
        if (recorder is null) return;
        _recorder = null;
        recorder.Stop(reason);
        recorder.Dispose();
        _clock.Stop();
        UpdateChrome();
    }

    private void Load(string path)
    {
        if (_recorder is { IsRecording: true }) return;
        _status.Text = "Loading…";
        Task.Run(() =>
        {
            var recording = ResourceRecording.Load(path);
            var context = ResourceContext.Load(recording);
            return (recording, context, ResourceAnalysis.Build(recording, context));
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || _recorder is { IsRecording: true }) return;
            if (!t.IsCompletedSuccessfully)
            {
                _status.Text = "Couldn't read that recording.";
                return;
            }
            var (recording, context, analysis) = t.Result;
            _recording = recording;
            _context = context;
            _analysedContext = context;
            _analysis = analysis;
            _analysedVersion = recording.Version;
            _follow = false;
            _selected = null;
            _cursor = analysis.Findings.FirstOrDefault()?.PeakTick ?? analysis.TickCount - 1;
            Refresh();
        }));
    }

    // ── View ────────────────────────────────────────────────────────────────

    // Reads the transcripts and hook settings for the current recording off the UI thread, from copies taken here.
    private void LoadContext()
    {
        if (_contextLoading || _recording is not { Ticks.Count: > 0 } recording) return;
        _contextLoading = true;
        _contextLoadedAt = DateTime.UtcNow;
        var sessions = recording.Sessions.ToList();
        var hooks = recording.Hooks.ToList();
        var first = recording.Ticks[0].AtUtc;
        var last = recording.Ticks[^1].AtUtc;
        Task.Run(() => ResourceContext.Load(sessions, hooks, first, last)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _contextLoading = false;
            if (!IsVisible || !ReferenceEquals(_recording, recording) || !t.IsCompletedSuccessfully) return;
            _context = t.Result;
            Refresh();
        }));
    }

    private void Refresh()
    {
        if (_recording is { } r && (r.Version != _analysedVersion || !ReferenceEquals(_context, _analysedContext)))
        {
            _analysis = ResourceAnalysis.Build(r, _context);
            _analysedVersion = r.Version;
            _analysedContext = _context;
        }
        else if (_recording is null)
        {
            _analysis = null;
        }

        int count = _analysis?.TickCount ?? 0;
        if (count == 0) _cursor = -1;
        else if (_follow || _cursor < 0 || _cursor >= count) _cursor = count - 1;

        if (_selected is not null && _analysis is not null && !_analysis.Findings.Contains(_selected)) _selected = null;

        _timeline.SetAnalysis(_recording is null ? null : _analysis);
        _timeline.SetCursor(_cursor);
        _timeline.SetSelected(_selected);
        _breakdown.SetData(_recording is null ? null : _analysis, _cursor);
        RebuildFindings();
        RefreshHooks();
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool recording = _recorder is { IsRecording: true };
        _startStop.Content = recording ? "Stop recording" : "Start recording";
        _live.IsVisible = recording && !_follow;
        _recordings.IsEnabled = !recording;

        int samples = _analysis?.TickCount ?? 0;
        if (recording && _recording?.Header is { } h)
        {
            _status.Text = $"● Recording  ·  {ResourceFormat.Duration(DateTime.UtcNow - h.StartedUtc)}  ·  {samples} samples every 5 s";
            _status.Foreground = Palette.ErrorBrush;
        }
        else if (_recording?.Header is { } loaded && samples > 0)
        {
            var ticks = _recording.Ticks;
            var length = ticks[^1].AtUtc - loaded.StartedUtc;
            _status.Text = $"{loaded.StartedUtc.ToLocalTime():ddd d MMM, HH:mm}  ·  {ResourceFormat.Duration(length)}  ·  {samples} samples"
                           + (_recording.End is null ? "  ·  cut off (Perch closed mid-recording)" : "");
            _status.Foreground = Palette.FgBrush;
        }
        else
        {
            _status.Text = "Not recording";
            _status.Foreground = Palette.MutedBrush;
        }
    }

    private void RebuildFindings()
    {
        var findings = _analysis?.Findings ?? [];
        if (_shownFindings is not null && findings.SequenceEqual(_shownFindings) && _findings.Children.Count > 0)
        {
            foreach (var child in _findings.Children.OfType<Button>())
                child.Background = Equals(child.Tag, _selected) ? Palette.ButtonBgBrush : Brushes.Transparent;
            return;
        }
        _shownFindings = findings;
        _findings.Children.Clear();

        if (findings.Count == 0)
        {
            _findings.Children.Add(new TextBlock
            {
                Text = _analysis is { TickCount: > 0 }
                    ? "Nothing stood out yet. Findings appear here as peaks, saturation, memory pressure, runaway or duplicated processes and orphans are spotted."
                    : "Findings appear here once there is a recording to look at.",
                TextWrapping = TextWrapping.Wrap, Foreground = Palette.MutedBrush, FontSize = 12.5,
            });
            return;
        }

        foreach (var f in findings)
            _findings.Children.Add(FindingCard(f));
    }

    private Button FindingCard(ResourceFinding f)
    {
        var a = _analysis!;
        var ticks = a.Recording.Ticks;
        string when = ticks.Count > f.PeakTick ? ticks[f.PeakTick].AtUtc.ToLocalTime().ToString("HH:mm:ss") : "";
        if (f.EndTick > f.StartTick) when += "  ·  " + ResourceFormat.Duration(a.TickInterval * (f.EndTick - f.StartTick + 1));

        var text = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = f.Title, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = Palette.TitleBrush, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = f.Detail, FontSize = 12, Foreground = Palette.FgBrush, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = when, FontSize = 11, Foreground = Palette.MutedBrush },
            },
        };
        if (f.Advice is { Length: > 0 } advice)
        {
            var adviceBody = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "What you can do", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.AccentBrush },
                    new TextBlock { Text = advice, FontSize = 12, Foreground = Palette.FgBrush, TextWrapping = TextWrapping.Wrap },
                },
            };
            if (LocalPath(f.ActionPath) is { } file)
            {
                var open = new Button
                {
                    Content = f.ActionLabel ?? "Open file", FontSize = 11.5, Padding = new Thickness(8, 3),
                    CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                };
                ToolTip.SetTip(open, file);
                open.Click += (_, e) =>
                {
                    e.Handled = true;
                    try { PlatformServices.FileRevealer.OpenInEditor(file); } catch { }
                };
                adviceBody.Children.Add(open);
            }
            if (f.HookCauseKey is { } hookKey)
            {
                var runs = new Button
                {
                    Content = "See its runs by event", FontSize = 11.5, Padding = new Thickness(8, 3),
                    CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                };
                runs.Click += (_, e) =>
                {
                    e.Handled = true;
                    ShowPage(hooks: true, focusHook: hookKey);
                };
                adviceBody.Children.Add(runs);
            }
            text.Children.Add(new Border
            {
                Child = adviceBody, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(5), Background = OverlayDrawBrush(Palette.Accent, 0.08),
                BorderBrush = OverlayDrawBrush(Palette.Accent, 0.35), BorderThickness = new Thickness(1, 0, 0, 0),
            });
        }
        var bar = new Border
        {
            Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(ResourceTimelineView.SeverityColor(f.Severity)),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { bar, text } };
        Grid.SetColumn(text, 1);

        var button = new Button
        {
            Content = grid, Tag = f, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8),
            Background = Equals(f, _selected) ? Palette.ButtonBgBrush : Brushes.Transparent,
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        };
        button.Click += (_, _) => SelectFinding(f);
        return button;
    }

    private static IBrush OverlayDrawBrush(Color color, double opacity) => Rendering.OverlayDraw.Brush(color, opacity);

    // A finding's file, as a path on this machine: recorded paths have the profile folder masked as "~". Null when it
    // isn't here (a recording from another machine, or a plugin since removed).
    private static string? LocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var local = path.StartsWith('~')
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
        return File.Exists(local) ? local : null;
    }

    private void ShowPage(bool hooks, string? focusHook = null)
    {
        _overviewPage.IsVisible = !hooks;
        _hooksScroll.IsVisible = hooks;
        StyleTabs();
        if (!hooks) return;
        RefreshHooks();
        if (focusHook is null) return;
        _hookView.FocusHook(focusHook);
        // Scroll once the expanded card has been laid out.
        Dispatcher.UIThread.Post(() =>
        {
            if (_hookView.TopOf(focusHook) is { } top) _hooksScroll.Offset = new Vector(0, Math.Max(0, top - 8));
        }, DispatcherPriority.Background);
    }

    private void StyleTabs()
    {
        bool hooks = _hooksScroll.IsVisible;
        foreach (var (tab, on) in new[] { (_overviewTab, !hooks), (_hooksTab, hooks) })
        {
            tab.Background = on ? Palette.AccentBrush : Palette.ButtonBgBrush;
            tab.Foreground = on ? Palette.OnAccentBrush : Palette.FgBrush;
        }
    }

    private void RefreshHooks()
    {
        if (!_hooksScroll.IsVisible || _analysis is not { } a || ReferenceEquals(_hooksBuiltFor, a)) return;
        _hooksBuiltFor = a;
        IReadOnlyList<HookReport> reports;
        try { reports = HookActivity.Build(a, _analysedContext ?? _context); }
        catch { reports = []; }
        _hookView.SetReports(reports, (_analysedContext ?? _context).Activity is { Count: > 0 });
    }

    private void SelectFinding(ResourceFinding f)
    {
        if (_analysis is not { } a) return;
        _selected = f;
        _follow = false;
        _cursor = Math.Clamp(f.PeakTick, 0, Math.Max(0, a.TickCount - 1));
        string? group = f.GroupKey ?? (f.ProcId is { } id ? a.GroupOf(id).Key : null);
        _breakdown.Highlight(group, f.ProcId);
        Refresh();
    }

    private void ShowRecordingsMenu()
    {
        var items = new List<Control>();
        var files = ResourceRecordingStore.List();
        if (files.Count == 0)
            items.Add(new MenuItem { Header = "No saved recordings", IsEnabled = false });
        foreach (var file in files)
        {
            string started = ResourceRecordingStore.StartedAt(file) is { } at ? at.ToString("ddd d MMM, HH:mm:ss") : file.Name;
            bool current = string.Equals(file.FullName, _recording?.FilePath, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = $"{started}  ({ResourceFormat.Bytes(file.Length)})",
                ToggleType = MenuItemToggleType.Radio, IsChecked = current,
            };
            var path = file.FullName;
            item.Click += (_, _) => Load(path);
            items.Add(item);
        }
        items.Add(new Separator());
        var folder = new MenuItem { Header = "Show folder" };
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(ResourceRecordingStore.Directory);
                PlatformServices.FileRevealer.RevealInFileManager(_recording?.FilePath ?? ResourceRecordingStore.Directory);
            }
            catch { }
        };
        items.Add(folder);
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(_recordings);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int count = _analysis?.TickCount ?? 0;
        int? to = e.Key switch
        {
            Key.Left when count > 0 => Math.Max(0, _cursor - 1),
            Key.Right when count > 0 => Math.Min(count - 1, _cursor + 1),
            Key.Home when count > 0 => 0,
            Key.End when count > 0 => count - 1,
            _ => null,
        };
        if (to is { } tick)
        {
            _cursor = tick;
            _follow = _recorder is { IsRecording: true } && tick == count - 1;
            _timeline.SetCursor(tick);
            _breakdown.SetData(_analysis, tick);
            UpdateChrome();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _recorder is not { IsRecording: true })
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private static Button ToolbarButton(string text, Action onClick, bool accent = false)
    {
        var b = new Button
        {
            Content = text, Height = 30, FontSize = 12.5, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (accent)
        {
            b.Background = Palette.AccentBrush;
            b.Foreground = Palette.OnAccentBrush;
        }
        b.Click += (_, _) => onClick();
        return b;
    }
}
