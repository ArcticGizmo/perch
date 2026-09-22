namespace Perch.Statusline;

using System.Text.Json.Nodes;

/// <summary>A representative statusLine stdin payload used by the designer's live preview and by tests.
/// It mirrors the shape Claude Code sends (model, workspace, cost, context_window, prompt_cache,
/// rate_limits, pr, effort, vim, version, output_style) and includes the Perch-injected <c>git</c>
/// block, so every token the catalogue lists resolves against it.</summary>
internal static class StatuslineSample
{
    public const string Json = """
    {
      "model": { "id": "claude-opus-5", "display_name": "Opus" },
      "version": "2.1.90",
      "output_style": { "name": "default" },
      "session_id": "5f2c9a10-3b7e-4d1a-9c22-1e8a6b0d4477",
      "cwd": "/home/jhowell/git/personal/perch",
      "workspace": {
        "current_dir": "/home/jhowell/git/personal/perch",
        "project_dir": "/home/jhowell/git/personal/perch",
        "repo": { "host": "github.com", "owner": "jhowell", "name": "perch" }
      },
      "cost": {
        "total_cost_usd": 0.4213,
        "total_duration_ms": 845000,
        "total_api_duration_ms": 61000,
        "total_lines_added": 156,
        "total_lines_removed": 23
      },
      "context_window": {
        "used_percentage": 34,
        "remaining_percentage": 66,
        "context_window_size": 200000,
        "total_input_tokens": 68000,
        "total_output_tokens": 5200
      },
      "prompt_cache": { "warm": true, "hit_ratio": 0.91, "ttl": "1h" },
      "rate_limits": {
        "five_hour": { "used_percentage": 23.5 },
        "seven_day": { "used_percentage": 41.2 }
      },
      "exceeds_200k_tokens": false,
      "pr": {
        "number": 30,
        "review_state": "pending",
        "url": "https://github.com/jhowell/perch/pull/30"
      },
      "effort": { "level": "high" },
      "vim": { "mode": "NORMAL" },
      "git": { "branch": "statusline-designer", "staged": 2, "unstaged": 3, "changes": 5, "dirty": true }
    }
    """;

    /// <summary>The sample payload as template data, with the rate-limit <c>resets_at</c> stamps injected
    /// relative to now so the pace-colour filter has something live to work with: the 5h window is ~halfway
    /// through with the reading behind pace (green), the 7d window is ~40% through with the reading a touch
    /// over pace (red) — so a designer preview shows two different pace colours rather than a static one.</summary>
    public static TemplateData Data()
    {
        var data = TemplateData.Parse(Json);
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (data.Root["rate_limits"] is JsonObject rl)
        {
            if (rl["five_hour"] is JsonObject f) f["resets_at"] = now + 9000;    // 2.5h of 5h left → expected ~50
            if (rl["seven_day"] is JsonObject s) s["resets_at"] = now + 370000;  // ~4.3d of 7d left → expected ~39
        }
        return data;
    }
}
