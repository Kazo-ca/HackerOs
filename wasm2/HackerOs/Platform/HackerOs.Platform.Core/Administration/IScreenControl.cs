using HackerOs.AppSdk.Administration;

namespace HackerOs.Platform.Core.Administration;

/// <summary>
/// The OS's secondary screens as <see cref="AppScreensGateway"/> drives them (ADR 0184). The browser
/// host implements it over its screen service and window manager; a host without screens leaves it
/// out, and <c>HackScreens</c> stays unsupported.
/// </summary>
public interface IScreenControl
{
    /// <summary>Raised when a screen opens, closes or changes, or when the permission changes.</summary>
    event Action? Changed;

    /// <summary>Gets whether the shell shows screens now: the desktop shell started them (never the mobile shell).</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the screens of the session as one app instance sees them.</summary>
    /// <param name="appInstanceId">The caller, whose window gives <see cref="HackScreensState.CallerScreen"/>.</param>
    HackScreensState GetState(Guid appInstanceId);

    /// <summary>Opens a secondary screen with the smallest free number, during the user's click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken);

    /// <summary>Reopens the screens of the last session, during the user's click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>How many screens opened.</returns>
    Task<int> RestoreScreensAsync(CancellationToken cancellationToken);

    /// <summary>Closes a secondary screen; nothing happens when it is not open.</summary>
    /// <param name="number">The screen, 2 to 9.</param>
    Task CloseScreenAsync(int number);

    /// <summary>Shows each screen's number for a few seconds.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task IdentifyAsync(CancellationToken cancellationToken);

    /// <summary>Enters or leaves full screen on the screen of the caller's window, during the user's click.</summary>
    /// <param name="appInstanceId">The caller.</param>
    /// <returns><see langword="true"/> when the browser accepted.</returns>
    Task<bool> ToggleFullScreenAsync(Guid appInstanceId);

    /// <summary>Asks the browser for the <c>window-management</c> permission, during the user's click.</summary>
    /// <returns>The permission after the user answered.</returns>
    Task<HackScreenPermission> RequestPermissionAsync();
}
