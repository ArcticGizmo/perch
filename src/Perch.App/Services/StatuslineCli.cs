using System.Text;
using System.Text.Json.Nodes;
using Perch.Data;
using Perch.Statusline;

namespace Perch.Avalonia.Services;

/// <summary>
/// The <c>perch statusline</c> CLI — the half of the statusline designer that actually runs inside a
/// Claude Code session. It never boots Avalonia (dispatched at the very top of <see cref="Program.Main"/>),
/// so it's cheap enough to be a per-refresh status line command.
///
/// <list type="bullet">
/// <item><c>perch statusline</c> (or <c>… render</c>) — the default; reads Claude Code's JSON payload on
///   stdin, renders the active Perch profile's template (with a live <c>git.branch</c> injected), and
///   writes the ANSI line to stdout. This is what <c>settings.json → statusLine.command</c> points at.</item>
/// <item><c>perch statusline list</c> — list saved profiles and the active one.</item>
/// <item><c>perch statusline use &lt;name&gt;</c> — make a profile active and write it into settings.json.</item>
/// <item><c>perch statusline backup [name]</c> — capture whatever command is in settings.json now as an
///   imported profile, so switching never loses your existing setup.</item>
/// <item><c>perch statusline import &lt;name&gt; &lt;command&gt;</c> — save a non-Perch command as a profile.</item>
/// <item><c>perch statusline install</c> — seed the default profiles and apply the active one.</item>
/// </list>
/// </summary>
internal static class StatuslineCli
{
    /// <summary>True for the stdin→stdout render path (no verb, or <c>render</c>) — the one that must stay
    /// silent apart from the status line itself and must not attach a console.</summary>
    public static bool IsRender(string[] args) =>
        args.Length < 2 || string.Equals(args[1], "render", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var verb = args.Length >= 2 ? args[1].ToLowerInvariant() : "render";
        try
        {
            return verb switch
            {
                "render"  => Render(w),
                "list"    => List(w),
                "use"     => Use(w, args.ElementAtOrDefault(2)),
                "backup"  => Backup(w, args.ElementAtOrDefault(2)),
                "import"  => Import(w, args.ElementAtOrDefault(2), args.ElementAtOrDefault(3)),
                "install" => Install(w),
                _         => Help(w),
            };
        }
        catch (Exception ex)
        {
            // Never let the render path throw into Claude Code's status bar; report for the others.
            if (verb != "render") w.WriteLine($"perch statusline: {ex.Message}");
            return verb == "render" ? 0 : 1;
        }
    }

    // ── render (the live path) ──────────────────────────────────────────────────────────
    private static int Render(StreamWriter w)
    {
        var stdin = Console.In.ReadToEnd();
        if (string.IsNullOrWhiteSpace(stdin)) return 0;

        TemplateData data;
        try { data = TemplateData.Parse(stdin); }
        catch { return 0; }   // malformed payload — print nothing rather than a stack trace

        var profile = StatuslineStore.Load().Active;
        if (profile is not { IsPerch: true, Template: { Length: > 0 } template })
            return 0;   // an external profile drives settings.json directly; Perch isn't in that loop

        InjectGitBranch(data);
        w.Write(StatuslineTemplate.RenderToString(template, data, color: true));
        return 0;
    }

    // Merge a live git.branch into the payload so {{git.branch}} works even though the raw payload
    // doesn't carry it. Cheap (reads .git/HEAD, no subprocess); best-effort.
    private static void InjectGitBranch(TemplateData data)
    {
        var cwd = data.TryGet("cwd", out var c) ? TemplateData.Str(c) : "";
        if (string.IsNullOrEmpty(cwd))
            cwd = data.TryGet("workspace.current_dir", out var wd) ? TemplateData.Str(wd) : "";
        var branch = GitHead.ReadBranch(cwd);
        if (branch is null) return;

        if (data.Root["git"] is not JsonObject git)
        {
            git = new JsonObject();
            data.Root["git"] = git;
        }
        git["branch"] = branch;
    }

    // ── management verbs ──────────────────────────────────────────────────────────────
    private static int List(StreamWriter w)
    {
        var cfg = StatuslineStore.Load();
        w.WriteLine("Statusline profiles:");
        foreach (var p in cfg.Profiles)
        {
            var mark = ReferenceEquals(p, cfg.Active) ? "*" : " ";
            var detail = p.IsPerch ? Preview(p) : p.Command;
            w.WriteLine($" {mark} {p.Name,-22} [{(p.IsPerch ? "perch" : "ext")}]  {detail}");
        }
        w.WriteLine();
        w.WriteLine($"settings.json → {ClaudeUserSettings.ReadStatusLineCommand() ?? "(no status line)"}");
        return 0;
    }

