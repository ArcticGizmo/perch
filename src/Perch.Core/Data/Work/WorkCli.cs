namespace Perch.Data.Work;

/// <summary>
/// <c>perch work …</c>: work items from a terminal (docs/work-items-plan.md W2). It edits the same markdown files the
/// tray does, through the same read-modify-write, so a running tray picks each change up from its folder watcher and
/// nothing needs the tray to be running. Output is plain text for the user's terminal; every failure is a message and
/// a non-zero exit, never an exception.
/// </summary>
internal static class WorkCli
{
    public const string Help = """
        perch work - name a piece of work and put the sessions doing it inside.

        Usage:
          perch work [list] [--all]          list work items (--all includes done ones)
          perch work new <name>              create a work item and print its id
          perch work show <item>             an item's links, sessions and recent log
          perch work log <item> <text>       add a line to an item's log
          perch work add <item> <session-id> put a session in an item
          perch work remove <item> <session-id>
                                             take a session out of an item
          perch work pause|resume|done <item>
                                             change an item's status (only you ever mark one done)

        <item> is an id, a name, or the start of a name.

        Start sessions inside an item from any terminal:
          PowerShell:  $env:PERCH_WORK = "<item>"; claude
          bash / zsh:  PERCH_WORK="<item>" claude
          Perch:       perch --work <item> [dir]
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, WorkItemStore store)
    {
        try
        {
            var verb = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToList();
            return verb switch
            {
                "list" or "ls" => List(rest, output, store),
                "new" or "create" => New(rest, output, error, store),
                "show" => Show(rest, output, error, store),
                "log" or "note" => Log(rest, output, error, store),
                "add" => Membership(rest, add: true, output, error, store),
                "remove" or "rm" => Membership(rest, add: false, output, error, store),
                "pause" => SetStatus(rest, WorkItemStatus.Paused, output, error, store),
                "resume" or "reopen" or "active" => SetStatus(rest, WorkItemStatus.Active, output, error, store),
                "done" => SetStatus(rest, WorkItemStatus.Done, output, error, store),
                "help" or "--help" or "-h" or "-?" or "/?" => Print(output, Help),
                _ => Fail(error, $"Unknown command \"{args[0]}\". Try: perch work help"),
            };
        }
        catch (Exception ex)
        {
            return Fail(error, $"perch work: {ex.Message}");
        }
    }

    private static int List(List<string> rest, TextWriter output, WorkItemStore store)
    {
        bool all = rest.Any(a => a is "--all" or "-a");
        var items = store.LoadAll().Where(i => all || i.Status != WorkItemStatus.Done)
            .OrderBy(i => i.Status).ToList();
        if (items.Count == 0)
        {
            output.WriteLine(all ? "No work items yet. Create one: perch work new \"<name>\""
                : "No active work items. Create one: perch work new \"<name>\" (perch work list --all shows done ones)");
            return 0;
        }
        foreach (var i in items)
            output.WriteLine($"  {i.Id}  {i.Status.ToString().ToLowerInvariant(),-6}  {i.Name}  ({Plural(i.Members.Count, "session")})");
        foreach (var bad in store.Unreadable)
            output.WriteLine($"  (couldn't read {Path.GetFileName(bad)}, left alone)");
        return 0;
    }

    private static int New(List<string> rest, TextWriter output, TextWriter error, WorkItemStore store)
    {
        var name = string.Join(' ', rest).Trim();
        if (name.Length == 0) return Fail(error, "Give the work item a name: perch work new \"Payments refactor\"");
        if (store.Create(name) is not { } item) return Fail(error, $"Couldn't write a work item in {store.Dir}.");
        output.WriteLine($"Created \"{item.Name}\" ({item.Id}).");
        output.WriteLine("Start sessions in it from any terminal:");
        output.WriteLine($"  PowerShell:  $env:PERCH_WORK = \"{item.Id}\"; claude");
        output.WriteLine($"  bash / zsh:  PERCH_WORK={item.Id} claude");
        output.WriteLine($"  Perch:       perch --work {item.Id}");
        return 0;
    }

    private static int Show(List<string> rest, TextWriter output, TextWriter error, WorkItemStore store)
    {
        if (Find(rest, error, store, out var item) is { } code) return code;
        output.WriteLine($"{item.Name}  ({item.Id}, {item.Status.ToString().ToLowerInvariant()})");
        output.WriteLine($"  file: {item.FilePath}");
        foreach (var l in item.Links)
            output.WriteLine($"  link: {(l.Label.Length > 0 ? l.Label + " " : "")}{l.Url}");
        foreach (var m in item.Members)
            output.WriteLine($"  session: {m.SessionId}  {m.Title ?? ""}  ({m.Via.ToString().ToLowerInvariant()})");
        foreach (var e in item.Log.TakeLast(10))
            output.WriteLine("  " + WorkItemFile.FormatLogLine(e)[2..]);
        return 0;
    }

    private static int Log(List<string> rest, TextWriter output, TextWriter error, WorkItemStore store)
    {
        if (rest.Count < 2) return Fail(error, "Usage: perch work log <item> <text>");
        if (Find(rest.Take(1).ToList(), error, store, out var item) is { } code) return code;
        var text = string.Join(' ', rest.Skip(1)).Trim();
        if (text.Length == 0) return Fail(error, "Nothing to log.");
        if (store.Mutate(item.Id, i => i.AddLog(text, byPerch: false)) is null)
            return Fail(error, $"Couldn't write {item.FilePath}.");
        output.WriteLine($"Logged to \"{item.Name}\".");
        return 0;
    }

    private static int Membership(List<string> rest, bool add, TextWriter output, TextWriter error, WorkItemStore store)
    {
        if (rest.Count < 2) return Fail(error, $"Usage: perch work {(add ? "add" : "remove")} <item> <session-id>");
        var sid = rest[^1].Trim();
        if (!ClaudeCli.IsSessionId(sid)) return Fail(error, $"\"{sid}\" doesn't look like a session id.");
        if (Find(rest.Take(rest.Count - 1).ToList(), error, store, out var item) is { } code) return code;

        bool changed = false;
        var written = store.Mutate(item.Id, i =>
        {
            changed = add ? i.AddMember(sid, WorkJoin.Manual, null, null) : i.RemoveMember(sid);
            if (changed)
                i.AddLog($"Session {sid[..Math.Min(8, sid.Length)]} {(add ? "joined (manual)" : "taken out")}", byPerch: true);
            return changed;
        });
        if (written is null) return Fail(error, $"Couldn't write {item.FilePath}.");
        output.WriteLine(changed
            ? add ? $"Added to \"{item.Name}\"." : $"Taken out of \"{item.Name}\"."
            : add ? $"Already in \"{item.Name}\"." : $"Wasn't in \"{item.Name}\".");
        return 0;
    }

    private static int SetStatus(List<string> rest, WorkItemStatus status, TextWriter output, TextWriter error, WorkItemStore store)
    {
        if (Find(rest, error, store, out var item) is { } code) return code;
        if (item.Status == status)
        {
            output.WriteLine($"\"{item.Name}\" is already {status.ToString().ToLowerInvariant()}.");
            return 0;
        }
        var written = store.Mutate(item.Id, i =>
        {
            i.Status = status;
            i.AddLog(status switch
            {
                WorkItemStatus.Done => "Marked done",
                WorkItemStatus.Paused => "Paused",
                _ => "Resumed",
            }, byPerch: true);
        });
        if (written is null) return Fail(error, $"Couldn't write {item.FilePath}.");
        output.WriteLine($"\"{item.Name}\" is now {status.ToString().ToLowerInvariant()}.");
        return 0;
    }

    // Resolves the item named by the words in rest (a name may have spaces). Null on success with `item` set; else
    // the exit code, with the reason written to error.
    private static int? Find(List<string> rest, TextWriter error, WorkItemStore store, out WorkItem item)
    {
        item = null!;
        var reference = string.Join(' ', rest).Trim();
        if (reference.Length == 0) return Fail(error, "Which work item? Give its id or name (perch work list shows them).");
        var items = store.LoadAll();
        if (WorkItemStore.Resolve(reference, items) is not { } found)
            return Fail(error, $"No work item matches \"{reference}\" (perch work list shows them).");
        item = found;
        return null;
    }

    private static int Print(TextWriter output, string text)
    {
        output.WriteLine(text);
        return 0;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 1;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
