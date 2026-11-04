using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Window sizes a <see cref="AppKind.Window"/> app declares (ADR 0095 §2), written as the optional
/// <c>window</c> object of <c>manifest.json</c>. Sizes are the outer size of the window in CSS pixels,
/// title bar included, like the geometry a window manager stores.
/// </summary>
/// <remarks>
/// When an app declares nothing, the OS uses <see cref="Default"/>: the 800×560 window with a
/// 360×240 minimum that every app had before ADR 0095, so no existing app changes its look.
/// </remarks>
public sealed record WindowOptions
{
    /// <summary>Default outer width used when the app declares none.</summary>
    public const int StandardWidth = 800;

    /// <summary>Default outer height used when the app declares none.</summary>
    public const int StandardHeight = 560;

    /// <summary>Default minimum width used when the app declares none.</summary>
    public const int StandardMinWidth = 360;

    /// <summary>Default minimum height used when the app declares none.</summary>
    public const int StandardMinHeight = 240;

    /// <summary>
    /// Creates window options. Every parameter is optional, so a <c>window</c> object in
    /// <c>manifest.json</c> that omits a field gets that field's default, as it does in C#.
    /// </summary>
    /// <param name="defaultWidth">Width the window opens with.</param>
    /// <param name="defaultHeight">Height the window opens with.</param>
    /// <param name="minWidth">Smallest width.</param>
    /// <param name="minHeight">Smallest height.</param>
    /// <param name="maxWidth">Largest width, or no limit.</param>
    /// <param name="maxHeight">Largest height, or no limit.</param>
    /// <param name="resizable">Whether the user can resize the window.</param>
    [JsonConstructor]
    public WindowOptions(
        int defaultWidth = StandardWidth,
        int defaultHeight = StandardHeight,
        int minWidth = StandardMinWidth,
        int minHeight = StandardMinHeight,
        int? maxWidth = null,
        int? maxHeight = null,
        bool resizable = true)
    {
        DefaultWidth = defaultWidth;
        DefaultHeight = defaultHeight;
        MinWidth = minWidth;
        MinHeight = minHeight;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        Resizable = resizable;
    }

    /// <summary>Gets the options applied to an app that declares no <c>window</c> object.</summary>
    public static WindowOptions Default { get; } = new();

    /// <summary>Gets the width the window opens with. Defaults to 800.</summary>
    public int DefaultWidth { get; init; }

    /// <summary>Gets the height the window opens with. Defaults to 560.</summary>
    public int DefaultHeight { get; init; }

    /// <summary>Gets the smallest width the user can resize the window to. Defaults to 360.</summary>
    public int MinWidth { get; init; }

    /// <summary>Gets the smallest height the user can resize the window to. Defaults to 240.</summary>
    public int MinHeight { get; init; }

    /// <summary>
    /// Gets the largest width the user can resize the window to, or <see langword="null"/> for no
    /// limit. Maximizing ignores it, as common Linux window managers do.
    /// </summary>
    public int? MaxWidth { get; init; }

    /// <summary>Gets the largest height the user can resize the window to, or <see langword="null"/> for no limit.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>
    /// Gets whether the user can resize the window. Defaults to <see langword="true"/>. A fixed-size
    /// window has no resize handles and no Maximize button, and keyboard resize, maximize and snap
    /// commands do nothing on it.
    /// </summary>
    public bool Resizable { get; init; }

    /// <summary>
    /// Gets whether the OS remembers this window's size and maximized state between two openings
    /// (ADR 0116; never its position, ADR 0122: the window always opens at its cascade slot, as under
    /// GNOME). Defaults to <see langword="true"/>. An app that must always open at its declared size
    /// (a calculator, a wizard) sets it to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The geometry is device-local state in <c>~/.local/state/hackeros/windows/&lt;appId&gt;.config</c>,
    /// written by the window manager, never by the app. Added in SDK 1.1.
    /// </remarks>
    [JsonIgnore]
    public bool RememberGeometry { get; init; } = true;

    /// <summary>
    /// JSON projection of <see cref="RememberGeometry"/>: <c>window.rememberGeometry</c> is written
    /// only when it is <see langword="false"/>, so manifests of apps that keep the default stay
    /// byte-identical to the ones generated before ADR 0116; an absent field reads as
    /// <see langword="true"/>.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("rememberGeometry")]
    internal bool? RememberGeometryJson
    {
        get => RememberGeometry ? null : false;
        init => RememberGeometry = value ?? true;
    }
}
