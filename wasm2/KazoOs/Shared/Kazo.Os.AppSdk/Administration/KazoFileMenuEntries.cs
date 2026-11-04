using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Lists the entries apps added to the file menus (ADR 0151) and hides or shows them for the session
/// user, as Settings &gt; File menus does (ADR 0159 §8). The choice lives in the user's
/// <c>~/.config/hackeros/context-menus.config</c>.
/// </summary>
/// <remarks>
/// Every member requires the capability <c>apps.manage</c>; a missing one raises
/// <see cref="KazoUnauthorizedAccessException"/>. Hiding an entry changes only the session user's
/// menus, applies at the next menu opened, and never uninstalls anything: the app keeps its entries
/// in <c>/etc/hackeros/context-menus.d</c>.
/// </remarks>
/// <example>
/// <code>
/// IReadOnlyList&lt;KazoFileMenuEntryInfo&gt; entries = await KazoFileMenuEntries.GetEntriesAsync();
/// await KazoFileMenuEntries.SetHiddenAsync("org.hackeros.file-explorer", "extract-here", hidden: true);
/// </code>
/// </example>
public static class KazoFileMenuEntries
{
    /// <summary>Lists the entries every installed app registered.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<IReadOnlyList<KazoFileMenuEntryInfo>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        GetEntriesAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Lists the entries through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<IReadOnlyList<KazoFileMenuEntryInfo>> GetEntriesAsync(
        IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetEntriesAsync(cancellationToken));

    /// <summary>Hides or shows one entry in the session user's file menus.</summary>
    /// <param name="appId">The app that registered it.</param>
    /// <param name="name">The entry's short id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="ArgumentException">No installed app registered this entry.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task SetHiddenAsync(string appId, string name, bool hidden, CancellationToken cancellationToken = default) =>
        SetHiddenAsync(KazoExecutionContext.Current, appId, name, hidden, cancellationToken);

    /// <summary>Hides or shows one entry through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="appId">The app that registered it.</param>
    /// <param name="name">The entry's short id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task SetHiddenAsync(
        IAppExecutionContext context, string appId, string name, bool hidden, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Call(context, async gateway =>
        {
            await gateway.SetHiddenAsync(appId, name, hidden, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppFileMenuEntryAdministrationGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.FileMenuEntryAdministration).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }
}
