using System.Text.Json;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="RoostLayoutLibrary"/> (docs/roost-tabs-plan.md T6): the painter's saved layouts — unique names,
/// overwrite on save, rename / delete, the cap, and a persisted list read back defensively — plus the
/// <see cref="RoostGridLayout"/> pieces the painter leans on (a split's chosen id, the geometry key).
/// </summary>
public class RoostLayoutLibraryTests
{
    private static RoostGridLayout Two => RoostGridLayout.FromTemplate(RoostSnapTemplate.Columns2);

    private static RoostGridLayout Three => RoostGridLayout.FromTemplate(RoostSnapTemplate.MainPlusTwo);

    [Fact]
    public void SavingATakenNameReplacesItInPlace()
    {
        var lib = new RoostLayoutLibrary();
        int changes = 0;
        lib.Changed += () => changes++;
        Assert.True(lib.Save("  Review  ", Two));
        Assert.True(lib.Save("Wide", Three));
        Assert.True(lib.Save("review", Three));
        Assert.Equal(["review", "Wide"], lib.Saved.Select(s => s.Name));
        Assert.Equal(Three.Signature, lib.Saved[0].Layout.Signature);
        Assert.Equal(3, changes);
        Assert.False(lib.Save("   ", Two));
    }

    [Fact]
    public void RenameRefusesBlanksAndClashes()
    {
        var lib = new RoostLayoutLibrary();
        lib.Save("A", Two);
        lib.Save("B", Three);
        Assert.False(lib.Rename("A", "b"));
        Assert.False(lib.Rename("A", " "));
        Assert.False(lib.Rename("nope", "C"));
        Assert.True(lib.Rename("a", "C"));
        Assert.True(lib.Rename("C", "c"));   // a change of case of its own name is fine
        Assert.Equal(["c", "B"], lib.Saved.Select(s => s.Name));
        Assert.True(lib.Contains("C"));
        Assert.True(lib.Delete("B"));
        Assert.False(lib.Delete("B"));
    }

    [Fact]
    public void TheLibraryCapsOutButAnOverwriteStillWorks()
    {
        var lib = new RoostLayoutLibrary();
        for (int i = 0; i < RoostLayoutLibrary.MaxSaved; i++) Assert.True(lib.Save($"L{i}", Two));
        Assert.False(lib.Save("one more", Two));
        Assert.True(lib.Save("L3", Three));
    }

    [Fact]
    public void APersistedListRoundTripsAndDropsWhatItCantTrust()
    {
        var lib = new RoostLayoutLibrary();
        lib.Save("Two", Two);
        lib.Save("Three", Three);
        var json = JsonSerializer.Serialize(lib.ToState());
        var state = JsonSerializer.Deserialize<List<RoostSavedLayout>>(json)!;
        state.Add(new RoostSavedLayout { Name = "two", Regions = Three.Regions.ToList() });          // duplicate name
        state.Add(new RoostSavedLayout { Name = "", Regions = Three.Regions.ToList() });             // blank
        state.Add(new RoostSavedLayout { Name = "Broken", Regions = [new RoostRegion(0, 0, 0, 6, 6)] });   // gaps

        var back = new RoostLayoutLibrary();
        back.Seed(state);
        Assert.Equal(["Two", "Three"], back.Saved.Select(s => s.Name));
        Assert.Equal(Three.Signature, back.Saved[1].Layout.Signature);
        Assert.Null(new RoostLayoutLibrary().ToState());
    }

    [Fact]
    public void ASplitCanBeGivenItsNewRegionsId()
    {
        var split = Two.Split(1, RoostSplit.Rows, newId: 7)!;
        Assert.NotNull(split.Find(7));
        Assert.Equal(7, split.MaxId);
        Assert.Null(Two.Split(1, RoostSplit.Rows, newId: 0));   // taken
    }

    [Fact]
    public void TheGeometryKeyIgnoresIds()
    {
        var renumbered = RoostGridLayout.Create(Three.Regions.Select(r => r with { Id = r.Id + 10 }))!;
        Assert.NotEqual(Three.Signature, renumbered.Signature);
        Assert.Equal(Three.GeometryKey, renumbered.GeometryKey);
        Assert.NotEqual(Two.GeometryKey, Three.GeometryKey);
    }
}
