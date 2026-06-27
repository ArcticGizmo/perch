using System.Reflection;

namespace Perch.Ui;

/// <summary>
/// Loads files embedded in the assembly (the app icon, the bundled CHANGELOG). Replaces the
/// per-form <c>LoadEmbeddedBitmap</c> / <c>LoadEmbeddedText</c> helpers that every window carried a
/// private copy of. Best-effort: a missing or unreadable resource yields null rather than throwing.
/// </summary>
internal static class EmbeddedResources
{
    private static readonly Assembly Assembly = typeof(EmbeddedResources).Assembly;

    /// <summary>Loads an embedded image (e.g. <c>"Perch.icon.png"</c>), or null if unavailable.</summary>
    public static Bitmap? LoadBitmap(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Loads an embedded UTF-8 text resource (e.g. the changelog), or null if unavailable.</summary>
    public static string? LoadText(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
