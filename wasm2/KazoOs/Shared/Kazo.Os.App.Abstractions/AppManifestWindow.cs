using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The frame a window app's window gets (ADR 0234 §1), as <c>FormBorderStyle</c> of a WinForms form.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WindowStyle>))]
public enum WindowStyle
{
    /// <summary>Title bar, border and resize handles; the buttons follow the <see cref="WindowOptions"/> switches.</summary>
    [JsonStringEnumMemberName("normal")]
    Normal,

    /// <summary>A shorter title bar with the Close button only, not resizable by the user, like the OS dialogs.</summary>
    [JsonStringEnumMemberName("tool")]
    Tool,

    /// <summary>
    /// No title bar and no border: only the client area shows, on a transparent surface whose empty
    /// areas let clicks through. The app draws its own buttons and marks its drag areas with
    /// <c>data-kazo-window-drag</c>.
    /// </summary>
    [JsonStringEnumMemberName("none")]
    None,
}

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
    /// The geometry is device-local state in <c>~/.local/state/kazoos/windows/&lt;appId&gt;.config</c>,
    /// written by the window manager, never by the app. Added in SDK 1.1.
    /// </remarks>
    [JsonIgnore]
    public bool RememberGeometry { get; init; } = true;

    /// <summary>
    /// Gets the frame of the window (ADR 0234 §1). Defaults to <see cref="WindowStyle.Normal"/>.
    /// <see cref="WindowStyle.Tool"/> turns <see cref="Resizable"/>, <see cref="MaximizeBox"/> and
    /// <see cref="MinimizeBox"/> off for the user. Added in SDK 3.1.
    /// </summary>
    [JsonIgnore]
    public WindowStyle Style { get; init; } = WindowStyle.Normal;

    /// <summary>Gets whether the title bar shows the app icon (ADR 0234 §2). Defaults to <see langword="true"/>. Added in SDK 3.1.</summary>
    [JsonIgnore]
    public bool ShowIcon { get; init; } = true;

    /// <summary>
    /// Gets whether the title bar shows the Maximize button (ADR 0234 §2). Defaults to
    /// <see langword="true"/>; a window that is not <see cref="Resizable"/> has none anyway. Off, the
    /// user can no longer maximize the window by double click, snapping or keyboard either. Added in SDK 3.1.
    /// </summary>
    [JsonIgnore]
    public bool MaximizeBox { get; init; } = true;

    /// <summary>
    /// Gets whether the title bar shows the Minimize button (ADR 0234 §2). Defaults to
    /// <see langword="true"/>. Off, the user can no longer minimize the window from its menu or the
    /// keyboard either; the app itself still can. Added in SDK 3.1.
    /// </summary>
    [JsonIgnore]
    public bool MinimizeBox { get; init; } = true;

    /// <summary>
    /// Gets whether the window has a taskbar button (ADR 0234 §2). Defaults to <see langword="true"/>.
    /// Off, the window stays in Alt+Tab and the window switcher but the user cannot minimize it.
    /// Added in SDK 3.1.
    /// </summary>
    [JsonIgnore]
    public bool ShowInTaskbar { get; init; } = true;

    /// <summary>
    /// Gets whether the window blocks the window that opened it until it closes (ADR 0234 §3).
    /// Defaults to <see langword="false"/>. It applies to a child window opened by an app; an app's
    /// main window has no owner, so the OS ignores it there. Added in SDK 3.1.
    /// </summary>
    [JsonIgnore]
    public bool Modal { get; init; }

    /// <summary>JSON projection of <see cref="Style"/>, written only when it is not <see cref="WindowStyle.Normal"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("style")]
    internal WindowStyle? StyleJson
    {
        get => Style == WindowStyle.Normal ? null : Style;
        init => Style = value ?? WindowStyle.Normal;
    }

    /// <summary>JSON projection of <see cref="ShowIcon"/>, written only when it is <see langword="false"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("showIcon")]
    internal bool? ShowIconJson
    {
        get => ShowIcon ? null : false;
        init => ShowIcon = value ?? true;
    }

    /// <summary>JSON projection of <see cref="MaximizeBox"/>, written only when it is <see langword="false"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("maximizeBox")]
    internal bool? MaximizeBoxJson
    {
        get => MaximizeBox ? null : false;
        init => MaximizeBox = value ?? true;
    }

    /// <summary>JSON projection of <see cref="MinimizeBox"/>, written only when it is <see langword="false"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("minimizeBox")]
    internal bool? MinimizeBoxJson
    {
        get => MinimizeBox ? null : false;
        init => MinimizeBox = value ?? true;
    }

    /// <summary>JSON projection of <see cref="ShowInTaskbar"/>, written only when it is <see langword="false"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("showInTaskbar")]
    internal bool? ShowInTaskbarJson
    {
        get => ShowInTaskbar ? null : false;
        init => ShowInTaskbar = value ?? true;
    }

    /// <summary>JSON projection of <see cref="Modal"/>, written only when it is <see langword="true"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("modal")]
    internal bool? ModalJson
    {
        get => Modal ? true : null;
        init => Modal = value ?? false;
    }

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
