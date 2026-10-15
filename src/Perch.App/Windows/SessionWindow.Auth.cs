using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The sign-in banner above the composer: warns when this session's process is about to lose (or has lost) its
/// access token — a headless session can't refresh it — and when the account's login is close to needing
/// <c>/login</c>. The wording and thresholds live in <see cref="AuthExpiryRules.Banner"/>; this only paints it,
/// amber while close and red once lapsed. A half-minute tick keeps the countdown current; it reads no files (the
/// process clocks are cached on <see cref="Services.PerchSession.ProcessAuth"/>, the account's on the usage poll).
/// While the warning is about the process's own access token it carries a Restart button
/// (<see cref="Services.PerchSession.Restart"/>), the same action as the End session dropdown's.
/// </summary>
internal sealed partial class SessionWindow
{
    private Border _authBanner = null!;
    private TextBlock _authBannerText = null!;
    private SessionButton _authRestartButton = null!;
    private DispatcherTimer? _authBannerTimer;

    private Border BuildAuthBanner()
    {
        _authBannerText = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 12, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = "If a restarted session still can't sign in, open Claude Code in a terminal once to refresh it.",
        };
        // Only while the warning is about this process's own access token — the one thing a restart fixes.
        _authRestartButton = new SessionButton(_p, "Restart", SessionButtonKind.Ghost, compact: true)
        {
            IsVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = "Restart the Claude process on this same session to pick up a fresh sign-in",
        };
        _authRestartButton.Click += async () => await ConfirmRestartAsync(_authRestartButton);
        _authRestartButton[DockPanel.DockProperty] = Dock.Right;
        _authBanner = new Border
        {
            IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 9),
            Child = new DockPanel { Children = { _authRestartButton, _authBannerText } },
        };
        return _authBanner;
    }

    private void StartAuthBannerTimer()
    {
        if (_authBannerTimer is not null) return;
        _authBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _authBannerTimer.Tick += (_, _) => RefreshAuthBanner();
        _authBannerTimer.Start();
    }

    private void StopAuthBannerTimer()
    {
        _authBannerTimer?.Stop();
        _authBannerTimer = null;
    }

    private void RefreshAuthBanner()
    {
        if (_closed) return;
        var s = _session;
        var account = UsageProvider?.Invoke().Auth ?? AuthExpiry.None;
        var process = s?.ProcessAuth ?? AuthExpiry.None;
        bool running = s is { IsRunning: true };
        var (text, level) = AuthExpiryRules.Banner(process, account, running, dormant: s is { IsDormant: true }, DateTime.Now);
        PaintAuthBanner(text, level, AuthExpiryRules.OffersRestart(process, running, DateTime.Now));
    }

    /// <summary>HeadlessRenderer: pose the banner for a running session holding <paramref name="process"/>'s clocks on
    /// an account at <paramref name="account"/> (a render session has no process, so it never reads as running).</summary>
    internal void ShowAuthBannerForRender(AuthExpiry process, AuthExpiry account)
    {
        StopAuthBannerTimer();
        var (text, level) = AuthExpiryRules.Banner(process, account, running: true, dormant: false, DateTime.Now);
        PaintAuthBanner(text, level, AuthExpiryRules.OffersRestart(process, running: true, DateTime.Now));
    }

    private void PaintAuthBanner(string? text, AuthExpiryLevel level, bool offerRestart)
    {
        if (text is null)
        {
            _authBanner.IsVisible = false;
            return;
        }
        _authRestartButton.IsVisible = offerRestart;
        bool expired = level == AuthExpiryLevel.Expired;
        _authBanner.Background = expired ? _p.ErrWash : _p.AwaitWash;
        _authBanner.BorderBrush = expired ? _p.Err : _p.AwaitLine;
        _authBannerText.Text = text;
        _authBanner.IsVisible = true;
    }
}
