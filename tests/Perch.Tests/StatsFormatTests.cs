using System.Globalization;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The compact token figure shared by the Stats dashboard, the overlay's burn readout and Perch Wrapped.</summary>
public class StatsFormatTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(789L, "789")]
    [InlineData(999L, "999")]
    [InlineData(1_000L, "1.0k")]
    [InlineData(45_600L, "45.6k")]
    [InlineData(999_949L, "999.9k")]
    [InlineData(999_960L, "1.0M")]                       // would read "1000.0k" — steps up instead
    [InlineData(12_300_000L, "12.3M")]
    [InlineData(999_960_000L, "1.0B")]
    [InlineData(1_500_000_000L, "1.5B")]
    [InlineData(987_600_000_000L, "987.6B")]
    [InlineData(4_200_000_000_000L, "4.2T")]
    [InlineData(1_234_500_000_000_000L, "1234.5T")]      // trillions are the ceiling
    public void Tokens_HumanisesThroughBillionsAndTrillions(long n, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, StatsFormat.Tokens(n)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
