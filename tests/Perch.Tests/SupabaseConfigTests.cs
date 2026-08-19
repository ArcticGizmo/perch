using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The Supabase config resolver: environment variables take precedence, and IsConfigured only holds when
/// both values are present. (The local-file and compiled-default tiers are covered by manual setup, since
/// they read machine paths.)
/// </summary>
public sealed class SupabaseConfigTests
{
    [Fact]
    public void Env_vars_take_precedence_and_are_trimmed()
    {
        var prevUrl = Environment.GetEnvironmentVariable("PERCH_SUPABASE_URL");
        var prevKey = Environment.GetEnvironmentVariable("PERCH_SUPABASE_PUBLISHABLE_KEY");
        try
        {
            Environment.SetEnvironmentVariable("PERCH_SUPABASE_URL", "  https://demo.supabase.co  ");
            Environment.SetEnvironmentVariable("PERCH_SUPABASE_PUBLISHABLE_KEY", "  sb_publishable_test  ");

            var cfg = SupabaseConfig.Resolve();
            Assert.True(cfg.IsConfigured);
            Assert.Equal("https://demo.supabase.co", cfg.Url);
            Assert.Equal("sb_publishable_test", cfg.PublishableKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERCH_SUPABASE_URL", prevUrl);
            Environment.SetEnvironmentVariable("PERCH_SUPABASE_PUBLISHABLE_KEY", prevKey);
        }
    }

    [Fact]
    public void IsConfigured_is_false_when_a_value_is_missing()
    {
        Assert.False(new SupabaseConfig("", "").IsConfigured);
        Assert.False(new SupabaseConfig("https://demo.supabase.co", "").IsConfigured);
        Assert.False(new SupabaseConfig("", "sb_publishable_test").IsConfigured);
        Assert.True(new SupabaseConfig("https://demo.supabase.co", "sb_publishable_test").IsConfigured);
    }
}
