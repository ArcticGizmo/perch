using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch.Data;

/// <summary>
/// A string-name enum converter that never throws while reading. It <em>writes</em> the member name — exactly
/// like <see cref="JsonStringEnumConverter"/>, so perch-hook's string-only settings parser and older readers keep
/// working and files round-trip unchanged — but on <em>read</em> an unrecognised name (or an out-of-range number)
/// falls back to <c>default</c> instead of throwing.
///
/// <para>This is what stops a single stray enum value from resetting the whole app. Persisted enums like
/// <see cref="StartMode"/>, <see cref="OverlayPresentationMode"/> and the <c>SectionOrder</c>'s
/// <see cref="OverlaySection"/> live inside one big <see cref="AppSettings"/> file; with the stock converter a
/// value the current build doesn't recognise (e.g. a settings.json written by a newer or feature-branch build
/// that had an extra member) makes the entire file fail to deserialize, and <see cref="AppSettings.Load"/> then
/// falls back to fresh defaults — silently wiping every setting and re-running first-run onboarding. Degrading an
/// unknown value to the default member keeps the rest of the file intact; collections that care (the section
/// order) re-normalise the result through their own logic.</para>
/// </summary>
public sealed class TolerantStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed) ? parsed : default;
            case JsonTokenType.Number:
                // Tolerate a numeric encoding too (a hand-edit, or a file predating the string form); an
                // undefined value degrades to the default rather than a member that doesn't exist.
                if (reader.TryGetInt64(out var n))
                {
                    var v = (T)Enum.ToObject(typeof(T), n);
                    return Enum.IsDefined(typeof(T), v) ? v : default;
                }
                return default;
            default:
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
