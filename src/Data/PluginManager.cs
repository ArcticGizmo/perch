namespace Perch.Data;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Perch.Data;

/// <summary>
/// What action (if any) the user can take on the perch Claude Code plugin, derived from the
/// CLI's view of marketplaces/plugins plus a version check. Drives the label and enabled-state of
/// the single button in the settings "Claude Code plugin" section.
/// </summary>
internal enum PluginStatus
{
    /// <summary>The <c>claude</c> CLI couldn't be located on PATH, so nothing can be installed.</summary>
    CliMissing,

    /// <summary>Marketplace and/or plugin are missing — offer "Enable".</summary>
    NeedsEnable,

    /// <summary>Installed and enabled, but a newer version exists upstream — offer "Update".</summary>
    UpdateAvailable,

    /// <summary>Installed, enabled, and current — nothing to do.</summary>
    UpToDate,
}

/// <summary>
/// Drives the Claude Code CLI to add the perch marketplace and install/update the
/// <c>perch</c> plugin, and reports its status. The plugin feeds each session's live
/// permission mode (and the /afk and /history commands) to the tray app; this class only manages
/// install/update/health, never the session files themselves.
///
/// "Is it installed?" is answered by reading <c>~/.claude/settings.json</c> directly (fast, no
/// subprocess); "is there a newer version?" needs the CLI to refresh the marketplace clone and a
/// git/version comparison against it.
/// </summary>
internal sealed class PluginManager
{
    // The repo doubles as the marketplace (see .claude-plugin/marketplace.json). The marketplace
    // *name* comes from that file's "name" field; the plugin id is "<plugin>@<marketplace>".
    private const string MarketplaceRepo = "ArcticGizmo/perch";
    private const string MarketplaceName = "perch";
    private const string PluginName = "perch";
    private const string PluginId = PluginName + "@" + MarketplaceName;

    private static string SettingsPath => ClaudePaths.UserSettingsFile;
    private static string InstalledPluginsPath =>
        Path.Combine(ClaudePaths.PluginsDir, "installed_plugins.json");
    private static string MarketplaceClonePath =>
        Path.Combine(ClaudePaths.PluginsDir, "marketplaces", MarketplaceName);

    /// <summary>The slash commands a user can paste into a session if the CLI isn't on PATH.</summary>
    public static string FallbackCommands =>
        $"/plugin marketplace add {MarketplaceRepo} --scope user\n/plugin install {PluginId} --scope user";

