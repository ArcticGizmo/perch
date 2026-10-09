using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the start-a-session dialog's own prompt templates (the "⋯" menu: Save as, Rename, Save, Delete): built-ins
/// stay read-only, names stay unique and one line, and a copy keeps the mode and reasons of what it was saved from.
/// </summary>
public class PrPromptLibraryTests
{
    private static PrPromptTemplate Builtin(string id) => PrSessionPrompts.Defaults.First(t => t.Id == id);

    [Fact]
    public void SaveAsCopiesTheModeAndReasonsUnderANewName()
    {
        var (customs, id) = PrPromptLibrary.SaveAs(null, Builtin("review"), "  Review for security  ", "Look for {repo} secrets.", () => "abc");
        var saved = Assert.Single(customs);
        Assert.Equal("custom-abc", id);
        Assert.Equal("Review for security", saved.Name);
        Assert.Equal(PrSessionMode.Plan, saved.Mode);
        Assert.Contains(GhAlertKind.ReviewRequested, saved.AppliesTo!);

        var all = PrPromptLibrary.All(customs);
        Assert.Equal(PrSessionPrompts.Defaults.Count + 1, all.Count);
        var t = all[^1];
        Assert.True(PrPromptLibrary.IsCustom(t));
        Assert.False(PrPromptLibrary.IsCustom(all[0]));
        Assert.Equal("Look for {repo} secrets.", t.Task);
    }

    [Fact]
    public void NamesStayUniqueAgainstBuiltInsAndEachOther()
    {
        var (a, _) = PrPromptLibrary.SaveAs(null, Builtin("free"), "Review this PR", "x", () => "1");
        Assert.Equal("Review this PR (2)", a[0].Name);
        var (b, _) = PrPromptLibrary.SaveAs(a, Builtin("free"), "review this pr", "y", () => "2");
        Assert.Equal("review this pr (3)", b[1].Name);
        // Renaming to its own name isn't a clash.
        Assert.Equal("Review this PR (2)", PrPromptLibrary.Rename(b, "custom-1", "Review this PR (2)")[0].Name);
    }

    [Theory]
    [InlineData("", "Fix failing checks (2)")]            // blank → the source's label, made unique
    [InlineData("Line one\nline two", "Line one line two")]
    public void NamesAreOneTrimmedLine(string name, string expected)
    {
        var (customs, _) = PrPromptLibrary.SaveAs(null, Builtin("fix-checks"), name, "t", () => "1");
        Assert.Equal(expected, customs[0].Name);
        Assert.True(PrPromptLibrary.SaveAs(null, Builtin("free"), new string('x', 200), "t").Customs[0].Name.Length <= PrPromptLibrary.MaxNameLength);
    }

    [Fact]
    public void SaveRenameAndDeleteTouchOnlyTheirTemplateAndReturnCopies()
    {
        var (one, _) = PrPromptLibrary.SaveAs(null, Builtin("free"), "A", "a", () => "1");
        var (two, _) = PrPromptLibrary.SaveAs(one, Builtin("free"), "B", "b", () => "2");

        var saved = PrPromptLibrary.Save(two, "custom-2", "b2");
        Assert.Equal(["a", "b2"], saved.Select(c => c.Task));
        Assert.Equal("b", two[1].Task);   // the input list is untouched

        var renamed = PrPromptLibrary.Rename(saved, "custom-1", "Alpha");
        Assert.Equal(["Alpha", "B"], renamed.Select(c => c.Name));

        Assert.Equal(["custom-2"], PrPromptLibrary.Delete(renamed, "custom-1").Select(c => c.Id));
    }

    [Fact]
    public void UnreadableSavedEntriesAreSkipped()
    {
        var customs = new List<PrCustomTemplate>
        {
            new() { Id = "review", Name = "Pretends to be built in" },
            new() { Id = "custom-x", Name = "   " },
            new() { Id = "custom-ok", Name = "Fine", Task = "t" },
        };
        Assert.Equal("Fine", Assert.Single(PrPromptLibrary.All(customs), PrPromptLibrary.IsCustom).Label);
    }
}
