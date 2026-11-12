using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TranscriptJsonPrefilterTests
{
    [Theory]
    [InlineData("""{"type":"tool_result","tool_use_id":"t1","is_error":true}""", true)]
    [InlineData("""{"type": "tool_result", "tool_use_id": "t1", "is_error": true}""", true)]
    [InlineData("""{"type":"tool_result","tool_use_id":"t1","is_error":false}""", false)]
    [InlineData("""{"type": "tool_result", "is_error": false}""", false)]
    [InlineData("""{"type":"tool_result","tool_use_id":"t1","content":"ok"}""", false)]
    public void MayHoldErrorResult_MatchesOnlyAFailedResult(string line, bool expected) =>
        Assert.Equal(expected, TranscriptJson.MayHoldErrorResult(line));

    [Theory]
    [InlineData("""{"type":"tool_use","id":"t1","name":"Write"}""", true)]
    [InlineData("""{"type": "tool_use", "id": "t1"}""", true)]
    [InlineData("""{"type":"tool_result","tool_use_id":"t1","content":"x.md"}""", false)]
    public void MayHoldToolUse_IgnoresTheResultsToolUseIdKey(string line, bool expected) =>
        Assert.Equal(expected, TranscriptJson.MayHoldToolUse(line));
}
