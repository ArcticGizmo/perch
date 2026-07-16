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

    // All managed command objects across every event, as (event, arg, command, version, dev) tuples.
    private static List<(string Event, string Arg, string Command, string? Version, bool Dev)> ManagedHooks(JsonObject root)
    {
        var result = new List<(string, string, string, string?, bool)>();
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
                    bool dev = p["dev"]?.GetValue<bool>() == true;
                    result.Add((evt, arg, h["command"]?.GetValue<string>() ?? "",
                                p["version"]?.GetValue<string>(), dev));
                }
            }
        }
        return result;
    }

    // Raw count of hook command objects whose command equals the given path — regardless of any _perch
    // marker — so we can assert on entries an external rewrite has left marker-less.
    private static int CountCommand(JsonObject root, string command)
    {
        int n = 0;
        if (root["hooks"] is not JsonObject hooks) return 0;
        foreach (var (_, entriesNode) in hooks)
            if (entriesNode is JsonArray entries)
                foreach (var entryNode in entries)
                    if (entryNode is JsonObject entry && entry["hooks"] is JsonArray list)
                        foreach (var hookNode in list)
                            if (hookNode is JsonObject h && h["command"]?.GetValue<string>() == command)
                                n++;
        return n;
    }

    // Simulate Claude Code rewriting settings.json: it round-trips hooks through its own schema, keeping
    // the known {type,command,args} fields but dropping our unknown "_perch" marker off every entry.
    private void StripPerchMarkers()
    {
        var root = Read();
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var (_, entriesNode) in hooks)
            if (entriesNode is JsonArray entries)
                foreach (var entryNode in entries)
                    if (entryNode is JsonObject entry && entry["hooks"] is JsonArray list)
                        foreach (var hookNode in list)
                            (hookNode as JsonObject)?.Remove("_perch");
        File.WriteAllText(_settings, root.ToJsonString());
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
    public void Reconcile_CollapsesMarkerlessDuplicates_FromExternalRewrite()
    {
        // Reproduce the duplicate-accumulation bug: Claude Code rewrites settings.json between launches
        // and drops our unknown "_perch" marker, so past launches kept re-adding an unrecognised set.
        // Three reconciles, each preceded by a simulated marker-stripping rewrite of the *previous* one.
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");
        StripPerchMarkers();
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");
        StripPerchMarkers();
        ClaudeUserSettings.ReconcileHooks(_settings, "/bin/perch-hook", "0.2.0");

        // The marker-less duplicates are recognised by command and collapsed to the single managed set.
        Assert.Equal(Expected.Length, ManagedHooks(Read()).Count);
        var hooks = (JsonObject)Read()["hooks"]!;
        foreach (var (evt, _) in Expected)
            Assert.Single((JsonArray)hooks[evt]!);
    }

    [Fact]
    public void Reconcile_MatchesBinaryPath_AcrossSeparatorsAndExeSuffix()
    {
        // A prior Windows install left a marker-less entry with a backslash path and ".exe"; the current
        // reconcile runs with a POSIX path. It must still be recognised as ours and replaced, not doubled.
        File.WriteAllText(_settings, """
        {
          "hooks": {
            "PreToolUse": [
              { "matcher": "", "hooks": [ { "type": "command",
                "command": "C:\\Users\\me\\AppData\\Roaming\\Perch\\bin\\perch-hook.exe", "args": ["mode"] } ] }
            ]
          }
        }
        """);

        ClaudeUserSettings.ReconcileHooks(_settings, "/opt/perch/bin/perch-hook", "0.2.0");

        Assert.Equal(Expected.Length, ManagedHooks(Read()).Count);
        Assert.Single((JsonArray)((JsonObject)Read()["hooks"]!)["PreToolUse"]!);
    }

    // ── dev / release profile scope ─────────────────────────────────────────────────────
    [Fact]
    public void Reconcile_Dev_LeavesReleaseHooksIntact_AndAddsItsOwn()
    {
        // A release Perch has wired its hooks; then a dev build runs. The dev reconcile must add its own
        // set without disturbing release's — so both coexist while developing.
        ClaudeUserSettings.ReconcileHooks(_settings, "/rel/perch-hook", "1.0.0", isDev: false);
        ClaudeUserSettings.ReconcileHooks(_settings, "/dev/perch-hook", "9.9.9", isDev: true);

        var managed = ManagedHooks(Read());
        Assert.Equal(Expected.Length * 2, managed.Count);
        Assert.Equal(Expected.Length, managed.Count(m => !m.Dev && m.Command == "/rel/perch-hook"));
        Assert.Equal(Expected.Length, managed.Count(m => m.Dev && m.Command == "/dev/perch-hook"));
    }

    [Fact]
    public void Reconcile_Release_ClearsEverythingThenReaddsReleaseOnly()
    {
        // Release is authoritative: with both a dev and a release set present, a release reconcile sweeps
        // both away and leaves exactly its own clean set.
        ClaudeUserSettings.ReconcileHooks(_settings, "/rel/perch-hook", "1.0.0", isDev: false);
        ClaudeUserSettings.ReconcileHooks(_settings, "/dev/perch-hook", "9.9.9", isDev: true);

        ClaudeUserSettings.ReconcileHooks(_settings, "/rel/perch-hook", "1.0.0", isDev: false);

        var managed = ManagedHooks(Read());
        Assert.Equal(Expected.Length, managed.Count);
        Assert.All(managed, m => Assert.False(m.Dev));
        Assert.All(managed, m => Assert.Equal("/rel/perch-hook", m.Command));
        Assert.Equal(0, CountCommand(Read(), "/dev/perch-hook"));
    }

    [Fact]
    public void Reconcile_Dev_AfterMarkerStrip_CollapsesOwnByPath_KeepsRelease()
    {
        // Both sets present, then Claude Code rewrites the file and drops every _perch marker. A fresh dev
        // reconcile must recognise its own now-marker-less entries by command path and collapse them,
        // while leaving release's (a different path) entirely alone.
        ClaudeUserSettings.ReconcileHooks(_settings, "/rel/perch-hook", "1.0.0", isDev: false);
        ClaudeUserSettings.ReconcileHooks(_settings, "/dev/perch-hook", "9.9.9", isDev: true);
        StripPerchMarkers();

        ClaudeUserSettings.ReconcileHooks(_settings, "/dev/perch-hook", "9.9.9", isDev: true);

        var root = Read();
        Assert.Equal(Expected.Length, CountCommand(root, "/dev/perch-hook")); // collapsed, not doubled
        Assert.Equal(Expected.Length, CountCommand(root, "/rel/perch-hook")); // release untouched
        // Release stayed marker-less (dev didn't rewrite it); only dev's set carries fresh markers.
        Assert.All(ManagedHooks(root), m => Assert.True(m.Dev));
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
