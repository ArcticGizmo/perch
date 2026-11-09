using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Remote;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Workers page (docs/remote-workers-plan.md, W2): this machine's name and key, the worker listener, the pairing
/// window, and the machines paired in each direction. The listener's settings are also registry entries (search and
/// the catalogue find them); pairing and the lists only live here.
/// </summary>
internal sealed partial class SettingsWindow
{
    private WorkerHost? _workerHost;
    private TextBlock? _workerStatus, _pairingLine;
    private Button? _pairButton;
    private StackPanel? _controllersList, _workersList;
    private TextBlock? _addWorkerStatus;
    private CancellationTokenSource? _pairingAttempt;

    private void BuildWorkersPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Workers"));
        _workerHost = _hooks.Workers?.Invoke();
        if (_workerHost is not { IsSupported: true } host)
        {
            page.Children.Add(SettingsUi.BodyText("Remote workers aren't available on this system yet."));
            return;
        }
        page.Children.Add(SettingsUi.BodyText(
            "Drive Perch sessions running on your other machines from this one, or let them drive this one's. Machines " +
            "pair once, here, by comparing a six-digit code; after that they recognise each other by key and talk over " +
            "an encrypted connection. They reach each other directly, so they need to be on the same network or on " +
            "Tailscale (or another VPN)."));

