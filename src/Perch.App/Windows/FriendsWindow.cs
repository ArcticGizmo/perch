using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Manage the friend graph: add someone by exact handle, accept/decline incoming requests, and see your
/// friends (and requests you've sent). Reads the graph via <see cref="ISocialClient.GetFriendsAsync"/> and
/// refreshes after each action. Owned by the overlay and reused via <see cref="WindowHost"/>.
/// </summary>
internal sealed class FriendsWindow : Window
{
    private readonly ISocialClient _social;
    private readonly TextBox _addBox;
    private readonly TextBlock _status;
    private readonly StackPanel _requestsPanel;
    private readonly StackPanel _friendsPanel;

    public FriendsWindow(ISocialClient social)
    {
        _social = social;
        Title = "Friends";
        Width = 420;
        Height = 540;
        MinWidth = 320;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        _addBox = SettingsUi.ThemedTextBox("");
        _addBox.Watermark = "handle";
        _addBox.Width = 200;
        var addBtn = SettingsUi.FlatButton("Send request");
        addBtn.Click += async (_, _) => await AddFriend();
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        addRow.Children.Add(new TextBlock { Text = "@", Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center });
        addRow.Children.Add(_addBox);
        addRow.Children.Add(addBtn);

        _status = SettingsUi.BodyText("");
        _requestsPanel = new StackPanel { Spacing = 6 };
        _friendsPanel = new StackPanel { Spacing = 6 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(SettingsUi.SectionTitle("Add a friend"));
        panel.Children.Add(SettingsUi.BodyText("Enter a friend's exact handle to send them a request."));
        panel.Children.Add(addRow);
        panel.Children.Add(_status);
        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(SettingsUi.SectionTitle("Requests"));
        panel.Children.Add(_requestsPanel);
        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(SettingsUi.SectionTitle("Friends"));
        panel.Children.Add(_friendsPanel);

        Content = new ScrollViewer { Content = panel };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }, RoutingStrategies.Tunnel);
    }

    /// <summary>Reload the graph (used by <see cref="WindowHost.ShowOrFocus"/> on re-open).</summary>
    public void RefreshExternal() => _ = Refresh();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _addBox.Focus();
        _ = Refresh();
    }

    private async Task Refresh()
    {
        try
        {
            var friends = await _social.GetFriendsAsync();
            _requestsPanel.Children.Clear();
            _friendsPanel.Children.Clear();

            var incoming = friends.Where(f => f.State == FriendshipState.Incoming).ToList();
            var others = friends.Where(f => f.State is FriendshipState.Accepted or FriendshipState.Pending).ToList();

            if (incoming.Count == 0) _requestsPanel.Children.Add(SettingsUi.BodyText("No pending requests."));
            else foreach (var f in incoming) _requestsPanel.Children.Add(RequestRow(f));

            if (others.Count == 0) _friendsPanel.Children.Add(SettingsUi.BodyText("No friends yet — add one above."));
            else foreach (var f in others.OrderBy(f => f.State).ThenBy(f => f.Profile.Handle)) _friendsPanel.Children.Add(FriendRow(f));
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't load your friends. Please try again."; }
    }

    private Control RequestRow(Friend f)
    {
        var label = new TextBlock { Text = $"@{f.Profile.Handle}", Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center };
        var accept = SettingsUi.FlatButton("Accept");
        accept.Click += async (_, _) => await Respond(f, true);
        var decline = SettingsUi.FlatButton("Decline");
        decline.Click += async (_, _) => await Respond(f, false);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(accept);
        btns.Children.Add(decline);
        return Row(label, btns);
    }

    private Control FriendRow(Friend f)
    {
        var label = new TextBlock { Text = $"@{f.Profile.Handle}", Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center };
        var tag = new TextBlock
        {
            Text = f.State == FriendshipState.Pending ? "requested" : "friend",
            Foreground = Palette.MutedBrush, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        return Row(label, tag);
    }

    private static Control Row(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private async Task Respond(Friend f, bool accept)
    {
        try { await _social.RespondAsync(f.Profile.Id, accept); await Refresh(); }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't update the request."; }
    }

    private async Task AddFriend()
    {
        var handle = _addBox.Text?.Trim() ?? "";
        if (handle.Length == 0) return;
        _status.Text = "Looking up…";
        try
        {
            var profile = await _social.FindByHandleAsync(handle);
            if (profile is null) { _status.Text = $"No user @{handle}."; return; }
            if (profile.Id == _social.Current.Me?.Id) { _status.Text = "That's you!"; return; }
            await _social.SendRequestAsync(profile.Id);
            _status.Text = $"Request sent to @{profile.Handle}.";
            _addBox.Text = "";
            await Refresh();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't send the request. Please try again."; }
    }
}
