using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Games;
using Perch.Platform;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Developer testing tool for the Social feature. Signing in with GitHub only gives you one identity, which
/// makes the friends/posts/reactions loop impossible to exercise alone. This window drives a second "puppet"
/// account — an ordinary user you create in the Supabase dashboard (Authentication → Users → Add user, with
/// "Auto Confirm") — via the email/password grant, so from a single machine you can have the puppet befriend
/// you, post, and react, and watch it all land in your real overlay.
///
/// <para>Laid out as a staged flow: <b>Account</b> (sign in) → <b>Handle</b> (claim one, or show the one the
/// login already has) → <b>Actions</b> + <b>Games</b>. Each later stage stays hidden until the one before it is
/// done, and every action reports its progress / error on a status line directly beneath its own button.</para>
///
/// <para>Gated behind <see cref="SocialDebug.Enabled"/> (Debug builds only), so it never appears in a normal
/// install. The puppet keeps its session in an in-memory secret store, so it never touches your real signed-in
/// session.</para>
/// </summary>
internal sealed class DebugSocialWindow : Window
{
    private readonly SupabaseSocialClient _real;
    private readonly Action _refreshReal;
    private readonly Action<string>? _testReaction;
    private readonly Func<string>? _gateStatus;
    private readonly SocialDebugConfig _dbg;
    private SupabaseSocialClient? _puppet;   // non-null only once signed in

    // A ring of reactions so each "React" click uses a different emoji — reactions are one-per-user, so
    // re-clicking the same emoji is a delete-then-insert that leaves the count unchanged and so wouldn't
    // trigger a big-reaction bubble. Cycling guarantees a genuinely new reaction each time.
    private static readonly string[] ReactCycle = ["🔥", "🎉", "😂", "❤️", "👍", "🙌", "😮", "😢"];
    private int _reactIx;

    private readonly List<Window> _c4Windows = new();    // the current pair of Connect 4 boards (yours + puppet's)
    private readonly List<Window> _drawWindows = new();  // the current pair of Draw with Perch boards (yours + puppet's)
    private ComposeWindow? _compose;

    // ── Account stage ──
    private readonly TextBox _email, _password;
    private readonly Control _signedOutForm, _signedInRow;
    private readonly TextBlock _signedInAs;
    private readonly InlineStatus _signInStatus = new();

    // ── Handle stage ──
    private readonly Control _handleSection, _claimForm;
    private readonly TextBox _handle;
    private readonly TextBlock _handleShown;
    private readonly InlineStatus _claimStatus = new();

    // ── Actions + games stages ──
    private readonly Control _actionsSection, _gamesSection;
    private readonly TextBox _requestTarget;
    private readonly InlineStatus _postStatus = new(), _requestStatus = new(), _friendsStatus = new(),
        _reactStatus = new(), _gameStatus = new();
    private readonly StackPanel _friendsList = new() { Spacing = 6 };
    private readonly TextBlock _gateLine;

    private int _game;   // 0 = Connect 4, 1 = Draw with Perch
    private readonly TextBlock _gameBlurb;
    private readonly Button _gameInviteBtn;

