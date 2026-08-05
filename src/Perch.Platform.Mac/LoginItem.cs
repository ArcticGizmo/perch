using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ILoginItem"/>: a per-user LaunchAgent plist in <c>~/Library/LaunchAgents</c> with
/// <c>RunAtLoad</c>, which launchd honours at login without sudo and without the app having to talk to
/// the (sandbox-only) SMAppService API. The label carries the profile suffix so a dev instance gets its
/// own agent rather than replacing the installed one's.
///
/// An installed build is launched through LaunchServices (<c>/usr/bin/open -a Perch.app</c>) so it gets a
/// proper app activation context; a dev build outside a bundle is exec'd directly. Pure managed file ops —
/// no interop — and best-effort: a read-only home just means Perch doesn't start at login. The agent is
/// written but not <c>launchctl load</c>ed; launchd picks it up at the next login, which is when it matters.
/// </summary>
public sealed class LoginItem : ILoginItem
{
    private static string Label => "com.quartexsoftware.perch" + (AppProfile.IsDev ? ".dev" : "");

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    public void Register()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            string path = PlistPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Plist(Label, LaunchArguments(exe)));
        }
        catch { /* best-effort */ }
    }

    public void Unregister()
    {
        try { File.Delete(PlistPath); } // no-op if absent
        catch { /* best-effort */ }
    }

    public bool IsRegistered()
    {
        try { return File.Exists(PlistPath); }
        catch { return false; }
    }

    // …/Perch.app/Contents/MacOS/perch -> `open -a /…/Perch.app`; anything else (a dev build) runs directly.
    private static string[] LaunchArguments(string exe)
    {
        const string marker = "/Contents/MacOS/";
        int cut = exe.IndexOf(marker, StringComparison.Ordinal);
        return cut < 0 ? [exe] : ["/usr/bin/open", "-a", exe[..cut]];
    }

    private static string Plist(string label, string[] args)
    {
        var programArgs = string.Concat(args.Select(a => $"        <string>{Escape(a)}</string>\n"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Escape(label)}</string>
                <key>ProgramArguments</key>
                <array>
            {programArgs.TrimEnd('\n')}
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>

            """;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
