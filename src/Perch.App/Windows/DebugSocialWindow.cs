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
    private SupabaseSocialClient? _puppet;

    private readonly TextBox _email, _password, _handle, _target, _status, _emoji;
    private readonly TextBlock _log;

    public DebugSocialWindow(SupabaseSocialClient real, Action refreshReal)
    {
        _real = real;
        _refreshReal = refreshReal;
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
        _emoji.Width = 60;

        _log = new TextBlock
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
        panel.Children.Add(reactRow);

        panel.Children.Add(SettingsUi.Separator());
        var refresh = SettingsUi.FlatButton("Refresh my overlay now");
        refresh.Click += (_, _) => { _refreshReal(); Log("Asked your overlay to re-poll."); };
        panel.Children.Add(Left(refresh));
        panel.Children.Add(_log);

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
        var emoji = string.IsNullOrWhiteSpace(_emoji.Text) ? "🔥" : _emoji.Text.Trim();
        await p.ReactAsync(latest.Id, emoji, on: true);
        Log($"Reacted {emoji} to @{target.Handle}'s latest post.");
        _refreshReal();
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

/// <summary>Whether the Social developer testing tool is enabled — via the <c>PERCH_SOCIAL_DEBUG</c>
/// environment variable, or the same key in the repo's <c>.env.local</c> (dev builds). Off by default, so the
/// tool never surfaces in a normal install.</summary>
internal static class SocialDebug
{
    public static bool Enabled { get; } = Resolve();

    private static bool Resolve()
    {
        var v = Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG");
        if (string.IsNullOrEmpty(v))
        {
            try
            {
                if (DotEnv.FindRepoEnvLocal(AppContext.BaseDirectory) is { } envFile)
                    DotEnv.Parse(File.ReadAllText(envFile)).TryGetValue("PERCH_SOCIAL_DEBUG", out v);
            }
            catch { /* best-effort */ }
        }
        return v is "1" or "true" or "yes" or "on";
    }
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
