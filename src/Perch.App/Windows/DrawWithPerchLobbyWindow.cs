using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Perch.Avalonia.Theming;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The lobby for Draw with Perch: start a new game against any accepted friend, or resume one of your games.
/// Reads the friend graph and game/invite lists via <see cref="ISocialClient"/> and hands the chosen
/// <see cref="DrawGameSummary"/> back through <c>onPlay</c> (the caller opens the board). Templated controls +
/// async refresh, mirroring <see cref="Connect4LobbyWindow"/>.
/// </summary>
internal sealed class DrawWithPerchLobbyWindow : Window
{
    private readonly ISocialClient _social;
    private readonly Action<DrawGameSummary> _onPlay;
    private readonly Action<Profile> _onChallenge;
    private readonly TextBlock _status;
    private readonly StackPanel _gamesPanel;
    private readonly StackPanel _requestsPanel;
    private readonly StackPanel _friendsPanel;

    public DrawWithPerchLobbyWindow(ISocialClient social, Action<DrawGameSummary> onPlay, Action<Profile> onChallenge)
    {
        _social = social;
        _onPlay = onPlay;
        _onChallenge = onChallenge;
        Title = "Draw with Perch";
        Width = 420;
        Height = 520;
        MinWidth = 320;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        _status = SettingsUi.BodyText("");
        _gamesPanel = new StackPanel { Spacing = 6 };
        _requestsPanel = new StackPanel { Spacing = 6 };
        _friendsPanel = new StackPanel { Spacing = 6 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(SettingsUi.SectionTitle("Your games"));
        panel.Children.Add(_gamesPanel);
        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(SettingsUi.SectionTitle("Challenges"));
        panel.Children.Add(_requestsPanel);
        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(SettingsUi.SectionTitle("Start a game"));
        panel.Children.Add(SettingsUi.BodyText("Challenge a friend — you draw first, then they accept and guess."));
        panel.Children.Add(_friendsPanel);
        panel.Children.Add(_status);

        Content = new ScrollViewer { Content = panel };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }, RoutingStrategies.Tunnel);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = Refresh();
    }

    private async Task Refresh()
    {
        try
        {
            var games = await _social.GetDrawGamesAsync();
            var requests = await _social.GetDrawRequestsAsync();
            var friends = await _social.GetFriendsAsync();
            var accepted = friends.Where(f => f.State == FriendshipState.Accepted).ToList();
            var meId = _social.Current.Me?.Id ?? Guid.Empty;

            _gamesPanel.Children.Clear();
            _requestsPanel.Children.Clear();
            _friendsPanel.Children.Clear();

            if (games.Count == 0) _gamesPanel.Children.Add(SettingsUi.BodyText("No games yet — challenge a friend below."));
            else foreach (var g in games) _gamesPanel.Children.Add(GameRow(g, meId));

            if (requests.Count == 0) _requestsPanel.Children.Add(SettingsUi.BodyText("No pending challenges."));
            else foreach (var r in requests) _requestsPanel.Children.Add(RequestRow(r, meId));

            if (accepted.Count == 0) _friendsPanel.Children.Add(SettingsUi.BodyText("No friends yet — add some in the Friends window."));
            else foreach (var f in accepted.OrderBy(f => f.Profile.Handle)) _friendsPanel.Children.Add(FriendRow(f));
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't load your games. Please try again."; }
    }

    private Control GameRow(DrawGameSummary g, Guid meId)
    {
        var opp = g.Opponent(meId);
        var label = new TextBlock
        {
            Text = $"@{opp?.Handle ?? "?"}  ·  {Describe(g, meId)}  ·  {g.MyScore(meId)}–{g.TheirScore(meId)}",
            Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        var open = SettingsUi.FlatButton(g.Status == DrawGameStatus.InProgress ? "Open" : "View");
        open.Click += (_, _) => { _onPlay(g); Close(); };
        var remove = SettingsUi.FlatButton("Remove");
        remove.Click += async (_, _) => await RemoveGame(g);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(open);
        right.Children.Add(remove);
        return Row(label, right);
    }

    private Control FriendRow(Friend f)
    {
        var label = new TextBlock { Text = $"@{f.Profile.Handle}", Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center };
        var invite = SettingsUi.FlatButton("Challenge");
        invite.Click += (_, _) => { _onChallenge(f.Profile); Close(); };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(invite);
        return Row(label, right);
    }

    private Control RequestRow(DrawRequest r, Guid meId)
    {
        bool incoming = r.IsIncoming(meId);
        var label = new TextBlock
        {
            Text = incoming ? $"@{r.Requester.Handle} challenged you" : $"you challenged @{r.Addressee.Handle}",
            Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (incoming)
        {
            var accept = SettingsUi.FlatButton("Accept");
            accept.Click += async (_, _) => await AcceptInvite(r);
            var decline = SettingsUi.FlatButton("Decline");
            decline.Click += async (_, _) => await DeclineInvite(r, "Declined the challenge.");
            right.Children.Add(accept);
            right.Children.Add(decline);
        }
        else
        {
            var cancel = SettingsUi.FlatButton("Cancel");
            cancel.Click += async (_, _) => await DeclineInvite(r, "Cancelled the challenge.");
            right.Children.Add(cancel);
        }
        return Row(label, right);
    }

    private async Task RemoveGame(DrawGameSummary g)
    {
        try { await _social.DeleteDrawGameAsync(g.Id); await Refresh(); }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't remove that game. Please try again."; }
    }

    private async Task AcceptInvite(DrawRequest r)
    {
        try
        {
            var state = await _social.AcceptDrawRequestAsync(r.Id);
            _onPlay(state.Summary);
            Close();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't accept the challenge. Please try again."; }
    }

    private async Task DeclineInvite(DrawRequest r, string done)
    {
        try { await _social.DeclineDrawRequestAsync(r.Id); _status.Text = done; await Refresh(); }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't update the challenge. Please try again."; }
    }

    private static string Describe(DrawGameSummary g, Guid meId) => g.Status switch
    {
        DrawGameStatus.InProgress => g.IsTurnOf(meId)
            ? (g.Phase == DrawPhase.Guess ? "your turn to guess" : "your turn to draw")
            : "their turn",
        _ => "abandoned",
    };

    private static Control Row(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
