using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// The window manager's side of one app window (ADR 0117 §2): the platform hands each rendered
/// window app its own instance, bound to that window only.
/// </summary>
/// <remarks>
/// Implemented by the platform and passed to <see cref="WindowAppBase"/> as a fixed cascading
/// value, so the SDK never references <c>Kazo.Os.Windowing.*</c> (ADR 0055). Apps do not use it
/// directly: they call <see cref="WindowAppBase.SetWindowTitleAsync"/> and read
/// <see cref="WindowAppBase.WindowTitle"/>. Holding an instance only ever reaches the window it
/// was created for, never another window of the same app, so no capability guards it.
/// </remarks>
public interface IAppWindowGateway
{
    /// <summary>Gets the title the window manager currently shows for the window.</summary>
    string Title { get; }

    /// <summary>
    /// Gets the window's default title: the app's name in the desktop language (ADR 0104 §4), the
    /// one a new window gets and an empty title restores.
    /// </summary>
    string DefaultTitle { get; }

    /// <summary>
    /// Replaces the window's title. The window manager cleans it (control characters removed, white
    /// space trimmed, at most 256 characters) and shows it as given; <see langword="null"/>, empty
    /// or blank restores the app's localized name.
    /// </summary>
    /// <param name="title">The full title, e.g. <c>photo.png — Kazo Paint</c>.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns>The title actually shown once cleaned.</returns>
    ValueTask<string> SetTitleAsync(string? title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the window's current options (ADR 0234 §4): the manifest's <c>window</c> object until
    /// <see cref="SetOptionsAsync"/> changes them. Added in SDK 3.1; an older implementation reports
    /// <see cref="WindowOptions.Default"/>.
    /// </summary>
    WindowOptions Options => WindowOptions.Default;

    /// <summary>
    /// Replaces the window's style, switches and size constraints at run time (ADR 0234 §4), as
    /// setting <c>FormBorderStyle</c> or <c>MaximizeBox</c> on a shown WinForms form. The default
    /// sizes only matter for a new window and are ignored here.
    /// </summary>
    /// <param name="options">The new options.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window manager applied them.</returns>
    ValueTask<bool> SetOptionsAsync(WindowOptions options, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Minimizes the window (ADR 0234 §4, issue #384), whatever buttons its frame shows.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is minimized.</returns>
    ValueTask<bool> MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>Maximizes the window to its screen's work area (ADR 0234 §4); needs a resizable window.</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is maximized.</returns>
    ValueTask<bool> MaximizeAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>Gives a minimized, maximized or snapped window back its normal geometry (ADR 0234 §4).</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is in its normal state.</returns>
    ValueTask<bool> RestoreAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>
    /// Moves the window's top-left corner to <paramref name="x"/>, <paramref name="y"/> in its
    /// screen's work area (ADR 0234 §4); the window manager keeps it on the screen.
    /// </summary>
    /// <param name="x">Left edge, in CSS pixels from the work area's left.</param>
    /// <param name="y">Top edge, in CSS pixels from the work area's top.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window moved; <see langword="false"/> when it is not in its normal state.</returns>
    ValueTask<bool> MoveAsync(double x, double y, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>
    /// Resizes the window (ADR 0234 §4) within its declared minimum and maximum and the work area.
    /// </summary>
    /// <param name="width">Outer width in CSS pixels.</param>
    /// <param name="height">Outer height in CSS pixels.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window was resized; <see langword="false"/> when it is not resizable or not in its normal state.</returns>
    ValueTask<bool> ResizeAsync(double width, double height, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}
