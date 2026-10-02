using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>Guards the Roost's shortcut table (D6): every bound row resolves, modifiers must match exactly, and the
/// tooltip/hint helpers read the same rows.</summary>
public class RoostKeysTests
{
    [Fact]
    public void EveryBoundRowResolvesToItsCommand()
    {
        foreach (var row in RoostKeys.All.Where(r => r.Command is not null))
            for (int i = 0; i < row.Keys!.Count; i++)
                Assert.Equal((row.Command!.Value, row.Keys.Count > 1 ? i + 1 : 0), RoostKeys.Resolve(row.Keys[i], row.Mods));
    }

    [Fact]
    public void ChordsDontCollide()
    {
        var bound = RoostKeys.All.Where(r => r.Keys is not null).SelectMany(r => r.Keys!.Select(k => (k, r.Mods))).ToList();
        Assert.Equal(bound.Count, bound.Distinct().Count());
    }

    [Fact]
    public void ModifiersMustMatchExactly()
    {
        Assert.Equal((RoostCommand.Tab, 3), RoostKeys.Resolve("D3", RoostMods.Ctrl));
        Assert.Null(RoostKeys.Resolve("D3", RoostMods.Ctrl | RoostMods.Shift));
        Assert.Equal((RoostCommand.Region, 3), RoostKeys.Resolve("D3", RoostMods.Alt));
        Assert.Null(RoostKeys.Resolve("D9", RoostMods.Alt));   // only 8 regions
        Assert.Equal((RoostCommand.PreviousTab, 0), RoostKeys.Resolve("Tab", RoostMods.Ctrl | RoostMods.Shift));
        Assert.Null(RoostKeys.Resolve("Enter", RoostMods.None));   // a pane's own key
    }

    [Fact]
    public void TypingIsNeverAChord()
    {
        // The window's chord handler tunnels every key (a composer's included) through Resolve, so an unmodified
        // letter, digit or editing key must resolve to nothing. F2 is the one bare key bound.
        var typed = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(0, 10).Select(d => $"D{d}"))
            .Concat(["Space", "Back", "Delete", "Tab", "Enter", "Escape", "OemPeriod", "Left", "Right", "Up", "Down"]);
        foreach (var key in typed)
        {
            Assert.Null(RoostKeys.Resolve(key, RoostMods.None));
            Assert.Null(RoostKeys.Resolve(key, RoostMods.Shift));
        }
        Assert.Equal((RoostCommand.RenameTab, 0), RoostKeys.Resolve("F2", RoostMods.None));
    }

    [Fact]
    public void TooltipChordsAndTheHintLineComeFromTheTable()
    {
        Assert.Equal("Ctrl+3", RoostKeys.ChordFor(RoostCommand.Tab, 3));
        Assert.Equal("Alt+2", RoostKeys.ChordFor(RoostCommand.Region, 2));
        Assert.Equal("Ctrl+0", RoostKeys.ChordFor(RoostCommand.FocusTab));
        Assert.Equal("Ctrl+T", RoostKeys.ChordFor(RoostCommand.NewTab));
        Assert.Equal("Ctrl+1–9 tab  ·  Alt+1–8 region  ·  Ctrl+. next needing you", RoostKeys.HintLine);
    }
}
