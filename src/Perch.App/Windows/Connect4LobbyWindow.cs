using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Perch.Games;
using Perch.Avalonia.Theming;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The "Play a friend" lobby for online Connect 4: start a new game against any accepted friend, or resume one
/// of your in-progress / recent games. Reads the friend graph and game list via <see cref="ISocialClient"/> and
/// hands the chosen <see cref="GameSummary"/> back through the <c>onPlay</c> callback (the caller opens the board
/// window). Templated controls + async refresh, mirroring <see cref="FriendsWindow"/>.
/// </summary>
internal sealed class Connect4LobbyWindow : Window
{
    private readonly ISocialClient _social;
    private readonly Action<GameSummary> _onPlay;
    private readonly TextBlock _status;
    private readonly StackPanel _gamesPanel;
    private readonly StackPanel _requestsPanel;
    private readonly StackPanel _friendsPanel;

    public Connect4LobbyWindow(ISocialClient social, Action<GameSummary> onPlay)
    {
        _social = social;
        _onPlay = onPlay;
        Title = "Play a friend";
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
        panel.Children.Add(SettingsUi.SectionTitle("Invites"));
        panel.Children.Add(_requestsPanel);
        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(SettingsUi.SectionTitle("Start a game"));
        panel.Children.Add(SettingsUi.BodyText("Invite a friend — they accept, then you play red and move first."));
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
            var games = await _social.GetGamesAsync();
            var requests = await _social.GetGameRequestsAsync();
            var friends = await _social.GetFriendsAsync();
            var accepted = friends.Where(f => f.State == FriendshipState.Accepted).ToList();
            var meId = _social.Current.Me?.Id ?? Guid.Empty;

            _gamesPanel.Children.Clear();
            _requestsPanel.Children.Clear();
            _friendsPanel.Children.Clear();

            if (games.Count == 0) _gamesPanel.Children.Add(SettingsUi.BodyText("No games yet — invite a friend below."));
            else foreach (var g in games) _gamesPanel.Children.Add(GameRow(g));

            if (requests.Count == 0) _requestsPanel.Children.Add(SettingsUi.BodyText("No pending invites."));
            else foreach (var r in requests) _requestsPanel.Children.Add(RequestRow(r, meId));

            if (accepted.Count == 0) _friendsPanel.Children.Add(SettingsUi.BodyText("No friends yet — add some in the Friends window."));
            else foreach (var f in accepted.OrderBy(f => f.Profile.Handle)) _friendsPanel.Children.Add(FriendRow(f));
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't load your games. Please try again."; }
    }

    private Control GameRow(GameSummary g)
    {
        var meId = _social.Current.Me?.Id ?? Guid.Empty;
        var opp = g.Opponent(meId);
        var label = new TextBlock
        {
            Text = $"@{opp?.Handle ?? "?"}  ·  {Describe(g, meId)}",
            Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        var open = SettingsUi.FlatButton(g.Status == GameStatus.InProgress ? "Open" : "View");
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
        var invite = SettingsUi.FlatButton("Invite");
        invite.Click += async (_, _) => await SendInvite(f.Profile);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(invite);
        return Row(label, right);
    }

    // A pending invite: one you received (Accept / Decline) or one you sent (awaiting, with Cancel).
    private Control RequestRow(GameRequest r, Guid meId)
    {
        bool incoming = r.IsIncoming(meId);
        var label = new TextBlock
        {
            Text = incoming ? $"@{r.Requester.Handle} invited you" : $"you invited @{r.Addressee.Handle}",
            Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (incoming)
        {
            var accept = SettingsUi.FlatButton("Accept");
            accept.Click += async (_, _) => await AcceptInvite(r);
            var decline = SettingsUi.FlatButton("Decline");
            decline.Click += async (_, _) => await DeclineInvite(r, "Declined the invite.");
            right.Children.Add(accept);
            right.Children.Add(decline);
        }
        else
        {
            var cancel = SettingsUi.FlatButton("Cancel");
            cancel.Click += async (_, _) => await DeclineInvite(r, "Cancelled the invite.");
            right.Children.Add(cancel);
        }
        return Row(label, right);
    }

    private async Task RemoveGame(GameSummary g)
    {
        try
        {
            await _social.DeleteGameAsync(g.Id);
            await Refresh();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't remove that game. Please try again."; }
    }

    private async Task SendInvite(Profile opponent)
    {
        _status.Text = $"Inviting @{opponent.Handle}…";
        try
        {
            await _social.RequestGameAsync(opponent.Id);
            _status.Text = $"Invited @{opponent.Handle} — they'll get a request to accept.";
            await Refresh();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't send the invite. Please try again."; }
    }

    private async Task AcceptInvite(GameRequest r)
    {
        try
        {
            var state = await _social.AcceptGameRequestAsync(r.Id);
            _onPlay(state.Summary);   // jump straight into the new game
            Close();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't accept the invite. Please try again."; }
    }

    private async Task DeclineInvite(GameRequest r, string done)
    {
        try
        {
            await _social.DeclineGameRequestAsync(r.Id);
            _status.Text = done;
            await Refresh();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't update the invite. Please try again."; }
    }

    // How a game reads from the signed-in player's point of view.
    private static string Describe(GameSummary g, Guid meId) => g.Status switch
    {
        GameStatus.InProgress => g.IsTurnOf(meId) ? "your turn" : "their turn",
        GameStatus.Draw => "draw",
        GameStatus.RedWon => g.Seat(meId) == Connect4Disc.Red ? "you won" : "you lost",
        GameStatus.YellowWon => g.Seat(meId) == Connect4Disc.Yellow ? "you won" : "you lost",
        _ => "ended",
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
