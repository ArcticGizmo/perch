namespace Perch.Data;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Migrates users off the retired <c>perch</c> Claude Code marketplace plugin. Perch now self-manages
/// its hooks directly in <c>~/.claude/settings.json</c> (see <see cref="ClaudeUserSettings"/>), so the
/// old plugin must be removed or its hooks would fire in addition to ours — every event delivered
/// twice.
///
/// "Is the old plugin still registered?" is answered by reading <c>~/.claude/settings.json</c> directly
/// (fast, no subprocess); removal strips those keys (authoritative — it stops event delivery at once)
/// and then best-effort asks the CLI to drop the installed plugin and its marketplace clone.
/// </summary>
internal sealed class PluginManager
{
    // The repo doubles as the marketplace (see .claude-plugin/marketplace.json). The marketplace
    // *name* comes from that file's "name" field; the plugin id is "<plugin>@<marketplace>".
    private const string MarketplaceName = "perch";
    private const string PluginName = "perch";
    private const string PluginId = PluginName + "@" + MarketplaceName;

    private static string SettingsPath => ClaudePaths.UserSettingsFile;

    // ── Quick state from settings.json (no subprocess) ───────────────────────────────
    /// <summary>
    /// Reads <c>~/.claude/settings.json</c> and reports whether our marketplace is registered
    /// (under <c>extraKnownMarketplaces</c>) and our plugin is enabled (under <c>enabledPlugins</c>).
    /// Tolerant of a missing/unreadable file (both false). Used to decide whether a migration is needed.
    /// </summary>
    public static (bool marketplace, bool plugin) ReadInstalledState() =>
        ReadInstalledState(SettingsPath);

    /// <summary>As <see cref="ReadInstalledState()"/>, against an explicit settings file (test seam).</summary>
    public static (bool marketplace, bool plugin) ReadInstalledState(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return (false, false);

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath), JsonLeniency);
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

    // ── Migration / removal ──────────────────────────────────────────────────────────
    /// <summary>
    /// Removes the old marketplace plugin: strips our marketplace from <c>extraKnownMarketplaces</c>
    /// and our plugin from <c>enabledPlugins</c> in settings.json (which stops Claude Code delivering
    /// the plugin's hooks immediately), then best-effort asks the CLI to uninstall the plugin and drop
    /// the marketplace clone so no stale state lingers. Idempotent and safe when nothing is installed.
    /// </summary>
    public async Task RemoveAsync()
    {
        RemoveRegistration(SettingsPath); // authoritative: disables the plugin at once, no CLI needed

        if (await IsCliPresentAsync())
        {
            // Best-effort tidy-up of the on-disk plugin/marketplace clones; failures are ignored (the
            // settings strip above already stopped the double events).
            await RunClaudeAsync($"plugin uninstall {PluginId}");
            await RunClaudeAsync($"plugin marketplace remove {MarketplaceName}");
        }
    }

    /// <summary>
    /// Strips our marketplace + plugin keys from the given settings file, preserving every other key
    /// (and dropping the parent objects only when they empty). Tolerant of a hand-edited file
    /// (// comments, trailing commas); a missing/garbage file is a no-op. Returns true if it rewrote.
    /// Exposed as a static test seam; <see cref="RemoveAsync"/> is the production entry point.
    /// </summary>
    public static bool RemoveRegistration(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return false;

            if (JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: JsonLeniency) is not JsonObject root)
                return false;

            bool changed = false;

            if (root["extraKnownMarketplaces"] is JsonObject mkts && mkts.Remove(MarketplaceName))
            {
                changed = true;
                if (mkts.Count == 0) root.Remove("extraKnownMarketplaces");
            }

            if (root["enabledPlugins"] is JsonObject plugins && plugins.Remove(PluginId))
            {
                changed = true;
                if (plugins.Count == 0) root.Remove("enabledPlugins");
            }

            if (changed)
                File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return changed;
        }
        catch
        {
            return false;
        }
    }

    // ── Process helpers ────────────────────────────────────────────────────────────
    private async Task<bool> IsCliPresentAsync() =>
        (await RunClaudeAsync("--version")).exitCode == 0;

    // Runs `claude <args>` via cmd.exe so PATHEXT shims (.exe/.cmd/.bat) all resolve.
    private static Task<(int exitCode, string output)> RunClaudeAsync(string args) =>
        RunProcessAsync("cmd.exe", $"/c claude {args}");

    // Runs a process, capturing combined stdout+stderr, with a hard timeout so a hung CLI/network
    // call can never wedge the caller. A non-zero exit code (including "command not found") is failure.
    private static async Task<(int exitCode, string output)> RunProcessAsync(string fileName, string args)
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
                try { proc.Kill(entireProcessTree: true); }
                catch { }
                return (-1, "Timed out.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (proc.ExitCode, stdout + stderr);
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
}
