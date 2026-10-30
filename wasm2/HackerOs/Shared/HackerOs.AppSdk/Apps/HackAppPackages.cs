using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.Apps;

/// <summary>
/// Installs apps from their GitHub repository (ADR 0182), as <c>hackpkg</c> does: a repository
/// publishes <c>.hackapp</c> packages on its <c>hackeros-packages</c> branch, and the OS downloads
/// them itself from a host of <c>/etc/hackeros/app-sources.config</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member requires the dangerous capability <c>apps.manage</c> (ADR 0049). A user installs
/// for themselves from a trusted source without an administrator (ADR 0189 §3). An install for the
/// whole system, from an untrusted source, or of an app of the block list asks an administrator
/// twice, every time, even under <c>sudo</c>: a fresh sign-in, then the OS's approval window with
/// the app, its source and its capabilities (§4). Editing the sources or the block list asks an
/// administrator to authenticate once.
/// </para>
/// <para>
/// An installed app's code runs with the same rights as the OS: the trust in its source is the
/// real barrier. The app joins the catalog at the next start of the OS.
/// </para>
/// </remarks>
public static class HackAppPackages
{
    /// <summary>Reads the hosts and the trusted owners.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackageSources> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        CallAsync(gateway => gateway.GetPackageSourcesAsync(cancellationToken));

    /// <summary>Lists the apps installed from a package.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<IReadOnlyList<HackInstalledPackage>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        CallAsync(gateway => gateway.GetInstalledPackagesAsync(cancellationToken));

    /// <summary>Reads what a repository publishes.</summary>
    /// <param name="source"><c>github:owner/repo</c>, <c>owner/repo</c> or <c>https://github.com/owner/repo</c>.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="HackPackageException">The source is invalid, publishes nothing or cannot be downloaded.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackageInfo> GetInfoAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return CallAsync(gateway => gateway.GetPackageInfoAsync(source, cancellationToken));
    }

    /// <summary>
    /// Installs or updates an app; it appears at the next start of the OS. From a trusted source it
    /// installs for the session user without an administrator (ADR 0189 §3); for the system, from an
    /// untrusted source, or when the block list stops it, an administrator signs in and approves in
    /// the OS's window, every time (§4).
    /// </summary>
    /// <param name="source">The repository, optionally followed by <c>@version</c>.</param>
    /// <param name="scope">For whom; <see cref="HackPackageScope.Auto"/> picks the personal install when allowed.</param>
    /// <param name="cancellationToken">Cancels the download, the approval and the writes.</param>
    /// <exception cref="HackPackageException">The package is refused; its code says why.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or the administrator did not approve.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackageInstallReport> InstallAsync(
        string source, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return CallAsync(gateway => gateway.InstallPackageAsync(source, scope, cancellationToken));
    }

    /// <summary>Removes an app installed from a package: the user's own copy freely, a system copy after the two-step approval.</summary>
    /// <param name="appId">The installed app.</param>
    /// <param name="scope">Which copy; <see cref="HackPackageScope.Auto"/> takes the user's own, else the system's.</param>
    /// <param name="cancellationToken">Cancels the approval and the writes.</param>
    /// <exception cref="HackPackageException">The app was not installed from a package.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or the administrator did not approve.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackInstalledPackage> RemoveAsync(
        string appId, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return CallAsync(gateway => gateway.RemovePackageAsync(appId, scope, cancellationToken));
    }

    /// <summary>Adds a catalog or a GitHub owner or repository to the sources, after an administrator authenticated (ADR 0189 §1).</summary>
    /// <param name="source"><c>name=https://…/catalog.json</c>, <c>https://…/catalog.json</c>, <c>github:owner</c> or <c>github:owner/repo</c>.</param>
    /// <param name="trusted">Whether its apps install for one user without an administrator; not by default (Q25).</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="HackPackageException">The source is not valid (<c>apps.source.invalid</c>).</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackageSources> AddSourceAsync(string source, bool trusted = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return CallAsync(gateway => gateway.AddPackageSourceAsync(source, trusted, cancellationToken));
    }

    /// <summary>Removes a source, after an administrator authenticated.</summary>
    /// <param name="source">A catalog (<c>catalog:name</c>) or a GitHub owner or repository.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="HackPackageException">The source is not valid or not listed.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackageSources> RemoveSourceAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return CallAsync(gateway => gateway.RemovePackageSourceAsync(source, cancellationToken));
    }

    /// <summary>Reads the administrator's block list (ADR 0189 §5).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackagePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        CallAsync(gateway => gateway.GetPackagePolicyAsync(cancellationToken));

    /// <summary>Blocks or unblocks an app, a category or a capability for personal installs, after an administrator authenticated.</summary>
    /// <param name="kind">Kind of rule.</param>
    /// <param name="value">App ID, category or capability.</param>
    /// <param name="blocked"><see langword="true"/> to block, <see langword="false"/> to unblock.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackagePolicy> SetPolicyAsync(
        HackPackageBlockKind kind, string value, bool blocked, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return CallAsync(gateway => gateway.SetPackagePolicyAsync(kind, value, blocked, cancellationToken));
    }

    private static async Task<T> CallAsync<T>(Func<IAppManagementGateway, ValueTask<T>> call)
    {
        try
        {
            return await call(HackExecutionContext.Current.AppManagement).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }
}
