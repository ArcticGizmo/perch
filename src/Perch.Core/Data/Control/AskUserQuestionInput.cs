using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>One option of an <c>AskUserQuestion</c> question.</summary>
internal sealed record UserQuestionOption(string Label, string Description);

/// <summary>One question Claude is asking the user via the <c>AskUserQuestion</c> tool.</summary>
internal sealed record UserQuestion(string Question, string Header, IReadOnlyList<UserQuestionOption> Options, bool MultiSelect);

/// <summary>
/// The <c>AskUserQuestion</c> tool as it crosses the stream-json control channel. It arrives as an ordinary
/// <c>can_use_tool</c> permission request whose <c>input</c> carries the questions; the client answers by
/// <em>allowing</em> the tool with an <c>updatedInput</c> that adds an <c>answers</c> object mapping each
/// question's text to the chosen option label(s) — the Agent SDK convention (multi-select labels are joined
/// with ", "). So in the rich UI it is not a permission prompt at all but a question card; this type does the
/// parsing and the answer-building, UI-free and tested.
/// </summary>
internal static class AskUserQuestionInput
{
    public const string ToolName = "AskUserQuestion";

    /// <summary>Parses the tool input's <c>questions</c>; empty on any malformed shape.</summary>
    public static IReadOnlyList<UserQuestion> Parse(string inputJson)
    {
        try
        {
            if (JsonNode.Parse(inputJson)?["questions"] is not JsonArray questions) return [];
            var list = new List<UserQuestion>();
            foreach (var q in questions)
            {
                var text = TranscriptJson.AsString(q?["question"]);
                if (string.IsNullOrEmpty(text)) continue;
                var options = new List<UserQuestionOption>();
                if (q?["options"] is JsonArray opts)
                    foreach (var o in opts)
                    {
                        var label = TranscriptJson.AsString(o?["label"]);
                        if (!string.IsNullOrEmpty(label))
                            options.Add(new UserQuestionOption(label, TranscriptJson.AsString(o?["description"]) ?? ""));
                    }
                list.Add(new UserQuestion(
                    text,
                    TranscriptJson.AsString(q?["header"]) ?? "",
                    options,
                    q?["multiSelect"]?.GetValue<bool>() ?? false));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The <c>updatedInput</c> for the allow reply: the original input plus <c>answers</c>
    /// (question text → chosen label, multi-select labels joined with ", ").</summary>
    public static JsonObject BuildAnswer(string inputJson, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        JsonObject input;
        try { input = JsonNode.Parse(inputJson) as JsonObject ?? new JsonObject(); }
        catch { input = new JsonObject(); }
        var a = new JsonObject();
        foreach (var (question, labels) in answers) a[question] = string.Join(", ", labels);
        input["answers"] = a;
        return input;
    }

    /// <summary>A one-line receipt: "Fruit: Apple · Size: Large".</summary>
    public static string Summarise(IReadOnlyList<UserQuestion> questions, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var parts = new List<string>();
        foreach (var q in questions)
        {
            if (!answers.TryGetValue(q.Question, out var labels) || labels.Count == 0) continue;
            var key = q.Header.Length > 0 ? q.Header : ToolSummary.Clip(q.Question);
            parts.Add($"{key}: {string.Join(", ", labels)}");
        }
        return string.Join("  ·  ", parts);
    }
}
