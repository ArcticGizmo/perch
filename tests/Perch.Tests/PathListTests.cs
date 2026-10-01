using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP19: installing or uninstalling Perch used to rewrite the user's PATH through
/// <c>Environment.SetEnvironmentVariable</c>, which expanded every <c>%VAR%</c> entry for good, and uninstall also
/// re-trimmed and dropped every other entry. The editor now works on the raw registry string and leaves what it
/// doesn't own byte-for-byte alone.
/// </summary>
public sealed class PathListTests
{
    private const string Dir = @"C:\Users\A\AppData\Local\Perch\current";

    // A stand-in for Environment.ExpandEnvironmentVariables with fixed values.
    private static string Expand(string s) => s
        .Replace("%LOCALAPPDATA%", @"C:\Users\A\AppData\Local", StringComparison.OrdinalIgnoreCase)
        .Replace("%USERPROFILE%", @"C:\Users\A", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Add_appends_and_keeps_variables_unexpanded()
    {
        // Only the trailing separator is folded; the padded entry is kept as written.
        Assert.Equal(@"%USERPROFILE%\bin; C:\tools ;" + Dir, PathList.WithEntry(@"%USERPROFILE%\bin; C:\tools ;", Dir, Expand));
    }

    [Fact]
    public void Add_to_an_empty_path_is_just_the_dir() =>
        Assert.Equal(Dir, PathList.WithEntry("", Dir, Expand));

    [Theory]
    [InlineData(@"C:\x;C:\Users\A\AppData\Local\Perch\current")]
    [InlineData(@"C:\x;c:\users\a\appdata\local\perch\CURRENT\")]           // case and a trailing slash
    [InlineData(@"C:\x;%LOCALAPPDATA%\Perch\current")]                      // written with a variable
    [InlineData(@"C:\x; C:\Users\A\AppData\Local\Perch\current ;C:\y")]     // padded
    public void Add_is_a_no_op_when_already_present(string current) =>
        Assert.Null(PathList.WithEntry(current, Dir, Expand));

    [Fact]
    public void Remove_drops_only_our_entry_and_leaves_the_rest_as_written()
    {
        var current = @"%USERPROFILE%\bin;;  C:\tools  ;%LOCALAPPDATA%\Perch\current;C:\Users\A\AppData\Local\Perch\current\;C:\last";
        Assert.Equal(@"%USERPROFILE%\bin;;  C:\tools  ;C:\last", PathList.WithoutEntry(current, Dir, Expand));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"%USERPROFILE%\bin;C:\tools")]
    [InlineData(@"C:\Users\A\AppData\Local\Perch\current-old")]              // a prefix is not a match
    public void Remove_is_a_no_op_when_absent(string current) =>
        Assert.Null(PathList.WithoutEntry(current, Dir, Expand));
}
