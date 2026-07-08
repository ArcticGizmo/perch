namespace Perch.Data;

/// <summary>
/// Selects the app "profile" so a development instance can run alongside an installed one without
/// colliding. Dev mode uses a separate settings directory (<c>Perch (Dev)</c>), a separate single-instance
/// mutex, and a labelled tray — so a dev build can run while the installed Perch keeps running (you can't
/// otherwise: the single-instance guard makes the second launch a no-op, and both would share one
/// settings file).
///
/// It's on automatically for <b>Debug</b> builds — so <c>dotnet run</c> is isolated from an installed
/// Release Perch with zero ceremony — and can be forced either way with the <c>PERCH_DEV</c> environment
/// variable: any non-empty value other than <c>0</c>/<c>false</c> forces it on; <c>0</c>/<c>false</c>
/// forces it off (e.g. to run a Debug build against the real profile).
///
/// Note this isolates only <em>Perch's own</em> state. The <c>~/.claude</c> data view is still shared by
/// default (so a dev build watches your real sessions); set <c>CLAUDE_CONFIG_DIR</c> to isolate that too
/// — e.g. point a dev instance and a hook at a throwaway config dir for a fully hermetic test.
/// </summary>
internal static class AppProfile
{
    /// <summary>True when running as an isolated development instance (see the type remarks).</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>The APPDATA subfolder for this profile's settings — <c>Perch</c> or <c>Perch (Dev)</c>.</summary>
    public static string DataFolderName => IsDev ? "Perch (Dev)" : "Perch";

    /// <summary>Suffix for user-facing labels (tray tooltip / menu) — <c>""</c> or <c>" (Dev)"</c>.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    private static bool ComputeIsDev()
    {
        var env = Environment.GetEnvironmentVariable("PERCH_DEV");
        if (!string.IsNullOrEmpty(env))
            return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
