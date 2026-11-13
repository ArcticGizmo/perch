using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data.Control;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// The resume bench (perf review PF12), run by <c>render</c> under <c>PERCH_BENCH=1</c>. Resuming a long session
/// froze the window: its history was decoded and every item built on the UI thread in one go. This times the same
/// path a resumed session window takes: a synthetic ~3,600-line transcript (or a real one named by
/// <c>PERCH_RESUME_TRANSCRIPT</c>) loaded into a conversation already bound to a full-size thread.
/// </summary>
internal static class ResumeBench
{
    private const int SyntheticTurns = 400;

    public static void Run(string outDir, string cwd)
    {
        if (Environment.GetEnvironmentVariable("PERCH_BENCH") != "1") return;

        Measure("synthetic", Synthetic(SyntheticTurns), cwd, Path.Combine(outDir, "session_resumed_1x.png"));
        if (Environment.GetEnvironmentVariable("PERCH_RESUME_TRANSCRIPT") is { Length: > 0 } real && File.Exists(real))
        {
            var (lines, _) = Perch.Data.TranscriptScan.LastLines(
                Perch.Data.TranscriptChain.ActiveLines(() => File.ReadLines(real), null), 4000);
            Measure(Path.GetFileName(real), lines, cwd, null);
        }
    }

    private static void Measure(string label, IReadOnlyList<string> lines, string cwd, string? capture)
    {
        var p = SessionPalette.For(dark: true);
        var conv = new SessionConversation();
        conv.AddNote("resumed session 0000…");
        var thread = new SessionThreadView(p) { Cwd = cwd };
        var w = new Window { Width = 880, Height = 980, Background = p.Surface, Content = thread };
        thread.Bind(conv);
        w.Show();
        Settle(w);

        // What the worker does before the history reaches the UI thread.
        var sw = Stopwatch.StartNew();
        var parsed = SessionConversation.ParseHistory(lines, "0000");
        double workerMs = sw.Elapsed.TotalMilliseconds;

        // The UI thread's single block: apply, the rebuild it triggers, and the first layout of what got built.
        sw.Restart();
        int items = conv.LoadHistory(parsed);
        w.UpdateLayout();
        double blockMs = sw.Elapsed.TotalMilliseconds;
        var builtAtBlock = thread.BuiltItems;

        // Whatever the view defers (older items prepended in short slices) until the column settles. The bench runs
        // the slices back to back; in the app other input runs between them, so the longest slice is what a user feels.
        SessionThreadView.LongestBacklogSliceMs = 0;
        sw.Restart();
        int passes = 0;
        for (var last = (-1, -1); passes < 2000; passes++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var now = thread.BuiltItems;
            if (now == last && passes > 6) break;
            last = now;
        }
        double drainMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"resume bench ({label}): {lines.Count:N0} lines -> {items:N0} items; " +
                          $"worker parse {workerMs:N0} ms; UI block {blockMs:N0} ms (built {builtAtBlock.Built}/{builtAtBlock.Total}); " +
                          $"deferred build {drainMs:N0} ms, longest slice {SessionThreadView.LongestBacklogSliceMs:N0} ms " +
                          $"(built {thread.BuiltItems.Built}/{thread.BuiltItems.Total})");

        if (capture is not null && w.CaptureRenderedFrame() is { } frame)
        {
            using var fs = File.Create(capture);
            frame.Save(fs);
        }
        thread.Unbind();
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window w)
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // A transcript shaped like a long agentic session: per turn a prompt, a thought, Grep/Read/Edit/Bash calls with
    // their results and two markdown replies, one record per line as Claude Code writes them.
    private static List<string> Synthetic(int turns)
    {
        var read = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"{i,6}\t    var line{i} = Compute(screen, {i});"));
        var oldBody = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"    var v{i} = px{i} / scale;"));
        var newBody = string.Join("\n", Enumerable.Range(1, 30).Select(i => i % 3 == 0 ? $"    var v{i} = Math.Round(px{i} / scale);" : $"    var v{i} = px{i} / scale;"));
        var tests = "Passed!  -  Failed: 0, Passed: 2238, Skipped: 1  ·  58s\n\n" +
                    string.Join("\n", Enumerable.Range(1, 12).Select(i => $"  PlacementMathTests.Case{i} [OK]"));
        var lines = new List<string>();
        int n = 0;
        string Ts() => DateTime.UtcNow.AddSeconds(n).ToString("O");

        void User(JsonNode content) => lines.Add(new JsonObject
        {
            ["type"] = "user", ["uuid"] = $"u{++n}", ["timestamp"] = Ts(),
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        }.ToJsonString());
        void Assistant(JsonObject block) => lines.Add(new JsonObject
        {
            ["type"] = "assistant", ["uuid"] = $"a{++n}", ["timestamp"] = Ts(),
            ["message"] = new JsonObject
            {
                ["role"] = "assistant", ["model"] = "claude-opus-5-5", ["content"] = new JsonArray(block),
                ["usage"] = new JsonObject { ["input_tokens"] = 12, ["cache_read_input_tokens"] = 40000 + n },
            },
        }.ToJsonString());
        void Tool(string id, string name, JsonObject input, string result)
        {
            Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input });
            User(new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = result, ["is_error"] = false }));
        }

        for (int t = 1; t <= turns; t++)
        {
            User(JsonValue.Create($"Turn {t}: tighten the placement maths for monitor {t % 4} and re-run the tests.")!);
            Assistant(new JsonObject
            {
                ["type"] = "thinking",
                ["thinking"] = $"Turn {t}: the clamp runs before the DPI conversion, so a shrunken screen pulls the window inward.",
            });
            Tool($"g{t}", "Grep", new JsonObject { ["pattern"] = "PlacementMath" }, "6 files");
            Tool($"r{t}", "Read", new JsonObject { ["file_path"] = "src/Perch.Core/Data/PlacementMath.cs" }, read);
            Assistant(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"**Turn {t}.** The drift comes from `PlacementMath.Normalize` in `src/Perch.Core/Data/PlacementMath.cs` rounding late:\n\n" +
                           "1. Round the DIP offset as soon as it's converted.\n2. Clamp against the target monitor's bounds.\n\n" +
                           "```csharp\nvar dip = Math.Round(px / scale);\nreturn Clamp(dip, bounds);\n```",
            });
            Tool($"e{t}", "Edit", new JsonObject
            {
                ["file_path"] = "src/Perch.Core/Data/PlacementMath.cs", ["old_string"] = oldBody, ["new_string"] = newBody,
            }, "The file has been updated.");
            Tool($"b{t}", "Bash", new JsonObject { ["command"] = "dotnet test" }, tests);
            Assistant(new JsonObject { ["type"] = "text", ["text"] = $"Turn {t} is green — the corner distance now survives a resolution change." });
        }
        return lines;
    }
}
