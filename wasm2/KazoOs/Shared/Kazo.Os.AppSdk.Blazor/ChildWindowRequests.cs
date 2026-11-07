using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Opens child windows and dialogs for <see cref="WindowAppBase"/> and <see cref="KazoWindowBase"/>
/// (ADR 0234 §3), so both share one behavior.
/// </summary>
internal static class ChildWindowRequests
{
    /// <summary>Opens a child window, modal when <paramref name="request"/> asks for it.</summary>
    public static async ValueTask<IKazoWindow> ShowAsync(
        IAppWindowGateway? gateway,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        KazoWindowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!typeof(KazoWindowBase).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"A child window's component must derive from {nameof(KazoWindowBase)}.", nameof(componentType));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (gateway is not IAppChildWindowGateway children)
        {
            throw new InvalidOperationException("This window cannot open child windows: no window manager is attached (a test, a detached window, or a platform older than SDK 3.1).");
        }

        return await children.OpenWindowAsync(componentType, request, cancellationToken)
            ?? throw new InvalidOperationException("The window manager refused the child window: its owner is closed.");
    }

    /// <summary>Opens a modal dialog and waits for its result.</summary>
    public static async ValueTask<KazoDialogResult> ShowDialogAsync(
        IAppWindowGateway? gateway,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        KazoWindowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        KazoWindowRequest modal = request with { Options = (request.Options ?? WindowOptions.Default) with { Modal = true } };
        IKazoWindow window = await ShowAsync(gateway, componentType, modal, cancellationToken);
        if (!cancellationToken.CanBeCanceled)
        {
            return await window.Closed;
        }

        // Cancelling the wait closes the dialog, as an owner that gives up on its question.
        await using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => _ = ((IKazoWindow)state!).CloseAsync(KazoDialogResult.Cancel),
            window);
        return await window.Closed;
    }
}