    /// <param name="testReaction">Spawns a big-reaction bubble directly (bypassing the network and the
    /// ShowLargeReactions / Do Not Disturb gates), so the animation can be verified in isolation.</param>
    /// <param name="gateStatus">Returns a human-readable line describing the big-reaction gates
    /// (ShowLargeReactions setting, DND) so a failure to fire can be diagnosed.</param>
    public DebugSocialWindow(SupabaseSocialClient real, Action refreshReal, Action<string>? testReaction = null,
        Func<string>? gateStatus = null)
    {
        _real = real;
        _refreshReal = refreshReal;
        _testReaction = testReaction;
        _gateStatus = gateStatus;
        Title = "Social testing (puppet)";
        Width = 500;
        Height = 760;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        // Prefill the puppet credentials from the environment / .env.local (PERCH_SOCIAL_DEBUG_EMAIL / _PASSWORD
        // / _HANDLE) so they don't have to be retyped each run.
        _dbg = SocialDebugConfig.Resolve();

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(SettingsUi.SectionTitle("Social testing tool"));
        panel.Children.Add(SettingsUi.BodyText(
            "Drive a second (puppet) account created in the Supabase dashboard (Auth → Users → Add user, Auto " +
            "Confirm) so you can test the whole social loop from one machine."));

        // ── 1. Account ──────────────────────────────────────────────────────────────
        _email = Field("puppet email", _dbg.Email);
        _password = Field("password", _dbg.Password);
        _password.PasswordChar = '•';
        var signIn = SettingsUi.FlatButton("Sign in as puppet");
        Wire(signIn, _signInStatus, SignIn);
        _password.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = Run(signIn, _signInStatus, SignIn); } };
        _signedOutForm = Stack(
            Row("Email", _email),
            Row("Password", _password),
            Buttons(signIn));

        _signedInAs = new TextBlock { Foreground = Palette.FgBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var signOut = SettingsUi.FlatButton("Sign out");
        Wire(signOut, _signInStatus, SignOut);
        var signedIn = new DockPanel();
        DockPanel.SetDock(signOut, Dock.Right);
        signedIn.Children.Add(signOut);
        signedIn.Children.Add(_signedInAs);
        _signedInRow = signedIn;

        panel.Children.Add(Section("1 · Account", _signedOutForm, _signedInRow, _signInStatus.View));

        // ── 2. Handle ───────────────────────────────────────────────────────────────
        _handle = Field("puppet handle (e.g. testbot)", _dbg.Handle);
        var claim = SettingsUi.FlatButton("Claim handle");
        Wire(claim, _claimStatus, ClaimHandle);
        _claimForm = Stack(
            SettingsUi.BodyText("This login has no handle yet — claim one to unlock the actions below."),
            Row("Handle", _handle),
            Buttons(claim));
        _handleShown = new TextBlock { Foreground = Palette.FgBrush, FontSize = 13 };
        _handleSection = Section("2 · Handle", _claimForm, _handleShown, _claimStatus.View);
        panel.Children.Add(_handleSection);

        // ── 3. Actions ──────────────────────────────────────────────────────────────
        // Post a status — through the same composer the real "Post a status" uses (its own errors show in it).
        var post = SettingsUi.FlatButton("Post a status as puppet…");
        post.Click += (_, _) => OpenCompose();

        // Friend request — defaults to your real handle, the usual target.
        _requestTarget = Field("handle to befriend", _real.Current.Me?.Handle);
        var send = SettingsUi.FlatButton("Send friend request");
        Wire(send, _requestStatus, SendRequest);

        // Friends — the puppet's friend graph + block list, each row with its own actions.
        var refreshFriends = SettingsUi.FlatButton("Refresh");
        Wire(refreshFriends, _friendsStatus, async () => { await RefreshFriends(); return null; });

        // Large emojis — reactions on your latest post, which fire the big-reaction bubble in your overlay.
        _gateLine = new TextBlock { Foreground = Palette.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var react = SettingsUi.FlatButton("React to my latest post");
        Wire(react, _reactStatus, React);
        var unreact = SettingsUi.FlatButton("Remove reaction");
        Wire(unreact, _reactStatus, Unreact);
        var local = SettingsUi.FlatButton("Local bubble (no network)");
        local.Click += (_, _) =>
        {
            var emoji = ReactCycle[_reactIx++ % ReactCycle.Length];
            _testReaction?.Invoke(emoji);
            if (_testReaction is null) _reactStatus.Error("Test hook not wired.");
            else _reactStatus.Ok($"Spawned a local {emoji} bubble (bypasses settings / DND).");
            UpdateGateLine();
        };

        _actionsSection = Section("3 · Actions",
            Sub("Post a status"), Buttons(post), _postStatus.View,
            Sub("Send a friend request"), Row("To", _requestTarget), Buttons(send), _requestStatus.View,
            SubWith("Friends", refreshFriends), _friendsList, _friendsStatus.View,
            Sub("Large emojis"),
            SettingsUi.BodyText("Reacts to your latest post as the puppet (cycling emoji so each is new) — a new " +
                "reaction should float a big bubble up your screen."),
            Buttons(react, unreact, local), _gateLine, _reactStatus.View);
        panel.Children.Add(_actionsSection);

        // ── 4. Games ────────────────────────────────────────────────────────────────
        _gameBlurb = SettingsUi.BodyText("");
        var openBoards = SettingsUi.FlatButton("Open both boards");
        Wire(openBoards, _gameStatus, () => _game == 0 ? StartConnect4() : StartDraw());
        _gameInviteBtn = SettingsUi.FlatButton("");
        Wire(_gameInviteBtn, _gameStatus, () => _game == 0 ? InviteFromPuppet() : ChallengeFromPuppet());
        var picker = SettingsUi.Segmented(["Connect 4", "Draw with Perch"], 0, i => { _game = i; UpdateGameCopy(); _gameStatus.Clear(); });
        _gamesSection = Section("4 · Games", picker, _gameBlurb, Buttons(openBoards, _gameInviteBtn), _gameStatus.View);
        panel.Children.Add(_gamesSection);
        UpdateGameCopy();

        Content = new ScrollViewer { Content = panel };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        UpdateStages();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // With credentials configured, sign the puppet in on open (the result shows under the Sign in button).
        if (_dbg.HasCredentials && _puppet is null) _ = Run(null, _signInStatus, SignIn);
    }

    protected override void OnClosed(EventArgs e)
    {
        _compose?.Close();
        base.OnClosed(e);
    }

    // ── stage gating ────────────────────────────────────────────────────────────────

    // Shows each stage only once the one before it is done: sign in → handle → actions/games.
    private void UpdateStages()
    {
        bool signedIn = _puppet is not null;
        var me = _puppet?.Current.Me;

        _signedOutForm.IsVisible = !signedIn;
        _signedInRow.IsVisible = signedIn;
        _signedInAs.Text = signedIn ? $"Signed in as {_email.Text?.Trim()}" : "";

        _handleSection.IsVisible = signedIn;
        _claimForm.IsVisible = signedIn && me is null;
        _handleShown.IsVisible = me is not null;
        _handleShown.Text = me is null ? "" : $"@{me.Handle}";

        _actionsSection.IsVisible = me is not null;
        _gamesSection.IsVisible = me is not null;
        if (me is not null) UpdateGateLine();
    }

    private void UpdateGameCopy()
    {
        if (_game == 0)
        {
            _gameBlurb.Text = "Opens two boards — yours and the puppet's — for one game, so you can play both sides " +
                "and watch moves sync through the real backend. Befriends the puppet first if needed.";
            _gameInviteBtn.Content = "Invite me (from puppet)";
        }
        else
        {
            _gameBlurb.Text = "The puppet challenges you with a quick doodle; \"Open both boards\" also accepts it so " +
                "your board lands on the guess screen. Befriends the puppet first if needed.";
            _gameInviteBtn.Content = "Challenge me (from puppet)";
        }
    }

    private void UpdateGateLine() => _gateLine.Text = _gateStatus is null ? "" : "Gates: " + _gateStatus();

    // ── account / handle ────────────────────────────────────────────────────────────

    private async Task<string?> SignIn()
    {
        var client = new SupabaseSocialClient(SupabaseConfig.Resolve(), new InMemorySecretStore(), new NoopUrlOpener());
        var state = await client.SignInWithPasswordAsync(_email.Text?.Trim() ?? "", _password.Text ?? "");
        _puppet = client;   // only once the sign-in actually succeeded
        UpdateStages();
        if (state.Me is not null) await RefreshFriends();
        return state.Me is { } me ? $"Signed in — handle @{me.Handle}." : "Signed in — claim a handle below.";
    }

    private async Task<string?> SignOut()
    {
        var p = _puppet;
        _puppet = null;
        _compose?.Close();
        _friendsList.Children.Clear();
        foreach (var s in new[] { _claimStatus, _postStatus, _requestStatus, _friendsStatus, _reactStatus, _gameStatus }) s.Clear();
        UpdateStages();
        if (p is not null) await p.SignOutAsync();
        return "Signed out.";
    }

    private async Task<string?> ClaimHandle()
    {
        var me = await Puppet().ClaimHandleAsync(_handle.Text?.Trim() ?? "");
        UpdateStages();
        await RefreshFriends();
        return $"Claimed @{me.Handle}.";
    }

    // ── actions ─────────────────────────────────────────────────────────────────────

    private void OpenCompose()
    {
        if (_puppet is not { } p) return;
        if (_compose is { } open) { open.Activate(); return; }
        _postStatus.Clear();
        _compose = new ComposeWindow(async (body, mood) =>
        {
            await p.PostAsync(body, mood);
            _postStatus.Ok($"Posted \"{body}\" — it should appear in your overlay shortly.");
            _refreshReal();
        }, p.Current.Me?.MoodEmoji, title: $"Post as @{p.Current.Me?.Handle}");
        _compose.Closed += (_, _) => _compose = null;
        _compose.Show(this);
    }

    private async Task<string?> SendRequest()
    {
        var p = Puppet();
        var handle = _requestTarget.Text?.Trim().TrimStart('@') ?? "";
        if (handle.Length == 0) throw new SocialException("Enter a handle to send the request to.");
        var target = await p.FindByHandleAsync(handle) ?? throw new SocialException($"No user @{handle}.");
        await p.SendRequestAsync(target.Id);
        _refreshReal();
        await RefreshFriends();
        return $"Sent a request to @{target.Handle}. Accept it in your Friends window (the + in the region).";
    }

    // Rebuilds the friends list: every edge in the puppet's graph plus its block list, each row carrying the
    // actions that make sense for its state and its own status line for errors.
    private async Task RefreshFriends()
    {
        var p = Puppet();
        var friends = await p.GetFriendsAsync();
        var blocked = await p.GetBlockedAsync();
        var blockedIds = blocked.Select(b => b.Id).ToHashSet();

        _friendsList.Children.Clear();
        foreach (var f in friends.Where(f => !blockedIds.Contains(f.Profile.Id)).OrderBy(f => f.State))
            _friendsList.Children.Add(FriendRow(f.Profile, f.State));
        foreach (var b in blocked)
            _friendsList.Children.Add(FriendRow(b, FriendshipState.Blocked));
        if (_friendsList.Children.Count == 0)
            _friendsList.Children.Add(SettingsUi.FieldCaption("No friends, requests or blocks yet."));
    }

    private Control FriendRow(Profile who, FriendshipState state)
    {
        var rowStatus = new InlineStatus();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };

        void Act(string label, Func<SupabaseSocialClient, Task> act, string done)
        {
            var b = SettingsUi.FlatButton(label);
            b.Padding = new Thickness(8, 3);
            b.FontSize = 12;
            b.Margin = new Thickness(0, 0, 6, 0);
            // Errors land on this row's own status line; a success refreshes the list (replacing the row), so its
            // confirmation goes on the section's status instead.
            b.Click += async (_, _) =>
            {
                if (await Run(b, rowStatus, async () => { await act(Puppet()); return null; }))
                {
                    _refreshReal();
                    await Run(null, _friendsStatus, async () => { await RefreshFriends(); return done; });
                }
            };
            buttons.Children.Add(b);
        }

        var h = $"@{who.Handle}";
        switch (state)
        {
            case FriendshipState.Incoming:
                Act("Accept", p => p.RespondAsync(who.Id, accept: true), $"Accepted {h}.");
                Act("Reject", p => p.RespondAsync(who.Id, accept: false), $"Rejected {h}.");
                Act("Block", p => p.BlockAsync(who.Id), $"Blocked {h}.");
                break;
            case FriendshipState.Pending:
                Act("Cancel", p => p.RemoveFriendAsync(who.Id), $"Cancelled the request to {h}.");
                Act("Block", p => p.BlockAsync(who.Id), $"Blocked {h}.");
                break;
            case FriendshipState.Accepted:
                Act("Remove", p => p.RemoveFriendAsync(who.Id), $"Removed {h}.");
                Act("Block", p => p.BlockAsync(who.Id), $"Blocked {h}.");
                break;
            case FriendshipState.Blocked:
                Act("Unblock", p => p.UnblockAsync(who.Id), $"Unblocked {h}.");
                break;
        }

        var name = new TextBlock
        {
            Text = h, Foreground = Palette.FgBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stateText = new TextBlock
        {
            Text = state switch
            {
                FriendshipState.Incoming => "wants to be friends",
                FriendshipState.Pending => "request sent",
                FriendshipState.Accepted => "friends",
                _ => "blocked",
            },
            Foreground = Palette.MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var line = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        line.Children.Add(buttons);
        line.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { name, stateText } });

        return new Border
        {
            Background = Palette.ButtonBgBrush, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5),
            Child = new StackPanel { Spacing = 2, Children = { line, rowStatus.View } },
        };
    }

    private async Task<string?> React()
    {
        var (p, me, _) = Players();
        var latest = (await p.GetFeedAsync(50)).FirstOrDefault(x => x.Author.Id == me.Id)
            ?? throw new SocialException($"No visible post by @{me.Handle} — are you accepted friends, and have you posted?");
        // Cycle the emoji so each click is a genuinely new reaction (see ReactCycle) — otherwise a repeat with
        // the same emoji leaves the count unchanged and no big-reaction bubble fires.
        var emoji = ReactCycle[_reactIx++ % ReactCycle.Length];
        await p.ReactAsync(latest.Id, emoji, on: true);
        _refreshReal();
        UpdateGateLine();
        return $"Reacted {emoji} to your latest post.";
    }

    // Clears the puppet's reaction from your latest post (reactions are one-per-user, so this removes whichever
    // emoji it currently holds). Lets you react → remove → react again to re-trigger the big-reaction bubble.
    private async Task<string?> Unreact()
    {
        var (p, me, _) = Players();
        var latest = (await p.GetFeedAsync(50)).FirstOrDefault(x => x.Author.Id == me.Id)
            ?? throw new SocialException($"No visible post by @{me.Handle} — nothing to un-react.");
        await p.ReactAsync(latest.Id, "", on: false);   // on:false removes the puppet's own reaction, if any
        _refreshReal();
        return "Removed the puppet's reaction from your latest post.";
    }

    // ── games ───────────────────────────────────────────────────────────────────────

    // Creates a real-vs-puppet game (befriending first if the two aren't already accepted friends) and opens
    // two online boards side by side — one signed in as you, one as the puppet. Since both clients run in this
    // process against the real backend, you can play both sides and watch each move propagate via the other
    // board's realtime nudge / poll.
    private async Task<string?> StartConnect4()
    {
        var (_, me, pup) = Players();
        GameSummary? game = null;
        await Befriending(me, pup, async () => game = await _real.CreateGameAsync(pup.Id));
        _refreshReal();
        OpenBoards(game!);
        return $"Opened both boards. You (@{me.Handle}) are red and move first; the puppet (@{pup.Handle}) plays in the other window.";
    }

    // Opens both sides of one game (yours + the puppet's), side by side. Closes any previous pair first so a
    // rematch swaps the pair cleanly rather than leaving stale windows. Passed as each board's onRematch hook,
    // so "Rematch" reopens both boards for the new game (not just one side — that was the glitch).
    private void OpenBoards(GameSummary game)
    {
        if (_real.Current.Me is not { } me || _puppet?.Current.Me is not { } pup) return;

        foreach (var w in _c4Windows) { try { w.Close(); } catch { } }
        _c4Windows.Clear();

        var mine = new Connect4Window(_real, me.Id, game, OpenBoards) { WindowStartupLocation = WindowStartupLocation.Manual };
        mine.Position = new PixelPoint(60, 90);
        mine.Title = $"Connect 4 — YOU (@{me.Handle})";
        mine.Show();

        var theirs = new Connect4Window(_puppet, pup.Id, game, OpenBoards) { WindowStartupLocation = WindowStartupLocation.Manual };
        theirs.Position = new PixelPoint(620, 90);
        theirs.Title = $"Connect 4 — PUPPET (@{pup.Handle})";
        theirs.Show();

        _c4Windows.Add(mine);
        _c4Windows.Add(theirs);
    }

    // Has the puppet send you a Connect 4 invite, so the accept/decline flow can be exercised from the overlay's
    // GAMES strip (or the lobby). The puppet drops its opening disc in the centre column as part of the invite
    // (so when you accept, it's already your move) — mirrors the real compose flow.
    private async Task<string?> InviteFromPuppet()
    {
        var (p, me, pup) = Players();
        const int puppetFirstCol = 3;
        await Befriending(me, pup, () => p.RequestGameAsync(me.Id, puppetFirstCol));
        _refreshReal();
        return $"@{pup.Handle} invited you to Connect 4 (opening move played) — accept it from the overlay's GAMES strip or the lobby.";
    }

    // Starts a real-vs-puppet Draw game and opens both boards. Draw has no direct-create, so the puppet challenges
    // you (carrying a seeded doodle) and you accept straight away — leaving your board on the guess screen and the
    // puppet's board waiting.
    private async Task<string?> StartDraw()
    {
        var (p, me, pup) = Players();
        DrawRequest? req = null;
        await Befriending(me, pup, async () => req = await p.RequestDrawGameAsync(
            me.Id, DrawDifficulty.Easy, "cat", DrawGuessing.LetterHint("cat"), PuppetDoodle()));
        var state = await _real.AcceptDrawRequestAsync(req!.Id);
        _refreshReal();
        OpenDrawBoards(state.Summary);
        return $"Opened both boards. The puppet (@{pup.Handle}) drew a {req.Difficulty} word; it's your turn to guess. " +
            "Solve it and you draw next — the puppet's board becomes the guesser.";
    }

    // Opens both sides of one Draw game (yours + the puppet's), side by side. Closes any previous pair first.
    private void OpenDrawBoards(DrawGameSummary game)
    {
        if (_real.Current.Me is not { } me || _puppet?.Current.Me is not { } pup) return;

        foreach (var w in _drawWindows) { try { w.Close(); } catch { } }
        _drawWindows.Clear();

        var mine = new DrawWithPerchWindow(_real, me.Id, game) { WindowStartupLocation = WindowStartupLocation.Manual };
        mine.Position = new PixelPoint(60, 90);
        mine.Title = $"Draw — YOU (@{me.Handle})";
        mine.Show();

        var theirs = new DrawWithPerchWindow(_puppet, pup.Id, game) { WindowStartupLocation = WindowStartupLocation.Manual };
        theirs.Position = new PixelPoint(660, 90);
        theirs.Title = $"Draw — PUPPET (@{pup.Handle})";
        theirs.Show();

        _drawWindows.Add(mine);
        _drawWindows.Add(theirs);
    }

    // Has the puppet challenge you to Draw (with a seeded doodle), so the accept/decline flow can be exercised
    // from the overlay's GAMES strip (or the lobby).
    private async Task<string?> ChallengeFromPuppet()
    {
        var (p, me, pup) = Players();
        await Befriending(me, pup, () => p.RequestDrawGameAsync(
            me.Id, DrawDifficulty.Easy, "cat", DrawGuessing.LetterHint("cat"), PuppetDoodle()));
        _refreshReal();
        return $"@{pup.Handle} challenged you to Draw — accept it from the overlay's GAMES strip or the lobby, then guess.";
    }

    // Runs a game action; if it fails (most likely because you and the puppet aren't accepted friends yet), does
    // the handshake — puppet requests, you accept — and retries once. A second failure surfaces to Run().
    private async Task Befriending(Profile me, Profile pup, Func<Task> attempt)
    {
        try { await attempt(); }
        catch (SocialException)
        {
            _gameStatus.Busy("Not friends yet — befriending the puppet, then retrying…");
            try { await Puppet().SendRequestAsync(me.Id); } catch { }
            try { await _real.RespondAsync(pup.Id, accept: true); } catch { }
            await attempt();
        }
    }

    // A quick throwaway doodle for the puppet's challenge (a rough cat-ish shape on the 0..1000 canvas).
    private static IReadOnlyList<DrawStroke> PuppetDoodle() => new[]
    {
        new DrawStroke(0, 1, new List<DrawPoint>
            { new(300, 620), new(300, 360), new(230, 240), new(360, 320), new(500, 300),
              new(640, 320), new(770, 240), new(700, 360), new(700, 620), new(300, 620) }),
        new DrawStroke(0, 0, new List<DrawPoint> { new(400, 440), new(420, 440) }),   // eye
        new DrawStroke(0, 0, new List<DrawPoint> { new(580, 440), new(600, 440) }),   // eye
        new DrawStroke(3, 0, new List<DrawPoint> { new(480, 500), new(500, 520), new(520, 500) }),   // nose
    };

    private SupabaseSocialClient Puppet() =>
        _puppet ?? throw new SocialException("Sign in as the puppet first.");

    // The puppet client plus both claimed profiles — what every "puppet ↔ you" action needs.
    private (SupabaseSocialClient Puppet, Profile Me, Profile Pup) Players()
    {
        var p = Puppet();
        if (_real.Current.Me is not { } me) throw new SocialException("Your real account needs to be signed in with a claimed handle first.");
        if (p.Current.Me is not { } pup) throw new SocialException("Claim a puppet handle first.");
        return (p, me, pup);
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────

    private void Wire(Button button, InlineStatus status, Func<Task<string?>> action) =>
        button.Click += async (_, _) => await Run(button, status, action);

    // Runs an async action with its result reported on the status line beside the button that triggered it:
    // "Working…" while in flight (trigger disabled), then the returned message (or cleared when null), or the
    // error. Never throws; returns whether the action succeeded.
    private static async Task<bool> Run(Button? trigger, InlineStatus status, Func<Task<string?>> action)
    {
        if (trigger is not null) trigger.IsEnabled = false;
        status.Busy("Working…");
        try
        {
            var message = await action();
            if (message is null) status.Clear(); else status.Ok(message);
            return true;
        }
        catch (Exception ex)
        {
            status.Error(ex.Message);
            return false;
        }
        finally
        {
            if (trigger is not null) trigger.IsEnabled = true;
        }
    }

    /// <summary>A one-line (wrapping) status shown directly beneath an action: muted while working / on success,
    /// the error colour on failure, hidden when empty.</summary>
    private sealed class InlineStatus
    {
        public readonly TextBlock View = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        public void Busy(string text) => Set(text, Palette.MutedBrush);
        public void Ok(string text) => Set("✓ " + text, Palette.MutedBrush);
        public void Error(string text) => Set("⚠ " + text, Palette.ErrorBrush);
        public void Clear() { View.Text = ""; View.IsVisible = false; }
        private void Set(string text, IBrush brush) { View.Text = text; View.Foreground = brush; View.IsVisible = true; }
    }

    private static TextBox Field(string placeholder, string? value)
    {
        var t = SettingsUi.ThemedTextBox(value ?? "");
        t.PlaceholderText = placeholder;
        t.Width = 260;
        return t;
    }

    private static Control Row(string label, Control field)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = label, Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Width = 80 });
        row.Children.Add(field);
        return row;
    }

    // A wrapping row of buttons, so long labels flow onto a second line rather than off the window.
    private static Control Buttons(params Button[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0, 0, 8, 4);
            row.Children.Add(b);
        }
        return row;
    }

    private static StackPanel Stack(params Control[] children)
    {
        var s = new StackPanel { Spacing = 8 };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    // A titled card grouping one stage of the flow.
    private static Control Section(string title, params Control[] children)
    {
        var body = Stack(children);
        body.Children.Insert(0, new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush });
        return new Border
        {
            Background = Palette.SurfaceSunkenBrush, BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = body,
        };
    }

    // A subsection caption inside a card (with a little breathing room above it).
    private static Control Sub(string text) => new TextBlock
    {
        Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.FgBrush,
        Margin = new Thickness(0, 6, 0, 0),
    };

    // A subsection caption with a small control pinned to its right (e.g. the friends list's Refresh).
    private static Control SubWith(string text, Button right)
    {
        right.Padding = new Thickness(8, 2);
        right.FontSize = 11;
        var d = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right);
        d.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center });
        return d;
    }
}

/// <summary>Whether the Social developer testing tool is shown. Only in Debug builds, so it never surfaces in a
/// released install; a property (not a const) so the callers don't trip the unreachable-code warning.</summary>
internal static class SocialDebug
{
#if DEBUG
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif
}

/// <summary>An <see cref="ISecretStore"/> that keeps secrets only in memory — used for the puppet client so its
/// session never overwrites the real DPAPI/Keychain-stored one.</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _map = new();
    public void Set(string key, string value) => _map[key] = value;
    public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
    public void Delete(string key) => _map.TryRemove(key, out _);
}

/// <summary>A no-op <see cref="IUrlOpener"/> for the puppet client — its password sign-in never opens a browser.</summary>
internal sealed class NoopUrlOpener : IUrlOpener
{
    public void Open(string url) { }
    public void OpenInNewWindow(string url) { }
    public void OpenPrivate(string url) { }
}
