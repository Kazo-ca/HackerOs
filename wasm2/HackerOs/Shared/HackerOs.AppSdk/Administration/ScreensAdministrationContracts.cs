namespace HackerOs.AppSdk.Administration;

/// <summary>State of the host's <c>window-management</c> permission (ADR 0178 §4, ADR 0184).</summary>
public enum HackScreenPermission
{
    /// <summary>Not read yet.</summary>
    Unknown,

    /// <summary>The host has no Window Management API (Firefox, Safari): the user places each screen's window.</summary>
    Unsupported,

    /// <summary>The browser asks the user at the next request or new screen.</summary>
    Prompt,

    /// <summary>New screens open directly on a free monitor.</summary>
    Granted,

    /// <summary>The user refused: the user places each screen's window.</summary>
    Denied,
}

/// <summary>How <see cref="HackScreens.OpenScreenAsync(CancellationToken)"/> ended.</summary>
public enum HackScreenOpenStatus
{
    /// <summary>The screen is open.</summary>
    Opened,

    /// <summary>The browser refused the window (pop-up blocker, or the call did not come from a click).</summary>
    Blocked,

    /// <summary>This host cannot open screens.</summary>
    Unavailable,

    /// <summary>Screens 2 to 9 are all open.</summary>
    LimitReached,
}

/// <summary>One screen of the session (ADR 0184).</summary>
/// <param name="Number">Its number: 1 for the main browser tab, 2 to 9 for the secondary screens.</param>
/// <param name="MonitorName">The monitor's name when Chrome or Edge gives it, otherwise <see langword="null"/>.</param>
/// <param name="Width">Width of its work area, in CSS pixels.</param>
/// <param name="Height">Height of its work area, in CSS pixels.</param>
/// <param name="IsFullScreen">Whether its browser window is in full screen.</param>
public sealed record HackScreenInfo(int Number, string? MonitorName, int Width, int Height, bool IsFullScreen);

/// <summary>The screens of the session as one app sees them (ADR 0184).</summary>
/// <param name="Screens">Every open screen, screen 1 first.</param>
/// <param name="Permission">The host's <c>window-management</c> permission.</param>
/// <param name="CanOpenScreen">Whether a screen number is still free.</param>
/// <param name="RestorableScreens">Screens of the last session that « Restore screens » would reopen.</param>
/// <param name="CallerScreen">The screen of the calling app's window: its focused one, else its first one, else 1.</param>
public sealed record HackScreensState(
    IReadOnlyList<HackScreenInfo> Screens,
    HackScreenPermission Permission,
    bool CanOpenScreen,
    IReadOnlyList<int> RestorableScreens,
    int CallerScreen);

/// <summary>
/// Trusted OS gateway behind <see cref="HackScreens"/> (ADR 0184). Every member but
/// <see cref="IsSupported"/> requires the capability <c>screens.manage</c>.
/// </summary>
public interface IAppScreensGateway
{
    /// <summary>Gets whether this host opens secondary screens.</summary>
    bool IsSupported { get; }

    /// <summary>Reads the screens of the session.</summary>
    HackScreensState GetState();

    /// <summary>Calls <paramref name="onChanged"/> each time the state changes, until the subscription is disposed.</summary>
    /// <param name="onChanged">The callback, entered in the app's ambient scope.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default);

    /// <summary>Opens a secondary screen with the smallest free number, during the user's click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken = default);

    /// <summary>Reopens the screens of the last session, during the user's click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>How many screens opened.</returns>
    ValueTask<int> RestoreScreensAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes a secondary screen; its windows come back to screen 1.</summary>
    /// <param name="number">The screen, 2 to 9.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask CloseScreenAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>Shows each screen's number on a veil for a few seconds.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask IdentifyAsync(CancellationToken cancellationToken = default);

    /// <summary>Enters or leaves full screen on the screen of the calling app's window, during the user's click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the browser accepted.</returns>
    ValueTask<bool> ToggleFullScreenAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the browser for the <c>window-management</c> permission, during the user's click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The permission after the user answered.</returns>
    ValueTask<HackScreenPermission> RequestPermissionAsync(CancellationToken cancellationToken = default);
}
