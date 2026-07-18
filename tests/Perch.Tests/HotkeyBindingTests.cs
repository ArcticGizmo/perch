using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

public class HotkeyBindingTests
{
    [Fact]
    public void KeyChar_RoundTripsThroughToken()
    {
        var b = new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'w');
        Assert.Equal("W", b.Key);          // stored upper-cased
        Assert.Equal('W', b.KeyChar);      // read back as a char
    }

    [Fact]
    public void Space_SerialisesAsWord()
    {
        var b = new HotkeyBinding(HotkeyModifiers.Alt, ' ');
        Assert.Equal("Space", b.Key);
        Assert.Equal(' ', b.KeyChar);
        Assert.Equal("Space", b.KeyLabel);
    }

    [Fact]
    public void Describe_OrdersModifiersCtrlAltShift()
    {
        var b = new HotkeyBinding(HotkeyModifiers.Shift | HotkeyModifiers.Control | HotkeyModifiers.Alt, 'K');
        Assert.Equal("Ctrl + Alt + Shift + K", b.Describe());
    }

    [Fact]
    public void Describe_Space_UsesWord()
    {
        var b = new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Shift, ' ');
        Assert.Equal("Alt + Shift + Space", b.Describe());
    }

    [Fact]
    public void IsValid_RequiresModifierAndMappableKey()
    {
        Assert.True(new HotkeyBinding(HotkeyModifiers.Alt, 'A').IsValid);
        Assert.False(new HotkeyBinding(HotkeyModifiers.None, 'A').IsValid);   // no modifier
        Assert.False(new HotkeyBinding(HotkeyModifiers.Alt, '\0').IsValid);   // no key
    }

    [Fact]
    public void UnsetKey_DescribesAsNotSet()
    {
        Assert.Equal("Not set", new HotkeyBinding { Modifiers = HotkeyModifiers.Alt }.Describe());
    }
}
