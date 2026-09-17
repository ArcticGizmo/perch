using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A modal dialog for adding or editing an <see cref="AccountRule"/> account guardrail (org discovery, Layer
/// 2 / M2): the governed directory (a Browse… folder picker) and the set of Claude accounts allowed under it
/// (a checklist of the accounts Perch can see signed into your config dirs, plus any already on the rule). On
/// Save it closes with <c>true</c> and exposes the chosen values via <see cref="RulePath"/> /
/// <see cref="SelectedAccounts"/>. Dark-themed to match the settings window; modelled on
/// <see cref="ConfigDirDialog"/>. Matched on org uuid — names are display only.
/// </summary>
internal sealed class AccountRuleDialog : Window
{
    private readonly TextBox _pathBox;
    private readonly List<(CheckBox Box, AccountRef Account)> _checks = new();
    private readonly Button _ok;

    public string RulePath => _pathBox.Text?.Trim() ?? "";

    public List<AccountRef> SelectedAccounts =>
        _checks.Where(c => c.Box.IsChecked == true).Select(c => c.Account).ToList();

    /// <param name="existing">The rule being edited, or null to add a new one.</param>
    /// <param name="knownAccounts">Accounts to offer (signed-in orgs ∪ the rule's current accounts).</param>
    public AccountRuleDialog(AccountRule? existing, IReadOnlyList<AccountRef> knownAccounts)
    {
        bool editing = existing is not null;
        var already = new HashSet<string>(
            existing?.Allowed.Select(a => a.Uuid) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        Title = editing ? "Edit account rule" : "Add account rule";
        Width = 520;
        Height = 460;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var help = SettingsUi.BodyText(
            "Everything under this directory is expected to run on one of the accounts you tick. A session " +
            "there signed into a different org gets an aggressive red outline on its overlay row.");

        _pathBox = SettingsUi.ThemedTextBox(existing?.Path ?? "");
        var browse = SettingsUi.FlatButton("Browse…");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += async (_, _) => await BrowseAsync();
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_pathBox, 0);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(_pathBox);
        pathRow.Children.Add(browse);

        Control accountsControl;
        if (knownAccounts.Count == 0)
        {
            accountsControl = new TextBlock
            {
                Text = "No signed-in accounts found across your config directories. Sign in with /login, then "
                       + "edit this rule to choose the allowed account(s).",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.MutedBrush,
            };
        }
        else
        {
            var checks = new StackPanel { Spacing = 6 };
            foreach (var acc in knownAccounts)
            {
                var box = new CheckBox
                {
                    IsChecked = already.Contains(acc.Uuid),
                    Content = new TextBlock
                    {
                        Text = Label(acc), Foreground = Palette.TitleBrush, TextWrapping = TextWrapping.Wrap,
                    },
                };
                _checks.Add((box, acc));
                checks.Children.Add(box);
            }
            accountsControl = new ScrollViewer { Content = checks, MaxHeight = 210 };
        }

        _ok = SettingsUi.FlatButton("Save");
        _ok.Width = 92;
        _ok.Click += (_, _) => Close(true);
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Width = 92;
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(help);
        layout.Children.Add(SettingsUi.FieldCaption("Directory"));
        layout.Children.Add(pathRow);
        layout.Children.Add(SettingsUi.FieldCaption("Allowed accounts"));
        layout.Children.Add(accountsControl);
        layout.Children.Add(buttons);
        Content = new ScrollViewer { Content = layout };

        _pathBox.TextChanged += (_, _) => RefreshOk();
        RefreshOk();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _pathBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && _ok.IsEnabled) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void RefreshOk() => _ok.IsEnabled = RulePath.Length > 0;

    private static string Label(AccountRef a) =>
        !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Email) ? $"{a.Name}  ·  {a.Email}"
        : !string.IsNullOrWhiteSpace(a.Name) ? a.Name!
        : !string.IsNullOrWhiteSpace(a.Email) ? a.Email!
        : a.Uuid;

    private async System.Threading.Tasks.Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the directory this rule governs", AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) _pathBox.Text = path;
    }
}
