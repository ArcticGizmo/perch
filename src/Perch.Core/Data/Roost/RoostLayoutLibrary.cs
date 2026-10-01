namespace Perch.Data.Roost;

/// <summary>A layout the user saved from the painter, by name (<c>AppSettings.RoostSavedLayouts</c>).</summary>
public sealed class RoostSavedLayout
{
    public string Name { get; set; } = "";
    public List<RoostRegion> Regions { get; set; } = [];
}

/// <summary>
/// The painter's saved layouts (docs/roost-tabs-plan.md T6), listed after the built-in presets. Names are unique
/// (ignoring case): saving under a name that's taken replaces that layout. UI-free, unit-tested; app-owned so the
/// list survives closing the Roost.
/// </summary>
public sealed class RoostLayoutLibrary
{
    public const int MaxSaved = 24;

    private readonly List<(string Name, RoostGridLayout Layout)> _saved = [];

    /// <summary>The list changed — save it.</summary>
    public event Action? Changed;

    public IReadOnlyList<(string Name, RoostGridLayout Layout)> Saved => _saved;

    /// <summary>
    /// Saves <paramref name="layout"/> as <paramref name="name"/> (trimmed, capped at
    /// <see cref="RoostTabSet.MaxNameLength"/>). A taken name is overwritten in place; a new one goes at the end.
    /// False for a blank name or a full library.
    /// </summary>
    public bool Save(string name, RoostGridLayout layout)
    {
        if (Clean(name) is not { } clean) return false;
        int at = IndexOf(clean);
        if (at >= 0) _saved[at] = (clean, layout);
        else if (_saved.Count >= MaxSaved) return false;
        else _saved.Add((clean, layout));
        Changed?.Invoke();
        return true;
    }

    /// <summary>Renames a saved layout. Refused for a blank name, an unknown one, or a name another layout has.</summary>
    public bool Rename(string name, string newName)
    {
        int at = IndexOf(name);
        if (at < 0 || Clean(newName) is not { } clean || clean == _saved[at].Name) return false;
        int other = IndexOf(clean);
        if (other >= 0 && other != at) return false;
        _saved[at] = (clean, _saved[at].Layout);
        Changed?.Invoke();
        return true;
    }

    public bool Delete(string name)
    {
        int at = IndexOf(name);
        if (at < 0) return false;
        _saved.RemoveAt(at);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Whether <paramref name="name"/> is taken (ignoring case).</summary>
    public bool Contains(string name) => Clean(name) is { } clean && IndexOf(clean) >= 0;

    /// <summary>The persisted form, or null when there's nothing saved.</summary>
    public List<RoostSavedLayout>? ToState() =>
        _saved.Count == 0 ? null : _saved.Select(s => new RoostSavedLayout { Name = s.Name, Regions = s.Layout.Regions.ToList() }).ToList();

    /// <summary>Restores a persisted list (replacing the current one). An entry with a blank or duplicate name, or a
    /// layout that doesn't validate, is dropped rather than repaired. Doesn't raise <see cref="Changed"/>.</summary>
    public void Seed(IEnumerable<RoostSavedLayout?>? state)
    {
        _saved.Clear();
        foreach (var s in state ?? [])
        {
            if (_saved.Count >= MaxSaved) break;
            if (s is null || Clean(s.Name) is not { } name || IndexOf(name) >= 0) continue;
            if (RoostGridLayout.Create(s.Regions) is { } layout) _saved.Add((name, layout));
        }
    }

    private int IndexOf(string name) => _saved.FindIndex(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? Clean(string? name)
    {
        var t = name?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > RoostTabSet.MaxNameLength ? t[..RoostTabSet.MaxNameLength].TrimEnd() : t;
    }
}
