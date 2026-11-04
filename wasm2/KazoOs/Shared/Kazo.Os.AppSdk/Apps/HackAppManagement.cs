using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// Uninstalls and reinstalls apps (ADR 0056, section 3), as Settings &gt; Installed Apps does.
/// Replaces <c>IAppInstallationService</c> and <c>AppCatalog</c>, which apps may no longer inject
/// (ADR 0055).
/// </summary>
/// <remarks>
/// <para>
/// Every member requires the dangerous capability <c>apps.manage</c> (ADR 0049); a missing one
/// raises <see cref="HackUnauthorizedAccessException"/>.
/// </para>
/// <para>
/// <see cref="HackInstallScope.Machine"/> writes <c>/etc/hackeros/apps.config</c> for every user. Like
/// <c>pkexec</c>, the OS then asks an administrator for their password in its own secure window
/// (ADR 0055, section 5): the app never sees the password. A refusal or a cancellation raises
/// <see cref="HackUnauthorizedAccessException"/>. A process already running as root through
/// <c>sudo</c> is not asked again.
/// </para>
/// <para>
/// Nothing is deleted by an uninstall: settings and files stay in place for a reinstall (ADR 0048).
/// </para>
/// </remarks>
public static class HackAppManagement
{
    /// <summary>Gets every catalog app, installed or not, with its state and uninstall closure.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<HackAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Gateway.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>
    /// Uninstalls an app and every app that needs it, stopping their running instances.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">For the current user only, or for every user (administrator).</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <returns>Every app ID uninstalled, dependents first.</returns>
    /// <exception cref="ArgumentException">The app is not in the catalog.</exception>
    /// <exception cref="InvalidOperationException">The app, or an app that needs it, is a system app.</exception>
    /// <exception cref="HackUnauthorizedAccessException">
    /// This app lacks <c>apps.manage</c>, no administrator authenticated, or the file's rights deny the write.
    /// </exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<string>> UninstallAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        try
        {
            return await Gateway.UninstallAsync(appId, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>
    /// Tells, without changing anything, what <see cref="UninstallAsync"/> would do (ADR 0097,
    /// section 5): the apps it removes and their running processes, which it would stop. A
    /// confirmation dialog shows both before asking.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The app is not in the catalog.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        try
        {
            return await Gateway.PlanUninstallAsync(appId, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>
    /// Uninstalls like <see cref="UninstallAsync"/> and reports the processes it stopped, and which
    /// of them had to be forced after their grace period (ADR 0097, section 5).
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">For the current user only, or for every user (administrator).</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="ArgumentException">The app is not in the catalog.</exception>
    /// <exception cref="InvalidOperationException">The app, or an app that needs it, is a system app.</exception>
    /// <exception cref="HackUnauthorizedAccessException">
    /// This app lacks <c>apps.manage</c>, no administrator authenticated, or the file's rights deny the write.
    /// </exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackUninstallReport> UninstallWithReportAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        try
        {
            return await Gateway.UninstallWithReportAsync(appId, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>Reinstalls an app and its uninstalled dependencies in one scope.</summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">The scope that uninstalled it.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <returns>Every app ID reinstalled, dependencies first; empty when nothing was uninstalled in that scope.</returns>
    /// <exception cref="ArgumentException">The app is not in the catalog.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="HackInstallScope.User"/> was asked for an app the administrator uninstalled for every user.
    /// </exception>
    /// <exception cref="HackUnauthorizedAccessException">
    /// This app lacks <c>apps.manage</c>, no administrator authenticated, or the file's rights deny the write.
    /// </exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<string>> ReinstallAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        try
        {
            return await Gateway.ReinstallAsync(appId, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    private static IAppManagementGateway Gateway => HackExecutionContext.Current.AppManagement;
}
