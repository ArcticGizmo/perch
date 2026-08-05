using System.Reflection;

namespace Perch.Theming;

/// <summary>
/// Enumerates a <see cref="Theme"/>'s colour roles as (name, value) pairs. Used by the contrast audit
/// tests and (later) the theme designer to iterate every role without hand-maintaining a list — reflection
/// over the record's <see cref="Rgb"/> properties keeps it automatically in sync as roles are added.
/// </summary>
public static class ThemeRoles
{
    private static readonly PropertyInfo[] RgbProps =
        typeof(Theme).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Rgb))
            .ToArray();

    /// <summary>Every <see cref="Rgb"/>-typed role on <paramref name="theme"/>, in declaration order.</summary>
    public static IEnumerable<(string Name, Rgb Value)> All(Theme theme)
    {
        foreach (var p in RgbProps)
            yield return (p.Name, (Rgb)p.GetValue(theme)!);
    }
}
