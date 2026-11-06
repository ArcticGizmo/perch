using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Perch.Data.Resources;

/// <summary>Reads and writes resource-recording lines. Samples are positional arrays rather than objects because
/// they are most of the file. Parsing never throws: a line that isn't a well-formed record is null, so a
/// truncated last line (the recorder died mid-write) is simply skipped.</summary>
public static class ResourceRecordCodec
{
    public static string Write(ResourceRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            switch (record)
            {
                case ResourceHeader h:
                    w.WriteString("t", "header");
                    w.WriteNumber("v", h.Version);
                    w.WriteString("started", Iso(h.StartedUtc));
                    w.WriteNumber("intervalMs", h.IntervalMs);
                    w.WriteNumber("cores", h.Cores);
                    w.WriteNumber("totalRam", h.TotalRam);
                    w.WriteNumber("self", h.SelfPid);
                    break;
                case ResourceProc p:
                    w.WriteString("t", "proc");
                    w.WriteNumber("id", p.Id);
                    w.WriteNumber("pid", p.Pid);
                    w.WriteNumber("ppid", p.ParentPid);
                    w.WriteString("name", p.Name);
                    if (p.StartUtc is { } start) w.WriteString("start", Iso(start));
                    if (p.CommandLine is { } cmd) w.WriteString("cmd", cmd);
                    if (p.Services is { Count: > 0 } services)
                    {
                        w.WriteStartArray("svc");
                        foreach (var s in services)
                        {
                            w.WriteStartArray();
                            w.WriteStringValue(s.Name);
                            w.WriteStringValue(s.DisplayName);
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                    break;
                case ResourceSession s:
                    w.WriteString("t", "session");
                    w.WriteNumber("pid", s.Pid);
                    w.WriteString("sid", s.SessionId);
                    w.WriteString("label", s.Label);
                    if (s.Cwd is { } cwd) w.WriteString("cwd", cwd);
                    break;
                case ResourceTick t:
                    w.WriteString("t", "tick");
                    w.WriteString("at", Iso(t.AtUtc));
                    w.WriteNumber("cpu", Round(t.Cpu));
                    w.WriteNumber("ram", t.RamUsed);
                    w.WriteNumber("commit", t.CommitUsed);
                    w.WriteNumber("commitLimit", t.CommitLimit);
                    w.WriteStartArray("p");
                    foreach (var s in t.Procs)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(s.Id);
                        w.WriteNumberValue(Round(s.Cpu));
                        w.WriteNumberValue(s.Memory);
                        w.WriteNumberValue(s.Commit);
                        w.WriteNumberValue(s.IoReadPerSec);
                        w.WriteNumberValue(s.IoWritePerSec);
                        w.WriteNumberValue(s.WorkingSet);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("rest");
                    w.WriteNumberValue(Round(t.Rest.Cpu));
                    w.WriteNumberValue(t.Rest.Memory);
                    w.WriteNumberValue(t.Rest.Count);
                    w.WriteEndArray();
                    if (t.Kernel is { } k)
                    {
                        w.WriteStartArray("kmem");
                        w.WriteNumberValue(k.PagedPoolResident);
                        w.WriteNumberValue(k.NonPagedPool);
                        w.WriteNumberValue(k.SystemCacheResident);
                        w.WriteNumberValue(k.DriverResident);
                        w.WriteNumberValue(k.ModifiedList);
                        w.WriteEndArray();
                    }
                    if (t.InterruptCpu > 0) w.WriteNumber("irq", Round(t.InterruptCpu));
                    if (t.NewProcesses is { } fresh) w.WriteNumber("new", fresh);
                    break;
                case ResourceHook { Hook: var h }:
                    w.WriteString("t", "hook");
                    w.WriteString("src", h.Source);
                    w.WriteString("kind", h.Kind.ToString());
                    if (h.PluginId is { } plugin) w.WriteString("plugin", plugin);
                    w.WriteString("file", h.File);
                    w.WriteString("event", h.Event);
                    w.WriteString("matcher", h.Matcher);
                    w.WriteString("cmd", h.Command);
                    break;
                case ResourceEnd e:
                    w.WriteString("t", "end");
                    w.WriteString("at", Iso(e.AtUtc));
                    w.WriteString("reason", e.Reason);
                    break;
                default:
                    throw new ArgumentException($"Unknown record {record.GetType().Name}", nameof(record));
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static ResourceRecord? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            return Str(o, "t") switch
            {
                "header" => new ResourceHeader(Int(o, "v"), Date(o, "started") ?? default, Int(o, "intervalMs"),
                    Int(o, "cores"), Long(o, "totalRam"), Int(o, "self")),
                "proc" => new ResourceProc(Int(o, "id"), Int(o, "pid"), Int(o, "ppid"), Str(o, "name") ?? "",
                    Date(o, "start"), Str(o, "cmd"), Services(o)),
                "session" => new ResourceSession(Int(o, "pid"), Str(o, "sid") ?? "", Str(o, "label") ?? "", Str(o, "cwd")),
                "tick" => ParseTick(o),
                "end" => new ResourceEnd(Date(o, "at") ?? default, Str(o, "reason") ?? ""),
                "hook" => new ResourceHook(new HookDef(Str(o, "src") ?? "", Enum.TryParse<HookSourceKind>(Str(o, "kind"), out var kind) ? kind : HookSourceKind.UserSettings,
                    Str(o, "file") ?? "", Str(o, "event") ?? "", Str(o, "matcher") ?? "", Str(o, "cmd") ?? "", Str(o, "plugin"))),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static ResourceTick? ParseTick(JsonElement o)
    {
        if (Date(o, "at") is not { } at) return null;
        var procs = new List<ProcSample>();
        if (o.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in p.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;
                procs.Add(new ProcSample(row[0].GetInt32(), row[1].GetDouble(), row[2].GetInt64(), row[3].GetInt64(),
                    row[4].GetInt64(), row[5].GetInt64(), row.GetArrayLength() > 6 ? row[6].GetInt64() : 0));
            }
        }
        var rest = default(RestSample);
        if (o.TryGetProperty("rest", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 3)
            rest = new RestSample(r[0].GetDouble(), r[1].GetInt64(), r[2].GetInt32());
        Perch.Platform.KernelMemory? kernel = null;
        if (o.TryGetProperty("kmem", out var k) && k.ValueKind == JsonValueKind.Array && k.GetArrayLength() >= 5)
            kernel = new Perch.Platform.KernelMemory(k[0].GetInt64(), k[1].GetInt64(), k[2].GetInt64(), k[3].GetInt64(), k[4].GetInt64());
        int? fresh = o.TryGetProperty("new", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
        return new ResourceTick(at, Double(o, "cpu"), Long(o, "ram"), Long(o, "commit"), Long(o, "commitLimit"), procs, rest,
            kernel, Double(o, "irq"), fresh);
    }

    private static List<Perch.Platform.ServiceInfo>? Services(JsonElement o)
    {
        if (!o.TryGetProperty("svc", out var svc) || svc.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Perch.Platform.ServiceInfo>();
        foreach (var row in svc.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
            if (row[0].ValueKind != JsonValueKind.String || row[0].GetString() is not { Length: > 0 } name) continue;
            string display = row[1].ValueKind == JsonValueKind.String && row[1].GetString() is { Length: > 0 } d ? d : name;
            list.Add(new Perch.Platform.ServiceInfo(name, display));
        }
        return list.Count > 0 ? list : null;
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static double Round(double v) => Math.Round(v, 1);

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    private static double Double(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DateTime? Date(JsonElement o, string name) =>
        Str(o, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
}
