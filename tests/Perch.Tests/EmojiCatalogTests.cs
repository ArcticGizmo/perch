using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class EmojiCatalogTests
{
    [Fact]
    public void Dataset_LoadsAThousandPlusEmoji()
    {
        // The embedded gemoji-derived set; a smoke test that the resource is wired and parsed.
        Assert.True(EmojiCatalog.All.Count > 1000, $"only {EmojiCatalog.All.Count} emoji loaded");
        Assert.All(EmojiCatalog.All, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Emoji));
            Assert.False(string.IsNullOrEmpty(e.Name));
        });
    }

    [Theory]
    [InlineData("fire", "🔥")]
    [InlineData("rocket", "🚀")]
    [InlineData("thumbs up", "👍")]      // multi-word, AND-ed across keywords
    [InlineData("thumbsup", "👍")]        // GitHub shortcode alias
    [InlineData("+1", "👍")]              // punctuation shortcut alias
    [InlineData("rofl", "🤣")]
    [InlineData("tada", "🎉")]
    public void Search_TopResult_IsExpected(string query, string expected)
    {
        var results = EmojiCatalog.Search(query, 8);
        Assert.NotEmpty(results);
        Assert.Equal(expected, results[0].Emoji);
    }

    [Fact]
    public void Search_ExactName_OutranksIncidentalKeywordHit()
    {
        // "grinning face" is the whole name of 😀 — it should sort above emoji that merely mention the words.
        var results = EmojiCatalog.Search("grinning face", 5);
        Assert.NotEmpty(results);
        Assert.Equal("😀", results[0].Emoji);
    }

    [Fact]
    public void Search_BlankQuery_ReturnsNothing()
    {
        Assert.Empty(EmojiCatalog.Search(""));
        Assert.Empty(EmojiCatalog.Search("   "));
        Assert.Empty(EmojiCatalog.Search(null));
    }

    [Fact]
    public void Search_HonoursLimit_AndFindsNonsenseNothing()
    {
        Assert.True(EmojiCatalog.Search("a", 5).Count <= 5);
        Assert.Empty(EmojiCatalog.Search("zzzxqnotanemoji"));
    }

    [Fact]
    public void RecordRecentEmoji_PromotesDedupesAndCaps()
    {
        var s = new AppSettings();
        s.RecordRecentEmoji("👍");
        s.RecordRecentEmoji("🔥");
        s.RecordRecentEmoji("👍");                          // re-picking promotes, doesn't duplicate

        Assert.Equal(new[] { "👍", "🔥" }, s.RecentEmojis);

        for (int i = 0; i < AppSettings.RecentEmojiCap + 10; i++)
            s.RecordRecentEmoji($"e{i}");
        Assert.Equal(AppSettings.RecentEmojiCap, s.RecentEmojis!.Count);
        Assert.Equal("e33", s.RecentEmojis[0]);            // most recent first

        s.RecordRecentEmoji("  ");                          // blank is ignored
        Assert.Equal(AppSettings.RecentEmojiCap, s.RecentEmojis!.Count);
    }
}
