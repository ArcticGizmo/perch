namespace Perch.Data;

/// <summary>
/// A user-assigned display label for a Claude Code config directory, persisted in
/// <see cref="AppSettings.ConfigDirLabels"/>. <see cref="Path"/> is the directory's <b>resolved real
/// path</b> — the same identity <see cref="ClaudeConfigDir.RealRoot"/> dedups on — so a label sticks to a
/// dir even across a launcher's link/junction aliasing. Org-free: a label is a display string, nothing more.
/// </summary>
public sealed class ConfigDirLabel
{
    /// <summary>The config dir's resolved real path (its <see cref="ClaudeConfigDir.RealRoot"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>The label to show on the overlay's per-session directory chip for that dir.</summary>
    public string Label { get; set; } = "";
}
