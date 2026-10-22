using System.Text;

namespace Perch.Data.Work;

/// <summary>
/// The user's work items: one markdown file each in the profile's <c>work/</c> folder (docs/work-items-plan.md D6),
/// next to <c>settings.json</c>. Shared by the tray, the <c>perch work …</c> CLI and the user's own editor.
///
/// <para><b>Every change is a read-modify-write of one file</b> (<see cref="Mutate"/>): the file is read fresh,
/// changed, and replaced atomically. So the tray, a CLI call and a hand edit can all touch one item without one of
/// them writing back a stale copy over another's change. A file that won't parse is never rewritten, and a file that
/// can't be read is skipped rather than treated as empty.</para>
///
/// <para>IO is a handful of small local files, so callers may run it inline; nothing here throws.</para>
/// </summary>
internal sealed class WorkItemStore
{
    /// <summary>The profile's work folder: <c>%APPDATA%\Perch\work</c> (or <c>Perch (Dev)</c>).</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppProfile.DataFolderName, "work");

    public string Dir { get; }

    // id → the file it was last seen in, refreshed by every LoadAll.
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public WorkItemStore(string dir) => Dir = dir;

    public static WorkItemStore Open() => new(DefaultDir);

    /// <summary>Files in the folder that exist but couldn't be read or parsed at the last <see cref="LoadAll"/>.
    /// Shown to the user so a broken hand edit isn't silently missing.</summary>
    public IReadOnlyList<string> Unreadable { get; private set; } = [];

    /// <summary>Every item in the folder, oldest first. Unreadable files are skipped (and listed in
    /// <see cref="Unreadable"/>); two files claiming one id keep the first.</summary>
    public IReadOnlyList<WorkItem> LoadAll()
    {
        lock (_lock)
        {
            var items = new List<WorkItem>();
            var bad = new List<string>();
            _paths.Clear();
            string[] files;
            try { files = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.md") : []; }
            catch { files = []; }

            foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Read(path) is not { } item) { bad.Add(path); continue; }
                if (!_paths.TryAdd(item.Id, path)) continue;
                items.Add(item);
            }
            Unreadable = bad;
            return items.OrderBy(i => i.CreatedUtc).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Creates and writes a new item named <paramref name="name"/>, in the next colour no other item uses.
    /// Null when the name is blank or the file can't be written.</summary>
    public WorkItem? Create(string name, string? notes = null, DateTime? nowUtc = null)
    {
        name = (name ?? "").ReplaceLineEndings(" ").Trim();
        if (name.Length == 0) return null;
        lock (_lock)
        {
            var existing = LoadAll();
            var ids = existing.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            string id;
            do id = Guid.NewGuid().ToString("N")[..8]; while (ids.Contains(id));

            var item = new WorkItem
            {
                Id = id,
                Name = name,
                Colour = WorkHues.NextFree(existing.Where(i => i.Status != WorkItemStatus.Done).Select(i => i.Colour)),
                CreatedUtc = nowUtc ?? DateTime.UtcNow,
                Notes = notes?.Trim() ?? "",
            };
            item.FilePath = Path.Combine(Dir, $"{Slug(name)}-{id}.md");
            if (!Write(item)) return null;
            _paths[id] = item.FilePath;
            return item;
        }
    }

    /// <summary>
    /// Reads the item <paramref name="id"/> fresh from its file, applies <paramref name="change"/>, and writes it back
    /// atomically. Returns the item as written, or null when there's no such item, its file can't be read, or the write
    /// failed (in which case nothing changed on disk). <paramref name="change"/> returning false means "nothing to do":
    /// the file isn't touched and the current item is returned.
    /// </summary>
    public WorkItem? Mutate(string id, Func<WorkItem, bool> change)
    {
        lock (_lock)
        {
            var item = Find(id);
            if (item is null) return null;
            if (!change(item)) return item;
            return Write(item) ? item : null;
        }
    }

    /// <summary><see cref="Mutate(string, Func{WorkItem, bool})"/> for a change that always writes.</summary>
    public WorkItem? Mutate(string id, Action<WorkItem> change) => Mutate(id, i => { change(i); return true; });

    /// <summary>Deletes the item's file. For mistakes; finishing an item is <see cref="WorkItemStatus.Done"/>.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (Find(id)?.FilePath is not { } path) return false;
            try { File.Delete(path); _paths.Remove(id); return true; }
            catch { return false; }
        }
    }

    // The item with this id, read fresh from disk: the known path first, then a rescan (the file may be new, or a
    // hand edit may have renamed it).
    private WorkItem? Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_paths.TryGetValue(id, out var path) && Read(path) is { } known && known.Id == id) return known;
        LoadAll();
        return _paths.TryGetValue(id, out path) ? Read(path) : null;
    }

    private static WorkItem? Read(string path)
    {
        if (AtomicFile.TryRead(path, out var text) != AtomicFile.ReadResult.Ok) return null;
        var item = WorkItemFile.Parse(text, Path.GetFileNameWithoutExtension(path));
        if (item is not null) item.FilePath = path;
        return item;
    }

    private static bool Write(WorkItem item)
    {
        if (item.FilePath is not { } path) return false;
        try
        {
            AtomicFile.Write(path, WorkItemFile.Serialize(item));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Refs ──

    /// <summary>
    /// The item a user-typed ref names (<c>PERCH_WORK</c>, <c>--work</c>, <c>perch work …</c>), trying in order: its
    /// id, its file stem, the slug of its name, its name (case-insensitive), then a name prefix that matches exactly one
    /// item. Active items win over paused, paused over done, when the same name is used twice. Null when nothing (or
    /// more than one thing, for a prefix) matches. Never creates an item.
    /// </summary>
    public static WorkItem? Resolve(string? reference, IReadOnlyList<WorkItem> items)
    {
        var r = (reference ?? "").Trim();
        if (r.Length == 0) return null;
        var ordered = items.OrderBy(i => i.Status).ToList();
        var slug = Slug(r);

        return ordered.FirstOrDefault(i => string.Equals(i.Id, r, StringComparison.OrdinalIgnoreCase))
            ?? ordered.FirstOrDefault(i => i.FilePath is { } p &&
                   string.Equals(Path.GetFileNameWithoutExtension(p), r, StringComparison.OrdinalIgnoreCase))
            ?? ordered.FirstOrDefault(i => Slug(i.Name) == slug)
            ?? ordered.FirstOrDefault(i => string.Equals(i.Name.Trim(), r, StringComparison.OrdinalIgnoreCase))
            ?? UniquePrefix(ordered, r);
    }

    private static WorkItem? UniquePrefix(List<WorkItem> items, string r)
    {
        var hits = items.Where(i => i.Name.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                                    || Slug(i.Name).StartsWith(Slug(r), StringComparison.Ordinal)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>A file-name-safe slug: lower-case ASCII letters and digits, everything else folded to single hyphens,
    /// at most 40 characters. "work" when nothing usable is left.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? "").Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "work" : s;
    }
}
