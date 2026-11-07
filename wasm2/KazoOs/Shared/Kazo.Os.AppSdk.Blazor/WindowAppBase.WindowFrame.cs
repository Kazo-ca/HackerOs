using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// What a window app may ask of its own window (ADR 0234 §4, issue #384): minimize, maximize,
/// restore, move, resize, and change its style and switches at run time, as a WinForms form sets
/// <c>WindowState</c>, <c>Location</c>, <c>Size</c> or <c>FormBorderStyle</c>.
/// </summary>
/// <remarks>
/// These requests only reach this app instance's window, so no capability guards them (ADR 0117 §2).
/// The switches (<see cref="WindowOptions.MinimizeBox"/>, <see cref="WindowOptions.MaximizeBox"/>…)
/// choose the buttons shown to the user, not what the app may do: an app with
/// <c>MinimizeBox = false</c> can still call <see cref="MinimizeWindowAsync"/>. Without a window
/// manager (tests, a detached browser window, ADR 0078) every request returns
/// <see langword="false"/>.
/// </remarks>
public abstract partial class WindowAppBase
{
    /// <summary>
    /// Gets this window's current options: the manifest's <c>window</c> object until
    /// <see cref="SetWindowOptionsAsync"/> or <see cref="SetWindowStyleAsync"/> changes them.
    /// </summary>
    protected WindowOptions WindowOptions =>
        WindowGateway?.Options ?? AppContext?.Manifest.Window ?? WindowOptions.Default;

    /// <summary>Minimizes this window (issue #384), whatever buttons its frame shows.</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is minimized.</returns>
    protected ValueTask<bool> MinimizeWindowAsync(CancellationToken cancellationToken = default) =>
        WindowGateway?.MinimizeAsync(cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Maximizes this window to its screen's work area; a fixed-size window refuses.</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is maximized.</returns>
    protected ValueTask<bool> MaximizeWindowAsync(CancellationToken cancellationToken = default) =>
        WindowGateway?.MaximizeAsync(cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Gives a minimized, maximized or snapped window back its normal geometry.</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is in its normal state.</returns>
    protected ValueTask<bool> RestoreWindowAsync(CancellationToken cancellationToken = default) =>
        WindowGateway?.RestoreAsync(cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>
    /// Moves this window's top-left corner, in CSS pixels from its screen's work area; the window
    /// manager keeps it on the screen. Only a window in its normal state moves.
    /// </summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window moved.</returns>
    protected ValueTask<bool> MoveWindowAsync(double x, double y, CancellationToken cancellationToken = default) =>
        WindowGateway?.MoveAsync(x, y, cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>
    /// Resizes this window within its declared minimum and maximum and the work area. Only a
    /// resizable window in its normal state is resized.
    /// </summary>
    /// <param name="width">Outer width in CSS pixels.</param>
    /// <param name="height">Outer height in CSS pixels.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window was resized.</returns>
    protected ValueTask<bool> ResizeWindowAsync(double width, double height, CancellationToken cancellationToken = default) =>
        WindowGateway?.ResizeAsync(width, height, cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>
    /// Changes this window's style (ADR 0234 §1) and keeps its other options, e.g. a player that
    /// switches between its own skin (<see cref="WindowStyle.None"/>) and the OS frame.
    /// </summary>
    /// <param name="style">The new style.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window manager applied it.</returns>
    protected ValueTask<bool> SetWindowStyleAsync(WindowStyle style, CancellationToken cancellationToken = default) =>
        SetWindowOptionsAsync(WindowOptions with { Style = style }, cancellationToken);

    /// <summary>
    /// Replaces this window's style, switches and size constraints at run time (ADR 0234 §2-4).
    /// Start from <see cref="WindowOptions"/> with a <c>with</c> expression to change one switch.
    /// The default sizes only matter for a new window and are ignored here.
    /// </summary>
    /// <param name="options">The new options.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window manager applied them.</returns>
    protected ValueTask<bool> SetWindowOptionsAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WindowGateway?.SetOptionsAsync(options, cancellationToken) ?? ValueTask.FromResult(false);
    }
}
