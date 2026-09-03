using System.Text.Json.Nodes;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>AskUserQuestion over the control channel: parsing the questions out of the tool input and
/// building the allow reply's <c>updatedInput</c> with the user's answers.</summary>
public class AskUserQuestionInputTests
{
    private const string Input =
        """{"questions":[{"question":"Which would you like, an apple or a banana?","header":"Fruit","options":[{"label":"Apple","description":"Crisp."},{"label":"Banana","description":"Soft."}],"multiSelect":false},{"question":"Toppings?","header":"Extras","options":[{"label":"Nuts","description":""},{"label":"Honey","description":""}],"multiSelect":true}]}""";

    [Fact]
    public void Parse_ReadsQuestionsOptionsAndMultiSelect()
    {
        var qs = AskUserQuestionInput.Parse(Input);
        Assert.Equal(2, qs.Count);
        Assert.Equal("Fruit", qs[0].Header);
        Assert.Equal("Which would you like, an apple or a banana?", qs[0].Question);
        Assert.Equal(new[] { "Apple", "Banana" }, qs[0].Options.Select(o => o.Label));
        Assert.Equal("Crisp.", qs[0].Options[0].Description);
        Assert.False(qs[0].MultiSelect);
        Assert.True(qs[1].MultiSelect);
    }

    [Fact]
    public void Parse_ToleratesGarbage()
    {
        Assert.Empty(AskUserQuestionInput.Parse("nope"));
        Assert.Empty(AskUserQuestionInput.Parse("{}"));
        Assert.Empty(AskUserQuestionInput.Parse("""{"questions":[{"header":"no question text"}]}"""));
    }

    [Fact]
    public void BuildAnswer_KeepsInputAndAddsAnswers()
    {
        var answers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Which would you like, an apple or a banana?"] = ["Apple"],
            ["Toppings?"] = ["Nuts", "Honey"],
        };
        var updated = AskUserQuestionInput.BuildAnswer(Input, answers);
        Assert.NotNull(updated["questions"]);   // the original input survives
        var a = Assert.IsType<JsonObject>(updated["answers"]);
        Assert.Equal("Apple", a["Which would you like, an apple or a banana?"]!.GetValue<string>());
        Assert.Equal("Nuts, Honey", a["Toppings?"]!.GetValue<string>());

        var summary = AskUserQuestionInput.Summarise(AskUserQuestionInput.Parse(Input), answers);
        Assert.Equal("Fruit: Apple  ·  Extras: Nuts, Honey", summary);
    }

    [Fact]
    public void ToolSummary_DescribesTheQuestion()
    {
        var summary = Perch.Data.ToolSummary.Describe("AskUserQuestion", JsonNode.Parse(Input));
        Assert.Equal("Asking: Which would you like, an apple or a banana?", summary);
    }
}