    private static int Use(StreamWriter w, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { w.WriteLine("usage: perch statusline use <name>"); return 1; }

        var cfg = StatuslineStore.Load();
        var p = cfg.Find(name);
        if (p is null) { w.WriteLine($"No profile named \"{name}\". Try: perch statusline list"); return 1; }

        cfg.ActiveName = p.Name;
        StatuslineStore.Save(cfg);
        if (!Apply(p)) { w.WriteLine("Couldn't write the script or ~/.claude/settings.json."); return 1; }

        w.WriteLine($"Active profile: {p.Name}");
        if (p.IsPerch)
        {
            w.WriteLine($"Generated standalone script: {StatuslineScript.DefaultScriptPath}");
            w.WriteLine($"Preview: {Preview(p)}");
        }
        return 0;
    }

    private static int Backup(StreamWriter w, string? name)
    {
        var current = ClaudeUserSettings.ReadStatusLineCommand();
        if (string.IsNullOrWhiteSpace(current))
        {
            w.WriteLine("Nothing to back up — settings.json has no statusLine command.");
            return 0;
        }

        var cfg = StatuslineStore.Load();
        var profileName = UniqueName(cfg, string.IsNullOrWhiteSpace(name) ? "settings.json backup" : name!);
        cfg.Upsert(new StatuslineProfile { Name = profileName, Kind = ProfileKind.External, Command = current });
        StatuslineStore.Save(cfg);
        w.WriteLine($"Backed up current status line as imported profile \"{profileName}\":");
        w.WriteLine($"  {current}");
        return 0;
    }

    private static int Import(StreamWriter w, string? name, string? command)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            w.WriteLine("usage: perch statusline import <name> \"<command>\"");
            return 1;
        }
        var cfg = StatuslineStore.Load();
        cfg.Upsert(new StatuslineProfile { Name = name!, Kind = ProfileKind.External, Command = command });
        StatuslineStore.Save(cfg);
        w.WriteLine($"Imported \"{name}\". Activate it with: perch statusline use \"{name}\"");
        return 0;
    }

    private static int Install(StreamWriter w)
    {
        var cfg = StatuslineStore.Load();
        StatuslineStore.Save(cfg);   // materialise the file if it didn't exist yet
        var active = cfg.Active;
        if (active is null) { w.WriteLine("No profiles to install."); return 1; }
        if (!Apply(active)) { w.WriteLine("Couldn't write the script or ~/.claude/settings.json."); return 1; }
        w.WriteLine($"Installed {cfg.Profiles.Count} profiles; active: {active.Name}.");
        if (active.IsPerch) w.WriteLine($"Standalone script: {StatuslineScript.DefaultScriptPath}");
        w.WriteLine("Open a new Claude Code session to see it.");
        return 0;
    }

    private static int Help(StreamWriter w)
    {
        w.WriteLine("perch statusline — design and switch Claude Code status lines");
        w.WriteLine("  Perch profiles compile to a standalone Node script; settings.json runs that");
        w.WriteLine("  directly (node \"…\"), so perch itself is never called at refresh time.");
        w.WriteLine();
        w.WriteLine("  (render)               local preview: read stdin JSON, print the active line");
        w.WriteLine("  list                   list saved profiles");
        w.WriteLine("  use <name>             activate a profile (generates the script + writes settings.json)");
        w.WriteLine("  backup [name]          save the current settings.json status line as a profile");
        w.WriteLine("  import <name> <cmd>    save a non-Perch command as a profile");
        w.WriteLine("  install                seed default profiles and apply the active one");
        return 0;
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────
    // Applies a profile to settings.json. A Perch profile is compiled to a standalone Node script that
    // settings.json runs directly (`node "…"`) — Perch is never in the refresh loop. An external profile
    // is written verbatim. Returns false if the script couldn't be written or settings.json couldn't be
    // updated.
    private static bool Apply(StatuslineProfile p)
    {
        if (p.IsPerch)
        {
            if (string.IsNullOrEmpty(p.Template)) return false;
            var path = StatuslineScript.DefaultScriptPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, StatuslineScript.Generate(p));
            }
            catch
            {
                return false;
            }
            return ClaudeUserSettings.SetStatusLine(StatuslineScript.CommandFor(path), p.Padding);
        }
        return !string.IsNullOrWhiteSpace(p.Command) && ClaudeUserSettings.SetStatusLine(p.Command!, p.Padding);
    }

    private static string Preview(StatuslineProfile p) =>
        p.Template is { Length: > 0 } t
            ? StatuslineTemplate.RenderToString(t, StatuslineSample.Data(), color: false).Replace("\n", " ⏎ ")
            : "";

    private static string UniqueName(StatuslineConfig cfg, string baseName)
    {
        if (cfg.Find(baseName) is null) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (cfg.Find(candidate) is null) return candidate;
        }
    }
}
