using Kazo.Os.AppSdk.VirtualKeyboard;

namespace Kazo.Os.AppSdk.VirtualKeyboard.Tests;

public sealed class VirtualKeyboardContractsTests
{
    [Fact]
    public void VirtualKeyPressedEvent_defaults_have_no_text_or_modifiers()
    {
        VirtualKeyPressedEvent evt = new(VirtualKey.Enter);

        Assert.Equal(VirtualKey.Enter, evt.Key);
        Assert.Null(evt.Text);
        Assert.False(evt.Control);
        Assert.False(evt.Alt);
        Assert.False(evt.Shift);
    }

    [Fact]
    public void VirtualKeyPressedEvent_carries_character_text_and_modifiers()
    {
        VirtualKeyPressedEvent evt = new(VirtualKey.Character, "c", Control: true);

        Assert.Equal(VirtualKey.Character, evt.Key);
        Assert.Equal("c", evt.Text);
        Assert.True(evt.Control);
        Assert.False(evt.Alt);
        Assert.False(evt.Shift);
    }

    [Fact]
    public void VirtualKeyboardOptions_defaults_enable_every_row()
    {
        VirtualKeyboardOptions options = new();

        Assert.True(options.ShowModifierRow);
        Assert.True(options.ShowSymbolRow);
        Assert.True(options.ShowArrowKeys);
        Assert.Equal("Shift", options.ShiftLabel);
        Assert.Equal("Ctrl", options.ControlLabel);
        Assert.Equal("Alt", options.AltLabel);
    }

    [Fact]
    public void VirtualKeyboardOptions_rows_can_be_disabled_independently()
    {
        VirtualKeyboardOptions options = new() { ShowModifierRow = false, ShowSymbolRow = false };

        Assert.False(options.ShowModifierRow);
        Assert.False(options.ShowSymbolRow);
        Assert.True(options.ShowArrowKeys);
    }
}
