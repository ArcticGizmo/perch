using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch.Theming;

/// <summary>
/// Serialises a <see cref="Theme"/> so that reading always <b>starts from <see cref="Themes.Midnight"/></b>
/// and overlays only the properties actually present in the JSON. A theme persisted before a role existed
/// therefore inherits Midnight's value for that role instead of the struct default (<c>default(Rgb)</c> =
/// black) — the same forgiveness the built-in presets get for free through their <c>Midnight with { … }</c>
/// construction. Without this, adding any future role to <see cref="Theme"/> would make every old custom
/// theme in <c>AppSettings.CustomThemes</c> deserialise that role to black.
///
/// <para>Writing still emits every property, so a re-saved custom theme is a complete snapshot. Property
/// discovery is reflective (like <see cref="ThemeRoles"/>), so new roles are covered with no edit here.</para>
///
/// <para>Applied via <c>[JsonConverter]</c> on <see cref="Theme"/>, so it covers every JSON round-trip:
/// settings load/save, <c>AppSettings.Clone</c>, and the settings live preview. (The QR/share path is
/// separate — it uses <see cref="ThemeCodec"/>'s own hex encoding, not JSON.)</para>
/// </summary>
public sealed class ThemeJsonConverter : JsonConverter<Theme>
{
    private static readonly PropertyInfo[] Props =
        typeof(Theme).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

    public override Theme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for Theme.");

        // Seed from Midnight (a fresh copy) so any property absent from the JSON keeps Midnight's value
        // rather than falling to the type default. `with { }` yields the compiler-generated clone.
        var theme = Themes.Midnight with { };

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return theme;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            var name = reader.GetString();
            reader.Read(); // advance onto the value

            var prop = Array.Find(Props, p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prop is null)
            {
                reader.Skip(); // unknown/removed property — ignore it
                continue;
            }

            // init-only setters are settable through reflection; SetValue mutates the local copy only.
            prop.SetValue(theme, JsonSerializer.Deserialize(ref reader, prop.PropertyType, options));
        }

        throw new JsonException("Unterminated Theme object.");
    }

    public override void Write(Utf8JsonWriter writer, Theme value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var prop in Props)
        {
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name);
            JsonSerializer.Serialize(writer, prop.GetValue(value), prop.PropertyType, options);
        }
        writer.WriteEndObject();
    }
}