    // ── Quick state from settings.json (no subprocess) ───────────────────────────────
    /// <summary>
    /// Reads <c>~/.claude/settings.json</c> and reports whether our marketplace is registered
    /// (under <c>extraKnownMarketplaces</c>) and our plugin is enabled (under <c>enabledPlugins</c>).
    /// Tolerant of a missing/unreadable file (both false).
    /// </summary>
    public static (bool marketplace, bool plugin) ReadInstalledState()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return (false, false);

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath), JsonLeniency);
            var root = doc.RootElement;

            bool marketplace =
                root.TryGetProperty("extraKnownMarketplaces", out var mkts)
                && mkts.ValueKind == JsonValueKind.Object
                && mkts.TryGetProperty(MarketplaceName, out _);

            bool plugin =
                root.TryGetProperty("enabledPlugins", out var plugins)
                && plugins.ValueKind == JsonValueKind.Object
                && plugins.TryGetProperty(PluginId, out var enabled)
                && enabled.ValueKind == JsonValueKind.True;

            return (marketplace, plugin);
        }
        catch
        {
            return (false, false);
        }
    }

    // ── Status (settings read + async version check) ─────────────────────────────────
    /// <summary>
    /// Decides which action to surface. Fast path: if the marketplace or plugin is missing, returns
    /// <see cref="PluginStatus.NeedsEnable"/> without touching the CLI. Otherwise refreshes the
    /// marketplace clone and compares the installed commit/version against it to decide
    /// <see cref="PluginStatus.UpdateAvailable"/> vs <see cref="PluginStatus.UpToDate"/>.
    /// </summary>
    public async Task<PluginStatus> GetStatusAsync()
    {
        var (marketplace, plugin) = ReadInstalledState();
        if (!marketplace || !plugin)
        {
            // Distinguish "needs enabling" from "can't enable" so the UI can explain the latter.
            return await IsCliPresentAsync() ? PluginStatus.NeedsEnable : PluginStatus.CliMissing;
        }

        if (!await IsCliPresentAsync())
            return PluginStatus.UpToDate; // installed but can't check — don't nag.

        // Refresh the local marketplace clone from its source so the comparison is against latest.
        await RunClaudeAsync($"plugin marketplace update {MarketplaceName}");

        return await IsUpdateAvailableAsync()
            ? PluginStatus.UpdateAvailable
            : PluginStatus.UpToDate;
    }

    /// <summary>
    /// Compares the installed plugin against the (freshly-refreshed) marketplace clone. Prefers the
    /// git commit sha — the marketplace is a github checkout, so its HEAD is the latest available
    /// commit — and falls back to comparing plugin.json <c>version</c> strings. Returns false when it
    /// can't tell, so we never nag with a phantom update.
    /// </summary>
    private async Task<bool> IsUpdateAvailableAsync()
    {
        var (installedVersion, installedSha) = ReadInstalledVersion();

        var latestSha = await GitHeadShaAsync(MarketplaceClonePath);
        if (!string.IsNullOrEmpty(installedSha) && !string.IsNullOrEmpty(latestSha))
            return !ShaEquals(installedSha!, latestSha!);

        var latestVersion = ReadMarketplaceVersion();
        if (!string.IsNullOrEmpty(installedVersion) && !string.IsNullOrEmpty(latestVersion))
            return !string.Equals(
                installedVersion,
                latestVersion,
                StringComparison.OrdinalIgnoreCase
            );

        return false;
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Adds the marketplace (idempotent) then installs the plugin. Best-effort and safe to re-run;
    /// returns a user-facing message. Used by the settings "Enable" button and the first-run
    /// auto-install.
    /// </summary>
    public async Task<(bool ok, string message)> EnableAsync()
    {
        if (!await IsCliPresentAsync())
            return (false, "claude CLI not found on PATH.");

        var (marketplace, _) = ReadInstalledState();
        if (!marketplace)
        {
            var add = await RunClaudeAsync(
                $"plugin marketplace add {MarketplaceRepo} --scope user"
            );
            if (add.exitCode != 0)
                return (false, $"Adding marketplace failed: {FirstLine(add.output)}");
        }

        var install = await RunClaudeAsync($"plugin install {PluginId} --scope user");
        if (install.exitCode != 0)
            return (false, $"Install failed: {FirstLine(install.output)}");

        return (
            true,
            "Claude Code plugin installed. Run /reload-plugins in any open session (or restart it) to load it."
        );
    }

    /// <summary>
    /// Refreshes the marketplace then updates the plugin to the latest version. Returns a
    /// user-facing message.
    /// </summary>
    public async Task<(bool ok, string message)> UpdateAsync()
    {
        if (!await IsCliPresentAsync())
            return (false, "claude CLI not found on PATH.");

        await RunClaudeAsync($"plugin marketplace update {MarketplaceName}");

        var update = await RunClaudeAsync($"plugin update {PluginId}");
        if (update.exitCode != 0)
            return (false, $"Update failed: {FirstLine(update.output)}");

        return (
            true,
            "Claude Code plugin updated. Run /reload-plugins in any open session (or restart it) to apply it."
        );
    }

    // ── On-disk reads ──────────────────────────────────────────────────────────────
    // Reads the installed plugin's version and git commit from installed_plugins.json. The value is
    // an array of per-scope entries; the first is fine for our single-scope (user) install.
    private static (string? version, string? sha) ReadInstalledVersion()
    {
        try
        {
            if (!File.Exists(InstalledPluginsPath))
                return (null, null);

            using var doc = JsonDocument.Parse(
                File.ReadAllText(InstalledPluginsPath),
                JsonLeniency
            );
            if (
                !doc.RootElement.TryGetProperty("plugins", out var plugins)
                || !plugins.TryGetProperty(PluginId, out var entries)
                || entries.ValueKind != JsonValueKind.Array
            )
                return (null, null);

            foreach (var entry in entries.EnumerateArray())
            {
                var version = entry.TryGetProperty("version", out var v) ? v.GetString() : null;
                var sha = entry.TryGetProperty("gitCommitSha", out var s) ? s.GetString() : null;
                return (version, sha);
            }
        }
        catch { }
        return (null, null);
    }

    // Reads the latest plugin version from the refreshed marketplace clone: marketplace.json names
    // the plugin's source dir; that dir's .claude-plugin/plugin.json carries the version.
    private static string? ReadMarketplaceVersion()
    {
        try
        {
            var marketplaceJson = Path.Combine(
                MarketplaceClonePath,
                ".claude-plugin",
                "marketplace.json"
            );
            if (!File.Exists(marketplaceJson))
                return null;

            string sourceRel = "./plugins/" + PluginName; // sensible default for our repo layout
            using (var doc = JsonDocument.Parse(File.ReadAllText(marketplaceJson), JsonLeniency))
            {
                if (
                    doc.RootElement.TryGetProperty("plugins", out var arr)
                    && arr.ValueKind == JsonValueKind.Array
                )
                    foreach (var p in arr.EnumerateArray())
                        if (
                            p.TryGetProperty("name", out var n)
                            && string.Equals(
                                n.GetString(),
                                PluginName,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && p.TryGetProperty("source", out var src)
                            && src.ValueKind == JsonValueKind.String
                        )
                        {
                            sourceRel = src.GetString() ?? sourceRel;
                            break;
                        }
            }

            var pluginJson = Path.Combine(
                MarketplaceClonePath,
                sourceRel.Replace('/', Path.DirectorySeparatorChar),
                ".claude-plugin",
                "plugin.json"
            );
            if (!File.Exists(pluginJson))
                return null;

            using var pdoc = JsonDocument.Parse(File.ReadAllText(pluginJson), JsonLeniency);
            return pdoc.RootElement.TryGetProperty("version", out var ver) ? ver.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ShaEquals(string a, string b)
    {
        // installed_plugins.json may record a short sha; compare on the shorter common length.
        int len = Math.Min(a.Length, b.Length);
        return len > 0 && string.Equals(a[..len], b[..len], StringComparison.OrdinalIgnoreCase);
    }

    // ── Process helpers ────────────────────────────────────────────────────────────
    private async Task<bool> IsCliPresentAsync() =>
        (await RunClaudeAsync("--version")).exitCode == 0;

    // The marketplace clone's HEAD commit — the latest available version for a github source.
    private static async Task<string?> GitHeadShaAsync(string repoDir)
    {
        if (!Directory.Exists(repoDir))
            return null;
        var (exitCode, output) = await RunProcessAsync("git", $"-C \"{repoDir}\" rev-parse HEAD");
        return exitCode == 0 ? output.Trim() : null;
    }

    // Runs `claude <args>` via cmd.exe so PATHEXT shims (.exe/.cmd/.bat) all resolve.
    private static Task<(int exitCode, string output)> RunClaudeAsync(string args) =>
        RunProcessAsync("cmd.exe", $"/c claude {args}");

    // Runs a process, capturing combined stdout+stderr, with a hard timeout so a hung CLI/network
    // call can never wedge the UI. A non-zero exit code (including "command not found") is failure.
    private static async Task<(int exitCode, string output)> RunProcessAsync(
        string fileName,
        string args
    )
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (-1, "");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch { }
                return (-1, "Timed out.");
            }

            var sb = new StringBuilder(await stdoutTask);
            var err = await stderrTask;
            if (err.Length > 0)
                sb.Append(err);
            return (proc.ExitCode, sb.ToString());
        }
        catch
        {
            return (-1, "");
        }
    }

    private static readonly JsonDocumentOptions JsonLeniency = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var nl = trimmed.IndexOf('\n');
        return nl < 0 ? trimmed : trimmed[..nl].Trim();
    }
}
