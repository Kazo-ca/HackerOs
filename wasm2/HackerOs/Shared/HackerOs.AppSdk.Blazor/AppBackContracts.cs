namespace HackerOs.AppSdk.Blazor;

/// <summary>
/// Identifies what triggered a Back request, per doc/en/wasm/dev/old/mobile-interface-platform-plan.md §8. An app
/// rarely needs to branch on this, but it's available for the rare case where a Desktop chrome
/// click and a keyboard shortcut should be distinguished.
/// </summary>
public enum AppBackSource
{
    /// <summary>The Mobile system navigation bar's Back (triangle) button.</summary>
    SystemNavigationBar,

    /// <summary>The Desktop-only Back button in the window's own chrome (<c>MOB-013</c>).</summary>
    DesktopChromeButton,

    /// <summary>The documented keyboard shortcut, per plan §8's "le raccourci clavier documenté utilise le même contrat".</summary>
    KeyboardShortcut,

    /// <summary>The mouse's Back (button 4) or Forward (button 5) button (ADR 0220 §1).</summary>
    MouseButton,

    /// <summary>
    /// The host browser's or the phone's Back: Android Back button or gesture, iOS edge swipe, or the host browser's
    /// own Back arrow, caught through the tab's history (ADR 0220 §1).
    /// </summary>
    SystemBack
}

/// <summary>Describes one Back navigation request.</summary>
/// <param name="Source">What triggered this request.</param>
public sealed record AppBackRequest(AppBackSource Source);

/// <summary>Identifies the stable outcome of an app's attempt to handle a Back request.</summary>
public enum AppBackResultStatus
{
    /// <summary>The app consumed the request and moved to a previous internal state.</summary>
    Handled,

    /// <summary>The app has nothing to navigate back to; the shell should apply its own fallback.</summary>
    NotHandled,

    /// <summary>The app refuses to navigate back right now (e.g. a blocking operation is in progress).</summary>
    Blocked,

    /// <summary>The app needs to show its own confirmation (e.g. unsaved changes) before deciding.</summary>
    ConfirmationRequired,

    /// <summary>Handling the request threw; the shell should treat this like <see cref="NotHandled"/>.</summary>
    Faulted
}

/// <summary>The result of one <see cref="IAppBackHandler.NavigateBackAsync"/> call.</summary>
/// <param name="Status">Stable outcome discriminator.</param>
public sealed record AppBackResult(AppBackResultStatus Status)
{
    /// <summary>A ready-made <see cref="AppBackResultStatus.Handled"/> result.</summary>
    public static AppBackResult Handled { get; } = new(AppBackResultStatus.Handled);

    /// <summary>A ready-made <see cref="AppBackResultStatus.NotHandled"/> result.</summary>
    public static AppBackResult NotHandled { get; } = new(AppBackResultStatus.NotHandled);
}

/// <summary>
/// Lets a window app participate in the platform's Back navigation, per
/// doc/en/wasm/dev/old/mobile-interface-platform-plan.md §8 (<c>MOB-012</c>) — the Mobile system navigation bar's
/// triangle and the Desktop-only chrome Back button (<c>MOB-013</c>) both call the same contract. A
/// manifest declaring <c>supportsBack</c> is validated at discovery time to implement this interface
/// (see <c>AppEntryPointDiscovery</c>); an app that doesn't declare it simply never gets a Back
/// affordance shown for it.
/// </summary>
public interface IAppBackHandler
{
    /// <summary>Gets whether the app currently has a previous internal state to navigate back to.</summary>
    bool CanNavigateBack { get; }

    /// <summary>
    /// Raised when <see cref="CanNavigateBack"/> changes, so the shell can update its controls
    /// (e.g. the Desktop chrome button's disabled state) without polling.
    /// </summary>
    event Action? CanNavigateBackChanged;

    /// <summary>Asks the app to navigate back to its previous internal state.</summary>
    /// <param name="request">Describes what triggered the request.</param>
    /// <param name="cancellationToken">Cancels the pending navigation.</param>
    ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Describes one Forward navigation request (ADR 0220 §3).</summary>
/// <param name="Source">What triggered this request; the enumeration is shared with Back requests.</param>
public sealed record AppForwardRequest(AppBackSource Source);

/// <summary>
/// Lets a window app go forward again after going back, when the user presses the mouse's Forward button,
/// Alt+→ or the keyboard's Browser Forward key (ADR 0220 §3). The platform sends the request to the active
/// window; an app that implements this interface needs nothing in its manifest.
/// </summary>
public interface IAppForwardHandler
{
    /// <summary>Gets whether the app currently has a later internal state to navigate forward to.</summary>
    bool CanNavigateForward { get; }

    /// <summary>Raised when <see cref="CanNavigateForward"/> changes.</summary>
    event Action? CanNavigateForwardChanged;

    /// <summary>Asks the app to navigate forward to the state it left by going back.</summary>
    /// <param name="request">Describes what triggered the request.</param>
    /// <param name="cancellationToken">Cancels the pending navigation.</param>
    /// <returns>The outcome; <see cref="AppBackResultStatus.NotHandled"/> when there is nothing to go forward to.</returns>
    ValueTask<AppBackResult> NavigateForwardAsync(AppForwardRequest request, CancellationToken cancellationToken = default);
}
