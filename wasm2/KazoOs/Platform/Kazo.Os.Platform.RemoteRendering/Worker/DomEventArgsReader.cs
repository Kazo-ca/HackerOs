using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Platform.RemoteRendering.Worker;

/// <summary>
/// Builds the <see cref="EventArgs"/> a component handler expects from the plain object the page's
/// module sends for a DOM event (ADR 0133, section 3): the same choice of type by event name as
/// Blazor's own event registry, filled from public properties only.
/// </summary>
internal static class DomEventArgsReader
{
    /// <summary>Reads the event arguments of <paramref name="eventName"/>.</summary>
    /// <param name="eventName">The DOM event's name, without <c>on</c>.</param>
    /// <param name="a">The object the page sent.</param>
    public static EventArgs Read(string eventName, JsonElement a) => eventName switch
    {
        "click" or "dblclick" or "mousedown" or "mouseup" or "mousemove" or "mouseover" or "mouseout"
            or "mouseenter" or "mouseleave" or "contextmenu" => FillMouse(new MouseEventArgs(), a),
        "pointerdown" or "pointerup" or "pointermove" or "pointerover" or "pointerout" or "pointerenter"
            or "pointerleave" or "pointercancel" or "gotpointercapture" or "lostpointercapture" => ReadPointer(a),
        "wheel" or "mousewheel" => ReadWheel(a),
        "keydown" or "keyup" or "keypress" => ReadKeyboard(a),
        "input" or "change" => new ChangeEventArgs { Value = ReadValue(a) },
        "focus" or "blur" or "focusin" or "focusout" => new FocusEventArgs { Type = String(a, "type") },
        _ => EventArgs.Empty
    };

    /// <summary>Gets the field value an <c>input</c> or <c>change</c> event carries, for <c>EventFieldInfo</c>.</summary>
    public static object? ReadValue(JsonElement a) =>
        a.TryGetProperty("value", out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => value.GetString(),
                _ => null
            }
            : null;

    private static KeyboardEventArgs ReadKeyboard(JsonElement a) => new()
    {
        Key = String(a, "key"),
        Code = String(a, "code"),
        Location = (float)Number(a, "location"),
        Repeat = Flag(a, "repeat"),
        CtrlKey = Flag(a, "ctrlKey"),
        ShiftKey = Flag(a, "shiftKey"),
        AltKey = Flag(a, "altKey"),
        MetaKey = Flag(a, "metaKey"),
        Type = String(a, "type")
    };

    private static PointerEventArgs ReadPointer(JsonElement a)
    {
        PointerEventArgs args = FillMouse(new PointerEventArgs(), a);
        args.PointerId = (long)Number(a, "pointerId");
        args.PointerType = String(a, "pointerType");
        args.IsPrimary = Flag(a, "isPrimary");
        return args;
    }

    private static WheelEventArgs ReadWheel(JsonElement a)
    {
        WheelEventArgs args = FillMouse(new WheelEventArgs(), a);
        args.DeltaX = Number(a, "deltaX");
        args.DeltaY = Number(a, "deltaY");
        args.DeltaZ = Number(a, "deltaZ");
        args.DeltaMode = (long)Number(a, "deltaMode");
        return args;
    }

    private static T FillMouse<T>(T args, JsonElement a) where T : MouseEventArgs
    {
        args.Detail = (long)Number(a, "detail");
        args.ScreenX = Number(a, "screenX");
        args.ScreenY = Number(a, "screenY");
        args.ClientX = Number(a, "clientX");
        args.ClientY = Number(a, "clientY");
        args.OffsetX = Number(a, "offsetX");
        args.OffsetY = Number(a, "offsetY");
        args.PageX = Number(a, "pageX");
        args.PageY = Number(a, "pageY");
        args.MovementX = Number(a, "movementX");
        args.MovementY = Number(a, "movementY");
        args.Button = (long)Number(a, "button");
        args.Buttons = (long)Number(a, "buttons");
        args.CtrlKey = Flag(a, "ctrlKey");
        args.ShiftKey = Flag(a, "shiftKey");
        args.AltKey = Flag(a, "altKey");
        args.MetaKey = Flag(a, "metaKey");
        args.Type = String(a, "type");
        return args;
    }

    private static string String(JsonElement a, string name) =>
        a.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    private static double Number(JsonElement a, string name) =>
        a.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static bool Flag(JsonElement a, string name) =>
        a.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
