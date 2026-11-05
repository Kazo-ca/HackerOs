using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Chooses which app provides the OS's file view and file dialogs for the whole machine (ADR 0156),
/// as Settings &gt; OS components does. The choice lives in <c>/etc/kazoos/components.config</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member requires the capability <c>apps.manage</c>; a missing one raises
/// <see cref="KazoUnauthorizedAccessException"/>.
/// </para>
/// <para>
/// Choosing an app that is not a system app shows the OS's approval window of the dangerous
/// capabilities (ADR 0049), since that app's code will run inside every app that shows files, with
/// that app's rights: an administrator must type their password there. Choosing a system app, or going
/// back to the default, only asks an administrator in the OS's own secure window (ADR 0055 §5). The app
/// never sees the password. A refusal or a cancellation raises
/// <see cref="KazoUnauthorizedAccessException"/> and nothing is written. The choice applies to the next
/// control shown, in every app.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IReadOnlyList&lt;KazoComponentContractInfo&gt; contracts = await KazoComponentProviders.GetOverviewAsync();
/// await KazoComponentProviders.SetProviderAsync(["kazoos.file-view", "kazoos.file-dialogs"], "com.example.files");
/// </code>
/// </example>
public static class KazoComponentProviders
{
    /// <summary>Lists each contract an administrator chooses, its providers and the current choice.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<IReadOnlyList<KazoComponentContractInfo>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        GetOverviewAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Lists the contracts through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<IReadOnlyList<KazoComponentContractInfo>> GetOverviewAsync(
        IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetOverviewAsync(cancellationToken));

    /// <summary>Chooses the provider of <paramref name="contracts"/> for the whole machine.</summary>
    /// <param name="contracts">The contracts, at least one; several share one approval.</param>
    /// <param name="appId">The app, or <see langword="null"/> to go back to the default (the File Explorer).</param>
    /// <param name="cancellationToken">Cancels the approval and the write.</param>
    /// <exception cref="KazoUnauthorizedAccessException">
    /// This app lacks <c>apps.manage</c>, or no administrator approved.
    /// </exception>
    /// <exception cref="ArgumentException">A contract is not chosen by an administrator, or the app does not provide it or is not installed.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task SetProviderAsync(IReadOnlyList<string> contracts, string? appId, CancellationToken cancellationToken = default) =>
        SetProviderAsync(KazoExecutionContext.Current, contracts, appId, cancellationToken);

    /// <summary>Chooses the provider through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="contracts">The contracts, at least one.</param>
    /// <param name="appId">The app, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">Cancels the approval and the write.</param>
    public static Task SetProviderAsync(
        IAppExecutionContext context, IReadOnlyList<string> contracts, string? appId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return Call(context, async gateway =>
        {
            await gateway.SetProviderAsync(contracts, appId, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppComponentProviderAdministrationGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.ComponentProviderAdministration).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }
}
