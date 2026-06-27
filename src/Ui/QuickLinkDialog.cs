using Perch.Ui;

using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// A small modal dialog for adding or editing a custom <see cref="QuickLink"/>: a display name and
/// the executable to launch (with a Browse… picker). Dark-themed to match the settings window.
/// On <see cref="DialogResult.OK"/> the chosen values are exposed via <see cref="LinkName"/> and
/// <see cref="LinkPath"/>; the caller decides whether they map onto a new or an existing link.
/// </summary>
internal sealed class QuickLinkDialog : Form
{
    private readonly TextBox _nameBox;
    private readonly TextBox _pathBox;
    private readonly Label   _statusLabel;

    // Debounces the Start Menu name lookup so it runs after the user pauses typing, not per keystroke.
    private readonly System.Windows.Forms.Timer _nameCheck;

    public string LinkName => _nameBox.Text.Trim();
    public string LinkPath => _pathBox.Text.Trim();

    public QuickLinkDialog(QuickLink? existing)
    {
        Text            = existing == null ? "Add quick link" : "Edit quick link";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize      = new Size(460, 300);

        const int pad = 16, gap = 8, browseW = 92;
        int innerW = ClientSize.Width - pad * 2;

        var help = new Label
        {
            Text      = "Name the link after the app. If an installed app matches that name, its icon " +
                        "is found automatically and a program path is optional — otherwise choose one below.",
            AutoSize  = false,
            ForeColor = Theme.Muted,
            Bounds    = new Rectangle(pad, pad, innerW, 46),
        };

        var nameCaption = Caption("Name", pad, help.Bottom + 6);
        _nameBox = MakeTextBox(existing?.Name ?? "");
        _nameBox.SetBounds(pad, nameCaption.Bottom + 4, innerW, _nameBox.Height);

        // Live result of the name lookup — sits directly under the Name box. Tall enough to wrap to
        // multiple lines so the longer "found" message isn't clipped.
        _statusLabel = new Label
        {
            AutoSize  = false,
            ForeColor = Theme.Muted,
            Bounds    = new Rectangle(pad, _nameBox.Bottom + 5, innerW, 52),
        };

        var pathCaption = Caption("Program (optional)", pad, _statusLabel.Bottom + 6);
        _pathBox = MakeTextBox(existing?.ExePath ?? "");
        _pathBox.SetBounds(pad, pathCaption.Bottom + 4, innerW - browseW - gap, _pathBox.Height);

        var browse = ThemedControls.FlatButton("Browse…");
        browse.SetBounds(_pathBox.Right + gap, _pathBox.Top - 1, browseW, _pathBox.Height + 2);
        browse.Click += (_, _) => Browse();

        var ok = ThemedControls.FlatButton("Save");
        ok.DialogResult = DialogResult.OK;
        var cancel = ThemedControls.FlatButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;

        const int btnW = 92, btnH = 30;
        int btnY = ClientSize.Height - pad - btnH;
        cancel.SetBounds(ClientSize.Width - pad - btnW, btnY, btnW, btnH);
        ok.SetBounds(cancel.Left - btnW - gap, btnY, btnW, btnH);

        _nameCheck = new System.Windows.Forms.Timer { Interval = 350 };
        _nameCheck.Tick += (_, _) => { _nameCheck.Stop(); RefreshNameStatus(); };

        // Guard against an empty name — the overlay needs something to label the icon's fallback.
        void OnNameChanged()
        {
            ok.Enabled = _nameBox.Text.Trim().Length > 0;
            _nameCheck.Stop();
            _nameCheck.Start();
        }
        _nameBox.TextChanged += (_, _) => OnNameChanged();
        ok.Enabled = _nameBox.Text.Trim().Length > 0;

        Controls.AddRange([help, nameCaption, _nameBox, _statusLabel, pathCaption, _pathBox, browse, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshNameStatus();  // reflect the initial name (e.g. when editing an existing link)
    }

    // Looks up the current name in the Start Menu and reports whether it resolves on its own. Run on
    // the UI thread off the debounce timer; the lookup is quick enough not to need a worker.
    private void RefreshNameStatus()
    {
        string name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("", Theme.Muted);
        }
        else if (ShellIcon.StartMenuAppExists(name))
        {
            SetStatus($"✓  Found “{name}” in your Start Menu — a program path is optional.", Theme.Green);
        }
        else
        {
            SetStatus($"No installed app matches “{name}”. Set a program below to give it an icon.", Theme.Muted);
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text      = text;
        _statusLabel.ForeColor = color;
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title            = "Choose a program",
            Filter           = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists  = true,
        };
        if (!string.IsNullOrWhiteSpace(_pathBox.Text))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(_pathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.FileName;
            // Offer a sensible default name from the file when the name field is still empty.
            if (_nameBox.Text.Trim().Length == 0)
                _nameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private static Label Caption(string text, int x, int y) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Muted,
        Location  = new Point(x, y),
    };

    private static TextBox MakeTextBox(string value) => new()
    {
        Text        = value,
        Height      = 26,
        BackColor   = Theme.ButtonBg,
        ForeColor   = Theme.Fg,
        BorderStyle = BorderStyle.FixedSingle,
        Font        = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _nameCheck?.Dispose();
        base.Dispose(disposing);
    }
}
