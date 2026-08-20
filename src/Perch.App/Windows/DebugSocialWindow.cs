using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
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
/// <para>Gated behind <see cref="SocialDebug.Enabled"/> (the <c>PERCH_SOCIAL_DEBUG</c> flag), so it never
/// appears in a normal install. The puppet keeps its session in an in-memory secret store, so it never touches
/// your real signed-in session.</para>
/// </summary>
internal sealed class DebugSocialWindow : Window
{
    private readonly SupabaseSocialClient _real;
    private readonly Action _refreshReal;
    private readonly Action<string>? _testReaction;
    private SupabaseSocialClient? _puppet;

    // A ring of reactions so each "React" click uses a different emoji — reactions are one-per-user, so
    // re-clicking the same emoji is a delete-then-insert that leaves the count unchanged and so wouldn't
    // trigger a big-reaction bubble. Cycling guarantees a genuinely new reaction each time.
    private static readonly string[] ReactCycle = ["🔥", "🎉", "😂", "❤️", "👍", "🙌", "😮", "😢"];
    private int _reactIx;

    private readonly Func<string>? _gateStatus;

    private readonly TextBox _email, _password, _handle, _target, _status, _emoji;
    private readonly SelectableTextBlock _log;
    private readonly SelectableTextBlock _diagLog;
    private readonly List<string> _diagLines = new();

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
        Width = 460;
        Height = 620;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        _email = Field("test user email");
        _password = Field("password");
        _password.PasswordChar = '•';
        _handle = Field("puppet handle (e.g. testbot)");
        _target = Field("your handle");
        if (_real.Current.Me is { } me) _target.Text = me.Handle;
        _status = Field("a status to post as the puppet");
        _emoji = Field("🔥");
        _emoji.Text = "🔥";   // a default mood/reaction so puppet posts show a mood in the roster
        _emoji.Width = 60;

        _log = new SelectableTextBlock
        {
            Foreground = Palette.MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Ready. Create a user in Supabase (Auth → Users → Add user, Auto Confirm), then sign in below.",
        };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(SettingsUi.SectionTitle("Social testing tool"));
        panel.Children.Add(SettingsUi.BodyText(
            "Drive a second (puppet) account created in the Supabase dashboard, so you can test the whole loop " +
            "from one machine. Steps: sign in → claim a handle → send yourself a request → accept it in your " +
            "real Friends window → post / react."));

        panel.Children.Add(Row("Email", _email));
        panel.Children.Add(Row("Password", _password));
        panel.Children.Add(ButtonRow(("Sign in as puppet", SignIn)));

        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(Row("Puppet handle", _handle));
        panel.Children.Add(ButtonRow(("Claim handle", ClaimHandle)));

        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(Row("Your handle", _target));
        panel.Children.Add(ButtonRow(
            ("Send me a friend request", SendRequest),
            ("Accept my requests", AcceptRequests)));

        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(Row("Status", _status));
        panel.Children.Add(ButtonRow(("Post as puppet", Post)));
        var reactRow = SettingsUi.ButtonRow();
        reactRow.Children.Add(new TextBlock { Text = "Emoji", Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Width = 90 });
        reactRow.Children.Add(_emoji);
        var reactBtn = SettingsUi.FlatButton("React to my latest post");
        reactBtn.Click += (_, _) => Run(React);
        reactRow.Children.Add(reactBtn);
        var unreactBtn = SettingsUi.FlatButton("Remove reaction");
        unreactBtn.Click += (_, _) => Run(Unreact);
        reactRow.Children.Add(unreactBtn);
        panel.Children.Add(reactRow);

        // Fire the big-reaction bubble directly — no network, no ShowLargeReactions / DND gate — so you can
        // confirm the animation itself works independently of the detection path.
        var testBubble = SettingsUi.FlatButton("Test big reaction (local)");
        testBubble.Click += (_, _) =>
        {
            var emoji = ReactCycle[_reactIx++ % ReactCycle.Length];
            _testReaction?.Invoke(emoji);
            Log(_testReaction is null ? "Test hook not wired." : $"Spawned a local {emoji} bubble (bypasses settings/DND).");
        };
        var diagnose = SettingsUi.FlatButton("Diagnose big reactions");
        diagnose.Click += (_, _) => Run(Diagnose);
        var testRow = SettingsUi.ButtonRow();
        testRow.Children.Add(testBubble);
        testRow.Children.Add(diagnose);
        panel.Children.Add(testRow);

