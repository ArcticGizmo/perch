using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Find only rebuilds the items whose model could hold the query (perf review PF19); a miss here is a match the user
/// never sees, so formatting, punctuation and line breaks between the query's words must not hide it.
/// </summary>
public sealed class ConversationFindTests
{
    private static AssistantMessageItem Reply(string text)
    {
        var conv = new SessionConversation();
        conv.Apply(new AssistantTextEvent(text));
        return (AssistantMessageItem)conv.Items[^1];
    }

    [Theory]
    [InlineData("use the cache", "Then **use** the `cache`.")]
    [InlineData("Use the cache", "use the\ncache")]
    [InlineData("ClampDip", "calls `Clamp(dip)`")]
    public void Formatting_doesnt_hide_a_phrase(string query, string model) =>
        Assert.True(ConversationFind.MayContain(Reply(model), ConversationFind.Key(query)!));

    [Fact]
    public void An_unrelated_item_is_skipped() =>
        Assert.False(ConversationFind.MayContain(Reply("Nothing to see here."), ConversationFind.Key("PlacementMath")!));

    [Fact]
    public void An_edit_matches_across_its_lines()
    {
        var conv = new SessionConversation();
        conv.Apply(new ToolUseEvent("e1", "Edit", "Editing a.cs", """{"file_path":"a.cs","old_string":"var dip = px;\nreturn Clamp(dip);","new_string":"x"}"""));
        var item = conv.Items[^1];
        Assert.True(ConversationFind.MayContain(item, ConversationFind.Key("px; return")!));
    }

    [Fact]
    public void A_query_without_letters_or_digits_narrows_nothing() => Assert.Null(ConversationFind.Key("-> ::"));
}
