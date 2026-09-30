using System.Text;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>The realtime socket's message reassembly and reconnect jitter (review fixes CP16).</summary>
public sealed class RealtimeFramingTests
{
    [Fact]
    public void A_character_split_across_fragments_decodes_intact()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"body":"ship it 🚀"}""");
        int split = Array.IndexOf(bytes, (byte)0xF0) + 2;   // cut the 4-byte rocket in half
        var a = new FrameAssembler();

        Assert.Null(a.Add(bytes.AsSpan(0, split), endOfMessage: false));
        Assert.Equal("""{"body":"ship it 🚀"}""", a.Add(bytes.AsSpan(split), endOfMessage: true));
    }

    [Fact]
    public void An_oversized_message_is_dropped_and_the_next_one_still_arrives()
    {
        var a = new FrameAssembler();
        var chunk = new byte[64 * 1024];
        for (int sent = 0; sent <= FrameAssembler.MaxMessageBytes; sent += chunk.Length)
            Assert.Null(a.Add(chunk, endOfMessage: false));
        Assert.Null(a.Add(chunk, endOfMessage: true));   // the over-cap message ends: dropped whole

        Assert.Equal("{}", a.Add("{}"u8, endOfMessage: true));
    }

    [Theory]
    [InlineData(0.0, 7.5)]
    [InlineData(0.5, 10.0)]
    [InlineData(0.9, 12.0)]
    public void Reconnect_backoff_is_jittered_by_a_quarter_either_way(double unit, double expectedSeconds) =>
        Assert.Equal(expectedSeconds,
            SupabaseRealtimeConnection.Jittered(TimeSpan.FromSeconds(10), unit).TotalSeconds, precision: 2);
}
