using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the self-managed-hook reconcile/removal in <see cref="ClaudeUserSettings"/> and the
/// migration-off-plugin strip in <see cref="PluginManager"/> — the settings.json read/merge/write logic
/// that must stay idempotent and never clobber a user's own keys. Each test works against a throwaway
/// settings file (the path-taking overloads) so nothing touches the shared fixture config dir.
/// </summary>
public sealed class ClaudeUserSettingsHookTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settings;

    // The seven (event, arg) pairs Perch manages — mirror of ClaudeUserSettings.ManagedHooks.
    private static readonly (string Event, string Arg)[] Expected =
    {
        ("PreToolUse", "mode"), ("PostToolUse", "mode"), ("Stop", "mode"),
        ("SubagentStop", "agentstop"), ("TeammateIdle", "teammateidle"),
        ("SessionStart", "start"), ("SessionEnd", "cleanup"),
    };

    public ClaudeUserSettingsHookTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-hooktests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────
    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_settings))!;

    // All managed command objects across every event, as (event, arg, command, version) tuples.
    private static List<(string Event, string Arg, string Command, string? Version)> ManagedHooks(JsonObject root)
    {
        var result = new List<(string, string, string, string?)>();
        if (root["hooks"] is not JsonObject hooks) return result;

        foreach (var (evt, entriesNode) in hooks)
        {
            if (entriesNode is not JsonArray entries) continue;
            foreach (var entryNode in entries)
            {
                if (entryNode is not JsonObject entry || entry["hooks"] is not JsonArray list) continue;
                foreach (var hookNode in list)
                {
                    if (hookNode is not JsonObject h || h["_perch"] is not JsonObject p ||
                        p["managed"]?.GetValue<bool>() != true)
                        continue;

                    string arg = (h["args"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "";
                    result.Add((evt, arg, h["command"]?.GetValue<string>() ?? "", p["version"]?.GetValue<string>()));
                }
            }
        }
        return result;
    }

    // ── reconcile ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void Reconcile_OnMissingFile_WritesTheFullManagedSet()
    {
        Assert.True(ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0"));

        var managed = ManagedHooks(Read());
        Assert.Equal(Expected.Length, managed.Count);
        foreach (var (evt, arg) in Expected)
            Assert.Contains(managed, m => m.Event == evt && m.Arg == arg &&
                                          m.Command == "/bin/perch-hook" && m.Version == "0.2.0");
    }

    [Fact]
    public void Reconcile_IsIdempotent_NoDuplicatesAcrossRepeatedRuns()
    {
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");

        Assert.Equal(Expected.Length, ManagedHooks(Read()).Count);
    }

    [Fact]
    public void Reconcile_PathAndVersionDrift_SelfCorrects()
    {
        ClaudeUserSettings.ReconcileHooks(_settings, "/old/perch-hook", "0.1.0");
        ClaudeUserSettings.ReconcileHooks(_settings, "/new/perch-hook", "0.2.0");

        var managed = ManagedHooks(Read());
        Assert.Equal(Expected.Length, managed.Count);
        Assert.All(managed, m => Assert.Equal("/new/perch-hook", m.Command));
        Assert.All(managed, m => Assert.Equal("0.2.0", m.Version));
    }

    [Fact]
    public void Reconcile_PreservesUserHooksAndOtherKeys()
    {
        // A user's own PreToolUse hook (no _perch marker) plus an event Perch doesn't manage, and an
        // unrelated top-level key that must survive the rewrite.
        File.WriteAllText(_settings, """
        {
          "model": "claude-opus",
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "user-script.sh" } ] }
            ],
            "PreCompact": [
              { "hooks": [ { "type": "command", "command": "compact.sh" } ] }
            ]
          }
        }
        """);

        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");
        var root = Read();

        // Unrelated key untouched.
        Assert.Equal("claude-opus", root["model"]?.GetValue<string>());

        var hooks = (JsonObject)root["hooks"]!;
        // The user's PreToolUse hook survives alongside ours.
        var preEntries = (JsonArray)hooks["PreToolUse"]!;
        Assert.Contains(preEntries, e => ((JsonObject)e!)["hooks"] is JsonArray l &&
            l.Any(h => ((JsonObject)h!)["command"]?.GetValue<string>() == "user-script.sh"));
        // The unmanaged PreCompact event is left entirely alone.
        Assert.True(hooks.ContainsKey("PreCompact"));

        // And exactly our managed set is present.
        Assert.Equal(Expected.Length, ManagedHooks(root).Count);
    }

    // ── removal ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void RemoveManagedHooks_StripsOursButKeepsUserHooks()
    {
        File.WriteAllText(_settings, """
        {
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "user-script.sh" } ] }
            ]
          }
        }
        """);
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");

        Assert.True(ClaudeUserSettings.RemoveManagedHooks(_settings));
        var root = Read();

        Assert.Empty(ManagedHooks(root));
        var preEntries = (JsonArray)((JsonObject)root["hooks"]!)["PreToolUse"]!;
        Assert.Single(preEntries);
        Assert.Contains(preEntries, e => ((JsonObject)e!)["hooks"] is JsonArray l &&
            l.Any(h => ((JsonObject)h!)["command"]?.GetValue<string>() == "user-script.sh"));
    }

    [Fact]
    public void RemoveManagedHooks_DropsHooksKeyWhenOnlyOursExisted()
    {
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");
        ClaudeUserSettings.RemoveManagedHooks(_settings);

        // No user hooks were present, so the whole "hooks" object should be gone.
        Assert.False(Read().ContainsKey("hooks"));
    }

    // ── migration ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void RemoveRegistration_StripsMarketplaceAndPlugin_PreservingOtherKeys()
    {
        File.WriteAllText(_settings, """
        {
          "model": "claude-opus",
          "extraKnownMarketplaces": {
            "perch": { "source": { "source": "github", "repo": "ArcticGizmo/perch" } },
            "other": { "source": { "source": "github", "repo": "someone/else" } }
          },
          "enabledPlugins": {
            "perch@perch": true,
            "other@other": true
          }
        }
        """);

        Assert.True(PluginManager.RemoveRegistration(_settings));
        var (marketplace, plugin) = PluginManager.ReadInstalledState(_settings);
        Assert.False(marketplace);
        Assert.False(plugin);

        var root = Read();
        Assert.Equal("claude-opus", root["model"]?.GetValue<string>());
        // Another user's marketplace + plugin are untouched.
        Assert.True(((JsonObject)root["extraKnownMarketplaces"]!).ContainsKey("other"));
        Assert.True(((JsonObject)root["enabledPlugins"]!).ContainsKey("other@other"));
    }

    [Fact]
    public void RemoveRegistration_IsNoOpWhenNothingRegistered()
    {
        File.WriteAllText(_settings, """{ "model": "claude-opus" }""");
        Assert.False(PluginManager.RemoveRegistration(_settings));
    }
}