        panel.Children.Add(SettingsUi.Separator());
        var refresh = SettingsUi.FlatButton("Refresh my overlay now");
        refresh.Click += (_, _) => { _refreshReal(); Log("Asked your overlay to re-poll."); };
        var dnd = SettingsUi.FlatButton("Check DND state");
        dnd.Click += (_, _) => Log($"Windows Do Not Disturb detected as: {(PlatformServices.DoNotDisturb.IsActive ? "ON" : "off")}. " +
            "Toggle it in the Action Center and click again — if this stays 'off' when DND is on, the detection needs another tweak.");
        var actionsRow = SettingsUi.ButtonRow();
        actionsRow.Children.Add(refresh);
        actionsRow.Children.Add(dnd);
        panel.Children.Add(actionsRow);
        panel.Children.Add(_log);

        panel.Children.Add(SettingsUi.Separator());
        panel.Children.Add(SettingsUi.FieldCaption("Reaction diagnostics (live)"));
        _diagLog = new SelectableTextBlock
        {
            Foreground = Palette.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Text = "(each poll's reaction state on your post appears here — newest first)",
        };
        panel.Children.Add(_diagLog);

        Content = new ScrollViewer { Content = panel };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }, RoutingStrategies.Tunnel);
    }

    // ── actions ─────────────────────────────────────────────────────────────────

    private async Task SignIn()
    {
        _puppet = new SupabaseSocialClient(SupabaseConfig.Resolve(), new InMemorySecretStore(), new NoopUrlOpener());
        var state = await _puppet.SignInWithPasswordAsync(_email.Text?.Trim() ?? "", _password.Text ?? "");
        Log(state.Me is { } me ? $"Signed in as @{me.Handle}." : "Signed in — no handle yet; claim one below.");
    }

    private async Task ClaimHandle()
    {
        var p = RequirePuppet();
        var me = await p.ClaimHandleAsync(_handle.Text?.Trim() ?? "");
        Log($"Puppet handle is now @{me.Handle}.");
    }

    private async Task SendRequest()
    {
        var p = RequirePuppet();
        var target = await FindTarget(p);
        await p.SendRequestAsync(target.Id);
        Log($"Sent a friend request to @{target.Handle}. Accept it in your Friends window (the + in the region).");
        _refreshReal();
    }

    private async Task AcceptRequests()
    {
        var p = RequirePuppet();
        var incoming = (await p.GetFriendsAsync()).Where(f => f.State == FriendshipState.Incoming).ToList();
        foreach (var f in incoming) await p.RespondAsync(f.Profile.Id, accept: true);
        Log(incoming.Count == 0 ? "No incoming requests for the puppet." : $"Accepted {incoming.Count} request(s).");
        _refreshReal();
    }

    private async Task Post()
    {
        var p = RequirePuppet();
        var body = _status.Text?.Trim() ?? "";
        if (body.Length == 0) { Log("Enter a status to post."); return; }
        await p.PostAsync(body, string.IsNullOrWhiteSpace(_emoji.Text) ? null : _emoji.Text.Trim());
        Log($"Posted as the puppet: \"{body}\". It should appear in your overlay shortly.");
        _refreshReal();
    }

    private async Task React()
    {
        var p = RequirePuppet();
        var target = await FindTarget(p);
        var feed = await p.GetFeedAsync(50);
        var latest = feed.FirstOrDefault(x => x.Author.Id == target.Id);
        if (latest is null) { Log($"No visible post by @{target.Handle} — are you accepted friends, and have you posted?"); return; }
        // Cycle the emoji so each click is a genuinely new reaction (see ReactCycle) — otherwise a repeat with
        // the same emoji leaves the count unchanged and no big-reaction bubble fires.
        var emoji = ReactCycle[_reactIx++ % ReactCycle.Length];
        _emoji.Text = emoji;   // reflect what was actually used
        await p.ReactAsync(latest.Id, emoji, on: true);
        Log($"Reacted {emoji} to @{target.Handle}'s latest post.");
        _refreshReal();
    }

    // Clears the puppet's reaction from your latest post (reactions are one-per-user, so this removes whichever
    // emoji it currently holds). Lets you react → remove → react again to re-trigger the big-reaction bubble.
    private async Task Unreact()
    {
        var p = RequirePuppet();
        var target = await FindTarget(p);
        var feed = await p.GetFeedAsync(50);
        var latest = feed.FirstOrDefault(x => x.Author.Id == target.Id);
        if (latest is null) { Log($"No visible post by @{target.Handle} — nothing to un-react."); return; }
        await p.ReactAsync(latest.Id, "", on: false);   // on:false removes the puppet's own reaction, if any
        Log($"Removed the puppet's reaction from @{target.Handle}'s latest post.");
        _refreshReal();
    }

    // Why isn't the big reaction firing? Reports the gates (ShowLargeReactions / DND) and whether the real
    // client actually sees a reaction on your latest post. Big reactions only fire for reactions that arrive
    // *after* the feed starts (the backlog is baseline), so a reaction already present won't re-fire — react
    // again (a new emoji) to see it.
    private async Task Diagnose()
    {
        var gate = _gateStatus is null ? "Gate status hook not wired." : _gateStatus();
        var roster = await _real.GetRosterAsync();
        if (roster.MyLatest is null)
        {
            Log(gate + "\n\nYour roster has no latest status — you haven't posted, so there's nothing for a friend to react to. Post a status first.");
            return;
        }
        var rx = roster.MyReactions.Count == 0
            ? "(none)"
            : string.Join(", ", roster.MyReactions.Select(g => $"{g.Emoji}x{g.Count}"));
        var body = roster.MyLatest.Body;
        Log($"{gate}\n\nYour latest status: \"{(body.Length > 40 ? body[..40] + "…" : body)}\" — reactions the real client sees on it: {rx}.\n\n" +
            "If a reaction shows here but no bubble fired: ShowLargeReactions must be True and DND suppressing must be False above; and the " +
            "reaction must be NEW since the feed started (a reaction already present is baseline). Click React (it cycles emojis) to add a fresh one.");
    }

    private async Task<Profile> FindTarget(SupabaseSocialClient p)
    {
        var handle = _target.Text?.Trim() ?? "";
        if (handle.Length == 0) throw new SocialException("Enter your handle in \"Your handle\".");
        return await p.FindByHandleAsync(handle) ?? throw new SocialException($"No user @{handle}.");
    }

    private SupabaseSocialClient RequirePuppet() =>
        _puppet ?? throw new SocialException("Sign in as the puppet first.");

    // ── plumbing ────────────────────────────────────────────────────────────────

    // Runs an async action, reporting success/failure to the log and never letting it throw out of a handler.
    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (SocialException ex) { Log("⚠ " + ex.Message); }
        catch (Exception ex) { Log("⚠ " + ex.Message); }
    }

    private void Log(string message) => _log.Text = message;

    /// <summary>Appends a live diagnostic line (newest first), capped so the panel stays readable. Fed by the
    /// feed monitor host (per-poll reaction state) and the big-reaction gate decision.</summary>
    public void Diag(string message)
    {
        _diagLines.Insert(0, message);
        if (_diagLines.Count > 40) _diagLines.RemoveRange(40, _diagLines.Count - 40);
        _diagLog.Text = string.Join("\n", _diagLines);
    }

    private static TextBox Field(string watermark)
    {
        var t = SettingsUi.ThemedTextBox("");
        t.PlaceholderText = watermark;
        t.Width = 240;
        return t;
    }

    private Control Row(string label, Control field)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = label, Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Width = 90 });
        row.Children.Add(field);
        return row;
    }

    private Control ButtonRow(params (string Label, Func<Task> Action)[] buttons)
    {
        var row = SettingsUi.ButtonRow();
        foreach (var (label, action) in buttons)
        {
            var b = SettingsUi.FlatButton(label);
            b.Click += (_, _) => Run(action);
            row.Children.Add(b);
        }
        return row;
    }

    private static Control Left(Control c) =>
        new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Children = { c } };
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
}
