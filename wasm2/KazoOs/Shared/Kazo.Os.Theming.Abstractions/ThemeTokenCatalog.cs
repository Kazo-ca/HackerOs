using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Kazo.Os.Theming.Abstractions;

/// <summary>
/// The published list of tokens a user theme may set (ADR 0103 §2.3), with their type. The Theme
/// Editor builds its forms, help and documentation from this list, and theme files are validated
/// against it: an unknown token is ignored with a warning.
/// </summary>
/// <remarks>
/// The list is a curated subset of the <c>--kos-*</c> properties of <c>themes.css</c>: the
/// tokens whose value is a single color, length, duration, font list or number. Composite values
/// (borders, shadows, gradients, paddings) keep the base theme's value; they derive from these
/// tokens through <c>var()</c> in the stylesheet, so changing a color changes them too.
/// </remarks>
public static class ThemeTokenCatalog
{
    private static readonly ReadOnlyCollection<ThemeTokenDefinition> Items = Array.AsReadOnly<ThemeTokenDefinition>(
    [
        // Colors: surfaces, text, borders, accent, status, desktop.
        Color("--kos-bg-base", "Base background", "Deepest background: desktop surfaces, inputs and window content.", "#101417"),
        Color("--kos-bg-surface", "Surface", "Panels, toolbars, buttons and cards.", "#151c20"),
        Color("--kos-bg-elevated", "Elevated surface", "Menus, hovered buttons, table headers and popovers.", "#1b252a"),
        Color("--kos-bg-overlay", "Overlay", "Dimmed layer behind dialogs; usually translucent.", "rgb(4 8 10 / 72%)"),
        Color("--kos-text-primary", "Primary text", "Main text color.", "#f4f7f8", "--kos-text-primary-rgb"),
        Color("--kos-text-secondary", "Secondary text", "Labels, captions and inactive items.", "#a8bac4", "--kos-text-secondary-rgb"),
        Color("--kos-text-muted", "Muted text", "Placeholders, disabled items and hints.", "#6f838d", "--kos-text-muted-rgb"),
        Color("--kos-text-inverse", "Inverse text", "Text drawn on the accent color (primary buttons).", "#07100f"),
        Color("--kos-border-subtle", "Subtle border", "Separators between toolbars, cards and table rows.", "#27343a"),
        Color("--kos-border", "Border", "Default border of buttons and inputs.", "#3a4b53", "--kos-border-rgb"),
        Color("--kos-border-strong", "Strong border", "Hovered borders and emphasized outlines.", "#61747d"),
        Color("--kos-focus-ring", "Focus ring", "Keyboard focus outline; keep it visible against every surface.", "#42d392"),
        Color("--kos-accent", "Accent", "Primary buttons, selection, active tabs and links.", "#42d392", "--kos-accent-rgb"),
        Color("--kos-accent-hover", "Accent (hover)", "Accent color under the pointer.", "#62e4aa"),
        Color("--kos-accent-subtle", "Accent (subtle)", "Background of selected rows and active buttons.", "#173b2c"),
        Color("--kos-accent-glow", "Accent glow", "Soft halo around focused or primary elements; usually translucent.", "rgb(66 211 146 / 24%)"),
        Color("--kos-success", "Success", "Confirmation messages and healthy states.", "#42d392", "--kos-success-rgb"),
        Color("--kos-warning", "Warning", "Warnings and degraded states.", "#e0ad50", "--kos-warning-rgb"),
        Color("--kos-error", "Error", "Errors, destructive buttons and failed states.", "#ff8f87", "--kos-error-rgb"),
        Color("--kos-info", "Information", "Informational messages and badges.", "#64b5f6", "--kos-info-rgb"),
        Color("--kos-desktop-background", "Desktop background", "Color behind the desktop icons and windows.", "#07100f"),
        new("--kos-desktop-grid-opacity", ThemeTokenType.Number, ThemeTokenGroup.Colors, "Desktop grid opacity",
            "Opacity of the faint grid drawn on the desktop, from 0 (hidden) to 1.", "1"),

        // Typography.
        new("--kos-font-ui", ThemeTokenType.FontFamily, ThemeTokenGroup.Typography, "Interface font",
            "Font of menus, windows and dialogs. List fallbacks: only fonts installed on the machine are used.",
            "\"Segoe UI\", system-ui, sans-serif"),
        new("--kos-font-mono", ThemeTokenType.FontFamily, ThemeTokenGroup.Typography, "Monospace font",
            "Font of the terminal, the code editor and other fixed-width text.", "\"JetBrains Mono\", monospace"),
        Length("--kos-titlebar-font-size", ThemeTokenGroup.Typography, "Title font size", "Size of window titles.", "0.86rem"),
        new("--kos-titlebar-font-weight", ThemeTokenType.Number, ThemeTokenGroup.Typography, "Title font weight",
            "Weight of window titles, from 100 (thin) to 900 (black).", "650"),
        Length("--kos-taskbar-font-size", ThemeTokenGroup.Typography, "Taskbar font size", "Size of the taskbar labels.", "0.85rem"),
        new("--kos-btn-font-weight", ThemeTokenType.Number, ThemeTokenGroup.Typography, "Button font weight",
            "Weight of button labels.", "500"),

        // Spacing and shape.
        Length("--kos-radius-sm", ThemeTokenGroup.Spacing, "Small radius", "Corners of buttons, inputs and small controls.", "0.25rem"),
        Length("--kos-radius-md", ThemeTokenGroup.Spacing, "Medium radius", "Corners of cards and panels.", "0.45rem"),
        Length("--kos-radius-lg", ThemeTokenGroup.Spacing, "Large radius", "Corners of dialogs and large surfaces.", "0.75rem"),
        Length("--kos-btn-radius", ThemeTokenGroup.Spacing, "Button radius", "Corners of buttons (defaults to the small radius).", "0.25rem"),
        Length("--kos-input-radius", ThemeTokenGroup.Spacing, "Input radius", "Corners of text fields and selects.", "0.25rem"),
        Length("--kos-control-height", ThemeTokenGroup.Spacing, "Control height", "Height of buttons and inputs.", "2.25rem"),

        // Windows.
        Color("--kos-window-background", "Window frame", "Background of the window frame.", "#0c1516", group: ThemeTokenGroup.Windows),
        Color("--kos-window-content-background", "Window content", "Background behind an app's content.", "#0c1516", group: ThemeTokenGroup.Windows),
        Color("--kos-window-text", "Window text", "Default text color inside windows.", "#e7f7f1", group: ThemeTokenGroup.Windows),
        Color("--kos-window-focused-border", "Focused window border", "Border color of the active window.", "#55d6a1", group: ThemeTokenGroup.Windows),
        Length("--kos-window-radius", ThemeTokenGroup.Windows, "Window radius", "Corners of windows.", "0.35rem"),
        Length("--kos-titlebar-height", ThemeTokenGroup.Windows, "Title bar height", "Height of window title bars.", "2.35rem"),
        Color("--kos-titlebar-background", "Title bar", "Title bar of inactive windows.", "#101d1d", group: ThemeTokenGroup.Windows),
        Color("--kos-titlebar-focused-background", "Title bar (active)", "Title bar of the active window.", "#153128", group: ThemeTokenGroup.Windows),
        Color("--kos-titlebar-text", "Title text", "Title of inactive windows.", "#b9ccc6", group: ThemeTokenGroup.Windows),
        Color("--kos-titlebar-focused-text", "Title text (active)", "Title of the active window.", "#ecfff8", group: ThemeTokenGroup.Windows),
        Length("--kos-window-control-size", ThemeTokenGroup.Windows, "Window button size", "Size of the minimize, maximize and close buttons.", "1.7rem"),
        Length("--kos-window-control-radius", ThemeTokenGroup.Windows, "Window button radius", "Corners of the window buttons.", "0.2rem"),
        Color("--kos-window-control-background", "Window button", "Background of minimize and maximize.", "#111e1f", group: ThemeTokenGroup.Windows),
        Color("--kos-window-control-hover-background", "Window button (hover)", "Minimize and maximize under the pointer.", "#1a302b", group: ThemeTokenGroup.Windows),
        Color("--kos-window-control-color", "Window button icon", "Icon color of minimize and maximize.", "#b9ccc6", group: ThemeTokenGroup.Windows),
        Color("--kos-window-close-background", "Close button", "Background of the close button.", "#281719", group: ThemeTokenGroup.Windows),
        Color("--kos-window-close-hover-background", "Close button (hover)", "Close button under the pointer.", "#8f2932", group: ThemeTokenGroup.Windows),
        Color("--kos-window-close-color", "Close button icon", "Icon color of the close button.", "#ffb3ad", group: ThemeTokenGroup.Windows),

        // Taskbar and launcher.
        Length("--kos-taskbar-height", ThemeTokenGroup.Taskbar, "Taskbar height", "Height of the taskbar.", "2.75rem"),
        Length("--kos-taskbar-gap", ThemeTokenGroup.Taskbar, "Taskbar spacing", "Space between taskbar items.", "0.5rem"),
        Color("--kos-taskbar-background", "Taskbar", "Background of the taskbar.", "#11191c", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-button-background", "Task button", "Background of a window's taskbar button.", "#0d1417", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-button-hover-background", "Task button (hover)", "Task button under the pointer.", "#1b282d", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-button-active-background", "Task button (active)", "Button of the active window.", "#173b2c", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-button-color", "Task button text", "Label of task buttons.", "#a8bac4", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-button-active-color", "Task button text (active)", "Label of the active window's button.", "#f4f7f8", group: ThemeTokenGroup.Taskbar),
        Length("--kos-taskbar-button-radius", ThemeTokenGroup.Taskbar, "Task button radius", "Corners of task buttons.", "0.25rem"),
        Color("--kos-taskbar-active-indicator", "Active indicator", "Mark under the active window's button.", "#42d392", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-launcher-background", "Launcher button", "Background of the button that opens the launcher.", "#0d1417", group: ThemeTokenGroup.Taskbar),
        Color("--kos-taskbar-launcher-color", "Launcher button icon", "Icon color of the launcher button.", "#42d392", group: ThemeTokenGroup.Taskbar),
        Color("--kos-launcher-background", "Launcher", "Background of the app launcher.", "#151c20", group: ThemeTokenGroup.Taskbar),
        Color("--kos-launcher-sidebar-background", "Launcher sidebar", "Side column of the app launcher.", "#101417", group: ThemeTokenGroup.Taskbar),
        Color("--kos-launcher-power-background", "Power button", "Background of the Power button of the launcher.", "#0d1417", group: ThemeTokenGroup.Taskbar),
        Color("--kos-launcher-power-color", "Power button icon", "Icon and label color of the Power button.", "#a8bac4", group: ThemeTokenGroup.Taskbar),
        Color("--kos-launcher-power-hover-background", "Power button (hover)", "Power button under the pointer or with its menu open.", "#3a1c1f", group: ThemeTokenGroup.Taskbar),

        // Animations.
        new("--kos-transition-fast", ThemeTokenType.Duration, ThemeTokenGroup.Animations, "Fast transition",
            "Hover and press feedback. Ignored when animations are turned off.", "120ms ease"),
        new("--kos-transition-normal", ThemeTokenType.Duration, ThemeTokenGroup.Animations, "Normal transition",
            "Opening panels, menus and windows. Ignored when animations are turned off.", "180ms ease"),
        new("--kos-window-anim-duration", ThemeTokenType.Duration, ThemeTokenGroup.Animations, "Window animations",
            "Opening, closing, minimizing, maximizing and snapping windows. Ignored when animations are turned off.", "180ms")
    ]);

    private static readonly IReadOnlyDictionary<string, ThemeTokenDefinition> ByName =
        new ReadOnlyDictionary<string, ThemeTokenDefinition>(Items.ToDictionary(static token => token.Name, StringComparer.Ordinal));

    /// <summary>Gets every editable token, grouped and in editor order.</summary>
    public static IReadOnlyList<ThemeTokenDefinition> All => Items;

    /// <summary>Gets the tokens of one editor group, in editor order.</summary>
    /// <param name="group">Requested group.</param>
    public static IReadOnlyList<ThemeTokenDefinition> InGroup(ThemeTokenGroup group) =>
        [.. Items.Where(token => token.Group == group)];

    /// <summary>Resolves a token by its exact CSS custom property name.</summary>
    /// <param name="name">Name such as <c>--kos-accent</c>.</param>
    /// <param name="token">The definition when found.</param>
    /// <returns><see langword="true"/> when the token may be set by a user theme.</returns>
    public static bool TryGet(string? name, [NotNullWhen(true)] out ThemeTokenDefinition? token)
    {
        if (name is null)
        {
            token = null;
            return false;
        }

        return ByName.TryGetValue(name, out token);
    }

    private static ThemeTokenDefinition Color(
        string name, string label, string description, string example, string? rgbCompanion = null, ThemeTokenGroup group = ThemeTokenGroup.Colors) =>
        new(name, ThemeTokenType.Color, group, label, description, example, rgbCompanion);

    private static ThemeTokenDefinition Length(string name, ThemeTokenGroup group, string label, string description, string example) =>
        new(name, ThemeTokenType.Length, group, label, description, example);
}
