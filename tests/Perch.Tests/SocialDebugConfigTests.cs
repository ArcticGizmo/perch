using Perch.Social;
using Xunit;

namespace Perch.Tests;

public class SocialDebugConfigTests
{
    [Fact]
    public void Resolve_reads_and_trims_environment_variables()
    {
        var prevEmail = Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG_EMAIL");
        var prevPass = Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG_PASSWORD");
        var prevHandle = Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG_HANDLE");
        try
        {
            // Setting all three means the environment fully supplies them, so the resolver never consults a repo
            // .env.local — the result is deterministic regardless of the checkout.
            Environment.SetEnvironmentVariable("PERCH_SOCIAL_DEBUG_EMAIL", "  puppet@example.com  ");
            Environment.SetEnvironmentVariable("PERCH_SOCIAL_DEBUG_PASSWORD", "pw123");
            Environment.SetEnvironmentVariable("PERCH_SOCIAL_DEBUG_HANDLE", "testbot");

            var cfg = SocialDebugConfig.Resolve();
            Assert.Equal("puppet@example.com", cfg.Email);   // trimmed
            Assert.Equal("pw123", cfg.Password);
            Assert.Equal("testbot", cfg.Handle);
            Assert.True(cfg.HasCredentials);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERCH_SOCIAL_DEBUG_EMAIL", prevEmail);
            Environment.SetEnvironmentVariable("PERCH_SOCIAL_DEBUG_PASSWORD", prevPass);
            Environment.SetEnvironmentVariable("PERCH_SOCIAL_DEBUG_HANDLE", prevHandle);
        }
    }

    [Fact]
    public void HasCredentials_needs_both_email_and_password()
    {
        Assert.False(SocialDebugConfig.Empty.HasCredentials);
        Assert.False(new SocialDebugConfig(null, null, null).HasCredentials);
        Assert.False(new SocialDebugConfig("a@b.com", null, null).HasCredentials);
        Assert.False(new SocialDebugConfig(null, "pw", null).HasCredentials);
        Assert.False(new SocialDebugConfig("  ", "pw", null).HasCredentials);
        Assert.True(new SocialDebugConfig("a@b.com", "pw", null).HasCredentials);
    }
}
