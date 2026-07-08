using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// Maps a Claude Code tool call (name + its JSON <c>input</c>) to a short, present-tense phrase
/// ("Reading Foo.cs", "Running: npm test", "Editing Bar.cs"). Shared by the live-activity reader
/// (<see cref="TranscriptReader"/>) and the history viewer so both surfaces describe tools the same way.
/// </summary>
internal static class ToolSummary
{
    /// <summary>Maps a tool name + its input to a short present-tense phrase.</summary>
    public static string Describe(string tool, JsonNode? input)
    {
        string? Str(string key) => input?[key]?.GetValue<string>();

        switch (tool)
        {
            case "Read":
                return "Reading " + FileLabel(Str("file_path"));
            case "Edit":
            case "MultiEdit":
                return "Editing " + FileLabel(Str("file_path"));
            case "Write":
                return "Writing " + FileLabel(Str("file_path"));
            case "NotebookEdit":
                return "Editing " + FileLabel(Str("notebook_path"));
            case "Bash":
            case "PowerShell":
                return "Running: " + Clip(Str("command") ?? "command");
            case "Grep":
                return "Searching: " + Clip(Str("pattern") ?? "");
            case "Glob":
                return "Finding: " + Clip(Str("pattern") ?? "");
            case "Task":
            case "Agent":
                return "Delegating: " + Clip(Str("description") ?? "sub-agent");
            case "WebFetch":
                return "Fetching " + Clip(Str("url") ?? "");
            case "WebSearch":
                return "Searching web: " + Clip(Str("query") ?? "");
            case "TodoWrite":
                return "Updating todos";
            default:
                return tool;
        }
    }

    public static string FileLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "file";
        try
        {
            // Split on both separators, not Path.GetFileName: transcripts carry paths from whatever OS
            // wrote them, so a Windows path (C:\a\Foo.cs) must resolve to its leaf on a macOS/Linux host too.
            var trimmed = path.TrimEnd('/', '\\');
            int cut = trimmed.LastIndexOfAny(['/', '\\']);
            var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
            return string.IsNullOrEmpty(name) ? Clip(path) : name;
        }
        catch
        {
            return Clip(path);
        }
    }

    public static string Clip(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 60;
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
