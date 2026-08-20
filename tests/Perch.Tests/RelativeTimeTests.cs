using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "due now")]
    [InlineData(30, "due now")]        // within a minute either way
    [InlineData(-30, "due now")]
    [InlineData(2 * 60, "in 2m")]
    [InlineData(90 * 60, "in 1h")]
    [InlineData(3 * 86400, "in 3d")]
    [InlineData(-5 * 60, "overdue 5m")]
    [InlineData(-2 * 3600, "overdue 2h")]
    [InlineData(-1 * 86400, "overdue 1d")]
    public void DueLabel_FormatsBothDirections(int offsetSeconds, string expected)
    {
        var due = Now.AddSeconds(offsetSeconds);
        Assert.Equal(expected, RelativeTime.DueLabel(Now, due));
    }
}
