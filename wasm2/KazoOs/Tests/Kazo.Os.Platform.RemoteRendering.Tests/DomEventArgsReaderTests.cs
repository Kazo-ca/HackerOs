using System.Text.Json;
using Kazo.Os.Platform.RemoteRendering.Worker;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Platform.RemoteRendering.Tests;

/// <summary>ADR 0133: the event arguments a handler receives are built from the page's plain object.</summary>
public sealed class DomEventArgsReaderTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_key_event_reads_the_key_and_modifiers()
    {
        KeyboardEventArgs args = Assert.IsType<KeyboardEventArgs>(DomEventArgsReader.Read(
            "keydown", Json("""{"type":"keydown","key":"Enter","code":"NumpadEnter","location":3,"repeat":true,"ctrlKey":true,"shiftKey":false,"altKey":true,"metaKey":false}""")));

        Assert.Equal("Enter", args.Key);
        Assert.Equal("NumpadEnter", args.Code);
        Assert.Equal(3, args.Location);
        Assert.True(args.Repeat);
        Assert.True(args.CtrlKey);
        Assert.True(args.AltKey);
        Assert.False(args.ShiftKey);
        Assert.Equal("keydown", args.Type);
    }

    [Fact]
    public void A_click_reads_the_mouse_position_and_buttons()
    {
        MouseEventArgs args = Assert.IsType<MouseEventArgs>(DomEventArgsReader.Read(
            "click", Json("""{"type":"click","detail":2,"clientX":10.5,"clientY":20,"offsetX":1,"button":0,"buttons":1,"shiftKey":true}""")));

        Assert.Equal(2, args.Detail);
        Assert.Equal(10.5, args.ClientX);
        Assert.Equal(20, args.ClientY);
        Assert.Equal(1, args.OffsetX);
        Assert.Equal(1, args.Buttons);
        Assert.True(args.ShiftKey);
    }

    [Fact]
    public void Pointer_and_wheel_events_get_their_own_types()
    {
        PointerEventArgs pointer = Assert.IsType<PointerEventArgs>(DomEventArgsReader.Read(
            "pointerdown", Json("""{"type":"pointerdown","pointerId":7,"pointerType":"pen","isPrimary":true,"clientX":3}""")));
        Assert.Equal(7, pointer.PointerId);
        Assert.Equal("pen", pointer.PointerType);
        Assert.True(pointer.IsPrimary);
        Assert.Equal(3, pointer.ClientX);

        WheelEventArgs wheel = Assert.IsType<WheelEventArgs>(DomEventArgsReader.Read(
            "wheel", Json("""{"type":"wheel","deltaY":-120,"deltaMode":0}""")));
        Assert.Equal(-120, wheel.DeltaY);
    }

    [Theory]
    [InlineData("""{"type":"input","value":"abc"}""", "abc")]
    [InlineData("""{"type":"change","value":true}""", true)]
    [InlineData("""{"type":"change","value":false}""", false)]
    [InlineData("""{"type":"change"}""", null)]
    public void Input_and_change_carry_the_field_value(string json, object? expected)
    {
        ChangeEventArgs args = Assert.IsType<ChangeEventArgs>(DomEventArgsReader.Read("change", Json(json)));

        Assert.Equal(expected, args.Value);
        Assert.Equal(expected, DomEventArgsReader.ReadValue(Json(json)));
    }

    [Fact]
    public void Focus_and_unknown_events_get_their_plain_arguments()
    {
        FocusEventArgs focus = Assert.IsType<FocusEventArgs>(DomEventArgsReader.Read("focusin", Json("""{"type":"focusin"}""")));
        Assert.Equal("focusin", focus.Type);
        Assert.Same(EventArgs.Empty, DomEventArgsReader.Read("scroll", Json("""{"type":"scroll"}""")));
    }
}
