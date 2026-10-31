using HackerOs.AppSdk.Blazor;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Navigation;

/// <summary>Which way a hardware navigation gesture goes (ADR 0220 §1).</summary>
public enum HardwareNavigationDirection
{
    /// <summary>Mouse button 4, Alt+←, Browser Back, or the host browser's / phone's Back.</summary>
    Back,

    /// <summary>Mouse button 5, Alt+→ or Browser Forward.</summary>
    Forward,
}

/// <summary>One Back or Forward gesture caught by <see cref="HardwareNavigationListener"/>.</summary>
/// <param name="Direction">Which way to go.</param>
/// <param name="Source">What the user pressed; passed on unchanged to the app's handler.</param>
public sealed record HardwareNavigationRequest(HardwareNavigationDirection Direction, AppBackSource Source);

/// <summary>
/// Sends the Back and Forward gestures the browser would otherwise apply to the host tab to the active
/// window (ADR 0220 §2). <see cref="HardwareNavigationListener"/> only translates browser events; every
/// decision is made here or by the shell's <see cref="Route"/>.
/// </summary>
/// <remarks>
/// The desktop routes to the focused window and never leaves the OS: a window with no handler, or one that
/// answers <see cref="AppBackResultStatus.NotHandled"/>, simply ignores the gesture (ADR 0220 Q3). The mobile
/// shell replaces the route with its own ◁ sequence (previous window, close, home).
/// </remarks>
public sealed class HardwareNavigationService(WindowRuntime windowRuntime, AppBackHandlerRegistry handlers)
{
    /// <summary>
    /// Gets or sets the active shell's route. <see langword="null"/> (no shell signed in) means
    /// <see cref="NavigateFocusedWindowAsync"/>. A shell sets it when it starts and clears it when it is disposed.
    /// </summary>
    public Func<HardwareNavigationRequest, Task>? Route { get; set; }

    /// <summary>Handles one gesture through the shell's route, or the desktop default.</summary>
    /// <param name="request">The gesture.</param>
    public Task NavigateAsync(HardwareNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Route is { } route ? route(request) : NavigateFocusedWindowAsync(request);
    }

    /// <summary>
    /// Sends the gesture to the focused, non-minimized window's <see cref="IAppBackHandler"/> or
    /// <see cref="IAppForwardHandler"/>. Does nothing when no window has the focus.
    /// </summary>
    /// <param name="request">The gesture.</param>
    /// <returns>The app's answer, or <see cref="AppBackResultStatus.NotHandled"/> when there is no target.</returns>
    public async Task<AppBackResult> NavigateFocusedWindowAsync(HardwareNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WindowRuntimeState? focused = windowRuntime.Windows.FirstOrDefault(
            window => window.IsFocused && window.VisualState != WindowVisualState.Minimized);
        return focused is null
            ? AppBackResult.NotHandled
            : await NavigateWindowAsync(focused.Id, request).ConfigureAwait(false);
    }

    /// <summary>Sends the gesture to one window's handler.</summary>
    /// <param name="windowId">The target window.</param>
    /// <param name="request">The gesture.</param>
    /// <returns>The app's answer; a handler that throws counts as <see cref="AppBackResultStatus.Faulted"/>.</returns>
    public async Task<AppBackResult> NavigateWindowAsync(WindowId windowId, HardwareNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Direction == HardwareNavigationDirection.Forward
                ? await handlers.NavigateForwardAsync(windowId, new AppForwardRequest(request.Source)).ConfigureAwait(false)
                : await handlers.NavigateBackAsync(windowId, new AppBackRequest(request.Source)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An app's handler must not break the shell's input loop; the gesture just does nothing.
            return new AppBackResult(AppBackResultStatus.Faulted);
        }
    }
}