        // ── This machine ──
        page.Children.Add(SettingsUi.FieldCaption("This machine's name"));
        var nameBox = SettingsUi.ThemedTextBox(_settings.DeviceName ?? "");
        nameBox.PlaceholderText = Environment.MachineName;
        nameBox.TextChanged += (_, _) => _settings.DeviceName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
        nameBox.LostFocus += (_, _) => _settings.Save();
        page.Children.Add(nameBox);
        page.Children.Add(new TextBlock
        {
            Text = $"Key fingerprint: {host.Fingerprint ?? "unavailable"}", FontSize = 12, Foreground = Palette.MutedBrush,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), Margin = new Thickness(0, 6, 0, 0),
        });

        page.Children.Add(SettingsUi.Separator());

        // ── Being a worker ──
        page.Children.Add(SettingsUi.TitleRow("Let paired machines control this one",
            SaveToggle(_settings.WorkerEnabled, v =>
            {
                _settings.WorkerEnabled = v;
                _hooks.WorkersChanged?.Invoke();
            })));
        _workerStatus = new TextBlock { FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap };
        page.Children.Add(_workerStatus);
        page.Children.Add(SettingsUi.BodyText(
            "A machine you allow can run anything this machine's sessions are permitted to: send prompts, answer " +
            "permission prompts and interrupt. Only pair machines you own. Windows asks once to let Perch through the firewall."));

        var addressRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,120"), Margin = new Thickness(0, 6, 0, 0) };
        var bindStack = new StackPanel();
        bindStack.Children.Add(SettingsUi.FieldCaption("Listen address (blank = every network)"));
        var bindBox = SettingsUi.ThemedTextBox(_settings.WorkerBindAddress ?? "");
        bindBox.PlaceholderText = "e.g. this machine's Tailscale 100.x address";
        bindBox.LostFocus += (_, _) =>
        {
            _settings.WorkerBindAddress = string.IsNullOrWhiteSpace(bindBox.Text) ? null : bindBox.Text.Trim();
            _settings.Save();
            _hooks.WorkersChanged?.Invoke();
        };
        bindStack.Children.Add(bindBox);
        var portStack = new StackPanel();
        portStack.Children.Add(SettingsUi.FieldCaption("Port"));
        var portBox = SettingsUi.ThemedTextBox(_settings.WorkerPort.ToString());
        portBox.LostFocus += (_, _) =>
        {
            _settings.WorkerPort = int.TryParse(portBox.Text, out var p) && p is > 0 and < 65536 ? p : DeviceTrust.DefaultPort;
            portBox.Text = _settings.WorkerPort.ToString();
            _settings.Save();
            _hooks.WorkersChanged?.Invoke();
        };
        portStack.Children.Add(portBox);
        Grid.SetColumn(bindStack, 0);
        Grid.SetColumn(portStack, 2);
        addressRow.Children.Add(bindStack);
        addressRow.Children.Add(portStack);
        page.Children.Add(addressRow);

        var pairRow = SettingsUi.ButtonRow();
        _pairButton = SettingsUi.FlatButton("Pair a controller…");
        _pairButton.Click += (_, _) =>
        {
            if (host.PairingOpen) host.ClosePairing();
            else host.OpenPairing();
        };
        pairRow.Children.Add(_pairButton);
        page.Children.Add(pairRow);
        _pairingLine = new TextBlock { FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap };
        page.Children.Add(_pairingLine);

        page.Children.Add(SettingsUi.FieldCaption("Machines that can control this one"));
        _controllersList = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        page.Children.Add(_controllersList);

        page.Children.Add(SettingsUi.Separator());

        // ── Controlling workers ──
        page.Children.Add(SettingsUi.FieldCaption("Machines this one controls"));
        _workersList = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        page.Children.Add(_workersList);

        var addGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,100,12,Auto") };
        var hostBox = SettingsUi.ThemedTextBox("");
        hostBox.PlaceholderText = "the worker's name or address (LAN or Tailscale)";
        var addPortBox = SettingsUi.ThemedTextBox(DeviceTrust.DefaultPort.ToString());
        var addButton = SettingsUi.FlatButton("Pair");
        Grid.SetColumn(hostBox, 0);
        Grid.SetColumn(addPortBox, 2);
        Grid.SetColumn(addButton, 4);
        addGrid.Children.Add(hostBox);
        addGrid.Children.Add(addPortBox);
        addGrid.Children.Add(addButton);
        page.Children.Add(addGrid);
        _addWorkerStatus = new TextBlock
        {
            FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };
        page.Children.Add(_addWorkerStatus);
        page.Children.Add(SettingsUi.BodyText(
            "On the worker: turn on \"Let paired machines control this one\" and click \"Pair a controller…\", then " +
            "click Pair here and compare the code both machines show."));
        addButton.Click += async (_, _) => await PairWithWorker(host, hostBox.Text, addPortBox.Text, addButton);

        page.Children.Add(SettingsUi.Separator());
        var resetRow = SettingsUi.ButtonRow();
        var reset = SettingsUi.FlatButton("Reset this machine's key…");
        reset.Click += async (_, _) =>
        {
            if (await ConfirmDialog.ShowAsync(this, "Reset this machine's key?",
                    "Every machine paired with this one, in either direction, will need pairing again.", "Reset", "Cancel", reset))
                host.ResetIdentity();
        };
        resetRow.Children.Add(reset);
        page.Children.Add(resetRow);

        host.Changed += RefreshWorkersPage;
        Closed += (_, _) => host.Changed -= RefreshWorkersPage;
        RefreshWorkersPage();
    }

    private async Task PairWithWorker(WorkerHost host, string? hostText, string? portText, Button button)
    {
        if (_addWorkerStatus is null) return;
        var address = (hostText ?? "").Trim();
        if (address.Length == 0)
        {
            _addWorkerStatus.Text = "Enter the worker's name or address.";
            return;
        }
        int port = int.TryParse(portText, out var p) && p is > 0 and < 65536 ? p : DeviceTrust.DefaultPort;
        _pairingAttempt?.Cancel();
        _pairingAttempt = new CancellationTokenSource();
        button.IsEnabled = false;
        _addWorkerStatus.Text = $"Connecting to {address}:{port}…";
        try
        {
            var device = await host.PairWithWorkerAsync(address, port, code =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_addWorkerStatus is not null)
                        _addWorkerStatus.Text = $"Code {code}. Check {address} shows the same code, then click Allow there.";
                }), _pairingAttempt.Token);
            _addWorkerStatus.Text = $"Paired with {device.Name}.";
        }
        catch (RemoteLinkException ex)
        {
            _addWorkerStatus.Text = $"Not paired: {ex.Message}.";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void RefreshWorkersPage()
    {
        if (_workerHost is not { } host) return;
        if (_workerStatus is not null)
        {
            var connected = host.ConnectedControllers;
            _workerStatus.Text = host.Status + (connected > 0 ? $" · {connected} connected" : "");
        }
        if (_pairButton is not null)
        {
            _pairButton.IsEnabled = host.IsListening;
            _pairButton.Content = host.PairingOpen ? "Stop pairing" : "Pair a controller…";
        }
        if (_pairingLine is not null)
        {
            var left = host.PairingClosesUtc - DateTime.UtcNow;
            _pairingLine.Text = host.PairingOpen
                ? $"Pairing is open for {left.Minutes}:{left.Seconds:D2}. On the other machine, add this one as a worker " +
                  $"(this machine's name or address, port {_settings.WorkerPort}). You'll be asked here to compare a code."
                : "";
            _pairingLine.IsVisible = host.PairingOpen;
        }
        FillDeviceList(_controllersList, host.Paired(PairedRole.Controller), host, "No machine can control this one yet.");
        FillDeviceList(_workersList, host.Paired(PairedRole.Worker), host, "This machine doesn't control any workers yet.");
    }

    // How this machine's link to a worker stands, in a line.
    private static string LinkState(WorkerLinks.Worker link) => link switch
    {
        { Online: true } => link.Sessions.Count switch
        {
            0 => "Connected · no Perch sessions running there",
            1 => "Connected · 1 session",
            var n => $"Connected · {n} sessions",
        },
        { Connecting: true } => "Connecting…",
        { Problem: { Length: > 0 } why } => $"Offline: {why}. Trying again shortly.",
        _ => "Not connected yet",
    };

    private void FillDeviceList(StackPanel? list, IReadOnlyList<PairedDevice> devices, WorkerHost host, string empty)
    {
        if (list is null) return;
        list.Children.Clear();
        if (devices.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = empty, Foreground = Palette.MutedBrush, Margin = new Thickness(0, 2, 0, 6) });
            return;
        }
        foreach (var d in devices)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeight.SemiBold });
            var detail = d.Role == PairedRole.Worker ? $"{d.Host}:{d.Port} · " : "";
            text.Children.Add(new TextBlock
            {
                Text = $"{detail}key {DeviceTrust.Fingerprint(d.Pin)} · paired {d.AddedUtc.ToLocalTime():d MMM yyyy}",
                FontSize = 12, Foreground = Palette.MutedBrush,
            });
            if (d.Role == PairedRole.Controller && host.HeldOutUntilUtc(d.Pin) is { } until)
                text.Children.Add(new TextBlock
                {
                    Text = $"Disconnected by you · can reconnect after {until.ToLocalTime():t}",
                    FontSize = 12, Foreground = Palette.MutedBrush,
                });
            if (d.Role == PairedRole.Worker && host.Workers.ByPin(d.Pin) is { } link)
                text.Children.Add(new TextBlock
                {
                    Text = LinkState(link), FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap,
                });
            var remove = SettingsUi.FlatButton("Remove");
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += async (_, _) =>
            {
                if (await ConfirmDialog.ShowAsync(this, $"Remove {d.Name}?",
                        d.Role == PairedRole.Controller
                            ? $"{d.Name} won't be able to control this machine until it pairs again."
                            : $"This machine won't be able to control {d.Name} until they pair again.",
                        "Remove", "Cancel", remove))
                    host.Forget(d);
            };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            list.Children.Add(row);
        }
    }
}
