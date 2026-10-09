using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="SessionAccounts"/>: the account each session ran under, kept so a resume after the hook's marker is gone
/// still picks it. The suite runs under a pinned <c>CLAUDE_CONFIG_DIR</c>, so the file is redirected at a temp path.
/// </summary>
public sealed class SessionAccountsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-accounts-").FullName;

    public SessionAccountsTests() => SessionAccounts.ResetForTesting();

    public void Dispose()
    {
        ClaudeConfigSet.ResetForTesting();
        SessionAccounts.ResetForTesting();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (ClaudeConfigDir Primary, ClaudeConfigDir Work) TwoDirs()
    {
        var primary = new ClaudeConfigDir(Path.Combine(_root, "primary"));
        var work = new ClaudeConfigDir(Path.Combine(_root, "work"));
        ClaudeConfigSet.SetForTesting(new[] { primary, work });
        return (primary, work);
    }

    [Fact]
    public void Nothing_is_kept_with_a_single_config_dir()
    {
        SessionAccounts.Remember("s1", @"C:\elsewhere\.claude");
        Assert.Null(SessionAccounts.Recall("s1"));
    }

    [Fact]
    public void Null_means_the_primary_and_the_last_account_seen_wins()
    {
        var (primary, work) = TwoDirs();
        SessionAccounts.Remember("s1", null);
        Assert.Equal(primary.Root, SessionAccounts.Recall("s1"));

        SessionAccounts.Remember("s1", work.Root);
        Assert.Equal(work.Root, SessionAccounts.Recall("s1"));
    }

    [Fact]
    public void Round_trips_through_its_file()
    {
        var (_, work) = TwoDirs();
        SessionAccounts.PersistencePathForTesting = Path.Combine(_root, "session-accounts.json");
        SessionAccounts.Remember("s1", work.Root);
        SessionAccounts.SaveNowForTesting();

        SessionAccounts.ReloadForTesting();
        Assert.Equal(work.Root, SessionAccounts.Recall("s1"));
    }

    [Fact]
    public void A_corrupt_file_starts_empty()
    {
        TwoDirs();
        var path = Path.Combine(_root, "session-accounts.json");
        File.WriteAllText(path, "{ not json");
        SessionAccounts.PersistencePathForTesting = path;

        Assert.Null(SessionAccounts.Recall("s1"));
    }

    [Fact]
    public void Keeps_at_most_the_newest_entries()
    {
        var (_, work) = TwoDirs();
        for (int i = 0; i <= SessionAccounts.MaxEntries; i++)
            SessionAccounts.Remember($"s{i}", work.Root);

        Assert.Null(SessionAccounts.Recall("s0"));
        Assert.Equal(work.Root, SessionAccounts.Recall($"s{SessionAccounts.MaxEntries}"));
    }
}
