using System.Diagnostics;

// perch.com: runs the perch.exe beside it with our arguments, console and current directory, waits, and hands back
// its exit code, so the shell waits too and perch.exe's output lands before the next prompt. See Perch.Cli.csproj.
// Always the sibling by absolute path, never a PATH or current-directory lookup (review fixes CP7).
var gui = Path.Combine(AppContext.BaseDirectory, "perch.exe");
if (!File.Exists(gui))
{
    Console.Error.WriteLine($"perch: can't find perch.exe next to {Environment.ProcessPath}.");
    return 1;
}

// No redirection and no shell: perch.exe inherits our std handles and attaches to our console for its output.
// A tray it starts is launched detached (Program.DetachTray), so this wait ends as soon as the CLI part is done.
var psi = new ProcessStartInfo(gui) { UseShellExecute = false };
foreach (var a in args) psi.ArgumentList.Add(a);

try
{
    using var p = Process.Start(psi);
    if (p is null) return 1;
    p.WaitForExit();
    return p.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"perch: couldn't start perch.exe: {ex.Message}");
    return 1;
}
