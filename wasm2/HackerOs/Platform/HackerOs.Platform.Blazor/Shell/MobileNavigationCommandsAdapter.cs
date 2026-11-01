using HackerOs.AppSdk.Blazor;
using HackerOs.MobileShell.Blazor;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// Adapts <see cref="WindowRuntime"/> to <see cref="IMobileNavigationCommands"/> for
/// <see cref="MobileShell"/>. <see cref="RequestBack"/> implements the full ordered sequence from
/// plan §7.3 (<c>MOB-011</c>), plus one deliberate addition ahead of the literal five steps:
/// dismissing a shell-owned overlay (the notification shade or Recent) first, since that is
/// obviously the true topmost UI even though the plan's five steps don't separately name it.
/// </summary>
public sealed class MobileNavigationCommandsAdapter(
    WindowRuntime windowRuntime,
    AppBackHandlerRegistry backHandlers,
    MobileNavigationHistory history,
    Func<WindowId, Task> closeWindow,
    Func<bool> isOverlayOpen,
    Action closeOverlay,
    Action toggleRecent) : IMobileNavigationCommands
{
    /// <inheritdoc />
    public async void RequestBack()
    {
        // async void is deliberate here: IMobileNavigationCommands.RequestBack is a fire-and-forget
        // UI command dispatch (the triangle button's onclick), matching MobileSystemNavigationBar's
        // existing synchronous IMobileNavigationCommands contract.
        await RequestBackAsync(AppBackSource.SystemNavigationBar);
    }

    /// <summary>
    /// Runs the ordered Back sequence for one gesture: the ◁ button, or a hardware gesture caught by
    /// <c>HardwareNavigationListener</c> (ADR 0220 §2), whose source the app receives unchanged.
    /// </summary>
    /// <param name="source">What the user pressed.</param>
    public async Task RequestBackAsync(AppBackSource source)
    {
        // Step 0 (deliberate addition, not one of the plan's literal five steps): a shell-owned
        // overlay is the true topmost UI regardless of what window is primary underneath it.
        if (isOverlayOpen())
        {
            closeOverlay();
            return;
        }

        WindowRuntimeState? primary = SelectPrimaryState();

        // Step 1: dismiss the topmost cancelable system dialog/modal. A window's own
        // OnRequestClose delegate IS the cancelable signal here -- the same one the taskbar's/
        // desktop's close paths already treat as "route close to this window's own cancel logic
        // instead of the ordinary close-guard flow" (see TaskbarCommandDispatcherAdapter.Close).
        if (primary is { Modality: WindowModality.OwnerModal, OnRequestClose: { } onRequestClose })
        {
            await onRequestClose();
            return;
        }

        // Step 2 (existing): the active window's declared IAppBackHandler.
        if (primary is not null)
        {
            AppBackResult result = await backHandlers.NavigateBackAsync(
                primary.Id,
                new AppBackRequest(source));
            if (result.Status is AppBackResultStatus.Handled or AppBackResultStatus.Blocked or AppBackResultStatus.ConfirmationRequired)
            {
                // Handled: the app moved back internally. Blocked/ConfirmationRequired: the app is
                // already presenting its own outcome (or nothing, per its own policy) -- either way
                // Back must not fall through past it to close the window out from under the app.
                return;
            }
        }

        // Step 3: return to the previous shell-navigation entry.
        if (history.TryPopPrevious(out WindowId previousId))
        {
            WindowRuntimeState? previous = windowRuntime.Windows.FirstOrDefault(window => window.Id == previousId);
            if (previous is { VisualState: WindowVisualState.Minimized })
            {
                windowRuntime.Apply(new RestoreWindowCommand(previousId));
            }

            windowRuntime.Apply(new FocusWindowCommand(previousId));
            return;
        }

        // Step 4: no previous state -- close the active surface per its own guarded close policy
        // and fall back to Home.
        if (primary is not null)
        {
            await closeWindow(primary.Id);
            return;
        }

        // Step 5: already at Home with no history -- a deliberate no-op per plan §7.3.
    }

    /// <summary>
    /// Sends Forward (the mouse's button 5, Alt+→, Browser Forward) to the foreground app's
    /// <see cref="IAppForwardHandler"/> (ADR 0220 §2). Nothing happens on Home or when the app can't go forward.
    /// </summary>
    /// <param name="source">What the user pressed.</param>
    public async Task RequestForwardAsync(AppBackSource source)
    {
        if (isOverlayOpen() || SelectPrimaryState() is not { } primary)
        {
            return;
        }

        await backHandlers.NavigateForwardAsync(primary.Id, new AppForwardRequest(source));
    }

    /// <inheritdoc />
    public void RequestHome()
    {
        // Hides the active surface without terminating its process, per plan §7.4. Reuses the same
        // Minimize mechanism SingleSurfacePresentationPolicy already uses to hide background
        // windows; a subsequent app-icon tap restores the same instance via the ordinary Restore
        // command. Does not clear the navigation history: returning to an app later still lets the
        // user Back through what it had, matching Android's own Home semantics.
        WindowId? primaryId = SingleSurfacePresentationPolicy.SelectPrimary(windowRuntime.Windows);
        if (primaryId is WindowId id)
        {
            windowRuntime.Apply(new MinimizeWindowCommand(id));
        }
    }

    /// <inheritdoc />
    public void RequestRecent() => toggleRecent();

    private WindowRuntimeState? SelectPrimaryState()
    {
        WindowId? primaryId = SingleSurfacePresentationPolicy.SelectPrimary(windowRuntime.Windows);
        return primaryId is WindowId id
            ? windowRuntime.Windows.FirstOrDefault(window => window.Id == id)
            : null;
    }
}
