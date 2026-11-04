namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Lets a window app confirm or reject a user-initiated close without exposing
/// platform window/runtime implementations to the app.
/// </summary>
public interface IWindowCloseGuard
{
    /// <summary>
    /// Confirms whether the window may close, including any app-owned unsaved
    /// change prompt required before process cancellation begins.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending close request.</param>
    /// <returns><see langword="true"/> to close; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default);
}
