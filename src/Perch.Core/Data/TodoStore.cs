namespace Perch.Data;

using System.Text.Json;

/// <summary>
/// The user's to-do / reminder list, persisted per-profile next to <c>settings.json</c> as
/// <c>todos.json</c>. Modelled on <see cref="AchievementStore"/>: a private ctor, a static
/// <see cref="Load"/> (plus an <see cref="LoadFrom"/> seam so tests round-trip a temp path), and
/// best-effort IO throughout — a read/write failure degrades to an empty list rather than throwing, but never
/// to an empty list that then overwrites the real one (see <see cref="LoadFrom"/>).
///
/// <para>Deliberately its own file rather than an <see cref="AppSettings"/> collection: the list is
/// edited from a dedicated window and polled by a monitor host, so it has no place in the settings
/// catalogue and doesn't need registry coverage.</para>
/// </summary>
internal sealed class TodoStore
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "todos.json");

    private readonly string _path;
    private readonly List<Todo> _todos;

    /// <summary>True when the file exists but couldn't be read at load (locked, permissions): the list is an empty
    /// stand-in, so <see cref="Save"/> declines rather than overwrite todos it never saw (review fixes CP14).</summary>
    internal bool SaveSuppressed { get; }

    private TodoStore(string path, List<Todo> todos, bool saveSuppressed = false)
    {
        _path = path;
        _todos = todos;
        SaveSuppressed = saveSuppressed;
    }

    public static TodoStore Load() => LoadFrom(DefaultPath);

    /// <summary>Where a <c>todos.json</c> that wouldn't parse is copied before the next save replaces it.</summary>
    internal static string UnreadableCopyPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", "todos.unreadable.json");

    // internal for tests — round-trips through a temp path without touching the real profile.
    // A read failure and a parse failure are both "not an empty list" (review fixes CP14): an unreadable file
    // suppresses saving for the session, and one that won't parse is copied aside (latest wins, like
    // settings.unreadable.json) before the list starts over — so the next Save can never silently erase todos.
    internal static TodoStore LoadFrom(string path)
    {
        switch (AtomicFile.TryRead(path, out var json))
        {
            case AtomicFile.ReadResult.Missing:
                return new TodoStore(path, []);
            case AtomicFile.ReadResult.Failed:
                return new TodoStore(path, [], saveSuppressed: true);
        }
        try
        {
            return new TodoStore(path, JsonSerializer.Deserialize<Model>(json)?.Todos ?? []);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { AtomicFile.Write(UnreadableCopyPath(path), json); }
                catch { return new TodoStore(path, [], saveSuppressed: true); }   // couldn't keep a copy: don't overwrite
            }
            return new TodoStore(path, []);
        }
    }

    /// <summary>Every todo, in stored order (newest additions last).</summary>
    public IReadOnlyList<Todo> All() => _todos;

    public Todo Add(string title, string description, DateTime? dueUtc)
    {
        var todo = new Todo
        {
            Title = title?.Trim() ?? "",
            Description = description?.Trim() ?? "",
            DueUtc = dueUtc,
        };
        _todos.Add(todo);
        return todo;
    }

    /// <summary>Replaces the stored todo sharing <paramref name="edited"/>'s <see cref="Todo.Id"/> with a
    /// clone of it. Returns false when no such id exists.</summary>
    public bool Update(Todo edited)
    {
        int i = _todos.FindIndex(t => t.Id == edited.Id);
        if (i < 0) return false;
        _todos[i] = edited.Clone();
        return true;
    }

    /// <summary>Marks the todo complete (idempotent). Returns false when the id is unknown.</summary>
    public bool Complete(string id)
    {
        var todo = _todos.FirstOrDefault(t => t.Id == id);
        if (todo is null) return false;
        if (!todo.Completed)
        {
            todo.Completed = true;
            todo.CompletedUtc = DateTime.UtcNow;
        }
        return true;
    }

    /// <summary>Un-completes a todo (from the detail pane's "Reopen"). Returns false when the id is unknown.</summary>
    public bool Reopen(string id)
    {
        var todo = _todos.FirstOrDefault(t => t.Id == id);
        if (todo is null) return false;
        todo.Completed = false;
        todo.CompletedUtc = null;
        return true;
    }

    public bool Remove(string id) => _todos.RemoveAll(t => t.Id == id) > 0;

    /// <summary>The <paramref name="n"/> outstanding items most in need of attention: incomplete only,
    /// dated items first ordered by due (so overdue/soonest lead), then undated items by creation.</summary>
    public IReadOnlyList<Todo> TopOutstanding(int n) =>
        _todos.Where(t => !t.Completed)
              .OrderBy(t => t.DueUtc is null ? 1 : 0)
              .ThenBy(t => t.DueUtc ?? DateTime.MaxValue)
              .ThenBy(t => t.CreatedUtc)
              .Take(n)
              .ToList();

    /// <summary>Outstanding items whose due instant has passed and that haven't yet fired their reminder,
    /// relative to <paramref name="nowUtc"/>. Pure and static so the reminder logic is testable without a
    /// timer. The monitor host stamps <see cref="Todo.ReminderFiredUtc"/> on the returned items and saves,
    /// so a subsequent call (or a restart) won't re-select them.</summary>
    public static IEnumerable<Todo> DueForReminder(IEnumerable<Todo> todos, DateTime nowUtc) =>
        todos.Where(t => !t.Completed
                         && t.DueUtc is { } due
                         && due <= nowUtc
                         && t.ReminderFiredUtc is null);

    public void Save()
    {
        if (SaveSuppressed) return;
        try
        {
            var model = new Model { Todos = _todos };
            AtomicFile.Write(_path, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private sealed class Model
    {
        public List<Todo> Todos { get; set; } = [];
    }
}
