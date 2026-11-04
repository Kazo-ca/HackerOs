using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Administration;

/// <summary>
/// Reads the raw storage of the machine for <c>HackStorageDiagnostics</c> (ADR 0056, section 11).
/// The browser host implements it over IndexedDB (<c>IndexedDbInspectorService</c> of
/// <c>Infrastructure.Browser</c>); Platform Core never touches the database itself.
/// </summary>
public interface IStorageInspector
{
    /// <summary>Reads every object store in one transaction and resolves each file's virtual path.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<HackStorageSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>Trusted <see cref="IAppStorageDiagnosticsGateway"/>: checks <c>system.storage.inspect</c>, then reads the snapshot.</summary>
internal sealed class AppStorageDiagnosticsGateway : IAppStorageDiagnosticsGateway
{
    private readonly Func<IStorageInspector>? _inspector;
    private readonly ICapabilityChecker _capabilities;
    private readonly WebData.IWebSiteDatabaseStore? _webSites;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="inspector">Lazily resolved inspector; <see langword="null"/> on a host without persistent storage.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="webSites">The browser databases of web sites (ADR 0088, section 4); <see langword="null"/> when the host has none.</param>
    public AppStorageDiagnosticsGateway(Func<IStorageInspector>? inspector, ICapabilityChecker capabilities, WebData.IWebSiteDatabaseStore? webSites = null)
    {
        _inspector = inspector;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _webSites = webSites;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackWebSiteDatabaseInfo>> GetWebSiteDatabasesAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.SystemStorageInspect);
        return _webSites is null ? [] : await _webSites.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteWebSiteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.SystemStorageInspect);
        WebData.WebSiteDatabaseNames.Require(databaseName);
        if (_webSites is not null)
        {
            await _webSites.DeleteAsync(databaseName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<HackStorageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.SystemStorageInspect);
        IStorageInspector inspector = _inspector?.Invoke()
            ?? throw new NotSupportedException("This KazoOS host has no persistent storage to inspect.");
        // ADR 0100: private settings files and keyrings are masked in every report and export.
        return StorageSnapshotRedactor.Redact(await inspector.CaptureAsync(cancellationToken).ConfigureAwait(false));
    }
}
