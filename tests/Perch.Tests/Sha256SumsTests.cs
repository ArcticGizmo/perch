using System.Text;
using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class Sha256SumsTests
{
    [Fact]
    public void Parses_standard_and_binary_star_lines_and_looks_up_by_name()
    {
        var text = """
            e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  a.zip
            AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899 *b.zip
            """;
        var sums = Sha256Sums.Parse(text);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sums.Expected("a.zip"));
        // normalised to lower-case, and the leading * on binary-mode lines is ignored
        Assert.Equal("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", sums.Expected("b.zip"));
    }

    [Fact]
    public void Missing_name_returns_null()
    {
        var sums = Sha256Sums.Parse("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  a.zip");
        Assert.Null(sums.Expected("other.zip"));
    }

    [Fact]
    public void Hash_matches_a_known_value_for_empty_input()
    {
        // The well-known SHA-256 of the empty byte string.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Sha256Sums.Hash([]));
    }

    [Fact]
    public void Hash_round_trips_against_a_parsed_manifest()
    {
        var bytes = Encoding.UTF8.GetBytes("hello perch");
        var hash = Sha256Sums.Hash(bytes);
        var sums = Sha256Sums.Parse($"{hash}  payload.zip");
        Assert.Equal(hash, sums.Expected("payload.zip"));
    }
}
