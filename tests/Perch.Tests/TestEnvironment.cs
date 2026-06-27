using Perch.Data;
using System.Runtime.CompilerServices;

namespace Perch.Tests;

/// <summary>
/// Points the whole data layer at the synthetic fixture tree by setting <c>CLAUDE_CONFIG_DIR</c>
/// before any test touches <c>ClaudePaths</c> (which snapshots the config dir on first access). A
/// module initializer guarantees that ordering — it runs when the test assembly loads, before any
/// test method and before the referenced <c>Perch</c> assembly's statics are first read.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>The fixtures act as the Claude config dir for the entire test run. Deployed next to the
    /// test assembly via <c>CopyToOutputDirectory</c>.</summary>
    public static string FixtureConfigDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "claude");

    /// <summary>A working directory the fixture transcripts pretend to belong to. Its encoded form
    /// (<c>C--fixtures-proj</c>) is the project folder the rich fixtures live under.</summary>
    public const string FixtureCwd = @"C:\fixtures\proj";

    [ModuleInitializer]
    public static void Init() =>
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", FixtureConfigDir);
}
