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
/// Every member requires the dangerous capability <c>apps.manage</c> (ADR 0049). An install, an
/// update or a removal changes the machine: the OS asks an administrator first (ADR 0055,
/// section 5), unless the process already runs as root through <c>sudo</c>.
/// </para>
/// <para>
/// An installed app's code runs with the same rights as the OS: only the owners of
/// <c>[trusted]</c> are accepted, unless the caller passes <c>allowUntrusted</c>. The app joins the
/// catalog at the next start of the OS.
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

    /// <summary>Installs or updates an app for every user; it appears at the next start of the OS.</summary>
    /// <param name="source">The repository, optionally followed by <c>@version</c>.</param>
    /// <param name="allowUntrusted">Accepts an owner missing from <c>[trusted]</c>.</param>
    /// <param name="cancellationToken">Cancels the download, the authentication and the writes.</param>
    /// <exception cref="HackPackageException">The package is refused; its code says why.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackPackageInstallReport> InstallAsync(
        string source, bool allowUntrusted = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return CallAsync(gateway => gateway.InstallPackageAsync(source, allowUntrusted, cancellationToken));
    }

    /// <summary>Removes an app installed from a package, for every user.</summary>
    /// <param name="appId">The installed app.</param>
    /// <param name="cancellationToken">Cancels the authentication and the writes.</param>
    /// <exception cref="HackPackageException">The app was not installed from a package.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.manage</c>, or no administrator authenticated.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackInstalledPackage> RemoveAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return CallAsync(gateway => gateway.RemovePackageAsync(appId, cancellationToken));
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
