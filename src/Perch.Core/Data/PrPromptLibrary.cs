namespace Perch.Data;

/// <summary>A prompt template the user saved from the start-a-session dialog (persisted in
/// <see cref="AppSettings.PrSessionCustomTemplates"/>). <see cref="AppliesTo"/> and <see cref="Mode"/> come from the
/// template it was saved from, so it's offered first for the same kinds of PR and runs the same way.</summary>
public sealed class PrCustomTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Task { get; set; } = "";
    public PrSessionMode Mode { get; set; }
    public List<GhAlertKind>? AppliesTo { get; set; }
}

/// <summary>
/// The quick prompts the dialog offers: the built-ins (<see cref="PrSessionPrompts.Defaults"/>, read-only) and the
/// user's own, which can be saved as, renamed, overwritten and deleted. Every operation returns a new list (the caller
/// persists it). Pure.
/// </summary>
public static class PrPromptLibrary
{
    public const int MaxNameLength = 60;
    private const string CustomPrefix = "custom-";

    /// <summary>Built-ins first, then the user's templates (unreadable entries skipped).</summary>
    public static IReadOnlyList<PrPromptTemplate> All(IReadOnlyList<PrCustomTemplate>? customs) =>
        PrSessionPrompts.Defaults.Concat((customs ?? []).Where(c => c.Id.StartsWith(CustomPrefix, StringComparison.Ordinal)
                && c.Name.Trim().Length > 0)
            .Select(ToTemplate)).ToList();

    /// <summary>Whether <paramref name="t"/> is the user's (renamable, savable, deletable) rather than built in.</summary>
    public static bool IsCustom(PrPromptTemplate t) => t.Id.StartsWith(CustomPrefix, StringComparison.Ordinal);

    /// <summary>A saved copy of a template under <paramref name="name"/> with <paramref name="task"/>, keeping
    /// <paramref name="from"/>'s mode and the reasons it fits. The name is made unique among the templates.</summary>
    public static (List<PrCustomTemplate> Customs, string Id) SaveAs(IReadOnlyList<PrCustomTemplate>? customs, PrPromptTemplate from,
        string name, string task, Func<string>? newId = null)
    {
        var list = Copy(customs);
        var id = CustomPrefix + (newId?.Invoke() ?? Guid.NewGuid().ToString("N")[..12]);
        list.Add(new PrCustomTemplate
        {
            Id = id, Name = UniqueName(customs, Clean(name, from.Label), null), Task = task, Mode = from.Mode,
            AppliesTo = from.AppliesTo.Count > 0 ? from.AppliesTo.ToList() : null,
        });
        return (list, id);
    }

    /// <summary><paramref name="id"/>'s task replaced by <paramref name="task"/>.</summary>
    public static List<PrCustomTemplate> Save(IReadOnlyList<PrCustomTemplate>? customs, string id, string task) =>
        Copy(customs).Select(c => c.Id == id ? With(c, task: task) : c).ToList();

    /// <summary><paramref name="id"/> renamed (unique among the templates; a blank name leaves it as it was).</summary>
    public static List<PrCustomTemplate> Rename(IReadOnlyList<PrCustomTemplate>? customs, string id, string name) =>
        Copy(customs).Select(c => c.Id == id ? With(c, name: UniqueName(customs, Clean(name, c.Name), id)) : c).ToList();

    public static List<PrCustomTemplate> Delete(IReadOnlyList<PrCustomTemplate>? customs, string id) =>
        Copy(customs).Where(c => c.Id != id).ToList();

    private static PrPromptTemplate ToTemplate(PrCustomTemplate c) =>
        new(c.Id, c.Name.Trim(), "Your template", c.Task, c.AppliesTo ?? [], c.Mode);

    private static List<PrCustomTemplate> Copy(IReadOnlyList<PrCustomTemplate>? customs) =>
        (customs ?? []).Select(c => With(c)).ToList();

    private static PrCustomTemplate With(PrCustomTemplate c, string? name = null, string? task = null) => new()
    {
        Id = c.Id, Name = name ?? c.Name, Task = task ?? c.Task, Mode = c.Mode, AppliesTo = c.AppliesTo?.ToList(),
    };

    // One trimmed line of at most MaxNameLength, or the fallback when that leaves nothing.
    private static string Clean(string name, string fallback)
    {
        var flat = PrSessionPrompts.OneLine(name, MaxNameLength);
        return flat.Length > 0 ? flat : fallback;
    }

    // "Fix flaky tests", else "Fix flaky tests (2)", "(3)" … against the built-ins and the other custom templates.
    private static string UniqueName(IReadOnlyList<PrCustomTemplate>? customs, string name, string? exceptId)
    {
        var taken = PrSessionPrompts.Defaults.Select(d => d.Label)
            .Concat((customs ?? []).Where(c => c.Id != exceptId).Select(c => c.Name.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{name} ({n})")) return $"{name} ({n})";
    }
}
