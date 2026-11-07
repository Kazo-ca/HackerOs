using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Child windows and dialogs of a window app (ADR 0234 §3), the equivalent of WinForms
/// <c>Form.Show(owner)</c> and <c>Form.ShowDialog(owner)</c>.
/// </summary>
public abstract partial class WindowAppBase
{
    /// <summary>
    /// Opens a window owned by this app's window: same app instance, same rights, closed with the
    /// app or its owner. With <see cref="WindowOptions.Modal"/> it blocks this window until it closes.
    /// </summary>
    /// <typeparam name="TComponent">The new window's component, deriving from <see cref="KazoWindowBase"/>.</typeparam>
    /// <param name="request">Title, options and component parameters.</param>
    /// <param name="cancellationToken">Cancels the request before the window opens.</param>
    /// <returns>The new window; await <see cref="IKazoWindow.Closed"/> for its result.</returns>
    /// <exception cref="InvalidOperationException">No window manager is attached, or this window is closed.</exception>
    protected ValueTask<IKazoWindow> ShowWindowAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        KazoWindowRequest request,
        CancellationToken cancellationToken = default)
        where TComponent : KazoWindowBase =>
        ChildWindowRequests.ShowAsync(WindowGateway, typeof(TComponent), request, cancellationToken);

    /// <summary>
    /// Opens a modal dialog owned by this app's window and waits until it closes, as
    /// <c>Form.ShowDialog(owner)</c>. Its Close button and Alt+F4 give
    /// <see cref="KazoDialogResult.Cancel"/>.
    /// </summary>
    /// <typeparam name="TComponent">The dialog's component, deriving from <see cref="KazoWindowBase"/>.</typeparam>
    /// <param name="request">Title, options and parameters; <see cref="WindowOptions.Modal"/> is forced on.</param>
    /// <param name="cancellationToken">Cancels the wait and closes the dialog.</param>
    /// <returns>The dialog's result.</returns>
    /// <exception cref="InvalidOperationException">No window manager is attached, or this window is closed.</exception>
    protected ValueTask<KazoDialogResult> ShowDialogAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        KazoWindowRequest request,
        CancellationToken cancellationToken = default)
        where TComponent : KazoWindowBase =>
        ChildWindowRequests.ShowDialogAsync(WindowGateway, typeof(TComponent), request, cancellationToken);
}
