namespace Kazo.Os.AppSdk.VirtualKeyboard;

/// <summary>
/// Host-supplied toggles for which key rows <see cref="VirtualKeyboard"/> renders, since needs
/// vary by consumer (e.g. a terminal needs modifiers and navigation keys; a simple text field
/// would not). Mirrors the host-supplied-options pattern already used by
/// <c>Kazo.Os.MobileShell.Blazor</c>'s <c>MobileSystemNavigationBarOptions</c>.
/// </summary>
public sealed record VirtualKeyboardOptions
{
    /// <summary>Gets whether the modifier/navigation row (Ctrl, Alt, Esc, Tab, Delete, Home, End, PageUp, PageDown) renders above the main keys.</summary>
    public bool ShowModifierRow { get; init; } = true;

    /// <summary>Gets whether the symbol row (<c>/ - _ . | &gt; &lt; ~ *</c>, quotes, backslash) renders.</summary>
    public bool ShowSymbolRow { get; init; } = true;

    /// <summary>Gets whether arrow keys render.</summary>
    public bool ShowArrowKeys { get; init; } = true;

    /// <summary>Gets the accessible label for the Shift toggle key.</summary>
    public string ShiftLabel { get; init; } = "Shift";

    /// <summary>Gets the accessible label for the Ctrl toggle key.</summary>
    public string ControlLabel { get; init; } = "Ctrl";

    /// <summary>Gets the accessible label for the Alt toggle key.</summary>
    public string AltLabel { get; init; } = "Alt";
}
