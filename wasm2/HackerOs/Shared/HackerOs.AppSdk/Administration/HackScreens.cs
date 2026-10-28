using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.Administration;

/// <summary>
/// Lists, opens, restores, closes and identifies the secondary screens of the session, as Settings
/// &gt; Screens does (ADR 0184). Each secondary screen is a window of the host browser (ADR 0178).
/// </summary>
/// <remarks>
/// <para>
/// Every member but <see cref="IsSupported"/> requires the capability <c>screens.manage</c>; a
/// missing one raises <see cref="HackUnauthorizedAccessException"/>. A host without screens (mobile,
/// tests) raises <see cref="NotSupportedException"/>: check <see cref="IsSupportedFor"/> first.
/// </para>
/// <para>
/// The browser only opens a window, asks for its permission or enters full screen during a click:
/// call <see cref="OpenScreenAsync(CancellationToken)"/>, <see cref="RestoreScreensAsync(CancellationToken)"/>,
/// <see cref="ToggleFullScreenAsync(CancellationToken)"/> and <see cref="RequestPermissionAsync(CancellationToken)"/>
/// straight from a click handler. Otherwise the window is <see cref="HackScreenOpenStatus.Blocked"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// if (HackScreens.IsSupported &amp;&amp; HackScreens.GetState().CanOpenScreen)
/// {
///     HackScreenOpenStatus status = await HackScreens.OpenScreenAsync();
/// }
/// </code>
/// </example>
public static class HackScreens
{
    /// <summary>Gets whether the current host opens secondary screens.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static bool IsSupported => IsSupportedFor(HackExecutionContext.Current);

    /// <summary>Gets whether the host of <paramref name="context"/> opens secondary screens.</summary>
    /// <param name="context">The calling app's execution context.</param>
    public static bool IsSupportedFor(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Screens.IsSupported;
    }

    /// <summary>Reads the screens of the session.</summary>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="NotSupportedException">This host has no screens.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static HackScreensState GetState() => GetState(HackExecutionContext.Current);

    /// <summary>Reads the screens through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    public static HackScreensState GetState(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return context.Screens.GetState();
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>Calls <paramref name="onChanged"/> each time a screen opens, closes, changes or the permission changes.</summary>
    /// <param name="onChanged">The callback, run in this app's ambient scope.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    /// <returns>Dispose it to stop.</returns>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
        WatchAsync(HackExecutionContext.Current, onChanged, cancellationToken);

    /// <summary>Follows the screens through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="onChanged">The callback, run in this app's ambient scope.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    public static Task<IAsyncDisposable> WatchAsync(
        IAppExecutionContext context, Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        return Call(context, gateway => gateway.WatchAsync(onChanged, cancellationToken));
    }

    /// <summary>Opens a secondary screen with the smallest free number. Call it from a click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken = default) =>
        OpenScreenAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Opens a secondary screen through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<HackScreenOpenStatus> OpenScreenAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.OpenScreenAsync(cancellationToken));

    /// <summary>Reopens the screens of the last session. Call it from a click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>How many screens opened; a browser may open only one window per click.</returns>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<int> RestoreScreensAsync(CancellationToken cancellationToken = default) =>
        RestoreScreensAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Reopens the screens of the last session through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<int> RestoreScreensAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.RestoreScreensAsync(cancellationToken));

    /// <summary>Closes a secondary screen; its windows come back to screen 1 and it is not restored.</summary>
    /// <param name="number">The screen, 2 to 9.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is not 2 to 9.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task CloseScreenAsync(int number, CancellationToken cancellationToken = default) =>
        CloseScreenAsync(HackExecutionContext.Current, number, cancellationToken);

    /// <summary>Closes a secondary screen through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="number">The screen, 2 to 9.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task CloseScreenAsync(IAppExecutionContext context, int number, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 9);
        return Call(context, async gateway =>
        {
            await gateway.CloseScreenAsync(number, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Shows each screen's number on a veil for a few seconds.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task IdentifyAsync(CancellationToken cancellationToken = default) =>
        IdentifyAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Shows each screen's number through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task IdentifyAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, async gateway =>
        {
            await gateway.IdentifyAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>Enters or leaves full screen on the screen of this app's window. Call it from a click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the browser accepted.</returns>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<bool> ToggleFullScreenAsync(CancellationToken cancellationToken = default) =>
        ToggleFullScreenAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Enters or leaves full screen through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<bool> ToggleFullScreenAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.ToggleFullScreenAsync(cancellationToken));

    /// <summary>Asks the browser for the <c>window-management</c> permission. Call it from a click.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The permission after the user answered.</returns>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>screens.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackScreenPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        RequestPermissionAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Asks for the permission through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<HackScreenPermission> RequestPermissionAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.RequestPermissionAsync(cancellationToken));

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppScreensGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.Screens).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }
}
