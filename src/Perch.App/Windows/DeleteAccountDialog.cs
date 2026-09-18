using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The irreversible confirmation for GDPR account deletion (Social, D3). Spells out what is erased and what
/// is retained, offers the GitHub-revoke deep-link, and gates the destructive button behind a
/// <b>type-to-confirm</b> box: the user must type their handle (or the word <c>DELETE</c> when they haven't
/// claimed one) before "Delete permanently" enables. Returns <c>true</c> only when they confirm; Esc / Cancel
/// / closing returns <c>false</c>. Dark-themed and centred on its owner; modelled on
/// <see cref="AccountRuleDialog"/>.
/// </summary>
internal sealed class DeleteAccountDialog : Window
{
    private readonly TextBox _confirmBox;
    private readonly Button _delete;
    private readonly string _phrase;   // what the user must type: their handle, or "DELETE" when handle-less

    private DeleteAccountDialog(string? handle)
    {
        _phrase = string.IsNullOrWhiteSpace(handle) ? "DELETE" : handle!.Trim();
        bool byHandle = !string.IsNullOrWhiteSpace(handle);

        Title = "Delete account and data";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var heading = SettingsUi.SectionTitle("Delete your account and data");

        var whatGoes = SettingsUi.BodyText(
            "This permanently deletes your Perch Social account and everything tied to it — your profile, "
            + "posts, reactions, friends, and games — along with the email GitHub gave us when you signed in. "
            + "It cannot be undone.");

        var whatStays = new TextBlock
        {
            Text = "Any abuse reports you filed about other people are kept, but anonymised — your identity as "
                   + "the reporter is removed — so moderation can still act on them. Nothing else you authored "
                   + "survives. See PRIVACY.md for the full detail.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.MutedBrush,
            Margin = new Thickness(0, 0, 0, 4),
        };

        // GitHub is a separate grant we can't revoke for them — link them to the exact "Revoke access" page.
        var githubNote = SettingsUi.BodyText(
            "You signed in with GitHub. Deleting here removes the email we stored, but you'll also want to "
            + "revoke Perch's access to your GitHub account:");
        var revoke = SettingsUi.FlatButton("Revoke Perch on GitHub…");
        revoke.Click += (_, _) => PlatformServices.UrlOpener.Open(AppInfo.SocialGitHubRevokeUrl);

        var prompt = SettingsUi.FieldCaption(byHandle
            ? $"Type your handle  @{_phrase}  to confirm"
            : "Type  DELETE  to confirm");
        _confirmBox = SettingsUi.ThemedTextBox("");
        _confirmBox.PlaceholderText = byHandle ? _phrase : "DELETE";

        _delete = SettingsUi.FlatButton("Delete permanently");
        _delete.Width = 160;
        _delete.Foreground = new SolidColorBrush(Palette.Danger);   // destructive — fixed red, not a theme role
        _delete.Click += (_, _) => Close(true);
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Width = 92;
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_delete);

        var support = new TextBlock
        {
            Text = $"Questions? {AppInfo.SupportEmail}",
            FontSize = 12, Foreground = Palette.MutedBrush, Margin = new Thickness(0, 10, 0, 0),
        };

        var layout = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        layout.Children.Add(heading);
        layout.Children.Add(whatGoes);
        layout.Children.Add(whatStays);
        layout.Children.Add(githubNote);
        layout.Children.Add(Left(revoke));
        layout.Children.Add(SettingsUi.Separator());
        layout.Children.Add(prompt);
        layout.Children.Add(_confirmBox);
        layout.Children.Add(buttons);
        layout.Children.Add(support);
        Content = layout;

        _confirmBox.TextChanged += (_, _) => RefreshDelete();
        RefreshDelete();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _confirmBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && _delete.IsEnabled) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // The destructive button stays disabled until the typed text matches the phrase (a leading @ is ignored,
    // case-insensitive) — a deliberate friction so deletion is never a single stray click.
    private void RefreshDelete()
    {
        var typed = (_confirmBox.Text ?? "").Trim().TrimStart('@');
        _delete.IsEnabled = string.Equals(typed, _phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static Control Left(Control c) { c.HorizontalAlignment = HorizontalAlignment.Left; return c; }

    /// <summary>Shows the modal, owned by <paramref name="owner"/>. <paramref name="handle"/> is the signed-in
    /// user's handle (the phrase they must type), or null when they haven't claimed one.</summary>
    public static Task<bool> ShowAsync(Window owner, string? handle) =>
        new DeleteAccountDialog(handle).ShowDialog<bool>(owner);
}
