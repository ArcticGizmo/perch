using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The opt-in debug logs (review fixes CP16): a value only switches a log on, never names where it goes,
/// and whatever is on is named for the tray's warning.</summary>
public sealed class DebugSwitchesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData(@"C:\somewhere\else.log", true)]   // an old-style path just means "on"; it is not used as a path
    public void Any_value_but_zero_or_false_switches_a_log_on(string? value, bool on) =>
        Assert.Equal(on, DebugSwitches.IsOnValue(value));

    [Fact]
    public void The_warning_names_what_is_on()
    {
        Assert.Null(DebugSwitches.Warning(false, false));
        Assert.Contains("PERCH_SESSION_LOG", DebugSwitches.Warning(true, false));
        Assert.DoesNotContain("PERCH_VDM_DEBUG", DebugSwitches.Warning(true, false));
        Assert.Contains("PERCH_VDM_DEBUG", DebugSwitches.Warning(false, true));
        Assert.Contains(DiagnosticLog.Dir, DebugSwitches.Warning(true, true));
    }

    [Theory]
    [InlineData(@"..\escape.log")]
    [InlineData(@"C:\elsewhere\x.log")]
    [InlineData("sub/x.log")]
    public void A_log_name_with_a_directory_part_is_refused(string name)
    {
        var outside = Path.GetFullPath(Path.Combine(DiagnosticLog.Dir, name));
        bool existed = File.Exists(outside);
        DiagnosticLog.AppendRaw(name, "x", 1024);
        Assert.Equal(existed, File.Exists(outside));   // nothing was written there
    }
}
