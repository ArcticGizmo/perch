namespace Perch.Data.Resources;

/// <summary>
/// Names the application a process belongs to, for grouping a recording by "who" rather than by exe. A generic
/// host (<c>node</c>, <c>python</c>, a shell) says nothing about who started it, so attribution looks past it to
/// an ancestor; a stop-point (<c>explorer</c>, <c>svchost</c>, a terminal) is an ancestor of half the machine, so
/// it is never inherited.
/// </summary>
public static class ProcessFamilies
{
    public const string DockerWsl = "Docker / WSL";
    public const string Windows = "Windows";

    /// <summary>The Memory Compression process: other programs' pages, compressed to stay in RAM.</summary>
    public const string CompressedMemory = "Compressed memory";

    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["vmmem"] = DockerWsl,
        ["vmmemwsl"] = DockerWsl,
        ["vmwp"] = DockerWsl,
        ["vmcompute"] = DockerWsl,
        ["docker"] = DockerWsl,
        ["dockerd"] = DockerWsl,
        ["docker desktop"] = DockerWsl,
        ["wsl"] = DockerWsl,
        ["wslhost"] = DockerWsl,
        ["wslservice"] = DockerWsl,
        ["wslrelay"] = DockerWsl,
        ["code"] = "VS Code",
        ["code - insiders"] = "VS Code",
        ["cursor"] = "Cursor",
        ["devenv"] = "Visual Studio",
        ["vbcscompiler"] = ".NET build server",
        ["msbuild"] = "MSBuild",
        ["rider64"] = "JetBrains IDE",
        ["idea64"] = "JetBrains IDE",
        ["webstorm64"] = "JetBrains IDE",
        ["pycharm64"] = "JetBrains IDE",
        ["goland64"] = "JetBrains IDE",
        ["clion64"] = "JetBrains IDE",
        ["datagrip64"] = "JetBrains IDE",
        ["ms-teams"] = "Teams",
        ["msteams"] = "Teams",
        ["teams"] = "Teams",
        ["slack"] = "Slack",
        ["outlook"] = "Outlook",
        ["olk"] = "Outlook",
        ["discord"] = "Discord",
        ["zoom"] = "Zoom",
        ["spotify"] = "Spotify",
        ["onedrive"] = "OneDrive",
        ["dropbox"] = "Dropbox",
        ["msmpeng"] = "Defender",
        ["mpdefendercoreservice"] = "Defender",
        ["nissrv"] = "Defender",
        ["mssense"] = "Defender",
        ["sensendr"] = "Defender",
        ["csfalconservice"] = "CrowdStrike",
        ["csfalconcontainer"] = "CrowdStrike",
        ["searchindexer"] = "Windows Search",
        ["searchprotocolhost"] = "Windows Search",
        ["searchfilterhost"] = "Windows Search",
        ["searchhost"] = "Windows Search",
        ["claude"] = "Claude",
        ["perch"] = "Perch",
        ["explorer"] = "Explorer",
        ["windowsterminal"] = "Windows Terminal",
        ["system"] = Windows,
        ["registry"] = Windows,
        ["memcompression"] = CompressedMemory,
        ["memory compression"] = CompressedMemory,
        ["secure system"] = Windows,
        ["smss"] = Windows,
        ["csrss"] = Windows,
        ["wininit"] = Windows,
        ["winlogon"] = Windows,
        ["services"] = Windows,
        ["lsass"] = Windows,
        ["svchost"] = Windows,
        ["dwm"] = Windows,
        ["fontdrvhost"] = Windows,
        ["spoolsv"] = Windows,
        ["audiodg"] = Windows,
        ["ctfmon"] = Windows,
        ["sihost"] = Windows,
        ["taskhostw"] = Windows,
        ["runtimebroker"] = Windows,
        ["dllhost"] = Windows,
        ["wmiprvse"] = Windows,
        ["tiworker"] = Windows,
        ["trustedinstaller"] = Windows,
        ["msiexec"] = Windows,
        ["startmenuexperiencehost"] = Windows,
        ["shellexperiencehost"] = Windows,
        ["textinputhost"] = Windows,
        ["widgets"] = Windows,
        ["lockapp"] = Windows,
    };

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "python", "python3", "pythonw", "py", "java", "javaw", "dotnet", "cmd", "powershell", "pwsh",
        "bash", "sh", "zsh", "conhost", "openconsole", "git", "git-remote-https", "ssh", "uv", "uvx", "bun", "deno",
        "npm", "npx", "msedgewebview2", "electron", "rundll32", "cargo", "go", "ruby", "perl", "php",
    };

    private static readonly HashSet<string> StopPoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "services", "svchost", "wininit", "winlogon", "userinit", "smss", "system", "windowsterminal",
        "openconsole", "launchd", "systemd", "init", "kernel_task",
    };

    /// <summary>The exe name without directory or <c>.exe</c>.</summary>
    public static string Stem(string exeName)
    {
        var name = Path.GetFileName(exeName ?? "");
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The known family for this exe, or null. Prefix families (<c>com.docker.*</c>, <c>CSFalcon*</c>,
    /// <c>ServiceHub.*</c>) are matched too.</summary>
    public static string? Family(string exeName)
    {
        var stem = Stem(exeName);
        if (Exact.TryGetValue(stem, out var family)) return family;
        if (stem.StartsWith("com.docker.", StringComparison.OrdinalIgnoreCase)) return DockerWsl;
        if (stem.StartsWith("csfalcon", StringComparison.OrdinalIgnoreCase)) return "CrowdStrike";
        if (stem.StartsWith("servicehub.", StringComparison.OrdinalIgnoreCase)) return "Visual Studio";
        if (stem.StartsWith("microsoft.servicehub", StringComparison.OrdinalIgnoreCase)) return "Visual Studio";
        return null;
    }

    public static bool IsGenericHost(string exeName) => Generic.Contains(Stem(exeName));

    public static bool IsStopPoint(string exeName) => StopPoints.Contains(Stem(exeName));
}
