using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The sign-in banner above the composer: warns when this session's process is about to lose (or has lost) its
/// access token — a headless session can't refresh it — and when the account's login is close to needing
/// <c>/login</c>. The wording and thresholds live in <see cref="AuthExpiryRules.Banner"/>; this only paints it,
/// amber while close and red once lapsed. A half-minute tick keeps the countdown current; it reads no files (the
/// process clocks are cached on <see cref="Services.PerchSession.ProcessAuth"/>, the account's on the usage poll).
/// </summary>
internal sealed partial class SessionWindow
{
    private Border _authBanner = null!;
    private TextBlock _authBannerText = null!;
    private DispatcherTimer? _authBannerTimer;

    private Border BuildAuthBanner()
    {
        _authBannerText = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 12, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap,
        };
        _authBanner = new Border
        {
            IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 9), Child = _authBannerText,
            [ToolTip.TipProperty] = "If a resumed session still can't sign in, open Claude Code in a terminal once to refresh it.",
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
        var (text, level) = AuthExpiryRules.Banner(s?.ProcessAuth ?? AuthExpiry.None, account,
            running: s is { IsRunning: true }, dormant: s is { IsDormant: true }, DateTime.Now);
        PaintAuthBanner(text, level);
    }

    /// <summary>HeadlessRenderer: pose the banner for a running session holding <paramref name="process"/>'s clocks on
    /// an account at <paramref name="account"/> (a render session has no process, so it never reads as running).</summary>
    internal void ShowAuthBannerForRender(AuthExpiry process, AuthExpiry account)
    {
        StopAuthBannerTimer();
        var (text, level) = AuthExpiryRules.Banner(process, account, running: true, dormant: false, DateTime.Now);
        PaintAuthBanner(text, level);
    }

    private void PaintAuthBanner(string? text, AuthExpiryLevel level)
    {
        if (text is null)
        {
            _authBanner.IsVisible = false;
            return;
        }
        bool expired = level == AuthExpiryLevel.Expired;
        _authBanner.Background = expired ? _p.ErrWash : _p.AwaitWash;
        _authBanner.BorderBrush = expired ? _p.Err : _p.AwaitLine;
        _authBannerText.Text = text;
        _authBanner.IsVisible = true;
    }
}
