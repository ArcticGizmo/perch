namespace Perch.Data;

using System.Text.Json.Serialization;
using Perch.Platform;

/// <summary>
/// A user-configurable global keyboard shortcut: a set of modifier keys (Ctrl/Alt/Shift) plus a single
/// main key — a letter, digit, or Space — and whether it's active. Serialised into <see cref="AppSettings"/>
/// and fed straight into <see cref="IGlobalHotkey.Register"/> by the app. <see cref="Enabled"/> == false
/// means "don't register this at all", so a shortcut that clashes with another app can be switched off
/// without losing its combo.
///
/// The main key is stored as a short token ("W", "1", "Space") so the JSON reads cleanly; the app consumes
/// it through <see cref="KeyChar"/>, which is the <see cref="char"/> the hotkey layer expects (Space => ' ').
/// A binding only ever registers when it <see cref="IsValid"/> — at least one modifier and a mappable key —
/// so a bare key can never be claimed system-wide.
/// </summary>
public sealed class HotkeyBinding
{
    public bool Enabled { get; set; } = true;

    /// <summary>The modifier keys held for the shortcut (Ctrl/Alt/Shift). Win/Meta isn't supported.</summary>
    public HotkeyModifiers Modifiers { get; set; }

    /// <summary>The main key as a display/serialisation token: a single upper-case character ("W", "7")
    /// or the word "Space". Empty when unset. Round-trips through <see cref="KeyChar"/>.</summary>
    public string Key { get; set; } = "";

    public HotkeyBinding() { }

    public HotkeyBinding(HotkeyModifiers modifiers, char key, bool enabled = true)
    {
        Modifiers = modifiers;
        Enabled = enabled;
        KeyChar = key;
    }

    /// <summary>The main key as the <see cref="char"/> <see cref="IGlobalHotkey.Register"/> expects
    /// (Space maps to <c>' '</c>); <c>'\0'</c> when unset or unrecognised. Not serialised — it's a view
    /// over <see cref="Key"/>.</summary>
    [JsonIgnore]
    public char KeyChar
    {
        get => TokenToChar(Key);
        set => Key = CharToToken(value);
    }

    /// <summary>True when this binding is safe to register: it has at least one modifier and a mappable
    /// main key. A modifier-less binding is rejected so a bare key is never claimed globally.</summary>
    [JsonIgnore]
    public bool IsValid => Modifiers != HotkeyModifiers.None && KeyChar != '\0';

    /// <summary>A human-readable label for the combo, e.g. "Alt + Shift + W" or "Alt + Space".
    /// Returns "Not set" when there's no mappable key.</summary>
    public string Describe()
    {
        if (KeyChar == '\0') return "Not set";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyLabel);
        return string.Join(" + ", parts);
    }

    /// <summary>The main key on its own ("W", "Space", "7"), for a compact display.</summary>
    [JsonIgnore]
    public string KeyLabel => KeyChar == ' ' ? "Space" : KeyChar == '\0' ? "" : KeyChar.ToString();

    private static char TokenToChar(string? token)
    {
        if (string.IsNullOrEmpty(token)) return '\0';
        if (token.Equals("Space", StringComparison.OrdinalIgnoreCase)) return ' ';
        return token.Length == 1 ? char.ToUpperInvariant(token[0]) : '\0';
    }

    private static string CharToToken(char c) =>
        c == ' ' ? "Space" : c == '\0' ? "" : char.ToUpperInvariant(c).ToString();
}
