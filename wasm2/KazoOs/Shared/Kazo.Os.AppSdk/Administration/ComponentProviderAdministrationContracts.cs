namespace Kazo.Os.AppSdk.Administration;

/// <summary>One app that provides a component contract an administrator chooses (ADR 0156).</summary>
/// <param name="AppId">The app.</param>
/// <param name="Name">Its display name.</param>
/// <param name="IsSystemApp">Whether it is a system app: choosing it needs no approval window, only an administrator.</param>
/// <param name="IsInstalled">Whether it is installed for the session user.</param>
public sealed record KazoComponentProviderInfo(string AppId, string Name, bool IsSystemApp, bool IsInstalled);

/// <summary>A contract an administrator chooses the provider of, such as <c>hackeros.file-view</c> (ADR 0156).</summary>
/// <param name="Contract">The contract name.</param>
/// <param name="SelectedAppId">The app chosen in <c>/etc/hackeros/components.config</c>, or <see langword="null"/> for the default.</param>
/// <param name="DefaultAppId">The app used without a choice (the File Explorer), or <see langword="null"/> when none is installed.</param>
/// <param name="Providers">Every app registering the contract, system apps first, then by id.</param>
public sealed record KazoComponentContractInfo(
    string Contract,
    string? SelectedAppId,
    string? DefaultAppId,
    IReadOnlyList<KazoComponentProviderInfo> Providers);

/// <summary>
/// Trusted OS gateway behind <see cref="KazoComponentProviders"/> (ADR 0156 §3). Every member
/// requires the capability <c>apps.manage</c>.
/// </summary>
public interface IAppComponentProviderAdministrationGateway
{
    /// <summary>Lists each contract an administrator chooses, its providers and the current choice.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<KazoComponentContractInfo>> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="appId"/> the provider of <paramref name="contracts"/> for the whole
    /// machine, or goes back to the default when it is <see langword="null"/>.
    /// </summary>
    /// <param name="contracts">The contracts, at least one; several share one approval.</param>
    /// <param name="appId">The app, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">Cancels the approval and the write.</param>
    ValueTask SetProviderAsync(IReadOnlyList<string> contracts, string? appId, CancellationToken cancellationToken = default);
}
