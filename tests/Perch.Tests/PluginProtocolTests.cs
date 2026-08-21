using System.Text.Json;
using System.Text.Json.Nodes;
using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginProtocolTests
{
    [Fact]
    public void Serializes_a_request_as_a_single_line_with_grants_and_context()
    {
        var req = new PluginRequest(
            PluginRequest.PollType, "0.9.0", ["read.cwd", "notify"],
            new Dictionary<string, string> { ["cwd"] = @"C:\proj" });

        var line = PluginProtocol.Serialize(req);
        Assert.DoesNotContain('\n', line);

        var obj = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("poll", (string?)obj["type"]);
        Assert.Equal("0.9.0", (string?)obj["perch"]);
        Assert.Equal(2, obj["grants"]!.AsArray().Count);
        Assert.Equal(@"C:\proj", (string?)obj["context"]!["cwd"]);
    }

    [Fact]
    public void Parses_a_render_message()
    {
        var msg = PluginProtocol.ParseLine("""{"type":"render","glyph":{"glyph":"☀","text":"24°","tooltip":"Sunny"}}""");
        var render = Assert.IsType<PluginRenderMessage>(msg);
        Assert.Equal("☀", render.Glyph.Glyph);
        Assert.Equal("24°", render.Glyph.Text);
        Assert.Equal("Sunny", render.Glyph.Tooltip);
    }

    [Fact]
    public void Parses_a_ready_message_with_and_without_render()
    {
        Assert.Null(Assert.IsType<PluginReady>(PluginProtocol.ParseLine("""{"type":"ready"}""")).Render);
        Assert.NotNull(Assert.IsType<PluginReady>(
            PluginProtocol.ParseLine("""{"type":"ready","render":{"text":"hi"}}""")).Render);
    }

    [Fact]
    public void Parses_a_notify_message()
    {
        var msg = PluginProtocol.ParseLine("""{"type":"notify","title":"Done","body":"Session finished"}""");
        var n = Assert.IsType<PluginNotifyMessage>(msg);
        Assert.Equal("Done", n.Title);
        Assert.Equal("Session finished", n.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]                 // array, not object
    [InlineData("""{"nope":true}""")]       // no type
    [InlineData("""{"type":123}""")]        // non-string type
    public void Skips_blank_or_malformed_lines(string line)
    {
        Assert.Null(PluginProtocol.ParseLine(line));
    }

    [Fact]
    public void A_render_with_neither_glyph_nor_text_is_treated_as_absent()
    {
        // render carrying nothing paintable → not a render message
        Assert.Null(PluginProtocol.ParseLine("""{"type":"render","glyph":{"tooltip":"only a tip"}}"""));
    }

    [Fact]
    public void An_unknown_type_round_trips_as_unknown_not_null()
    {
        var msg = PluginProtocol.ParseLine("""{"type":"teleport"}""");
        Assert.Equal("teleport", Assert.IsType<PluginUnknownMessage>(msg).Type);
    }
}
