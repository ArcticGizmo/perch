using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Plugins;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The install/enable consent prompt: spells out, in plain words, exactly what a plugin is asking to do
/// (the capabilities its manifest requests) and lets the user Allow or Deny. Returns <c>true</c> only on an
/// explicit Allow — Esc, the close button, and Deny all return <c>false</c>. This is the human gate in the
/// capability model: nothing the plugin declared is granted unless the user says yes here.
/// </summary>
internal sealed class PluginConsentDialog : Window
{
    private PluginConsentDialog(PluginManifest manifest, string source)
    {
        Title = "Install plugin";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var body = new StackPanel { Margin = new Thickness(18), Spacing = 8 };

        body.Children.Add(new TextBlock
        {
            Text = $"Install “{manifest.Name}”?",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Foreground = Palette.TitleBrush,
        });

        var provenance = manifest.Version + (string.IsNullOrEmpty(source) ? "" : $"  ·  {source}");
        body.Children.Add(new TextBlock { Text = provenance, FontSize = 12, Foreground = Palette.MutedBrush });

        if (!string.IsNullOrWhiteSpace(manifest.Description))
            body.Children.Add(new TextBlock
            {
                Text = manifest.Description,
                FontSize = 13,
                Foreground = Palette.FgBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });

        body.Children.Add(new TextBlock
        {
            Text = "This plugin is asking to:",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TitleBrush,
            Margin = new Thickness(0, 6, 0, 0),
        });

        foreach (var (line, sensitive) in Describe(manifest.Capabilities))
            body.Children.Add(new TextBlock
            {
                Text = "•  " + line,
                FontSize = 13,
                Foreground = sensitive ? new SolidColorBrush(Palette.Danger) : Palette.FgBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 0, 0, 0),
            });

        body.Children.Add(new TextBlock
        {
            Text = "It runs as a separate program on your machine. Only install plugins you trust.",
            FontSize = 12,
            Foreground = Palette.MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var deny = SettingsUi.FlatButton("Deny");
        deny.Width = 110;
        deny.Click += (_, _) => Close(false);

        var allow = SettingsUi.FlatButton("Allow");
        allow.Width = 110;
        allow.Foreground = Palette.AccentBrush;
        allow.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);
        body.Children.Add(buttons);

        Content = body;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // Plain-language capability descriptions; the sensitive flag reddens the ones worth pausing over
    // (reading your data, reaching the network).
    private static IReadOnlyList<(string Line, bool Sensitive)> Describe(PluginCapabilities caps)
    {
        var list = new List<(string, bool)>();
        if (caps.ReadCwd) list.Add(("Read files in your active project directory", true));
        if (caps.ReadSessions) list.Add(("Read your Claude Code session transcripts", true));
        if (caps.RequestsNetwork) list.Add(($"Access the network: {string.Join(", ", caps.Network)}", true));
        if (caps.Notify) list.Add(("Show you desktop notifications", false));
        if (list.Count == 0) list.Add(("Nothing sensitive — it requests no special access", false));
        return list;
    }

    /// <summary>Shows the consent dialog modally over <paramref name="owner"/>; true means the user allowed
    /// the plugin's requested capabilities.</summary>
    public static Task<bool> ShowAsync(Window owner, PluginManifest manifest, string source) =>
        new PluginConsentDialog(manifest, source).ShowDialog<bool>(owner);
}
