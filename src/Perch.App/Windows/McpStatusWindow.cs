using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The <c>/mcp</c> status view: a small modal listing the MCP servers the CLI reported at init, each with a
/// colour-coded status badge. Read-only for now — configuring servers and their (loopback/browser) auth
/// aren't drivable from Perch yet, so the footer points at <c>claude mcp</c> in a terminal
/// (docs/slash-command-group-a-plan.md, Group 3).
/// </summary>
internal sealed class McpStatusWindow : Window
{
    public McpStatusWindow(IReadOnlyList<McpServerInfo> servers, SessionPalette p)
    {
        Title = "MCP servers";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        Background = p.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch/Assets/icon.ico"))); } catch { }

        var list = new StackPanel { Spacing = 6 };
        if (servers.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No MCP servers are configured for this session.",
                FontFamily = p.Body, FontSize = 13.5, Foreground = p.Muted, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var s in servers) list.Children.Add(Row(s, p));
        }

        var footer = new TextBlock
        {
            Text = "Adding servers and signing in aren't available from Perch yet — run `claude mcp` in a terminal.",
            FontFamily = p.Body, FontSize = 12, Foreground = p.Faint, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var close = new SessionButton(p, "Close", SessionButtonKind.Primary, compact: true)
        {
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
        };
        close.Click += () => Close();

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "MCP servers", FontFamily = p.Display, FontWeight = FontWeight.Bold,
                            FontSize = 17, Foreground = p.Title, Margin = new Thickness(0, 0, 0, 14),
                        },
                        list, footer, close,
                    },
                },
            },
        };
    }

    private static Control Row(McpServerInfo s, SessionPalette p)
    {
        var name = new TextBlock
        {
            Text = s.Name, FontFamily = p.Body, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            Foreground = p.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var (label, colour) = Badge(s.Status, p);
        var badge = new Border
        {
            Background = Brushes.Transparent, BorderBrush = colour, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, FontFamily = p.Mono, FontSize = 11.5, Foreground = colour },
        };
        badge[DockPanel.DockProperty] = Dock.Right;
        name.Margin = new Thickness(0, 0, 12, 0);
        var row = new DockPanel { LastChildFill = true, Children = { badge, name } };
        return new Border
        {
            Background = p.Raised, BorderBrush = p.BorderSoft, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(13, 10), Child = row,
        };
    }

    // Map the CLI's status string to a label + a semantic colour (green ok, amber needs-auth, red failed).
    private static (string Label, IBrush Colour) Badge(string status, SessionPalette p)
    {
        var s = status.ToLowerInvariant();
        if (s is "connected" or "ready" or "ok" or "running") return (status, p.Ok);
        if (s.Contains("auth")) return (status, p.Await);          // "needs-auth", "authenticating"
        if (s is "failed" or "error" or "disconnected") return (status, p.Err);
        return (status.Length > 0 ? status : "unknown", p.Muted);
    }
}
