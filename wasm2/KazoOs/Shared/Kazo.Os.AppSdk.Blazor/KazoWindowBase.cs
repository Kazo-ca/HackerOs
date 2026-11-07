using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Base class of a child window's component (ADR 0234 §3): a window an app opens with
/// <see cref="WindowAppBase.ShowWindowAsync{TComponent}"/> or a dialog it opens with
/// <see cref="WindowAppBase.ShowDialogAsync{TComponent}"/>, as a WinForms <c>Form</c> shown by another.
/// </summary>
/// <remarks>
/// The window belongs to the app instance that opened it: same process, same rights, same
/// <see cref="KazoComponentBase.AppContext"/>, and it closes with its app or its owner. It has no app
/// lifecycle of its own (no <c>OnAppInitialized</c>): use the usual component methods. Its parameters
/// come from <see cref="KazoWindowRequest.Parameters"/>.
/// </remarks>
public abstract class KazoWindowBase : KazoComponentBase
{
    /// <summary>Gets or sets the platform's handle on this child window, cascaded by the window manager.</summary>
    [CascadingParameter]
    private IKazoWindow? KazoWindow { get; set; }

    /// <summary>Gets or sets the platform's gateway of this child window, cascaded by the window manager.</summary>
    [CascadingParameter]
    private IAppWindowGateway? WindowGateway { get; set; }

    /// <summary>Gets the title the window manager shows for this window.</summary>
    protected string WindowTitle => WindowGateway?.Title ?? AppContext?.Manifest.Name ?? string.Empty;

    /// <summary>Gets this window's current options: those of its request until it changes them.</summary>
    protected WindowOptions WindowOptions => WindowGateway?.Options ?? WindowOptions.Default;

    /// <summary>
    /// Closes this window and gives <paramref name="result"/> to the one that opened it, as setting
    /// <c>DialogResult</c> on a WinForms form.
    /// </summary>
    /// <param name="result">The result; <see langword="null"/> gives <see cref="KazoDialogResult.Cancel"/>.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when this call closed the window.</returns>
    protected ValueTask<bool> CloseAsync(KazoDialogResult? result = null, CancellationToken cancellationToken = default) =>
        KazoWindow?.CloseAsync(result, cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Replaces this window's title; <see langword="null"/> or blank gives the app's name.</summary>
    /// <param name="title">The full title.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns>The title actually shown once cleaned.</returns>
    protected ValueTask<string> SetWindowTitleAsync(string? title, CancellationToken cancellationToken = default) =>
        WindowGateway?.SetTitleAsync(title, cancellationToken) ?? ValueTask.FromResult(title ?? string.Empty);

    /// <summary>Minimizes this window (ADR 0234 §4).</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is minimized.</returns>
    protected ValueTask<bool> MinimizeWindowAsync(CancellationToken cancellationToken = default) =>
        WindowGateway?.MinimizeAsync(cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Maximizes this window; a fixed-size window refuses.</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is maximized.</returns>
    protected ValueTask<bool> MaximizeWindowAsync(CancellationToken cancellationToken = default) =>
        WindowGateway?.MaximizeAsync(cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Gives this window back its normal geometry.</summary>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window is in its normal state.</returns>
    protected ValueTask<bool> RestoreWindowAsync(CancellationToken cancellationToken = default) =>
        WindowGateway?.RestoreAsync(cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Moves this window's top-left corner, relative to its screen's work area.</summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window moved.</returns>
    protected ValueTask<bool> MoveWindowAsync(double x, double y, CancellationToken cancellationToken = default) =>
        WindowGateway?.MoveAsync(x, y, cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Resizes this window within its sizes and the work area.</summary>
    /// <param name="width">Outer width in CSS pixels.</param>
    /// <param name="height">Outer height in CSS pixels.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window was resized.</returns>
    protected ValueTask<bool> ResizeWindowAsync(double width, double height, CancellationToken cancellationToken = default) =>
        WindowGateway?.ResizeAsync(width, height, cancellationToken) ?? ValueTask.FromResult(false);

    /// <summary>Changes this window's style and keeps its other options.</summary>
    /// <param name="style">The new style.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window manager applied it.</returns>
    protected ValueTask<bool> SetWindowStyleAsync(WindowStyle style, CancellationToken cancellationToken = default) =>
        SetWindowOptionsAsync(WindowOptions with { Style = style }, cancellationToken);

    /// <summary>Replaces this window's style, switches and size constraints.</summary>
    /// <param name="options">The new options.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when the window manager applied them.</returns>
    protected ValueTask<bool> SetWindowOptionsAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WindowGateway?.SetOptionsAsync(options, cancellationToken) ?? ValueTask.FromResult(false);
    }

    /// <summary>Opens a window owned by this one (ADR 0234 §3), as <c>Form.Show(owner)</c>.</summary>
    /// <typeparam name="TComponent">The new window's component.</typeparam>
    /// <param name="request">Title, options and parameters.</param>
    /// <param name="cancellationToken">Cancels the request before the window opens.</param>
    /// <returns>The new window.</returns>
    protected ValueTask<IKazoWindow> ShowWindowAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        KazoWindowRequest request,
        CancellationToken cancellationToken = default)
        where TComponent : KazoWindowBase =>
        ChildWindowRequests.ShowAsync(WindowGateway, typeof(TComponent), request, cancellationToken);

    /// <summary>Opens a modal dialog owned by this window and waits for its result, as <c>Form.ShowDialog(owner)</c>.</summary>
    /// <typeparam name="TComponent">The dialog's component.</typeparam>
    /// <param name="request">Title, options and parameters; <see cref="WindowOptions.Modal"/> is forced on.</param>
    /// <param name="cancellationToken">Cancels the wait and closes the dialog.</param>
    /// <returns>The dialog's result.</returns>
    protected ValueTask<KazoDialogResult> ShowDialogAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        KazoWindowRequest request,
        CancellationToken cancellationToken = default)
        where TComponent : KazoWindowBase =>
        ChildWindowRequests.ShowDialogAsync(WindowGateway, typeof(TComponent), request, cancellationToken);
}
