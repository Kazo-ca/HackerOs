using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Restarts or factory-resets the machine (ADR 0056, section 11). Replaces the calls to
/// <c>location.reload</c> and to the IndexedDB module that Settings made through
/// <c>IJSRuntime</c> (ADR 0055).
/// </summary>
/// <remarks>
/// <para>
/// Both members require the dangerous capability <c>system.power</c> and an administrator: the OS
/// asks for a password in its own secure window, like <c>reboot</c> needs root under Linux. A
/// refusal raises <see cref="HackUnauthorizedAccessException"/> and nothing happens. A process
/// already running as root through <c>sudo</c> is not asked again.
/// </para>
/// <para>
/// Once an administrator authenticated, the OS flushes the pending log writes, ends the session
/// (every process is cancelled, this app included) and restarts. The returned task may therefore
/// never complete from the caller's point of view.
/// </para>
/// </remarks>
public static class HackSystem
{
    /// <summary>Restarts the machine after an administrator authenticated.</summary>
    /// <param name="cancellationToken">Cancels the authentication prompt.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>system.power</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task RebootAsync(CancellationToken cancellationToken = default) =>
        RebootAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Restarts the machine through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the authentication prompt.</param>
    public static Task RebootAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.RebootAsync(cancellationToken));

    /// <summary>
    /// Erases the whole machine (every account, file, setting and grant) after an administrator
    /// authenticated, then restarts it as freshly installed. Cannot be undone.
    /// </summary>
    /// <param name="cancellationToken">Cancels the authentication prompt.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>system.power</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task FactoryResetAsync(CancellationToken cancellationToken = default) =>
        FactoryResetAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Factory-resets the machine through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the authentication prompt.</param>
    public static Task FactoryResetAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.FactoryResetAsync(cancellationToken));

    private static async Task Call(IAppExecutionContext context, Func<IAppSystemPowerGateway, ValueTask> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await call(context.SystemPower).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }
}

/// <summary>
/// Reads the raw storage snapshot of the machine (ADR 0056, section 11), as the IndexedDB
/// Diagnostic app shows it. Replaces <c>IndexedDbInspectorService</c> of
/// <c>Infrastructure.Browser</c> (ADR 0055).
/// </summary>
/// <remarks>
/// Requires the dangerous capability <c>system.storage.inspect</c>: the snapshot contains the files
/// of every user, bypassing their permissions. A non-system app declaring it is approved by an
/// administrator at its first launch (ADR 0049).
/// </remarks>
public static class HackStorageDiagnostics
{
    /// <summary>Reads every object store in one transaction and resolves each file's virtual path.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>system.storage.inspect</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackStorageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetSnapshotAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Reads the snapshot through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<HackStorageSnapshot> GetSnapshotAsync(IAppExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await context.StorageDiagnostics.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>Lists the browser databases of web sites (<c>hackweb.*</c>, ADR 0088).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>system.storage.inspect</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<HackWebSiteDatabaseInfo>> GetWebSiteDatabasesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await HackExecutionContext.Current.StorageDiagnostics.GetWebSiteDatabasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>Deletes the browser database of a web site; it comes back empty on the site's next request.</summary>
    /// <param name="databaseName">The <c>hackweb.*</c> name.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>system.storage.inspect</c>.</exception>
    public static async Task DeleteWebSiteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        try
        {
            await HackExecutionContext.Current.StorageDiagnostics.DeleteWebSiteDatabaseAsync(databaseName, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }
}
