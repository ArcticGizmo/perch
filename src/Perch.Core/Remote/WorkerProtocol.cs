using System.Text.Json;
using System.Text.Json.Nodes;
using Perch.Data.Control;

namespace Perch.Remote;

/// <summary>One live session a worker offers, as its controllers list it. <paramref name="Sid"/> is the worker's handle
/// for it, fixed for the session's life; <paramref name="SessionId"/> is Claude's id, which a <c>/clear</c> changes.
/// <paramref name="State"/> is where its turn stands: <see cref="WorkerProtocol.Busy"/>, <see cref="WorkerProtocol.Waiting"/>
/// (on a permission or question) or <see cref="WorkerProtocol.Idle"/>.</summary>
internal sealed record WorkerSessionInfo(
    string Sid, string? SessionId, string? Title, string Cwd, string? Model, string? Mode, string? Effort, bool Running,
    string State = WorkerProtocol.Idle);

/// <summary>
/// The session frames two paired Perch machines exchange once connected (docs/remote-workers-plan.md, W3). Every frame
/// names its session by the worker's <c>sid</c>.
/// <list type="bullet">
/// <item>Controller → worker: <c>list</c>, <c>attach</c>, <c>detach</c>, <c>prompt</c>, <c>answer</c>, <c>interrupt</c>,
/// <c>mode</c>, <c>model</c>, <c>effort</c>, <c>stopTask</c>.</item>
/// <item>Worker → controller: <c>sessions</c> (on <c>list</c> and on any change), <c>history</c> (catch-up, in chunks
/// until <c>done</c>), <c>line</c> (one raw stream-json line), <c>ended</c>, <c>error</c>.</item>
/// </list>
/// Unknown frame types are ignored, so either side can add one without breaking the other.
/// </summary>
internal static class WorkerProtocol
{
    public const string Busy = "busy";
    public const string Waiting = "waiting";
    public const string Idle = "idle";

    /// <summary>How much catch-up goes in one <c>history</c> frame, in characters. JSON escaping can grow text up to six
    /// times (non-ASCII as <c>\uXXXX</c>), which still fits <see cref="RemoteLink.MaxFrameBytes"/>.</summary>
    public const int HistoryChunkChars = 1024 * 1024;

    /// <summary>The image types a prompt may carry: the ones the API takes.</summary>
    public static readonly string[] ImageMediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>The longest deny message a controller may send.</summary>
    public const int MaxDenyMessageChars = 2000;

    public static JsonObject SessionsFrame(IEnumerable<WorkerSessionInfo> sessions)
    {
        var list = new JsonArray();
        foreach (var s in sessions)
            list.Add(new JsonObject
            {
                ["sid"] = s.Sid,
                ["sessionId"] = s.SessionId,
                ["title"] = s.Title,
                ["cwd"] = s.Cwd,
                ["model"] = s.Model,
                ["mode"] = s.Mode,
                ["effort"] = s.Effort,
                ["running"] = s.Running,
                ["state"] = s.State,
            });
        var frame = RemoteLink.Frame("sessions");
        frame["sessions"] = list;
        return frame;
    }

    /// <summary>The sessions a worker listed; entries without a sid are skipped. Every text is the worker's, so the
    /// controller shows it as text only.</summary>
    public static List<WorkerSessionInfo> ParseSessions(JsonObject frame)
    {
        var result = new List<WorkerSessionInfo>();
        if (frame["sessions"] is not JsonArray list) return result;
        foreach (var node in list)
        {
            if (node is not JsonObject o || RemoteLink.Str(o, "sid") is not { Length: > 0 } sid) continue;
            result.Add(new WorkerSessionInfo(sid, RemoteLink.Str(o, "sessionId"), RemoteLink.Str(o, "title"),
                RemoteLink.Str(o, "cwd") ?? "", RemoteLink.Str(o, "model"), RemoteLink.Str(o, "mode"),
                RemoteLink.Str(o, "effort"), Bool(o, "running"), RemoteLink.Str(o, "state") switch
                {
                    Busy => Busy,
                    Waiting => Waiting,
                    _ => Idle,
                }));
        }
        return result;
    }

    /// <summary>The catch-up for one session as <c>history</c> frames, each under <see cref="HistoryChunkChars"/> (a single
    /// longer line goes alone). The last has <c>done</c>.</summary>
    public static IEnumerable<JsonObject> HistoryFrames(string sid, IReadOnlyList<string> transcript, IReadOnlyList<string> lines,
        bool truncated)
    {
        var frame = NewHistory();
        long chars = 0;
        foreach (var (key, source) in new[] { ("transcript", transcript), ("lines", lines) })
            foreach (var line in source)
            {
                if (chars > 0 && chars + line.Length > HistoryChunkChars)
                {
                    yield return frame;
                    frame = NewHistory();
                    chars = 0;
                }
                ((JsonArray)frame[key]!).Add(line);
                chars += line.Length;
            }
        frame["done"] = true;
        yield return frame;

        JsonObject NewHistory()
        {
            var f = Frame("history", sid);
            f["transcript"] = new JsonArray();
            f["lines"] = new JsonArray();
            f["truncated"] = truncated;
            return f;
        }
    }

    public static JsonObject Frame(string type, string sid)
    {
        var frame = RemoteLink.Frame(type);
        frame["sid"] = sid;
        return frame;
    }

    public static JsonObject PromptFrame(string sid, string uuid, string text, IReadOnlyList<ImageContent>? images)
    {
        var frame = Frame("prompt", sid);
        frame["uuid"] = uuid;
        frame["text"] = text;
        if (images is { Count: > 0 })
        {
            var list = new JsonArray();
            foreach (var img in images) list.Add(new JsonObject { ["mediaType"] = img.MediaType, ["data"] = img.Base64 });
            frame["images"] = list;
        }
        return frame;
    }

    /// <summary>A prompt's images, checked as the worker would check its own user's: a known type, real base64, at most
    /// <paramref name="maxBytes"/> decoded. Null when there are none; throws <see cref="InvalidDataException"/> for a
    /// bad one, which refuses the whole prompt.</summary>
    public static IReadOnlyList<ImageContent>? ParseImages(JsonObject frame, long maxBytes)
    {
        if (frame["images"] is not JsonArray list || list.Count == 0) return null;
        var images = new List<ImageContent>();
        foreach (var node in list)
        {
            var media = RemoteLink.Str(node as JsonObject, "mediaType");
            var data = RemoteLink.Str(node as JsonObject, "data");
            if (media is null || Array.IndexOf(ImageMediaTypes, media) < 0) throw new InvalidDataException("unsupported image type");
            if (data is null) throw new InvalidDataException("image has no data");
            var buffer = new byte[data.Length * 3 / 4 + 3];
            if (!Convert.TryFromBase64String(data, buffer, out int written)) throw new InvalidDataException("image isn't base64");
            if (written > maxBytes) throw new InvalidDataException("image is too large");
            images.Add(new ImageContent(media, data));
        }
        return images;
    }

    public static bool Bool(JsonObject? frame, string key) =>
        frame?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    public static int? Int(JsonObject? frame, string key)
    {
        if (frame?[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        return v.TryGetValue<int>(out var n) ? n : null;
    }
}
