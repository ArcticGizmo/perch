using Perch.Theming;
using Xunit;

namespace Perch.Tests;

public class CvdSimTests
{
    [Fact]
    public void None_IsIdentity()
    {
        var c = new Rgb(200, 60, 40);
        Assert.Equal(c, CvdSim.Simulate(c, CvdType.None));
        Assert.Same(Themes.Ember, CvdSim.Simulate(Themes.Ember, CvdType.None));
    }

    [Fact]
    public void Greys_SurviveRoughly()
    {
        // A neutral grey has R≈G≈B, so every CVD matrix (rows summing to ~1) leaves it ~unchanged.
        foreach (var type in new[] { CvdType.Protanopia, CvdType.Deuteranopia, CvdType.Tritanopia })
        {
            var g = CvdSim.Simulate(new Rgb(128, 128, 128), type);
            Assert.InRange(g.R, 120, 136);
            Assert.InRange(g.G, 120, 136);
            Assert.InRange(g.B, 120, 136);
        }
    }

    [Fact]
    public void Protanopia_ShiftsPureRed()
    {
        // Red is exactly what a protanope can't see normally — the simulation must change it noticeably.
        var red = new Rgb(255, 0, 0);
        var seen = CvdSim.Simulate(red, CvdType.Protanopia);
        Assert.NotEqual(red, seen);
        Assert.True(seen.G > 60, "protan red should gain a green/olive component");
    }

    [Fact]
    public void SimulatingATheme_PreservesIdentity_AndChangesColours()
    {
        var sim = CvdSim.Simulate(Themes.Midnight, CvdType.Deuteranopia);
        Assert.Equal(Themes.Midnight.Id, sim.Id);
        Assert.NotEqual(Themes.Midnight.Accent, sim.Accent); // the blue accent shifts under deutan
    }

    [Fact]
    public void SimulatingATheme_ChangesSemanticHues()
    {
        // Semantic status hues are now theme roles, so they simulate with the theme for the designer preview.
        var sim = CvdSim.Simulate(Themes.Midnight, CvdType.Deuteranopia);
        Assert.NotEqual(Themes.Midnight.StatusRunning, sim.StatusRunning); // green shifts under deutan
    }

    [Fact]
    public void SimulatingFixedColours_ChangesBrand()
    {
        // The fixed brand/Jira/destructive palette is theme-independent, so it simulates itself.
        var sim = FixedColors.Default.Simulate(CvdType.Deuteranopia);
        Assert.NotEqual(FixedColors.Default.Brand, sim.Brand);
        Assert.Equal(FixedColors.Default, FixedColors.Default.Simulate(CvdType.None)); // identity
    }
}
