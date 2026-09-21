namespace Perch.Statusline;

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
      "pr": {
        "number": 30,
        "review_state": "pending",
        "url": "https://github.com/jhowell/perch/pull/30"
      },
      "effort": { "level": "high" },
      "vim": { "mode": "NORMAL" },
      "git": { "branch": "statusline-designer", "changes": 4, "dirty": true }
    }
    """;

    public static TemplateData Data() => TemplateData.Parse(Json);
}
