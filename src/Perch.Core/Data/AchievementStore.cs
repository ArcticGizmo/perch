namespace Perch.Data;

using System.Text.Json;

/// <summary>
/// Durable record of which achievement badges have already been celebrated, so an unlock toast fires
/// exactly once per badge (and survives restarts). This is the first bit of Perch state that isn't a plain
/// settings toggle — the trophies themselves are recomputed from transcripts, but "have we told the user
/// about this one yet?" has nowhere else to live.
///
/// Persisted per-profile next to <c>settings.json</c> as <c>achievements.json</c>; best-effort throughout,
/// mirroring <see cref="AppSettings"/> — a read/write failure degrades to "no badges celebrated" rather
/// than throwing. <see cref="Existed"/> distinguishes a genuine first run (no file) from an empty one, so
/// the caller can seed silently instead of toasting every historical unlock at once.
/// </summary>
internal sealed class AchievementStore
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "achievements.json");

    private readonly string _path;
    private readonly HashSet<string> _unlocked;

    /// <summary>True when a store file already existed on disk at load — false on the very first run. Lets
    /// the caller seed silently the first time rather than firing a toast for every badge earned to date.</summary>
    public bool Existed { get; }

    private AchievementStore(string path, HashSet<string> unlocked, bool existed)
    {
        _path = path;
        _unlocked = unlocked;
        Existed = existed;
    }

    public static AchievementStore Load() => LoadFrom(DefaultPath);

    // internal for tests — round-trips through a temp path without touching the real profile.
    internal static AchievementStore LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path));
                var ids = model?.Unlocked ?? [];
                return new AchievementStore(path, new HashSet<string>(ids, StringComparer.Ordinal), existed: true);
            }
        }
        catch { }
        return new AchievementStore(path, new HashSet<string>(StringComparer.Ordinal), existed: false);
    }

    public bool Contains(string id) => _unlocked.Contains(id);

    /// <summary>Records an id as unlocked; returns true if it was newly added (false if already present).</summary>
    public bool Add(string id) => _unlocked.Add(id);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var model = new Model { Unlocked = _unlocked.OrderBy(x => x, StringComparer.Ordinal).ToList() };
            File.WriteAllText(_path, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private sealed class Model
    {
        public List<string> Unlocked { get; set; } = [];
    }
}
