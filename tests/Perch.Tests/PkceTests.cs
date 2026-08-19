using System.Security.Cryptography;
using System.Text;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

public sealed class PkceTests
{
    [Fact]
    public void Challenge_is_base64url_sha256_of_verifier()
    {
        var p = Pkce.Create();
        var expected = Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(p.Verifier)));
        Assert.Equal(expected, p.Challenge);
    }

    [Fact]
    public void Base64url_has_no_padding_or_url_unsafe_chars()
    {
        var s = Pkce.Base64Url([251, 239, 190, 255, 0, 1, 2, 3]);   // bytes that force +, / and = in std base64
        Assert.DoesNotContain('=', s);
        Assert.DoesNotContain('+', s);
        Assert.DoesNotContain('/', s);
    }

    [Fact]
    public void Verifier_length_is_within_the_spec_range()
    {
        var p = Pkce.Create();
        Assert.InRange(p.Verifier.Length, 43, 128);   // RFC 7636
    }

    [Fact]
    public void Loopback_parses_the_code_from_a_request_line()
    {
        var q = LoopbackListener.ParseRequestLineQuery("GET /callback?code=abc123&state=xy%20z HTTP/1.1");
        Assert.Equal("abc123", q["code"]);
        Assert.Equal("xy z", q["state"]);   // percent-decoded
    }

    [Fact]
    public void Loopback_query_is_empty_when_there_is_none()
    {
        Assert.Empty(LoopbackListener.ParseRequestLineQuery("GET /callback HTTP/1.1"));
    }
}
