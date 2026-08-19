using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>The minimal <c>.env.local</c> reader: line parsing (comments, quotes, export prefix) and the
/// walk-up-to-perch.slnx discovery that scopes it to a checkout.</summary>
public sealed class DotEnvTests
{
    [Fact]
    public void Parse_reads_keys_ignoring_comments_and_blanks()
    {
        var map = DotEnv.Parse("""
            # a comment
            PERCH_SUPABASE_URL=https://demo.supabase.co

            PERCH_SUPABASE_PUBLISHABLE_KEY=eyJtest
            """);
        Assert.Equal("https://demo.supabase.co", map["PERCH_SUPABASE_URL"]);
        Assert.Equal("eyJtest", map["PERCH_SUPABASE_PUBLISHABLE_KEY"]);
    }

    [Fact]
    public void Parse_strips_quotes_and_export_prefix()
    {
        var map = DotEnv.Parse("""
            export PERCH_SUPABASE_URL="https://demo.supabase.co"
            PERCH_SUPABASE_PUBLISHABLE_KEY='eyJtest'
            """);
        Assert.Equal("https://demo.supabase.co", map["PERCH_SUPABASE_URL"]);
        Assert.Equal("eyJtest", map["PERCH_SUPABASE_PUBLISHABLE_KEY"]);
    }

    [Fact]
    public void Parse_skips_malformed_lines()
    {
        var map = DotEnv.Parse("no_equals_here\n=leading_equals\nGOOD=1");
        Assert.False(map.ContainsKey("no_equals_here"));
        Assert.Single(map);
        Assert.Equal("1", map["GOOD"]);
    }

    [Fact]
    public void FindRepoEnvLocal_locates_the_file_beside_perch_slnx()
    {
        var root = Path.Combine(Path.GetTempPath(), $"perch-env-{Guid.NewGuid():N}");
        var deep = Path.Combine(root, "src", "Perch.App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(deep);
        try
        {
            File.WriteAllText(Path.Combine(root, "perch.slnx"), "<Solution/>");
            var envFile = Path.Combine(root, ".env.local");
            File.WriteAllText(envFile, "PERCH_SUPABASE_URL=x");

            Assert.Equal(envFile, DotEnv.FindRepoEnvLocal(deep));   // walks up from a bin dir
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindRepoEnvLocal_is_null_without_a_checkout_marker()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"perch-noenv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { Assert.Null(DotEnv.FindRepoEnvLocal(dir)); }   // no perch.slnx above → nothing
        finally { Directory.Delete(dir, recursive: true); }
    }
}
