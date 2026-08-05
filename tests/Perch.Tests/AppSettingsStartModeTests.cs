using System.Text.Json;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The StartMode setting: how it deserialises, how an older settings file's AutoStartOnFirstSession switch
/// folds into it, and the on-disk shape perch-hook reads (a top-level string, matched by member name).
/// Exercises the serializer + MigrateStartMode directly rather than AppSettings.Load, which reads the real
/// per-user settings.json.
/// </summary>
public class AppSettingsStartModeTests
{
    private static AppSettings Read(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        s.MigrateStartMode();
        return s;
    }

    [Fact]
    public void Migrate_LegacyAutoStartOn_BecomesOnSessionStart()
    {
        var s = Read("""{ "AutoStartOnFirstSession": true }""");
        Assert.Equal(StartMode.OnSessionStart, s.StartMode);
        Assert.Null(s.AutoStartOnFirstSession); // dropped, so it isn't written back
    }

    [Fact]
    public void Migrate_LegacyAutoStartOff_BecomesOff()
    {
        var s = Read("""{ "AutoStartOnFirstSession": false }""");
        Assert.Equal(StartMode.Off, s.StartMode);
        Assert.Null(s.AutoStartOnFirstSession);
    }

    [Fact]
    public void Migrate_NoLegacyKey_KeepsStartMode()
    {
        Assert.Equal(StartMode.OnLogin, Read("""{ "StartMode": "OnLogin" }""").StartMode);
        Assert.Equal(StartMode.Off, Read("{}").StartMode); // fresh install: off by default
    }

    [Fact]
    public void Migrate_LegacyKeyNeverOverridesAnExplicitStartMode()
    {
        // A hand-edited file carrying both keys keeps the newer one; the legacy switch is still dropped.
        var s = Read("""{ "StartMode": "OnLogin", "AutoStartOnFirstSession": false }""");
        Assert.Equal(StartMode.OnLogin, s.StartMode);
        Assert.Null(s.AutoStartOnFirstSession);
    }

    [Fact]
    public void StartMode_SerialisesByName_ForTheHooksMiniParser()
    {
        // perch-hook reads settings.json with a string-only reader (ReadFields), so the value must be the
        // member name, not the ordinal.
        var json = JsonSerializer.Serialize(new AppSettings { StartMode = StartMode.OnSessionStart });
        Assert.Contains("\"StartMode\":\"OnSessionStart\"", json);
        Assert.DoesNotContain("AutoStartOnFirstSession", json);
    }
}
